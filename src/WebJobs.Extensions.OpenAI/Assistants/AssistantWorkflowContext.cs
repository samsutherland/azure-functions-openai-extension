// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;

// The configured context operation owns authentication, authorization and snapshot
// freshness. Its opaque request context is used only for the internal invocation.
internal sealed record AssistantWorkflowContext(string Instructions, IList<ChatTool> AllowedTools, JObject? ArgumentOverrides, bool RequireOperationEvidence, JObject? ResponsePrerequisite, AssistantToolWorkflow.ToolExecution? ResponseConflict, bool RequireDetailsEvidence, JObject? ResponseDetails)
{
    internal JObject? OperationArgumentOverrides { get; init; }
    internal bool RequireObservationEvidence { get; init; }
    internal JObject? ResponseObservation { get; init; }

    internal const string UnavailableResponse = "I couldn't verify the current workflow context. Please try again; completion and provider delivery are not confirmed.";
    internal const string BlockedResponse = "A requested tool is unavailable for the current workflow. Completion and provider delivery are not confirmed.";

    internal static async Task<AssistantWorkflowContext?> LoadAsync(IAssistantSkillInvoker invoker, string? operation,
        string? requestContext, string assistantId, IList<ChatTool>? registered, CancellationToken cancellationToken, string? userMessage = null, string? internalOperationNames = null)
    {
        if (!Name(operation) || string.IsNullOrWhiteSpace(requestContext) || requestContext.Length > 16384
            || string.IsNullOrWhiteSpace(assistantId) || assistantId.Length > 1024 || userMessage?.Length > 16384
            || registered is null || !registered.Any(t => t.FunctionName == operation)) return null;
        var payload = new JObject { ["assistantId"] = assistantId, ["requestContext"] = requestContext, ["userMessage"] = userMessage };
        var arguments = new JObject { ["input"] = payload.ToString(Formatting.None) };
        string? text;
        try
        {
            text = await invoker.InvokeAsync(ChatToolCall.CreateFunctionToolCall("workflow-context", operation!,
                BinaryData.FromString(arguments.ToString(Formatting.None))), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
        if (text?.Length > 65536 || ParseContext(text) is not JObject result
            || result["schemaVersion"]?.Type != JTokenType.Integer || result["schemaVersion"]!.ToString() != "1"
            || result["operation"]?.Type != JTokenType.String || result["operation"]!.Value<string>() != operation
            || !True(result["success"]) || !True(result["verified"])
            || result["requireOperationEvidence"] is JToken evidencePolicy && evidencePolicy.Type != JTokenType.Boolean
            || result["requireDetailsEvidence"] is JToken detailsPolicy && detailsPolicy.Type != JTokenType.Boolean
            || result["requireObservationEvidence"] is JToken observationPolicy && observationPolicy.Type != JTokenType.Boolean
            || result["instructions"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(result["instructions"]!.Value<string>())
            || result["instructions"]!.Value<string>()!.Length > 16384
            || result["allowedToolNames"] is not JArray names || names.Count > 128
            || names.Any(n => n.Type != JTokenType.String || !Name(n.Value<string>()) || n.Value<string>() == operation
                || !registered.Any(t => t.FunctionName == n.Value<string>()))
            || names.Select(n => n.Value<string>()).Distinct(StringComparer.Ordinal).Count() != names.Count)
            return null;
        JObject? overrides = null;
        if (result["toolArgumentOverrides"] is JToken overrideToken && overrideToken.Type != JTokenType.Null)
        {
            if (overrideToken is not JObject values || values.Properties().Any(p => p.Name is not "userId" and not "externalUserId")
                || values["userId"] is JToken id && (id.Type != JTokenType.Integer || !int.TryParse(id.ToString(), out int value) || value <= 0)
                || values["externalUserId"] is JToken external && (external.Type != JTokenType.String || string.IsNullOrWhiteSpace(external.Value<string>()) || external.Value<string>()!.Length > 256))
                return null;
            overrides = (JObject)values.DeepClone();
        }
        var internalOperations = new HashSet<string>((internalOperationNames ?? "").Split(';').Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        JObject? operationOverrides = null;
        if (result["toolOperationArgumentOverrides"] is JToken bindings && bindings.Type != JTokenType.Null)
        {
            if (bindings is not JObject map || map.Count > 32 || map.ToString(Formatting.None).Length > 8192
                || map.Properties().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != map.Count
                || map.Properties().Any(p => !names.Any(n => n.Value<string>() == p.Name) || p.Name == operation || internalOperations.Contains(p.Name)
                    || p.Value is not JObject values || values.Count > 32
                    || values.Properties().Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Count
                    || values.Properties().Any(v => !Regex.IsMatch(v.Name, @"\A[A-Za-z][A-Za-z0-9_]{0,63}\z")
                        || Identity(v.Name) || v.Value.Type is not (JTokenType.String or JTokenType.Boolean or JTokenType.Integer or JTokenType.Null)
                        || v.Value.Type == JTokenType.String && v.Value.Value<string>()!.Length > 1024
                        || v.Value.Type == JTokenType.Integer && !long.TryParse(v.Value.ToString(), out _)))) return null;
            operationOverrides = (JObject)map.DeepClone();
        }
        JObject? prerequisite = null;
        if (result["responsePrerequisite"] is JToken prerequisiteToken && prerequisiteToken.Type != JTokenType.Null)
        {
            if (prerequisiteToken is not JObject read || read.ToString(Formatting.None).Length > 16384
                || read["schemaVersion"]?.Type != JTokenType.Integer || read["schemaVersion"]!.ToString() != "1"
                || read["operation"]?.Type != JTokenType.String || read["operation"]!.Value<string>() != "GetTrainingSetupStatusSkill"
                || !True(read["verified"]) || read["actorId"]?.Type != JTokenType.Integer
                || overrides?["userId"] is null || !JToken.DeepEquals(read["actorId"], overrides["userId"])
                || read["ready"]?.Type != JTokenType.Boolean || read["success"]?.Type != JTokenType.Boolean
                || True(read["ready"]) != True(read["success"])
                || read["outcome"]?.Type != JTokenType.String || read["outcome"]!.Value<string>() != (True(read["ready"]) ? "ready" : "needs_user_input")
                || !ValidOptionalText(read["nextQuestion"], 2048) || !ValidOptionalText(read["promptContext"], 8192)
                || !True(read["ready"]) && string.IsNullOrWhiteSpace(read["nextQuestion"]?.Value<string>())
                || True(read["ready"]) && !string.IsNullOrWhiteSpace(read["nextQuestion"]?.Value<string>())) return null;
            prerequisite = (JObject)read.DeepClone();
        }
        AssistantToolWorkflow.ToolExecution? conflict = null;
        if (result["responseConflict"] is JToken conflictToken && conflictToken.Type != JTokenType.Null)
        {
            if (conflictToken is not JObject rejection || rejection.ToString(Formatting.None).Length > 32768
                || overrides?["userId"] is null
                || (conflict = AssistantScopedConflictResponse.ContextEvidence(rejection, overrides["userId"]!)) is null) return null;
        }
        JObject? details = null;
        if (result["responseDetails"] is JToken detailsToken && detailsToken.Type != JTokenType.Null)
        {
            if (detailsToken is not JObject facts || (details = AssistantSavedWorkoutDetailsResponse.Validate(facts, overrides?["userId"])) is null) return null;
        }
        JObject? observation = null;
        if (result["responseObservation"] is JToken observationToken && observationToken.Type != JTokenType.Null)
        {
            if (observationToken is not JObject facts || (observation = AssistantExistingStateResponse.Validate(facts, overrides?["userId"])) is null) return null;
        }
        return new(result["instructions"]!.Value<string>()!, registered.Where(t => names.Any(n => n.Value<string>() == t.FunctionName)).ToList(), overrides, True(result["requireOperationEvidence"]), prerequisite, conflict, True(result["requireDetailsEvidence"]), details) { OperationArgumentOverrides = operationOverrides, RequireObservationEvidence = True(result["requireObservationEvidence"]), ResponseObservation = observation };
    }

    internal ChatToolCall? Canonicalize(ChatToolCall call)
    {
        if (AssistantToolWorkflow.ParseResult(call.FunctionArguments.ToString()) is not JObject args) return null;
        bool wrapped = args["input"] is not null;
        JObject? payload = args["input"] switch
        {
            JObject value => value,
            JValue { Type: JTokenType.String } value => AssistantToolWorkflow.ParseResult(value.Value<string>()) as JObject,
            null => args,
            _ => null
        };
        if (payload is null) return null;
        if (this.OperationArgumentOverrides?[call.FunctionName] is JObject bindings)
        {
            foreach (JProperty value in bindings.Properties())
            {
                foreach (JProperty competing in payload.Properties().Where(p => p.Name.Equals(value.Name, StringComparison.OrdinalIgnoreCase)).ToList()) competing.Remove();
                payload[value.Name] = value.Value.DeepClone();
            }
        }
        if (this.ArgumentOverrides is { Count: > 0 })
        {
            // Remove both competing selectors: the application resolver may prefer
            // external identity even when a canonical integer identity is present.
            foreach (JProperty identity in payload.Properties().Where(p => p.Name.Equals("userId", StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals("externalUserId", StringComparison.OrdinalIgnoreCase)).ToList()) identity.Remove();
            foreach (JProperty value in this.ArgumentOverrides.Properties()) payload[value.Name] = value.Value.DeepClone();
        }
        JObject canonical = wrapped ? new JObject { ["input"] = payload.ToString(Formatting.None) } : payload;
        return ChatToolCall.CreateFunctionToolCall(call.Id, call.FunctionName, BinaryData.FromString(canonical.ToString(Formatting.None)));
    }

    internal bool Allows(ChatToolCall call) => this.AllowedTools.Any(t => t.FunctionName == call.FunctionName) && this.Canonicalize(call) is not null;
    static JToken? ParseContext(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var settings = new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error };
            JToken value = JToken.Parse(text!, settings);
            for (int i = 0; i < 2 && value.Type == JTokenType.String; i++) value = JToken.Parse(value.Value<string>()!, settings);
            return value;
        }
        catch (JsonReaderException) { return null; }
    }
    static bool Identity(string name) => name.Equals("userId", StringComparison.OrdinalIgnoreCase) || name.Equals("externalUserId", StringComparison.OrdinalIgnoreCase);
    static bool ValidOptionalText(JToken? token, int limit) => token is null || token.Type == JTokenType.Null
        || token.Type == JTokenType.String && token.Value<string>()!.Length <= limit;
    static bool Name(string? name) => name is not null && Regex.IsMatch(name, @"\A[A-Za-z0-9_-]{1,64}\z");
    static bool True(JToken? token) => token?.Type == JTokenType.Boolean && token.Value<bool>();
}
