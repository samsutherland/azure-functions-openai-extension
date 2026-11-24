// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;

// One recovery attempt per declared read per processing invocation. Canonical
// actor evidence comes from application results, never model-selected arguments.
internal sealed class AssistantPrerequisiteRecovery
{
    readonly Dictionary<string, HashSet<string>> repairs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ReadState> states = new(StringComparer.OrdinalIgnoreCase);

    internal AssistantPrerequisiteRecovery(IEnumerable<AssistantToolWorkflow.DependencyRule>? rules)
    {
        foreach (var rule in rules ?? Enumerable.Empty<AssistantToolWorkflow.DependencyRule>())
        {
            if (!this.repairs.TryGetValue(rule.PrerequisiteOperation, out var names))
                this.repairs[rule.PrerequisiteOperation] = names = new(StringComparer.OrdinalIgnoreCase);
            names.Add(rule.DependentOperation);
        }
    }

    internal bool Controls(string operation) => this.repairs.ContainsKey(operation);

    // null: ordinary call; false: blocked recovery; true: consume the single retry.
    internal bool? Authorize(ChatToolCall call)
    {
        if (!this.states.TryGetValue(call.FunctionName, out var state))
            return null;
        if (AssistantToolWorkflow.Fingerprint(call) != state.Fingerprint)
            return false;
        if (state.Resolved)
            return null;
        if (!state.Credit || state.Used)
            return false;
        state.Used = true;
        state.Credit = false;
        state.Authorized = true;
        return true;
    }

    internal bool ObserveRead(ChatToolCall call, string? result)
    {
        JObject? evidence = Evidence(call.FunctionName, result);
        string? actor = Actor(evidence);
        bool ready = IsTrue(evidence?["success"]) && IsTrue(evidence?["verified"])
            && IsTrue(evidence?["ready"]) && Text(evidence?["outcome"]) == "ready" && actor is not null;
        if (!this.states.TryGetValue(call.FunctionName, out var state))
        {
            if (ready)
                return true;
            this.states[call.FunctionName] = new(AssistantToolWorkflow.Fingerprint(call), actor);
            return false;
        }
        bool matched = state.Fingerprint == AssistantToolWorkflow.Fingerprint(call)
            && state.Actor is not null && state.Actor == actor;
        state.Resolved = matched && ready && (state.Authorized || state.Resolved);
        state.Authorized = false;
        return state.Resolved;
    }

    internal void ObserveRepair(ChatToolCall call, string? result)
    {
        JObject? evidence = Evidence(call.FunctionName, result);
        string? actor = Actor(evidence);
        if (actor is null || !IsTrue(evidence?["success"]) || !IsTrue(evidence?["verified"])
            || !IsTrue(evidence?["persisted"]) || Text(evidence?["outcome"]) != "confirmed")
            return;
        foreach (var pair in this.states)
            if (!pair.Value.Used && !pair.Value.Resolved && pair.Value.Actor == actor
                && this.repairs[pair.Key].Contains(call.FunctionName))
                pair.Value.Credit = true;
    }

    internal static bool SameVerifiedRead(ChatToolCall original, string? originalResult, ChatToolCall current, string? currentResult)
    {
        JObject? before = Evidence(original.FunctionName, originalResult);
        JObject? after = Evidence(current.FunctionName, currentResult);
        return original.FunctionName == current.FunctionName
            && AssistantToolWorkflow.Fingerprint(original) == AssistantToolWorkflow.Fingerprint(current)
            && Actor(before) is string actor && Actor(after) == actor && IsTrue(after?["verified"])
            && ((Text(after?["outcome"]) == "ready" && IsTrue(after?["success"]) && IsTrue(after?["ready"]))
                || (Text(after?["outcome"]) == "needs_user_input" && after?["success"]?.Type == JTokenType.Boolean && !IsTrue(after["success"])));
    }

    internal static JObject? Evidence(string operation, string? result)
    {
        try
        {
            return JToken.Parse(result ?? string.Empty) is JObject obj
                && obj["schemaVersion"]?.Type == JTokenType.Integer && obj["schemaVersion"]!.ToString() == "1"
                && Text(obj["operation"]) == operation ? obj : null;
        }
        catch (JsonException) { return null; }
    }

    static string? Actor(JObject? obj) => obj?["actorId"]?.Type == JTokenType.Integer
        && int.TryParse(obj["actorId"]!.ToString(), out int actor) && actor > 0 ? actor.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    static string? Text(JToken? token) => token?.Type == JTokenType.String ? token.Value<string>() : null;
    static bool IsTrue(JToken? token) => token?.Type == JTokenType.Boolean && token.Value<bool>();

    sealed class ReadState
    {
        internal ReadState(string fingerprint, string? actor) { this.Fingerprint = fingerprint; this.Actor = actor; }
        internal string Fingerprint { get; }
        internal string? Actor { get; }
        internal bool Credit { get; set; }
        internal bool Used { get; set; }
        internal bool Authorized { get; set; }
        internal bool Resolved { get; set; }
    }
}
