// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;

// A verified rejection explains known requirements. It never grants persistence
// or delivery permission, and does not render free-form exception/tool text.
internal static class AssistantScopedConflictResponse
{
    static readonly IReadOnlyDictionary<string, string> Explanations = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ftp_required"] = "Power-based cycling needs a usable saved FTP before this plan can be accepted.",
        ["power_steps_missing"] = "Power-based cycling needs complete prescription steps.",
        ["power_target_missing"] = "Cycling work needs explicit power targets.",
        ["power_target_invalid"] = "Cycling power targets need a valid zone or watt range.",
        ["protected_constraint_unverifiable"] = "A protected training requirement overlaps this date. Its compatibility and any safety conflict need review; no session should be forced or silently omitted."
    };

    // Snapshot evidence is an assessment of the existing plan, never execution
    // credit. Bind its negative contract to the authenticated actor and exact week.
    internal static AssistantToolWorkflow.ToolExecution? ContextEvidence(JObject result, JToken actor)
    {
        if (Text(result["operation"]) != "GenerateWeeklyPlanSkill" || !PositiveId(actor)
            || !JToken.DeepEquals(actor, result["actorId"]) || !PositiveId(result["weeklyPlanId"])
            || !True(result["existingPlanPreserved"])) return null;
        var call = ChatToolCall.CreateFunctionToolCall("context-conflict", "GenerateWeeklyPlanSkill", BinaryData.FromString(
            new JObject { ["userId"] = actor.DeepClone(), ["weekStart"] = result["weekStartDate"]?.DeepClone() }.ToString()));
        var evidence = new AssistantToolWorkflow.ToolExecution(call, result.ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked);
        return ValidatedResult(evidence) is null ? null : evidence;
    }

    internal static JObject? ValidatedResult(AssistantToolWorkflow.ToolExecution execution)
    {
        if (execution.Disposition != AssistantToolWorkflow.ToolExecutionDisposition.Invoked
            || execution.Call.FunctionName is not "GenerateWeeklyPlanSkill" and not "ReplaceWeeklyWorkoutSkill" and not "FinalizeWeeklyPlanSkill" and not "SyncWorkoutToProviderSkill")
            return null;
        JObject? args = AssistantScopedDeliveryResponse.Arguments(execution.Call);
        if (args is null || AssistantToolWorkflow.ParseResult(execution.Result) is not JObject result
            || result["schemaVersion"]?.Type != JTokenType.Integer || result["schemaVersion"]!.ToString() != "1"
            || Text(result["operation"]) != execution.Call.FunctionName || !PositiveId(result["actorId"])
            || result["violationsTruncated"] is JToken truncated && truncated.Type != JTokenType.Boolean
            || !True(result["conflictEvidenceVerified"]) || Text(result["outcome"]) != "needs_reconciliation" || Text(result["errorCode"]) != "incomplete_plan"
            || !False(result["success"]) || !False(result["verified"]) || !False(result["persisted"])
            || !False(result["canTellAthleteSaved"]) || !False(result["canTellAthleteSynced"])
            || !Date(result["weekStartDate"], out DateTime start) || !Date(result["weekEndDate"], out DateTime end) || end < start || (end - start).Days > 6
            || args["userId"] is JToken actor && actor.Type != JTokenType.Null && !JToken.DeepEquals(actor, result["actorId"])) return null;
        if (execution.Call.FunctionName == "GenerateWeeklyPlanSkill")
        {
            if (result["weeklyPlanId"] is JToken plan && plan.Type != JTokenType.Null && !PositiveId(plan)) return null;
            if (args["weekStart"] is JToken requested && requested.Type != JTokenType.Null
                && (!Date(requested, out DateTime requestedDate) || (True(result["existingPlanPreserved"]) ? requestedDate < start || requestedDate > end : requestedDate != start))) return null;
        }
        else
        {
            if (!PositiveId(args["weeklyPlanId"]) || !JToken.DeepEquals(args["weeklyPlanId"], result["weeklyPlanId"])) return null;
            if (execution.Call.FunctionName is "ReplaceWeeklyWorkoutSkill" or "SyncWorkoutToProviderSkill"
                && (!Date(args["date"], out DateTime requestedDate) || requestedDate < start || requestedDate > end)) return null;
        }
        if (result["completenessViolations"] is not JArray violations || violations.Count == 0 || violations.Count > 64) return null;
        foreach (JToken token in violations)
        {
            if (token is not JObject violation || !Explanations.ContainsKey(Text(violation["code"]) ?? "")
                || !Date(violation["date"], out DateTime date) || date < start || date > end) return null;
            if (Text(violation["code"]) == "protected_constraint_unverifiable"
                && (!PositiveId(violation["constraintVersion"]) || !Date(violation["conflictStartDate"], out DateTime conflictStart)
                    || !Date(violation["conflictEndDate"], out DateTime conflictEnd) || date != conflictStart
                    || conflictStart < start || conflictEnd > end || conflictEnd < conflictStart)) return null;
        }
        return result;
    }

    internal static string? Render(AssistantToolWorkflow.ToolExecution execution)
    {
        JObject? result = ValidatedResult(execution);
        if (result is null) return null;
        var lines = new List<string> { "The weekly plan needs review before it can be accepted." };
        lines.AddRange(((JArray)result["completenessViolations"]!).Select(v => v["date"] + ": " + Explanations[Text(v["code"])!])
            .Distinct().OrderBy(line => line, StringComparer.Ordinal));
        if (True(result["violationsTruncated"])) lines.Add("Additional requirement conflicts also need review.");
        lines.Add("Plan changes and provider delivery are not confirmed. Review these conflicts before another write.");
        return string.Join(Environment.NewLine, lines);
    }

    static bool Date(JToken? token, out DateTime date) => DateTime.TryParseExact(Text(token), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    static bool PositiveId(JToken? token) => token?.Type == JTokenType.Integer && int.TryParse(token.ToString(), out int id) && id > 0;
    static bool True(JToken? token) => token?.Type == JTokenType.Boolean && token.Value<bool>();
    static bool False(JToken? token) => token?.Type == JTokenType.Boolean && !token.Value<bool>();
    static string? Text(JToken? token) => token?.Type == JTokenType.String ? token.Value<string>() : null;
}
