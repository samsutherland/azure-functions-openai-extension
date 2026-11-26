// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;

internal static class AssistantScopedReplacementResponse
{
    internal static bool IsReplacement(ChatToolCall call) => call.FunctionName == "ReplaceWeeklyWorkoutSkill";

    internal static JObject? Arguments(ChatToolCall call) => AssistantToolWorkflow.ParseResult(call.FunctionArguments.ToString()) as JObject;

    internal static string? Render(AssistantToolWorkflow.ToolExecution execution)
    {
        JObject? args = Arguments(execution.Call);
        if (args is null || !PositiveId(args["weeklyPlanId"]) || !Date(args["date"]))
            return null;
        string prefix = args["date"] + ": ";
        if (execution.Disposition != AssistantToolWorkflow.ToolExecutionDisposition.Invoked)
            return prefix + "The replacement was not attempted. Completion and provider delivery are not confirmed.";
        JObject? result = AssistantToolWorkflow.ParseResult(execution.Result) as JObject;
        bool bound = result is not null && result["schemaVersion"]?.Type == JTokenType.Integer && result["schemaVersion"]!.ToString() == "1"
            && Text(result["operation"]) == execution.Call.FunctionName
            && JToken.DeepEquals(result["weeklyPlanId"], args["weeklyPlanId"])
            && JToken.DeepEquals(result["date"], args["date"])
            && (args["userId"] is not JToken actor || actor.Type == JTokenType.Null || JToken.DeepEquals(actor, result["actorId"]));
        if (bound && True(result!["success"]) && Text(result["outcome"]) == "saved"
            && True(result["verified"]) && True(result["persisted"]) && True(result["canTellAthleteSaved"])
            && PositiveId(result["actorId"]) && PositiveId(result["weeklyPlanWorkoutId"])
            && Regex.IsMatch(Text(result["revision"]) ?? "", @"\Asha256:[a-f0-9]{64}\z")
            && (result["updatedUtc"]?.Type == JTokenType.Date || (Regex.IsMatch(Text(result["updatedUtc"]) ?? "", @"\A\d{4}-\d{2}-\d{2}T") && DateTimeOffset.TryParse(Text(result["updatedUtc"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            && (result["error"] is null || result["error"]!.Type == JTokenType.Null || Text(result["error"]) == ""))
        {
            string saved = prefix + "Your replacement workout was saved and verified in the app.";
            return result["deliveryOutcomes"] is JArray { Count: > 0 }
                ? saved + Environment.NewLine + AssistantScopedDeliveryResponse.RenderReplacementReceipts(result, args)
                : saved + " Provider delivery is not confirmed.";
        }
        if (bound && result!["success"]?.Type == JTokenType.Boolean && !True(result["success"])
            && (Text(result["outcome"]) is "failed" or "constraint_violation")
            && !True(result["canTellAthleteSaved"]) && !True(result["persisted"]))
            return prefix + "The replacement failed. Saved changes and provider delivery are not confirmed.";
        return prefix + "I couldn't verify the replacement. Check the saved workout before trying again; saved changes and provider delivery are not confirmed.";
    }

    static bool Date(JToken? token) => DateTime.TryParseExact(Text(token), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    static bool PositiveId(JToken? token) => token?.Type == JTokenType.Integer && int.TryParse(token.ToString(), out int id) && id > 0;
    static bool True(JToken? token) => token?.Type == JTokenType.Boolean && token.Value<bool>();
    static string? Text(JToken? token) => token?.Type == JTokenType.String ? token.Value<string>() : null;
}
