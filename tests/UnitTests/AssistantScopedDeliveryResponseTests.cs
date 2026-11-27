// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;
using Xunit;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Tests.Assistants;

public class AssistantScopedDeliveryResponseTests
{
    [Theory]
    [InlineData("garmin", "schedule_created_watch_sync_required", true, "Garmin accepted the workout schedule.")]
    [InlineData("garmin", "workout_created_schedule_unconfirmed", false, "Garmin workout was created; scheduling and watch visibility are not confirmed.")]
    [InlineData("wahoo", "workout_created", false, "Wahoo workout was created.")]
    public void FreshObjectsAndSchedulesAreDistinctFromWatchVisibility(string provider, string state, bool scheduled, string expected)
    {
        var result = Result();
        result["deliveryOutcomes"] = new JArray(Receipt("2026-11-26", provider, state, scheduled));
        string text = Observe(result).Render();
        Assert.Contains("2026-11-26: " + expected, text);
        Assert.Contains("not confirmed", text);
        Assert.DoesNotContain("on your watch", text);
    }

    [Theory]
    [InlineData("unknown", false, "couldn't verify")]
    [InlineData("failed", true, "delivery failed")]
    public void PartialDeliveryKeepsEachDatesActualEvidence(string state, bool verified, string expected)
    {
        var result = Result();
        var second = Receipt("2026-11-27", "garmin", state);
        second["verified"] = verified; second["workoutCreated"] = false;
        result["providerEvidenceVerified"] = false;
        result["deliveryOutcomes"] = new JArray(Receipt("2026-11-26", "garmin", "schedule_created_watch_sync_required", true), second);
        string text = Observe(result).Render();
        Assert.Contains("2026-11-26: Garmin accepted", text);
        Assert.Contains("2026-11-27:", text);
        Assert.Contains(expected, text);
    }

    [Theory]
    [InlineData("revision", "\"sha256:wrong\"")]
    [InlineData("weeklyPlanWorkoutId", "99")]
    [InlineData("date", "\"2027-01-01\"")]
    [InlineData("verified", "false")]
    [InlineData("watchVisible", "true")]
    [InlineData("externalId", "null")]
    [InlineData("scheduleCreated", "false")]
    public void InvalidReceiptNeverConfirmsProviderCompletion(string field, string value)
    {
        var result = Result();
        var receipt = Receipt("2026-11-26", "garmin", "schedule_created_watch_sync_required", true);
        receipt[field] = JToken.Parse(value);
        result["deliveryOutcomes"] = new JArray(receipt);
        Assert.DoesNotContain("accepted", Observe(result).Render());
        Assert.Contains("not confirmed", Observe(result).Render());
    }

    [Theory]
    [InlineData("actorId", "2")]
    [InlineData("weeklyPlanId", "0")]
    [InlineData("persisted", "false")]
    [InlineData("verified", "false")]
    [InlineData("revisions", "[]")]
    public void InvalidSavedScopeNeverConfirmsFinalization(string field, string value)
    {
        var result = Result(); result[field] = JToken.Parse(value);
        Assert.DoesNotContain("was finalized", Observe(result).Render());
    }

    [Fact]
    public void VerifiedFinalizationSupersedesEarlierGenerationAndReplacement()
    {
        var policy = new AssistantToolResponsePolicy("GenerateWeeklyPlanSkill;ReplaceWeeklyWorkoutSkill;FinalizeWeeklyPlanSkill");
        var gen = ChatToolCall.CreateFunctionToolCall("gen", "GenerateWeeklyPlanSkill", BinaryData.FromString("{\"userId\":1,\"weekStart\":\"2026-11-26\"}"));
        policy.ObserveResults([new(gen, AssistantScopedPlanResponseTests.Result().ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        policy.ObserveResults([new(AssistantScopedReplacementResponseTests.Call(), AssistantScopedReplacementResponseTests.Saved().ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        policy.ObserveResults([new(Call(), Result().ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        Assert.Contains("was finalized", policy.Render());
        Assert.DoesNotContain("replacement workout was saved", policy.Render());
        Assert.DoesNotContain("weekly plan was saved", policy.Render());
    }

    [Fact]
    public void LaterReplacementRetiresEarlierDeliveryClaims()
    {
        var policy = new AssistantToolResponsePolicy("ReplaceWeeklyWorkoutSkill;FinalizeWeeklyPlanSkill");
        var result = Result();
        result["deliveryOutcomes"] = new JArray(Receipt("2026-11-26", "garmin", "schedule_created_watch_sync_required", true));
        policy.ObserveResults([new(Call(), result.ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        policy.ObserveResults([new(AssistantScopedReplacementResponseTests.Call(), AssistantScopedReplacementResponseTests.Saved().ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        Assert.Contains("replacement workout was saved", policy.Render());
        Assert.DoesNotContain("accepted", policy.Render());
        Assert.DoesNotContain("was finalized", policy.Render());
    }

    [Fact]
    public void WrappedSyncRequestBindsRequestedProviderAndDate()
    {
        var result = Result("SyncWorkoutToProviderSkill");
        result["date"] = "2026-11-26";
        result["deliveryOutcomes"] = new JArray(Receipt("2026-11-26", "garmin", "schedule_created_watch_sync_required", true));
        var call = ChatToolCall.CreateFunctionToolCall("sync", "SyncWorkoutToProviderSkill", BinaryData.FromString("{\"input\":\"{\\\"userId\\\":1,\\\"weeklyPlanId\\\":15,\\\"date\\\":\\\"2026-11-26\\\",\\\"provider\\\":\\\"Garmin\\\"}\"}"));
        var policy = new AssistantToolResponsePolicy("SyncWorkoutToProviderSkill");
        policy.ObserveResults([new(call, result.ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        Assert.Contains("2026-11-26: Garmin accepted", policy.Render());
    }

    [Fact]
    public void ConflictingDuplicateReceiptsDoNotRenderAnEarlierCompletion()
    {
        var result = Result();
        var first = Receipt("2026-11-26", "garmin", "schedule_created_watch_sync_required", true);
        var second = (JObject)first.DeepClone(); second["syncState"] = "unknown"; second["verified"] = false;
        result["deliveryOutcomes"] = new JArray(first, second);
        Assert.DoesNotContain("accepted", Observe(result).Render());
    }

    [Theory]
    [InlineData("no_action_required", "already finalized")]
    [InlineData("finalized", "was finalized")]
    public void NoReceiptFinalizationDoesNotAssertAnyDelivery(string outcome, string expected)
    {
        var result = Result(); result["outcome"] = outcome;
        Assert.Contains(expected, Observe(result).Render());
        Assert.Contains("Provider delivery and watch visibility are not confirmed.", Observe(result).Render());
    }

    [Fact]
    public void IndependentDateSyncsRetainBothOutcomes()
    {
        var policy = new AssistantToolResponsePolicy("SyncWorkoutToProviderSkill");
        foreach (string date in new[] { "2026-11-26", "2026-11-27" })
        {
            var result = Result("SyncWorkoutToProviderSkill"); result["date"] = date;
            result["deliveryOutcomes"] = new JArray(Receipt(date, "garmin", "schedule_created_watch_sync_required", true));
            var call = ChatToolCall.CreateFunctionToolCall("sync" + date, "SyncWorkoutToProviderSkill", BinaryData.FromString("{\"userId\":1,\"weeklyPlanId\":15,\"date\":\"" + date + "\"}"));
            policy.ObserveResults([new(call, result.ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        }
        Assert.Contains("2026-11-26: Garmin accepted", policy.Render());
        Assert.Contains("2026-11-27: Garmin accepted", policy.Render());
    }

    [Fact]
    public void UnknownSyncRetiresAnEarlierWholePlanDeliveryReceipt()
    {
        var policy = new AssistantToolResponsePolicy("FinalizeWeeklyPlanSkill;SyncWorkoutToProviderSkill");
        var first = Result(); first["deliveryOutcomes"] = new JArray(Receipt("2026-11-26", "garmin", "schedule_created_watch_sync_required", true));
        policy.ObserveResults([new(Call(), first.ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        var call = ChatToolCall.CreateFunctionToolCall("sync", "SyncWorkoutToProviderSkill", BinaryData.FromString("{\"userId\":1,\"weeklyPlanId\":15,\"date\":\"2026-11-26\"}"));
        policy.ObserveResults([new(call, "{}", AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        Assert.DoesNotContain("accepted", policy.Render());
        Assert.Contains("couldn't verify", policy.Render());
    }

    internal static JObject Result(string operation = "FinalizeWeeklyPlanSkill") => JObject.FromObject(new
    {
        schemaVersion = 1, operation, actorId = 1, weeklyPlanId = 15,
        success = true, outcome = operation == "FinalizeWeeklyPlanSkill" ? "finalized" : "sync_completed",
        finalized = true, verified = true, persisted = true, providerEvidenceVerified = true, watchVisible = false,
        savedDates = new[] { "2026-11-26", "2026-11-27" },
        revisions = new[] { new { date = "2026-11-26", weeklyPlanWorkoutId = 40, revision = "sha256:" + new string('b', 64) },
            new { date = "2026-11-27", weeklyPlanWorkoutId = 41, revision = "sha256:" + new string('c', 64) } },
        deliveryOutcomes = Array.Empty<object>(), userFacingStatus = "Everything is on your watch!"
    });
    internal static JObject Receipt(string date, string provider, string state, bool scheduled = false) => JObject.FromObject(new
    {
        provider, date, weeklyPlanWorkoutId = date == "2026-11-26" ? 40 : 41,
        revision = "sha256:" + new string(date == "2026-11-26" ? 'b' : 'c', 64), verified = true,
        workoutCreated = true, scheduleCreated = scheduled, watchVisible = false, externalId = "workout-1", scheduleId = (string?)null, syncState = state
    });
    static AssistantToolResponsePolicy Observe(JObject result)
    {
        var policy = new AssistantToolResponsePolicy("FinalizeWeeklyPlanSkill");
        policy.ObserveResults([new(Call(), result.ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        return policy;
    }
    internal static ChatToolCall Call() => ChatToolCall.CreateFunctionToolCall("final", "FinalizeWeeklyPlanSkill", BinaryData.FromString("{\"userId\":1,\"weeklyPlanId\":15}"));
}
