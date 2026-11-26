// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

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
    readonly HashSet<string> prerequisites;
    readonly List<AssistantToolWorkflow.ToolExecution> results = new();
    readonly List<ChatToolCall> unexecutedScopedCalls = new();

    internal AssistantToolResponsePolicy(string? operationNames, string? prerequisiteNames = null)
    {
        this.operations = ParseNames(operationNames);
        this.prerequisites = ParseNames(prerequisiteNames);
    }

    static HashSet<string> ParseNames(string? names) => new((names ?? string.Empty).Split(';')
        .Select(name => name.Trim()).Where(name => name.Length > 0), StringComparer.OrdinalIgnoreCase);

    internal bool IsActive { get; private set; }
    bool hasUnexecutedSensitiveCalls;

    internal void ObserveCalls(IEnumerable<ChatToolCall> calls) =>
        this.IsActive |= calls.Any(call => this.operations.Contains(call.FunctionName));

    internal void ObserveUnexecutedCalls(IEnumerable<ChatToolCall> calls)
    {
        foreach (ChatToolCall call in calls)
        {
            if (!this.operations.Contains(call.FunctionName) && !this.prerequisites.Contains(call.FunctionName)) continue;
            this.IsActive = true;
            if (this.operations.Contains(call.FunctionName) && (AssistantScopedReplacementResponse.IsReplacement(call) || AssistantScopedDeliveryResponse.IsDelivery(call)))
                this.unexecutedScopedCalls.Add(call);
            else
                this.hasUnexecutedSensitiveCalls = true;
        }
    }

    internal void ObserveResults(IEnumerable<AssistantToolWorkflow.ToolExecution> executions)
    {
        foreach (AssistantToolWorkflow.ToolExecution execution in executions)
        {
            bool replacesPriorRead = execution.IsRecoveryRead && this.results.Any(prior =>
                AssistantPrerequisiteRecovery.SameVerifiedRead(prior.Call, prior.Result, execution.Call, execution.Result));
            if (replacesPriorRead)
                this.results.RemoveAll(prior => AssistantPrerequisiteRecovery.SameVerifiedRead(
                    prior.Call, prior.Result, execution.Call, execution.Result));
            bool blockedPrerequisite = this.prerequisites.Contains(execution.Call.FunctionName)
                && !IsVerifiedReady(execution);
            this.IsActive |= blockedPrerequisite;
            if (blockedPrerequisite || replacesPriorRead || this.operations.Contains(execution.Call.FunctionName))
                this.results.Add(execution);
        }
    }

    static bool IsVerifiedReady(AssistantToolWorkflow.ToolExecution execution)
    {
        JObject? result = ParseVerifiedResult(execution);
        return result is not null && IsTrue(result["success"]) && IsTrue(result["ready"])
            && result["outcome"]?.Type == JTokenType.String && result["outcome"]!.Value<string>() == "ready";
    }

    internal string Render()
    {
        if ((this.results.Count == 0 && this.unexecutedScopedCalls.Count == 0) || this.hasUnexecutedSensitiveCalls)
        {
            return UnverifiedResponse;
        }

        var messages = new List<string>();
        var planRevisions = new Dictionary<string, string>();
        var current = new List<AssistantToolWorkflow.ToolExecution>();
        foreach (AssistantToolWorkflow.ToolExecution execution in this.results.Concat(this.unexecutedScopedCalls.Select(call =>
            new AssistantToolWorkflow.ToolExecution(call, "", AssistantToolWorkflow.ToolExecutionDisposition.DependencyBlocked))))
        {
            JObject? args = AssistantScopedReplacementResponse.Arguments(execution.Call);
            JObject? evidence = ParseVerifiedResult(execution);
            if (AssistantScopedReplacementResponse.IsReplacement(execution.Call) && args?["weeklyPlanId"] is not null)
            {
                // A workout fingerprint is not a plan fingerprint. Retire earlier
                // whole-plan claims after any attempted edit, including uncertain writes.
                current.RemoveAll(prior =>
                {
                    JObject? priorResult = ParseVerifiedResult(prior);
                    JObject? priorArgs = AssistantScopedReplacementResponse.Arguments(prior.Call);
                    return priorResult?["weeklyPlanId"] is not null
                        && JToken.DeepEquals(priorResult["weeklyPlanId"], args["weeklyPlanId"])
                        && (priorResult["savedDates"] is not null || AssistantScopedDeliveryResponse.IsDelivery(prior.Call))
                        || AssistantScopedDeliveryResponse.IsDelivery(prior.Call)
                        && JToken.DeepEquals(AssistantScopedDeliveryResponse.Arguments(prior.Call)?["weeklyPlanId"], args["weeklyPlanId"])
                        || AssistantScopedReplacementResponse.IsReplacement(prior.Call)
                        && JToken.DeepEquals(priorArgs?["weeklyPlanId"], args["weeklyPlanId"])
                        && JToken.DeepEquals(priorArgs?["date"], args["date"]);
                });
            }
            else if (AssistantScopedDeliveryResponse.IsDelivery(execution.Call))
            {
                JObject? deliveryArgs = AssistantScopedDeliveryResponse.Arguments(execution.Call);
                current.RemoveAll(prior =>
                {
                    JObject? priorArgs = AssistantScopedDeliveryResponse.Arguments(prior.Call);
                    JObject? priorResult = ParseVerifiedResult(prior);
                    return deliveryArgs?["weeklyPlanId"] is not null
                        && (JToken.DeepEquals(priorArgs?["weeklyPlanId"], deliveryArgs["weeklyPlanId"])
                            || JToken.DeepEquals(priorResult?["weeklyPlanId"], deliveryArgs["weeklyPlanId"]))
                        && (execution.Call.FunctionName == "FinalizeWeeklyPlanSkill" || prior.Call.FunctionName == "FinalizeWeeklyPlanSkill"
                            || priorResult?["savedDates"] is not null && !AssistantScopedDeliveryResponse.IsDelivery(prior.Call)
                            || JToken.DeepEquals(priorArgs?["date"], deliveryArgs["date"]));
                });
            }
            else if (evidence?["savedDates"] is not null)
            {
                current.RemoveAll(prior => (AssistantScopedReplacementResponse.IsReplacement(prior.Call) || AssistantScopedDeliveryResponse.IsDelivery(prior.Call))
                    && JToken.DeepEquals(AssistantScopedDeliveryResponse.Arguments(prior.Call)?["weeklyPlanId"], evidence["weeklyPlanId"]));
            }
            current.Add(execution);
        }
        foreach (AssistantToolWorkflow.ToolExecution execution in current)
        {
            JObject? evidence = ParseVerifiedResult(execution);
            if (evidence?["savedDates"] is not null && evidence["weeklyPlanId"] is not null && !AssistantScopedDeliveryResponse.IsDelivery(execution.Call))
            {
                string scope = evidence["actorId"] + ":" + evidence["weeklyPlanId"];
                string revision = evidence["revision"]?.ToString() ?? "";
                if (planRevisions.TryGetValue(scope, out string? previous) && previous != revision)
                    return UnverifiedResponse;
                planRevisions[scope] = revision;
            }
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
        if (AssistantScopedReplacementResponse.IsReplacement(execution.Call))
            return AssistantScopedReplacementResponse.Render(execution);
        if (AssistantScopedDeliveryResponse.IsDelivery(execution.Call))
            return AssistantScopedDeliveryResponse.Render(execution);
        // Runtime disposition proves this attempt had no side effects. Preserve a
        // verified prerequisite question alongside it; invoked/unknown results still fail closed.
        if (execution.Disposition == AssistantToolWorkflow.ToolExecutionDisposition.DependencyBlocked)
            return "The requested operation was not attempted. Completion and provider delivery are not confirmed.";
        JObject? result = ParseVerifiedResult(execution);
        if (result is null)
            return null;
        bool success = result["success"]?.Type == JTokenType.Boolean && result["success"]!.Value<bool>();
        string? outcome = result["outcome"]?.Type == JTokenType.String ? result["outcome"]!.Value<string>() : null;
        if (outcome is "generated" or "regenerated" or "reused_existing" or "needs_reconciliation")
            return AssistantScopedPlanResponse.Render(result, execution.Call);
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
            string? question = result["nextQuestion"]!.Value<string>();
            if (string.IsNullOrWhiteSpace(question))
                return null;
            JToken? context = result["promptContext"];
            if (context is null || context.Type == JTokenType.Null)
                return question;
            if (context.Type != JTokenType.String)
                return null;
            string? savedContext = context.Value<string>();
            return string.IsNullOrWhiteSpace(savedContext) ? question
                : savedContext + Environment.NewLine + Environment.NewLine + question;
        }
        return null;
    }

    static JObject? ParseVerifiedResult(AssistantToolWorkflow.ToolExecution execution) =>
        AssistantToolWorkflow.ParseResult(execution.Result) is JObject result
            && result["schemaVersion"]?.Type == JTokenType.Integer
            && result["schemaVersion"]?.ToString() == "1"
            && result["operation"]?.Type == JTokenType.String
            && result["operation"]?.Value<string>() == execution.Call.FunctionName
            && IsTrue(result["verified"]) ? result : null;

    static bool IsTrue(JToken? token) => token?.Type == JTokenType.Boolean && token.Value<bool>();
}
