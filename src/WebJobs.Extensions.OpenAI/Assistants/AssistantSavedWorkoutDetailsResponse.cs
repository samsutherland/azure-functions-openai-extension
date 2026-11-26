// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;

// Only current, authenticated application facts cross this boundary. Model prose
// and free-form workout titles/descriptions never establish saved detail facts.
internal static class AssistantSavedWorkoutDetailsResponse
{
    internal const string UnavailableResponse = "I couldn't verify the saved workout details. Duration, intensity, targets and steps are not confirmed; please check the saved workout.";
    static readonly HashSet<string> TopFields = Names("schemaVersion operation source actorId verified outcome reason weeklyPlanId weekStartDate weekEndDate date weeklyPlanWorkoutId revision durationSeconds durationStatus prescriptionKind stepsStatus steps");
    static readonly HashSet<string> StepFields = Names("stepId parentStepId stepOrder type intensity durationType durationValue targetType targetValue powerZone heartRateZone targetValueLow targetValueHigh repeatType repeatValue");
    static readonly HashSet<string> Reasons = Names("date_required ambiguous_dates workout_not_found ambiguous_workout step_limit invalid_graph invalid_saved_value unsupported_representation");
    static readonly HashSet<string> Types = Names("WorkoutStep WorkoutRepeatStep UNKNOWN");
    static readonly HashSet<string> Intensities = Names("REST WARMUP COOLDOWN RECOVERY INTERVAL MAIN ACTIVE UNKNOWN");
    static readonly HashSet<string> Durations = Names("TIME DISTANCE HR_LESS_THAN HR_GREATER_THAN CALORIES OPEN POWER_LESS_THAN POWER_GREATER_THAN TIME_AT_VALID_CDA FIXED_REST REPS UNKNOWN");
    static readonly HashSet<string> Targets = Names("SPEED HEART_RATE OPEN CADENCE POWER GRADE RESISTANCE POWER_3S POWER_10S POWER_30S POWER_LAP SPEED_LAP HEART_RATE_LAP PACE UNKNOWN");
    static readonly HashSet<string> Repeats = Names("REPEAT_UNTIL_STEPS_CMPLT REPEAT_UNTIL_TIME REPEAT_UNTIL_DISTANCE REPEAT_UNTIL_CALORIES REPEAT_UNTIL_HR_LESS_THAN REPEAT_UNTIL_HR_GREATER_THAN REPEAT_UNTIL_POWER_LESS_THAN REPEAT_UNTIL_POWER_GREATER_THAN REPEAT_UNTIL_POWER_LAST_LAP_LESS_THAN REPEAT_UNTIL_MAX_POWER_LAST_LAP_LESS_THAN UNKNOWN");

    internal static JObject? Validate(JObject result, JToken? actor)
    {
        if (result.ToString(Newtonsoft.Json.Formatting.None).Length > 32768 || result.Properties().Any(p => !TopFields.Contains(p.Name))
            || !Integer(result["schemaVersion"], out int schema) || schema != 1
            || Text(result["operation"]) != "GetWorkflowContextSkill" || Text(result["source"]) != "saved_workout_detail"
            || !Positive(actor) || !JToken.DeepEquals(actor, result["actorId"]) || result["verified"]?.Type != JTokenType.Boolean) return null;
        if (!result["verified"]!.Value<bool>())
        {
            if (Text(result["outcome"]) != "unavailable" || !Reasons.Contains(Text(result["reason"]) ?? "")
                || result.Properties().Any(p => p.Name is not "schemaVersion" and not "operation" and not "source" and not "actorId" and not "verified" and not "outcome" and not "reason" and not "date")
                || !Absent(result["date"]) && !Date(result["date"], out _)) return null;
            return (JObject)result.DeepClone();
        }
        if (!Absent(result["reason"]) || Text(result["outcome"]) != "available"
            || !Positive(result["weeklyPlanId"]) || !Positive(result["weeklyPlanWorkoutId"])
            || !Date(result["weekStartDate"], out DateTime start) || !Date(result["weekEndDate"], out DateTime end)
            || end < start || (end - start).Days > 6 || !Date(result["date"], out DateTime date) || date < start || date > end
            || !Regex.IsMatch(Text(result["revision"]) ?? "", @"\Asha256:[0-9a-f]{64}\z")
            || Text(result["prescriptionKind"]) is not "training" and not "rest" and not "mobility"
            || Text(result["durationStatus"]) is not "saved" and not "not_saved" and not "not_applicable"
            || Text(result["durationStatus"]) == "not_applicable" && Text(result["prescriptionKind"]) != "rest"
            || (Text(result["durationStatus"]) == "saved" ? !Positive(result["durationSeconds"]) : !Absent(result["durationSeconds"]))
            || result["steps"] is not JArray steps || steps.Count > 64
            || Text(result["stepsStatus"]) != (steps.Count == 0 ? "not_saved" : "saved")) return null;
        var byId = new Dictionary<int, JObject>();
        foreach (JToken token in steps)
        {
            if (token is not JObject step || step.Properties().Any(p => !StepFields.Contains(p.Name))
                || !Integer(step["stepId"], out int id) || id <= 0 || !byId.TryAdd(id, step)
                || !Integer(step["stepOrder"], out int order) || order < 0 || !Absent(step["parentStepId"]) && !Positive(step["parentStepId"])
                || !Enum(step["type"], Types, false) || !Enum(step["intensity"], Intensities) || !Enum(step["durationType"], Durations)
                || !Enum(step["targetType"], Targets) || !Enum(step["repeatType"], Repeats)
                || new[] { "durationValue", "targetValue", "powerZone", "heartRateZone", "targetValueLow", "targetValueHigh", "repeatValue" }.Any(name => !Absent(step[name]) && !Positive(step[name]))
                || !Absent(step["targetValueLow"]) && !Absent(step["targetValueHigh"]) && step["targetValueLow"]!.Value<int>() > step["targetValueHigh"]!.Value<int>()) return null;
        }
        foreach (var pair in byId)
        {
            var seen = new HashSet<int> { pair.Key };
            JObject step = pair.Value;
            int depth = 1;
            while (!Absent(step["parentStepId"]))
            {
                int parent = step["parentStepId"]!.Value<int>();
                if (++depth > 8 || !seen.Add(parent) || !byId.TryGetValue(parent, out JObject? ancestor)) return null;
                step = ancestor;
            }
        }
        return (JObject)result.DeepClone();
    }

    internal static string Render(JObject? result)
    {
        if (result is null) return UnavailableResponse;
        if (result["verified"]!.Value<bool>() == false)
            return Text(result["reason"]) switch
            {
                "date_required" or "ambiguous_dates" => "Which date should I check for the saved workout?",
                "ambiguous_workout" => "More than one saved workout matches that date. Which workout should I check?",
                _ => UnavailableResponse
            };
        var lines = new List<string> { $"Saved workout details for {result["date"]}:" };
        lines.Add(Text(result["durationStatus"]) == "saved"
            ? $"Duration: {result["durationSeconds"]} {(result["durationSeconds"]!.Value<int>() == 1 ? "second" : "seconds")} ({Duration(result["durationSeconds"]!.Value<int>())})."
            : Text(result["durationStatus"]) == "not_applicable" ? "Duration: not applicable (saved rest entry)." : "Duration: not saved.");
        var steps = ((JArray)result["steps"]!).Cast<JObject>().ToList();
        lines.Add(steps.Any(s => !Absent(s["intensity"]) || !Absent(s["powerZone"]) || !Absent(s["heartRateZone"])) ? "Structured intensity/zone: saved in the steps below." : "Structured intensity/zone: not saved.");
        lines.Add(steps.Any(s => !Absent(s["targetType"])) ? "Structured target type: saved in the steps below." : "Structured target type: not saved.");
        if (steps.Count == 0) lines.Add("Steps: no saved step breakdown.");
        else
        {
            lines.Add("Saved steps:");
            var ordered = steps.OrderBy(s => s["stepOrder"]!.Value<int>()).ThenBy(s => s["stepId"]!.Value<int>()).ToList();
            foreach (JObject step in ordered)
            {
                var fields = new List<string> { $"type: {Label(step["type"])}", $"intensity: {Label(step["intensity"])}", $"duration type: {Label(step["durationType"])}", $"target type: {Label(step["targetType"])}" };
                if (!Absent(step["parentStepId"])) fields.Add($"parent: step {ordered.FindIndex(s => JToken.DeepEquals(s["stepId"], step["parentStepId"])) + 1}");
                foreach (var field in new[] { ("durationValue", "duration value"), ("targetValue", "target value"), ("targetValueLow", "target low"), ("targetValueHigh", "target high"), ("powerZone", "power zone"), ("heartRateZone", "heart rate zone"), ("repeatValue", "repeat value") })
                    if (!Absent(step[field.Item1])) fields.Add($"{field.Item2}: {step[field.Item1]}{(field.Item1 == "durationValue" && Text(step["durationType"]) == "TIME" ? " seconds" : "")}");
                if (!Absent(step["repeatType"])) fields.Add($"repeat type: {Label(step["repeatType"])}");
                lines.Add($"Step {ordered.IndexOf(step) + 1}: {string.Join("; ", fields)}.");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    static string Duration(int seconds)
    {
        int minutes = seconds / 60, remainder = seconds % 60;
        string MinuteText() => $"{minutes} {(minutes == 1 ? "minute" : "minutes")}";
        string SecondText() => $"{remainder} {(remainder == 1 ? "second" : "seconds")}";
        return minutes == 0 ? SecondText() : remainder == 0 ? MinuteText() : MinuteText() + " " + SecondText();
    }

    static string Label(JToken? token) => Text(token) switch { null => "not saved", "WorkoutStep" => "work", "WorkoutRepeatStep" => "repeat", "UNKNOWN" => "unrecognized saved value", var text => text.Replace('_', ' ') };
    static HashSet<string> Names(string names) => new(names.Split(' '), StringComparer.Ordinal);
    static bool Enum(JToken? token, HashSet<string> values, bool nullable = true) => nullable && Absent(token) || token?.Type == JTokenType.String && values.Contains(token.Value<string>()!);
    static bool Absent(JToken? token) => token is null || token.Type == JTokenType.Null;
    static bool Integer(JToken? token, out int value) { value = 0; return token?.Type == JTokenType.Integer && int.TryParse(token.ToString(), out value); }
    static bool Positive(JToken? token) => Integer(token, out int value) && value > 0;
    static string? Text(JToken? token) => token?.Type == JTokenType.String ? token.Value<string>() : null;
    static bool Date(JToken? token, out DateTime value) => DateTime.TryParseExact(Text(token), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
}
