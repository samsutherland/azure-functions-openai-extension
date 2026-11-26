// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;

internal static class AssistantScopedDeliveryResponse
{
    internal static bool IsDelivery(ChatToolCall call) => call.FunctionName is "FinalizeWeeklyPlanSkill" or "SyncWorkoutToProviderSkill";

    internal static JObject? Arguments(ChatToolCall call)
    {
        JObject? args = AssistantScopedReplacementResponse.Arguments(call);
        return args?["input"]?.Type == JTokenType.String
            ? AssistantToolWorkflow.ParseResult(args["input"]!.Value<string>()) as JObject : args;
    }

    internal static string? Render(AssistantToolWorkflow.ToolExecution execution)
    {
        JObject? args = Arguments(execution.Call);
        bool finalization = execution.Call.FunctionName == "FinalizeWeeklyPlanSkill";
        if (args is null || !PositiveId(args["weeklyPlanId"]) || !finalization && !Date(args["date"])) return null;
        string prefix = finalization ? "" : args["date"] + ": ";
        if (execution.Disposition != AssistantToolWorkflow.ToolExecutionDisposition.Invoked)
            return prefix + "The requested delivery operation was not attempted. Finalization and provider delivery are not confirmed.";
        JObject? result = AssistantToolWorkflow.ParseResult(execution.Result) as JObject;
        if (result is null || result["schemaVersion"]?.Type != JTokenType.Integer || result["schemaVersion"]!.ToString() != "1"
            || Text(result["operation"]) != execution.Call.FunctionName
            || !PositiveId(result["actorId"]) || !JToken.DeepEquals(result["weeklyPlanId"], args["weeklyPlanId"])
            || args["userId"] is JToken actor && actor.Type != JTokenType.Null && !JToken.DeepEquals(actor, result["actorId"])
            || !finalization && !JToken.DeepEquals(result["date"], args["date"]))
            return prefix + "I couldn't verify the delivery operation. Check saved state before retrying; finalization and provider delivery are not confirmed.";
        if (!True(result["verified"]) || !True(result["persisted"]))
            return prefix + (Text(result["outcome"]) == "needs_reconciliation"
                ? "The saved plan needs review before delivery. Finalization and provider delivery are not confirmed."
                : "I couldn't verify the delivery operation. Check saved state before retrying; finalization and provider delivery are not confirmed.");
        if (result["savedDates"] is not JArray dates || dates.Count == 0 || dates.Count > 7
            || dates.Any(d => !Date(d)) || dates.Select(d => d.ToString()).Distinct().Count() != dates.Count
            || !finalization && !dates.Any(d => JToken.DeepEquals(d, args["date"]))
            || result["revisions"] is not JArray revisions || revisions.Count == 0 || revisions.Count > 64
            || revisions.Any(r => r is not JObject || !PositiveId(r["weeklyPlanWorkoutId"]) || !Revision(r["revision"])
                || !dates.Any(d => JToken.DeepEquals(d, r["date"])))
            || dates.Any(d => !revisions.Any(r => JToken.DeepEquals(r["date"], d)))
            || revisions.Select(r => r["weeklyPlanWorkoutId"]!.ToString()).Distinct().Count() != revisions.Count)
            return prefix + "I couldn't verify the saved workout scope. Finalization and provider delivery are not confirmed.";

        string? outcome = Text(result["outcome"]);
        string header = finalization && True(result["success"]) && outcome == "finalized" && True(result["finalized"])
            ? "Your weekly plan was finalized and verified in the app."
            : finalization && True(result["success"]) && outcome == "no_action_required"
                ? "Your saved weekly plan was already finalized. No new delivery is confirmed."
                : finalization && outcome == "needs_reconciliation"
                    ? "Your saved plan needs review before finalization."
                    : "Saved plan state was verified in the app.";
        var messages = new List<string> { header + " Saved dates: " + string.Join(", ", dates.Select(d => d.ToString()).OrderBy(d => d, StringComparer.Ordinal)) + "." };
        messages.Add(RenderReceipts(result, args, revisions, finalization));
        return string.Join(Environment.NewLine, messages);
    }

    internal static string RenderReplacementReceipts(JObject result, JObject args)
    {
        var revisions = new JArray(new JObject
        {
            ["date"] = result["date"]?.DeepClone(), ["weeklyPlanWorkoutId"] = result["weeklyPlanWorkoutId"]?.DeepClone(),
            ["revision"] = result["revision"]?.DeepClone()
        });
        return RenderReceipts(result, args, revisions, finalization: false);
    }

    static string RenderReceipts(JObject result, JObject args, JArray revisions, bool finalization)
    {
        var messages = new List<string>();
        if (args["provider"] is JToken requested && requested.Type != JTokenType.Null && Provider(requested) is null)
            return "The requested provider could not be verified. Provider delivery and watch visibility are not confirmed.";
        if (result["deliveryOutcomes"] is not JArray receipts || receipts.Count == 0 || receipts.Count > 128
            || result["providerEvidenceVerified"]?.Type != JTokenType.Boolean)
        {
            messages.Add("Provider delivery and watch visibility are not confirmed.");
            return string.Join(Environment.NewLine, messages);
        }
        var duplicateKeys = receipts.OfType<JObject>().GroupBy(ReceiptKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        foreach (JToken token in receipts)
        {
            if (token is not JObject receipt || !Date(receipt["date"])
                || !revisions.Any(r => JToken.DeepEquals(r["date"], receipt["date"])
                    && JToken.DeepEquals(r["weeklyPlanWorkoutId"], receipt["weeklyPlanWorkoutId"])
                    && JToken.DeepEquals(r["revision"], receipt["revision"]))
                || !finalization && !JToken.DeepEquals(receipt["date"], args["date"]))
            {
                messages.Add("Some provider receipts could not be verified. Provider delivery and watch visibility are not confirmed for those receipts.");
                continue;
            }
            string? provider = Provider(receipt["provider"]);
            string? requestedProvider = args["provider"]?.Type == JTokenType.Null ? null : Text(args["provider"]);
            if (provider is null || requestedProvider is not null && Provider(args["provider"]) != provider
                || duplicateKeys.Contains(ReceiptKey(receipt)))
            {
                messages.Add("Some provider receipts could not be verified. Provider delivery and watch visibility are not confirmed for those receipts.");
                continue;
            }
            string status = RenderReceipt(receipt, provider);
            messages.Add(receipt["date"] + ": " + status);
        }
        return string.Join(Environment.NewLine, messages.Distinct());
    }

    static string ReceiptKey(JObject receipt) => Provider(receipt["provider"]) + ":" + receipt["date"] + ":" + receipt["weeklyPlanWorkoutId"];

    static string RenderReceipt(JObject receipt, string provider)
    {
        string unknown = "I couldn't verify " + provider + " delivery. Provider delivery and watch visibility are not confirmed.";
        if (!True(receipt["verified"]) || receipt["watchVisible"]?.Type != JTokenType.Boolean || True(receipt["watchVisible"])) return unknown;
        string? state = Text(receipt["syncState"]);
        if (state == "failed") return provider + " delivery failed. Watch visibility is not confirmed.";
        if (state == "app_only" && False(receipt["workoutCreated"]) && False(receipt["scheduleCreated"]))
            return "This date is saved in the app only; no " + provider + " workout delivery is confirmed.";
        if (!True(receipt["workoutCreated"]) || string.IsNullOrWhiteSpace(Text(receipt["externalId"]))) return unknown;
        if (provider == "Garmin" && state == "schedule_created_watch_sync_required" && True(receipt["scheduleCreated"]))
            return "Garmin accepted the workout schedule. Watch visibility is not confirmed; Garmin Connect/watch sync may still be required.";
        if (provider == "Garmin" && state == "workout_created_schedule_unconfirmed" && False(receipt["scheduleCreated"]))
            return "Garmin workout was created; scheduling and watch visibility are not confirmed.";
        if (provider == "Wahoo" && state == "workout_created" && False(receipt["scheduleCreated"]))
            return "Wahoo workout was created. Device visibility is not confirmed.";
        return unknown;
    }

    static string? Provider(JToken? token) => Text(token)?.Trim().ToLowerInvariant() switch { "garmin" => "Garmin", "wahoo" => "Wahoo", _ => null };
    static bool Revision(JToken? token) => Regex.IsMatch(Text(token) ?? "", @"\Asha256:[a-f0-9]{64}\z");
    static bool Date(JToken? token) => DateTime.TryParseExact(Text(token), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    static bool PositiveId(JToken? token) => token?.Type == JTokenType.Integer && int.TryParse(token.ToString(), out int id) && id > 0;
    static bool True(JToken? token) => token?.Type == JTokenType.Boolean && token.Value<bool>();
    static bool False(JToken? token) => token?.Type == JTokenType.Boolean && !token.Value<bool>();
    static string? Text(JToken? token) => token?.Type == JTokenType.String ? token.Value<string>() : null;
}
