// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
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
    AssistantToolWorkflow.ToolExecution? contextPrerequisite;
    AssistantToolWorkflow.ToolExecution? contextConflict;
    JObject? contextDetails;
    bool requiresDetailsEvidence;
    bool requiresOperationEvidence;

    internal AssistantToolResponsePolicy(string? operationNames, string? prerequisiteNames = null)
    {
        this.operations = ParseNames(operationNames);
        this.prerequisites = ParseNames(prerequisiteNames);
    }

    static HashSet<string> ParseNames(string? names) => new((names ?? string.Empty).Split(';')
        .Select(name => name.Trim()).Where(name => name.Length > 0), StringComparer.OrdinalIgnoreCase);

    internal bool IsActive { get; private set; }

    // Application-selected status workflows require proof even if the model
    // avoids making a sensitive call. Once active, this remains sticky for the turn.
    internal void RequireOperationEvidence() { this.IsActive = true; this.requiresOperationEvidence = true; }

    internal void ObserveContextDetails(bool required, JObject? details)
    {
        this.requiresDetailsEvidence |= required;
        this.IsActive |= required;
        this.contextDetails = details;
    }
    bool hasUnexecutedSensitiveCalls;

    // Current context may supply a verified application question without a model
    // tool call. Keep this separate from execution/dependency state and tool history.
    internal void ObserveContextPrerequisite(JObject? result)
    {
        this.contextPrerequisite = null;
        string? operation = result?["operation"]?.Type == JTokenType.String ? result["operation"]!.Value<string>() : null;
        if (result is null || operation is null || !this.prerequisites.Contains(operation)) return;
        var call = ChatToolCall.CreateFunctionToolCall("context-prerequisite", operation,
            BinaryData.FromString(new JObject { ["userId"] = result["actorId"]?.DeepClone() }.ToString()));
        var execution = new AssistantToolWorkflow.ToolExecution(call, result.ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked);
        if (ParseVerifiedResult(execution) is null) return;
        this.results.RemoveAll(prior => prior.Call.FunctionName == operation
            && JToken.DeepEquals(ParseVerifiedResult(prior)?["actorId"], result["actorId"]));
        if (!IsVerifiedReady(execution)) this.contextPrerequisite = execution;
    }

    // Replaced on every snapshot; never enters executed results or prerequisites.
    internal void ObserveContextConflict(AssistantToolWorkflow.ToolExecution? evidence) => this.contextConflict = evidence;

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
        if (this.hasUnexecutedSensitiveCalls) return UnverifiedResponse;
        // Narrow saved-detail reads have their own typed proof. A setup question
        // does not block reading an existing workout. Status requests and any
        // attempted configured mutation retain their existing outcome boundary.
        if (this.requiresDetailsEvidence && !this.requiresOperationEvidence
            && !this.results.Any(e => this.operations.Contains(e.Call.FunctionName)) && this.unexecutedScopedCalls.Count == 0)
            return AssistantSavedWorkoutDetailsResponse.Render(this.contextDetails);
        if (this.results.Count == 0 && this.unexecutedScopedCalls.Count == 0 && this.contextConflict is null)
            return this.contextPrerequisite is null ? UnverifiedResponse : RenderResult(this.contextPrerequisite) ?? UnverifiedResponse;

        var messages = new List<string>();
        var planRevisions = new Dictionary<string, string>();
        var current = new List<AssistantToolWorkflow.ToolExecution>();
        foreach (AssistantToolWorkflow.ToolExecution execution in this.results.Concat(this.unexecutedScopedCalls.Select(call =>
            new AssistantToolWorkflow.ToolExecution(call, "", AssistantToolWorkflow.ToolExecutionDisposition.DependencyBlocked))))
        {
            JObject? args = AssistantScopedReplacementResponse.Arguments(execution.Call);
            JObject? evidence = ParseVerifiedResult(execution);
            JObject? conflict = AssistantScopedConflictResponse.ValidatedResult(execution);
            if (conflict is not null)
            {
                current.RemoveAll(prior =>
                {
                    JObject? priorResult = ParseVerifiedResult(prior);
                    JObject? priorArgs = AssistantScopedDeliveryResponse.Arguments(prior.Call);
                    return conflict["weeklyPlanId"]?.Type == JTokenType.Integer
                        && (JToken.DeepEquals(conflict["weeklyPlanId"], priorResult?["weeklyPlanId"])
                            || JToken.DeepEquals(conflict["weeklyPlanId"], priorArgs?["weeklyPlanId"]))
                        || execution.Call.FunctionName == "GenerateWeeklyPlanSkill" && priorResult?["savedDates"] is not null
                            && JToken.DeepEquals(conflict["actorId"], priorResult["actorId"])
                            && JToken.DeepEquals(conflict["weekStartDate"], priorResult["weekStartDate"])
                            && JToken.DeepEquals(conflict["weekEndDate"], priorResult["weekEndDate"]);
                });
            }
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
        // A refreshed setup read can retire its earlier tool result while a
        // dependency-blocked operation remains. Preserve the current question
        // only after every operation outcome has passed the finite boundary.
        if (this.contextPrerequisite is not null)
        {
            string? question = RenderResult(this.contextPrerequisite);
            if (question is null) return UnverifiedResponse;
            messages.Insert(0, question);
        }
        // Executed mutations own their outcome. A current snapshot can explain
        // only turns with no executed operation, including model reads/no calls.
        if (this.IsActive && this.contextConflict is not null
            && !this.results.Any(e => this.operations.Contains(e.Call.FunctionName)
                && e.Disposition == AssistantToolWorkflow.ToolExecutionDisposition.Invoked))
        {
            string? conflict = AssistantScopedConflictResponse.Render(this.contextConflict);
            if (conflict is null) return UnverifiedResponse;
            messages.Add(conflict);
        }
        return messages.Count == 0 ? UnverifiedResponse : string.Join(Environment.NewLine + Environment.NewLine, messages.Distinct());
    }

    static string? RenderResult(AssistantToolWorkflow.ToolExecution execution)
    {
        string? conflict = AssistantScopedConflictResponse.Render(execution);
        if (conflict is not null) return conflict;
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
            && IsTrue(result["persisted"]) && IsTrue(result["canTellAthleteSaved"])
            && (execution.Call.FunctionName != "SetPowerZonesSkill" || IsBoundPowerZonesSave(result, execution.Call)))
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

    static bool IsBoundPowerZonesSave(JObject result, ChatToolCall call)
    {
        JObject? args = AssistantScopedDeliveryResponse.Arguments(call);
        if (args is null || result["actorId"]?.Type != JTokenType.Integer
            || !int.TryParse(result["actorId"]!.ToString(), out int actor) || actor <= 0
            || !JToken.DeepEquals(result["actorId"], result["userId"])
            || args["userId"] is JToken requestedActor && requestedActor.Type != JTokenType.Null
                && !JToken.DeepEquals(requestedActor, result["actorId"])
            || !args.TryGetValue("ftpOverrideWatts", out JToken? requested)
            || !result.TryGetValue("ftpOverrideWatts", out JToken? stored)) return false;
        if (requested.Type == JTokenType.Null) return stored.Type == JTokenType.Null;
        return requested.Type is JTokenType.Integer or JTokenType.String
            && int.TryParse(requested.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int watts)
            && watts is >= 50 and <= 500 && stored.Type == JTokenType.Integer && stored.ToString() == watts.ToString(CultureInfo.InvariantCulture);
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
