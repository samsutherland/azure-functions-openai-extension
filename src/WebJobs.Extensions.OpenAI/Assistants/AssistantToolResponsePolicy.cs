// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;

// Opt-in, per-turn boundary. Tool data remains in paired history; model prose on
// operation turns never establishes persistence or delivery. Unsupported outcome
// contracts deliberately fall back until their domain evidence is migrated.
internal sealed class AssistantToolResponsePolicy
{
    internal const string UnverifiedResponse = "I couldn't verify all requested operations. Check the saved state before trying again; completion and device delivery are not confirmed.";
    readonly HashSet<string> operations;
    readonly List<AssistantToolWorkflow.ToolExecution> results = new();

    internal AssistantToolResponsePolicy(string? operationNames)
    {
        this.operations = new HashSet<string>((operationNames ?? string.Empty).Split(';')
            .Select(name => name.Trim()).Where(name => name.Length > 0), StringComparer.OrdinalIgnoreCase);
    }

    internal bool IsActive { get; private set; }

    internal void ObserveCalls(IEnumerable<ChatToolCall> calls) =>
        this.IsActive |= calls.Any(call => this.operations.Contains(call.FunctionName));

    internal void ObserveResults(IEnumerable<AssistantToolWorkflow.ToolExecution> executions) =>
        this.results.AddRange(executions.Where(execution => this.operations.Contains(execution.Call.FunctionName)));

    internal string Render()
    {
        if (this.results.Count == 0)
        {
            return UnverifiedResponse;
        }

        var messages = new List<string>();
        foreach (AssistantToolWorkflow.ToolExecution execution in this.results)
        {
            string? message = RenderResult(execution);
            if (message is null)
            {
                // No partial success summary until entity/date/revision evidence
                // has been migrated. Do not let unrelated successes hide a failure.
                return UnverifiedResponse;
            }
            messages.Add(message);
        }
        return string.Join(Environment.NewLine + Environment.NewLine, messages.Distinct());
    }

    static string? RenderResult(AssistantToolWorkflow.ToolExecution execution)
    {
        try
        {
            if (JToken.Parse(execution.Result ?? string.Empty) is not JObject result
                || result["schemaVersion"]?.Type != JTokenType.Integer
                || result["schemaVersion"]?.ToString() != "1"
                || result["operation"]?.Type != JTokenType.String
                || result["operation"]?.Value<string>() != execution.Call.FunctionName
                || result["verified"]?.Type != JTokenType.Boolean
                || result["verified"]?.Value<bool>() != true)
            {
                return null;
            }

            bool success = result["success"]?.Type == JTokenType.Boolean && result["success"]!.Value<bool>();
            string? outcome = result["outcome"]?.Type == JTokenType.String ? result["outcome"]!.Value<string>() : null;
            if (success && outcome == "ready")
            {
                return "Your saved setup is ready.";
            }
            if (success && outcome == "saved"
                && IsTrue(result["persisted"]) && IsTrue(result["canTellAthleteSaved"]))
            {
                return "Your change was saved and verified in the app. Device delivery is not confirmed.";
            }
            // Missing input is an application-owned question, not a completion claim.
            if (!success && outcome == "needs_user_input" && result["nextQuestion"]?.Type == JTokenType.String)
            {
                return result["nextQuestion"]!.Value<string>();
            }
        }
        catch (JsonException)
        {
            // Malformed or unsupported evidence never establishes completion.
        }
        return null;
    }

    static bool IsTrue(JToken? token) => token?.Type == JTokenType.Boolean && token.Value<bool>();
}
