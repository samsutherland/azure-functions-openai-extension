// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;

// A current observation is not an executed confirmation, reuse or save receipt.
internal static class AssistantExistingStateResponse
{
    internal static JObject? Validate(JObject result, JToken? actor)
    {
        if (result.ToString(Formatting.None).Length > 16384 || !Fields(result, "schemaVersion", "operation", "source", "actorId", "verified", "outcome", "observationKind", "setup", "plan", "reason")
            || result["schemaVersion"]?.Type != JTokenType.Integer || result["schemaVersion"]!.ToString() != "1"
            || !Text(result["operation"], "GetWorkflowContextSkill") || !Text(result["source"], "existing_training_state")
            || !Positive(result["actorId"]) || !JToken.DeepEquals(result["actorId"], actor)) return null;
        if (result["verified"]?.Type == JTokenType.Boolean && !result["verified"]!.Value<bool>())
            return Text(result["outcome"], "unavailable") && result["setup"] is null && result["plan"] is null && result["observationKind"] is null
                && Text(result["reason"], "setup_not_confirmed", "date_required", "ambiguous_dates", "plan_not_found", "ambiguous_plan", "invalid_plan", "invalid_saved_value") ? (JObject)result.DeepClone() : null;
        if (!True(result["verified"]) || !Text(result["outcome"], "observed") || result["reason"] is not null
            || !Text(result["observationKind"], "setup", "saved_plan") || result["setup"] is not JObject setup
            || !Fields(setup, "ready", "confirmed", "confirmationEvidenceKind", "confirmationUtc", "currentFingerprint", "currentFingerprintVersion")
            || !True(setup["ready"]) || !True(setup["confirmed"]) || !Text(setup["confirmationEvidenceKind"], "fingerprint", "legacy_timestamp")
            || !UtcTimestamp(setup["confirmationUtc"])
            || setup["currentFingerprint"]?.Type != JTokenType.String || !Regex.IsMatch(setup["currentFingerprint"]!.Value<string>()!, @"\A[0-9A-Fa-f]{64}\z")
            || !Text(setup["currentFingerprintVersion"], "setup-v1")) return null;
        if (Text(result["observationKind"], "setup")) return result["plan"] is null ? (JObject)result.DeepClone() : null;
        if (result["plan"] is not JObject plan || !Fields(plan, "weeklyPlanId", "weekStartDate", "weekEndDate", "requestedDate", "status", "completenessVerified", "workouts")
            || !Positive(plan["weeklyPlanId"]) || !Date(plan["weekStartDate"], out DateTime start) || !Date(plan["weekEndDate"], out DateTime end)
            || end < start || (end - start).Days > 6 || !Date(plan["requestedDate"], out DateTime date) || date < start || date > end
            || !Text(plan["status"], "Completed") || !True(plan["completenessVerified"]) || plan["workouts"] is not JArray workouts || workouts.Count is 0 or > 64) return null;
        var ids = new HashSet<int>(); DateTime previous = start;
        foreach (JToken item in workouts)
        {
            if (item is not JObject workout || !Fields(workout, "date", "weeklyPlanWorkoutId", "revision") || !Positive(workout["weeklyPlanWorkoutId"])
                || !ids.Add(workout["weeklyPlanWorkoutId"]!.Value<int>()) || !Date(workout["date"], out DateTime workoutDate) || workoutDate < start || workoutDate > end || workoutDate < previous
                || workout["revision"]?.Type != JTokenType.String || !Regex.IsMatch(workout["revision"]!.Value<string>()!, @"\Asha256:[0-9a-f]{64}\z")) return null;
            previous = workoutDate;
        }
        return (JObject)result.DeepClone();
    }

    internal static string Render(JObject? observation)
    {
        if (observation is null || !True(observation["verified"])) return AssistantToolResponsePolicy.UnverifiedResponse;
        const string setup = "Your saved training setup is already marked confirmed.";
        return Text(observation["observationKind"], "saved_plan")
            ? setup + Environment.NewLine + Environment.NewLine + "A saved plan covers " + observation["plan"]!["requestedDate"]!.Value<string>() + ". No new save or provider delivery is confirmed."
            : setup;
    }
    static bool UtcTimestamp(JToken? value) => value?.Type == JTokenType.Date ? value.Value<DateTime>().Kind == DateTimeKind.Utc
        : value?.Type == JTokenType.String && value.Value<string>()!.EndsWith("Z", StringComparison.Ordinal)
            && DateTimeOffset.TryParse(value.Value<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
    static bool Fields(JObject value, params string[] names) => value.Properties().All(p => names.Contains(p.Name, StringComparer.Ordinal));
    static bool Text(JToken? value, params string[] allowed) => value?.Type == JTokenType.String && allowed.Contains(value.Value<string>(), StringComparer.Ordinal);
    static bool True(JToken? value) => value?.Type == JTokenType.Boolean && value.Value<bool>();
    static bool Positive(JToken? value) => value?.Type == JTokenType.Integer && int.TryParse(value.ToString(), out int number) && number > 0;
    static bool Date(JToken? value, out DateTime date) { date = default; return value?.Type == JTokenType.String && DateTime.TryParseExact(value.Value<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date); }
}
