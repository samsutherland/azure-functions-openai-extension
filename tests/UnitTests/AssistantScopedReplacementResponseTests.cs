// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;
using Xunit;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Tests.Assistants;

public class AssistantScopedReplacementResponseTests
{
    [Fact]
    public void SavedDateIgnoresLegacyDeliveryClaims()
    {
        var policy = Policy();
        Add(policy, Saved());
        Assert.Equal("2026-11-26: Your replacement workout was saved and verified in the app. Provider delivery is not confirmed.", policy.Render());
    }

    [Theory]
    [InlineData("actorId", "2")]
    [InlineData("weeklyPlanId", "16")]
    [InlineData("date", "\"2026-11-27\"")]
    [InlineData("weeklyPlanWorkoutId", "0")]
    [InlineData("revision", "\"invalid\"")]
    [InlineData("updatedUtc", "null")]
    [InlineData("updatedUtc", "\"2026-11-26\"")]
    [InlineData("persisted", "false")]
    [InlineData("verified", "false")]
    public void InvalidEvidenceNeverGrantsSave(string field, string value)
    {
        JObject result = Saved();
        result[field] = JToken.Parse(value);
        var policy = Policy();
        Add(policy, result);
        Assert.DoesNotContain("was saved", policy.Render());
        Assert.Contains("not confirmed", policy.Render());
    }

    [Theory]
    [InlineData("unknown", "couldn't verify")]
    [InlineData("failed", "failed")]
    [InlineData("constraint_violation", "failed")]
    public void PartialDatesRetainIndependentVerifiedSaveAndFailure(string outcome, string expected)
    {
        var policy = Policy();
        Add(policy, Saved());
        var failed = Saved("2026-11-27");
        failed["outcome"] = outcome; failed["success"] = false; failed["verified"] = false;
        failed["persisted"] = false; failed["canTellAthleteSaved"] = false;
        Add(policy, failed, "2026-11-27");
        Assert.Contains("2026-11-26: Your replacement workout was saved", policy.Render());
        Assert.Contains("2026-11-27:", policy.Render());
        Assert.Contains(expected, policy.Render());
    }

    [Fact]
    public void LaterUnknownReplacementRetiresEarlierSameDateSave()
    {
        var policy = Policy();
        Add(policy, Saved());
        policy.ObserveResults([new(Call(), "{}", AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        Assert.DoesNotContain("was saved", policy.Render());
        Assert.Contains("2026-11-26:", policy.Render());
    }

    [Fact]
    public void ReplacementRetiresEarlierGenerationWithoutComparingDifferentFingerprintKinds()
    {
        var policy = Policy();
        var generation = ChatToolCall.CreateFunctionToolCall("gen", "GenerateWeeklyPlanSkill", BinaryData.FromString("{\"userId\":1,\"weekStart\":\"2026-11-26\"}"));
        policy.ObserveResults([new(generation, AssistantScopedPlanResponseTests.Result().ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        Add(policy, Saved());
        Assert.Contains("replacement workout was saved", policy.Render());
        Assert.DoesNotContain("Saved dates", policy.Render());
    }

    [Fact]
    public void UnattemptedDatePreservesOtherDateAndSetupQuestion()
    {
        var policy = Policy();
        Add(policy, Saved());
        policy.ObserveUnexecutedCalls([Call("2026-11-27")]);
        Assert.Contains("2026-11-26: Your replacement workout was saved", policy.Render());
        Assert.Contains("2026-11-27: The replacement was not attempted.", policy.Render());
        var setup = ChatToolCall.CreateFunctionToolCall("setup", "Setup", BinaryData.FromString("{}"));
        policy.ObserveResults([new(setup, "{\"schemaVersion\":1,\"operation\":\"Setup\",\"verified\":true,\"success\":false,\"outcome\":\"needs_user_input\",\"nextQuestion\":\"Confirm your schedule?\"}", AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        Assert.Contains("Confirm your schedule?", policy.Render());
    }

    [Fact]
    public void LaterWholePlanGenerationRetiresEarlierDateEvidence()
    {
        var policy = Policy();
        Add(policy, Saved());
        var generation = ChatToolCall.CreateFunctionToolCall("gen", "GenerateWeeklyPlanSkill", BinaryData.FromString("{\"userId\":1,\"weekStart\":\"2026-11-26\"}"));
        policy.ObserveResults([new(generation, AssistantScopedPlanResponseTests.Result("regenerated").ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        Assert.Contains("weekly plan was regenerated", policy.Render());
        Assert.DoesNotContain("replacement workout", policy.Render());
    }

    [Fact]
    public void DistinctDatesUseIndependentWorkoutFingerprints()
    {
        var policy = Policy();
        Add(policy, Saved());
        var second = Saved("2026-11-27");
        second["revision"] = "sha256:" + new string('c', 64);
        Add(policy, second, "2026-11-27");
        Assert.Contains("2026-11-26: Your replacement workout was saved", policy.Render());
        Assert.Contains("2026-11-27: Your replacement workout was saved", policy.Render());
    }

    [Fact]
    public void ImmediateReplacementUsesOnlySameRevisionProviderReceipts()
    {
        var result = Saved();
        result["deliveryOutcomes"] = new JArray(AssistantScopedDeliveryResponseTests.Receipt("2026-11-26", "garmin", "schedule_created_watch_sync_required", true));
        result["providerEvidenceVerified"] = true;
        var policy = Policy(); Add(policy, result);
        Assert.Contains("Garmin accepted the workout schedule.", policy.Render());
        Assert.Contains("Watch visibility is not confirmed", policy.Render());
        result["revision"] = "sha256:" + new string('d', 64);
        policy = Policy(); Add(policy, result);
        Assert.DoesNotContain("accepted", policy.Render());
        Assert.Contains("could not be verified", policy.Render());
    }

    internal static JObject Saved(string date = "2026-11-26") => JObject.FromObject(new
    {
        schemaVersion = 1, operation = "ReplaceWeeklyWorkoutSkill", actorId = 1, weeklyPlanId = 15, date,
        weeklyPlanWorkoutId = 40, verified = true, persisted = true, success = true, outcome = "saved",
        canTellAthleteSaved = true, canTellAthleteSynced = true, providerEvidenceVerified = false,
        providerSyncStatus = "succeeded", revision = "sha256:" + new string('b', 64), updatedUtc = "2026-11-26T10:00:00Z",
        userFacingStatus = "Synced to your watch!"
    });
    static AssistantToolResponsePolicy Policy() => new("GenerateWeeklyPlanSkill;ReplaceWeeklyWorkoutSkill", "Setup");
    static void Add(AssistantToolResponsePolicy policy, JObject result, string date = "2026-11-26") =>
        policy.ObserveResults([new(Call(date), result.ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
    internal static ChatToolCall Call(string date = "2026-11-26") => ChatToolCall.CreateFunctionToolCall("replace" + date, "ReplaceWeeklyWorkoutSkill",
        BinaryData.FromString("{\"userId\":1,\"weeklyPlanId\":15,\"date\":\"" + date + "\",\"workout\":{\"sport\":\"CYCLING\"}}"));
}
