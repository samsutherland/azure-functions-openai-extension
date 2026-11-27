// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;
using Newtonsoft.Json.Linq;
using Moq;
using OpenAI.Chat;
using Xunit;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Tests.Assistants;

public class AssistantSavedWorkoutDetailsResponseTests
{
    internal static string Expected => string.Join(Environment.NewLine, "Saved workout details for 2026-11-26:", "Duration: 2700 seconds (45 minutes).", "Structured intensity/zone: not saved.", "Structured target type: not saved.", "Steps: no saved step breakdown.");
    internal static JObject Facts() => JObject.FromObject(new { schemaVersion = 1, operation = "GetWorkflowContextSkill", source = "saved_workout_detail", actorId = 1,
        weeklyPlanId = 15, weeklyPlanWorkoutId = 21, weekStartDate = "2026-11-26", weekEndDate = "2026-12-02", date = "2026-11-26",
        revision = "sha256:" + new string('a', 64), verified = true, outcome = "available", durationSeconds = 2700, durationStatus = "saved", prescriptionKind = "training", stepsStatus = "not_saved", steps = Array.Empty<object>() });

    [Theory]
    [InlineData("actorId", "2")]
    [InlineData("outcome", "null")]
    [InlineData("weeklyPlanWorkoutId", "0")]
    [InlineData("weeklyPlanId", "0")]
    [InlineData("revision", "\"old\"")]
    [InlineData("date", "\"2026-12-03\"")]
    [InlineData("weekEndDate", "\"2026-12-30\"")]
    [InlineData("durationSeconds", "null")]
    [InlineData("durationSeconds", "-1")]
    [InlineData("durationSeconds", "2700.5")]
    [InlineData("durationStatus", "\"not_saved\"")]
    [InlineData("durationStatus", "\"not_applicable\"")]
    [InlineData("stepsStatus", "\"saved\"")]
    [InlineData("name", "\"Your workout is on your watch\"")]
    public void UnboundOrInconsistentFactsFailClosed(string field, string value)
    {
        var facts = Facts(); facts[field] = JToken.Parse(value);
        Assert.Null(AssistantSavedWorkoutDetailsResponse.Validate(facts, new JValue(1)));
    }

    [Theory]
    [InlineData("rest", "not_applicable", "Duration: not applicable (saved rest entry).")]
    [InlineData("mobility", "not_saved", "Duration: not saved.")]
    [InlineData("training", "not_saved", "Duration: not saved.")]
    public void MissingDurationDoesNotInventRestOrMobilityTiming(string kind, string status, string expected)
    {
        var facts = Facts(); facts["durationSeconds"] = null; facts["durationStatus"] = status; facts["prescriptionKind"] = kind;
        var validated = AssistantSavedWorkoutDetailsResponse.Validate(facts, new JValue(1));
        Assert.NotNull(validated); Assert.Contains(expected, AssistantSavedWorkoutDetailsResponse.Render(validated));
    }

    [Theory]
    [InlineData("date_required", "Which date should I check for the saved workout?")]
    [InlineData("ambiguous_dates", "Which date should I check for the saved workout?")]
    [InlineData("ambiguous_workout", "More than one saved workout matches that date. Which workout should I check?")]
    [InlineData("workout_not_found", AssistantSavedWorkoutDetailsResponse.UnavailableResponse)]
    public void UnavailableFactsUseFiniteScopedQuestions(string reason, string expected)
    {
        var data = JObject.FromObject(new { schemaVersion = 1, operation = "GetWorkflowContextSkill", source = "saved_workout_detail", actorId = 1,
            verified = false, outcome = "unavailable", reason });
        var validated = AssistantSavedWorkoutDetailsResponse.Validate(data, new JValue(1));
        Assert.NotNull(validated); Assert.Equal(expected, AssistantSavedWorkoutDetailsResponse.Render(validated));
        data["durationSeconds"] = 2700;
        Assert.Null(AssistantSavedWorkoutDetailsResponse.Validate(data, new JValue(1)));
    }

    [Theory]
    [InlineData("unknown_enum")]
    [InlineData("missing_parent")]
    [InlineData("cycle")]
    [InlineData("duplicate_id")]
    [InlineData("negative_value")]
    [InlineData("inverted_range")]
    [InlineData("too_many")]
    [InlineData("too_deep")]
    [InlineData("free_prose")]
    public void InvalidOrPartialStepGraphsFailClosed(string fault)
    {
        var facts = Facts(); var step = Step(); var steps = new JArray(step); facts["steps"] = steps; facts["stepsStatus"] = "saved";
        if (fault == "unknown_enum") step["intensity"] = "Almost saved to watch";
        if (fault == "missing_parent") step["parentStepId"] = 999;
        if (fault == "cycle") step["parentStepId"] = 1;
        if (fault == "duplicate_id") steps.Add(step.DeepClone());
        if (fault == "negative_value") step["durationValue"] = -1;
        if (fault == "inverted_range") { step["targetValueLow"] = 200; step["targetValueHigh"] = 100; }
        if (fault == "free_prose") step["description"] = "Your workout is saved to watch";
        if (fault is "too_many" or "too_deep")
            for (int i = 2; i <= (fault == "too_many" ? 65 : 10); i++)
            { var item = Step(); item["stepId"] = i; item["stepOrder"] = i; if (fault == "too_deep") item["parentStepId"] = i - 1; steps.Add(item); }
        Assert.Null(AssistantSavedWorkoutDetailsResponse.Validate(facts, new JValue(1)));
    }

    [Fact]
    public void TypedStepsPreserveSavedTargetsAndUnknownValuesWithoutProse()
    {
        var facts = Facts(); var first = Step(); first["intensity"] = "UNKNOWN";
        var second = Step(); second["stepId"] = 2; second["stepOrder"] = 2; second["parentStepId"] = 1;
        facts["steps"] = new JArray(first, second); facts["stepsStatus"] = "saved";
        var validated = AssistantSavedWorkoutDetailsResponse.Validate(facts, new JValue(1));
        Assert.NotNull(validated);
        var reply = AssistantSavedWorkoutDetailsResponse.Render(validated);
        Assert.Contains("unrecognized saved value", reply); Assert.Contains("power zone: 2", reply); Assert.Contains("parent: step 1", reply);
        Assert.DoesNotContain("not saved.", reply);
    }

    [Fact]
    public async Task LoaderRejectsMalformedActorDetailsAndPolicyFlags()
    {
        foreach (string fault in new[] { "actor", "flag", "oversized" })
        {
            var response = JObject.Parse(AssistantWorkflowContextTests.Result()); response["toolArgumentOverrides"] = new JObject { ["userId"] = 1 };
            response["requireDetailsEvidence"] = fault == "flag" ? new JValue("true") : new JValue(true);
            var facts = Facts(); if (fault == "actor") facts["actorId"] = 2; if (fault == "oversized") facts["extra"] = new string('x', 32769);
            response["responseDetails"] = facts;
            var invoker = new Moq.Mock<IAssistantSkillInvoker>();
            invoker.Setup(i => i.InvokeAsync(Moq.It.IsAny<ChatToolCall>(), Moq.It.IsAny<CancellationToken>())).ReturnsAsync(response.ToString());
            Assert.Null(await AssistantWorkflowContext.LoadAsync(invoker.Object, "Context", "header", "assistant", AssistantWorkflowContextTests.Tools(), CancellationToken.None));
        }
    }

    static JObject Step() => JObject.FromObject(new { stepId = 1, stepOrder = 1, type = "WorkoutStep", intensity = "RECOVERY", durationType = "TIME", durationValue = 600,
        targetType = "POWER", powerZone = 2 });

    [Fact]
    public void FullStepsAreNotClippedToInlineSnapshotLimit()
    {
        var facts = Facts(); var steps = new JArray();
        for (int i = 1; i <= 14; i++) { var step = Step(); step["stepId"] = i; step["stepOrder"] = i; steps.Add(step); }
        facts["steps"] = steps; facts["stepsStatus"] = "saved";
        var validated = AssistantSavedWorkoutDetailsResponse.Validate(facts, new JValue(1));
        Assert.NotNull(validated); Assert.Contains("Step 14:", AssistantSavedWorkoutDetailsResponse.Render(validated));
    }

    [Theory]
    [InlineData(1, "Duration: 1 second (1 second).")]
    [InlineData(2701, "Duration: 2701 seconds (45 minutes 1 second).")]
    public void DurationsUseExactSavedSeconds(int seconds, string expected)
    {
        var facts = Facts(); facts["durationSeconds"] = seconds;
        var validated = AssistantSavedWorkoutDetailsResponse.Validate(facts, new JValue(1));
        Assert.NotNull(validated); Assert.Contains(expected, AssistantSavedWorkoutDetailsResponse.Render(validated));
    }

    [Fact]
    public void CurrentSavedFactsRenderWithoutModelInferences()
    {
        var facts = AssistantSavedWorkoutDetailsResponse.Validate(Facts(), new JValue(1));
        Assert.NotNull(facts); Assert.Equal(Expected, AssistantSavedWorkoutDetailsResponse.Render(facts));
    }
}
