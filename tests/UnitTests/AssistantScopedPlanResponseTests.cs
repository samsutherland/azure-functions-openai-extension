// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;
using Xunit;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Tests.Assistants;

public class AssistantScopedPlanResponseTests
{
    [Theory]
    [InlineData("generated", "Your weekly plan was saved and verified in the app.")]
    [InlineData("regenerated", "Your weekly plan was regenerated and verified in the app.")]
    [InlineData("reused_existing", "Your existing saved weekly plan was reused unchanged.")]
    [InlineData("needs_reconciliation", "Your weekly plan was saved, but some dates still need adjustment to match your availability.")]
    public void ScopedPlanRendersOnlyVerifiedDatesAndFiniteStatus(string outcome, string status)
    {
        var result = Result(outcome);
        result["userFacingStatus"] = "Saved and synced on your watch!";
        string text = Observe(result).Render();
        Assert.StartsWith(status, text);
        Assert.Contains("Saved dates: 2026-11-26, 2026-11-27.", text);
        Assert.Contains("Provider delivery is not confirmed.", text);
        Assert.DoesNotContain("watch", text);
        if (outcome == "needs_reconciliation")
            Assert.Contains("Dates to review: 2026-11-27.", text);
    }

    [Theory]
    [InlineData("actorId", "2")]
    [InlineData("actorId", "null")]
    [InlineData("weeklyPlanId", "0")]
    [InlineData("revision", "\"stale\"")]
    [InlineData("savedDates", "[]")]
    [InlineData("savedDates", "[\"2026-12-01\"]")]
    [InlineData("savedDates", "[\"Saved and synced!\"]")]
    [InlineData("savedDates", "[\"2026-11-26\",\"2026-11-26\"]")]
    [InlineData("weekStartDate", "\"2026-11-27\"")]
    [InlineData("weekEndDate", "\"2026-11-25\"")]
    [InlineData("providerSyncStatus", "\"synced\"")]
    [InlineData("status", "\"Failed\"")]
    [InlineData("persisted", "false")]
    [InlineData("canTellAthleteSaved", "false")]
    [InlineData("canTellAthleteSynced", "true")]
    [InlineData("availabilityValidation", "null")]
    [InlineData("error", "\"planner failed\"")]
    [InlineData("error", "0")]
    [InlineData("weekEndDate", "\"2027-11-29\"")]
    [InlineData("availabilityValidation", "{\"valid\":true,\"canTellAthleteMatchesAvailability\":true,\"constraintViolations\":[{\"date\":\"2026-11-27\"}]}")]
    public void InvalidOrMismatchedScopeNeverConfirmsSave(string property, string value)
    {
        var result = Result();
        result[property] = JToken.Parse(value);
        Assert.Equal(AssistantToolResponsePolicy.UnverifiedResponse, Observe(result).Render());
    }

    [Fact]
    public void UnexecutedSensitiveCallDoesNotPublishAnEarlierSuccessAsCompletion()
    {
        var policy = Observe(Result());
        policy.ObserveUnexecutedCalls([Call()]);
        Assert.Equal(AssistantToolResponsePolicy.UnverifiedResponse, policy.Render());
    }

    [Fact]
    public void ReconciliationDatesMustBelongToTheVerifiedSavedScope()
    {
        var result = Result("needs_reconciliation");
        result["availabilityValidation"]!["constraintViolations"]![0]!["date"] = "2026-12-01";
        Assert.Equal(AssistantToolResponsePolicy.UnverifiedResponse, Observe(result).Render());
    }

    [Fact]
    public void ConflictingRevisionsDoNotPublishAnEarlierPlanConfirmation()
    {
        var result = Result();
        var policy = Observe(result);
        result["revision"] = "sha256:" + new string('b', 64);
        policy.ObserveResults([new(Call(), result.ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        Assert.Equal(AssistantToolResponsePolicy.UnverifiedResponse, policy.Render());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ApiEncodedStringContractIsSupported(int encodingLayers)
    {
        string result = Result().ToString();
        for (int i = 0; i < encodingLayers; i++) result = JsonConvert.SerializeObject(result);
        var policy = new AssistantToolResponsePolicy("GenerateWeeklyPlanSkill");
        policy.ObserveResults([new(Call(), result, AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        Assert.Contains("Saved dates: 2026-11-26, 2026-11-27.", policy.Render());
    }

    internal static JObject Result(string outcome = "generated") => JObject.FromObject(new
    {
        schemaVersion = 1, operation = "GenerateWeeklyPlanSkill", actorId = 1, weeklyPlanId = 15,
        verified = true, persisted = true, success = true, outcome, canTellAthleteSaved = true, canTellAthleteSynced = false,
        status = "Completed", weekStartDate = "2026-11-26", weekEndDate = "2026-11-29",
        revision = "sha256:" + new string('a', 64), savedDates = new[] { "2026-11-26", "2026-11-27" },
        providerSyncStatus = outcome == "reused_existing" ? "unchanged" : "deferred_until_finalization",
        availabilityValidation = new
        {
            valid = outcome == "generated" || outcome == "regenerated",
            canTellAthleteMatchesAvailability = outcome == "generated" || outcome == "regenerated",
            constraintViolations = outcome == "needs_reconciliation" ? new[] { new { date = "2026-11-27" } } : []
        }
    });

    static AssistantToolResponsePolicy Observe(JObject result)
    {
        var policy = new AssistantToolResponsePolicy("GenerateWeeklyPlanSkill");
        policy.ObserveCalls([Call()]);
        policy.ObserveResults([new(Call(), result.ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        return policy;
    }

    static ChatToolCall Call() => ChatToolCall.CreateFunctionToolCall("plan", "GenerateWeeklyPlanSkill",
        BinaryData.FromString("{\"userId\":1,\"weekStart\":\"2026-11-26\"}"));
}
