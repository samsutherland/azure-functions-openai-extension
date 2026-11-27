// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;

// Application-owned plan evidence. No tool names, coaching rules or free-form
// tool/model text determine the confirmation. A revision is an observed fingerprint.
internal static class AssistantScopedPlanResponse
{
    internal static string? Render(JObject result, ChatToolCall call)
    {
        string? outcome = Text(result["outcome"]);
        bool reused = outcome == "reused_existing";
        bool reconcile = outcome == "needs_reconciliation";
        if (outcome != "generated" && outcome != "regenerated" && !reused && !reconcile)
            return null;
        if (!True(result["success"]) || !True(result["persisted"]) || !True(result["canTellAthleteSaved"])
            || result["canTellAthleteSynced"]?.Type != JTokenType.Boolean || True(result["canTellAthleteSynced"])
            || !PositiveId(result["actorId"]) || !PositiveId(result["weeklyPlanId"])
            || Text(result["status"]) != "Completed"
            || (result["error"] is JToken error && error.Type != JTokenType.Null
                && (error.Type != JTokenType.String || !string.IsNullOrWhiteSpace(Text(error))))
            || !Regex.IsMatch(Text(result["revision"]) ?? "", @"\Asha256:[a-f0-9]{64}\z")
            || Text(result["providerSyncStatus"]) != (reused ? "unchanged" : "deferred_until_finalization")
            || !Date(result["weekStartDate"], out DateTime start) || !Date(result["weekEndDate"], out DateTime end) || end < start || (end - start).Days > 6)
            return null;

        JObject? args = AssistantToolWorkflow.ParseResult(call.FunctionArguments.ToString()) as JObject;
        if (args is null)
            return null;
        if (args["userId"] is JToken actor && actor.Type != JTokenType.Null
            && actor.ToString() != result["actorId"]!.ToString())
            return null;
        if (args["weekStart"] is JToken requested && requested.Type != JTokenType.Null
            && (!Date(requested, out DateTime date) || (reused ? date < start || date > end : date != start)))
            return null;
        if (result["savedDates"] is not JArray dates || dates.Count == 0 || dates.Count > 7
            || dates.Any(d => !Date(d, out DateTime date) || date < start || date > end)
            || dates.Select(d => d.ToString()).Distinct().Count() != dates.Count)
            return null;

        JObject? validation = result["availabilityValidation"] as JObject;
        if (!reused && (validation is null || validation["valid"]?.Type != JTokenType.Boolean
            || validation["canTellAthleteMatchesAvailability"]?.Type != JTokenType.Boolean
            || True(validation["valid"]) == reconcile || True(validation["canTellAthleteMatchesAvailability"]) == reconcile))
            return null;
        if (!reused && !reconcile && validation!["constraintViolations"] is not JArray { Count: 0 })
            return null;
        var reviewDates = new List<string>();
        if (reconcile)
        {
            if (validation!["constraintViolations"] is not JArray violations || violations.Count == 0 || violations.Count > 7)
                return null;
            foreach (JToken violation in violations)
            {
                if (violation is not JObject row || !Date(row["date"], out _) || !dates.Any(d => JToken.DeepEquals(d, row["date"])))
                    return null;
                reviewDates.Add(row["date"]!.ToString());
            }
        }
        string status = reused ? "Your existing saved weekly plan was reused unchanged."
            : reconcile ? "Your weekly plan was saved, but some dates still need adjustment to match your availability."
            : outcome == "regenerated" ? "Your weekly plan was regenerated and verified in the app."
            : "Your weekly plan was saved and verified in the app.";
        string review = reconcile ? " Dates to review: " + string.Join(", ", reviewDates.Distinct().OrderBy(d => d, StringComparer.Ordinal)) + "." : "";
        return status + " Saved dates: " + string.Join(", ", dates.Select(d => d.ToString()).OrderBy(d => d, StringComparer.Ordinal))
            + "." + review + " Provider delivery is not confirmed.";
    }

    static bool Date(JToken? token, out DateTime value) => DateTime.TryParseExact(Text(token), "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    static string? Text(JToken? token) => token?.Type == JTokenType.String ? token.Value<string>() : null;
    static bool True(JToken? token) => token?.Type == JTokenType.Boolean && token.Value<bool>();
    static bool PositiveId(JToken? token) => token?.Type == JTokenType.Integer && int.TryParse(token.ToString(), out int id) && id > 0;
}
