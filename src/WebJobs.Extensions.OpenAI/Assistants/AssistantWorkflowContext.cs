// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;

// The configured context operation owns authentication, authorization and snapshot
// freshness. Its opaque request context is used only for the internal invocation.
internal sealed record AssistantWorkflowContext(string Instructions, IList<ChatTool> AllowedTools, JObject? ArgumentOverrides)
{
    internal const string UnavailableResponse = "I couldn't verify the current workflow context. Please try again; completion and provider delivery are not confirmed.";
    internal const string BlockedResponse = "A requested tool is unavailable for the current workflow. Completion and provider delivery are not confirmed.";

    internal static async Task<AssistantWorkflowContext?> LoadAsync(IAssistantSkillInvoker invoker, string? operation,
        string? requestContext, string assistantId, IList<ChatTool>? registered, CancellationToken cancellationToken, string? userMessage = null)
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
        if (text?.Length > 65536 || AssistantToolWorkflow.ParseResult(text) is not JObject result
            || result["schemaVersion"]?.Type != JTokenType.Integer || result["schemaVersion"]!.ToString() != "1"
            || result["operation"]?.Type != JTokenType.String || result["operation"]!.Value<string>() != operation
            || !True(result["success"]) || !True(result["verified"])
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
        return new(result["instructions"]!.Value<string>()!, registered.Where(t => names.Any(n => n.Value<string>() == t.FunctionName)).ToList(), overrides);
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
    static bool Name(string? name) => name is not null && Regex.IsMatch(name, @"\A[A-Za-z0-9_-]{1,64}\z");
    static bool True(JToken? token) => token?.Type == JTokenType.Boolean && token.Value<bool>();
}
