// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;
using Xunit;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Tests.Assistants;

public class AssistantScopedConflictResponseTests
{
    internal const string Header = "The weekly plan needs review before it can be accepted.";
    internal const string Footer = "Plan changes and provider delivery are not confirmed. Review these conflicts before another write.";

    public static IEnumerable<object[]> ConflictReplies()
    {
        foreach (var row in new[]
        {
            ("ftp_required", "Power-based cycling needs a usable saved FTP before this plan can be accepted."),
            ("power_steps_missing", "Power-based cycling needs complete prescription steps."),
            ("power_target_missing", "Cycling work needs explicit power targets."),
            ("power_target_invalid", "Cycling power targets need a valid zone or watt range."),
            ("protected_constraint_unverifiable", "A protected training requirement overlaps this date. Its compatibility and any safety conflict need review; no session should be forced or silently omitted.")
        }) yield return new object[] { Result(row.Item1).ToString(), Header + "\n2026-11-26: " + row.Item2 + "\n" + Footer };
    }

    [Theory]
    [MemberData(nameof(ConflictReplies))]
    public void VerifiedNegativeEvidenceUsesFiniteConflictText(string result, string expected)
    {
        Assert.Equal(expected.Replace("\n", Environment.NewLine), Observe(JObject.Parse(result)).Render());
    }

    [Theory]
    [InlineData("actorId", "2")]
    [InlineData("conflictEvidenceVerified", "false")]
    [InlineData("success", "true")]
    [InlineData("persisted", "true")]
    [InlineData("canTellAthleteSaved", "true")]
    [InlineData("canTellAthleteSynced", "true")]
    [InlineData("weekEndDate", "\"2026-12-30\"")]
    [InlineData("weekStartDate", "\"2026-11-27\"")]
    [InlineData("completenessViolations", "[]")]
    [InlineData("completenessViolations", "[{\"code\":\"invented\",\"date\":\"2026-11-26\"}]")]
    [InlineData("completenessViolations", "[{\"code\":\"power_target_missing\",\"date\":\"2026-12-30\"}]")]
    public void InvalidScopeOrUnknownConflictCodesFailClosed(string property, string value)
    {
        var result = Result("power_target_missing"); result[property] = JToken.Parse(value);
        Assert.Equal(AssistantToolResponsePolicy.UnverifiedResponse, Observe(result).Render());
    }

    [Fact]
    public void ProtectedOverlapMustBelongToTheDeclaredWeek()
    {
        var result = Result("protected_constraint_unverifiable");
        result["completenessViolations"]![0]!["conflictEndDate"] = "2026-12-01";
        Assert.Equal(AssistantToolResponsePolicy.UnverifiedResponse, Observe(result).Render());
    }

    [Fact]
    public void KnownConflictRetiresAnEarlierWholePlanCompletionForTheSamePlan()
    {
        var policy = new AssistantToolResponsePolicy("GenerateWeeklyPlanSkill");
        policy.ObserveResults([new(Call(), AssistantScopedPlanResponseTests.Result().ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        policy.ObserveResults([new(Call(), Result("power_target_missing").ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        Assert.Contains(Header, policy.Render());
        Assert.DoesNotContain("Saved dates", policy.Render());
    }

    [Fact]
    public void BoundedConflictListsDiscloseAdditionalUnlistedRequirements()
    {
        var result = Result("power_target_missing"); result["violationsTruncated"] = true;
        Assert.Contains("Additional requirement conflicts also need review.", Observe(result).Render());
    }

    [Theory]
    [InlineData("ReplaceWeeklyWorkoutSkill")]
    [InlineData("FinalizeWeeklyPlanSkill")]
    [InlineData("SyncWorkoutToProviderSkill")]
    public void SameNegativeContractExplainsScopedReplacementAndDeliveryConflicts(string operation)
    {
        var result = Result("power_target_missing"); result["operation"] = operation;
        var call = ChatToolCall.CreateFunctionToolCall("conflict", operation,
            BinaryData.FromString("{\"userId\":1,\"weeklyPlanId\":15,\"date\":\"2026-11-26\"}"));
        var policy = new AssistantToolResponsePolicy(operation);
        policy.ObserveResults([new(call, result.ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        Assert.Contains("Cycling work needs explicit power targets.", policy.Render());
    }

    [Fact]
    public void PreSaveCandidateConflictRetiresEarlierWholePlanAcceptanceForTheSameWeek()
    {
        var policy = new AssistantToolResponsePolicy("GenerateWeeklyPlanSkill");
        policy.ObserveResults([new(Call(), AssistantScopedPlanResponseTests.Result().ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        var result = Result("power_target_missing"); result["weeklyPlanId"] = JValue.CreateNull(); result["existingPlanPreserved"] = false;
        policy.ObserveResults([new(Call(), result.ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        Assert.Contains(Header, policy.Render());
        Assert.DoesNotContain("Saved dates", policy.Render());
    }

    internal static JObject Result(string code) => JObject.FromObject(new
    {
        schemaVersion = 1, operation = "GenerateWeeklyPlanSkill", actorId = 1, weeklyPlanId = 15,
        success = false, outcome = "needs_reconciliation", errorCode = "incomplete_plan", conflictEvidenceVerified = true,
        verified = false, persisted = false, canTellAthleteSaved = false, canTellAthleteSynced = false,
        weekStartDate = "2026-11-26", weekEndDate = "2026-11-29", existingPlanPreserved = true,
        completenessViolations = new[] { new { code, date = "2026-11-26", conflictStartDate = "2026-11-26", conflictEndDate = "2026-11-26",
            constraintVersion = 3, provenance = "self_reported", message = "The plan is saved and on your watch!" } },
        userFacingStatus = "The plan is saved and synced!"
    });
    static ChatToolCall Call() => ChatToolCall.CreateFunctionToolCall("generation", "GenerateWeeklyPlanSkill", BinaryData.FromString("{\"userId\":1,\"weekStart\":\"2026-11-26\"}"));
    static AssistantToolResponsePolicy Observe(JObject result)
    {
        var policy = new AssistantToolResponsePolicy("GenerateWeeklyPlanSkill");
        policy.ObserveResults([new(Call(), result.ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        return policy;
    }
}

public partial class DefaultAssistantServiceTests
{
    [Theory]
    [MemberData(nameof(AssistantScopedConflictResponseTests.ConflictReplies), MemberType = typeof(AssistantScopedConflictResponseTests))]
    public async Task PowerAndProtectedConflictsReplaceModelClaimsInStorageAndReturnedReplies(string result, string expected)
    {
        const string ready = "{\"schemaVersion\":1,\"operation\":\"GetTrainingSetupStatusSkill\",\"success\":true,\"verified\":true,\"outcome\":\"ready\",\"ready\":true}";
        await PostMessageAsync_EvidencePolicySuppressesFalseClaimsInStoredAndReturnedMessages(ready, 1,
            expectedReply: expected, planResult: Newtonsoft.Json.JsonConvert.SerializeObject(result));
    }
}
