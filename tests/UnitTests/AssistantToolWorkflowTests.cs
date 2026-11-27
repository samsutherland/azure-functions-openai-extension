// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;
using Xunit;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Tests.Assistants;

public class AssistantToolWorkflowTests
{
    [Fact]
    public void ArgumentNormalizer_AcceptsDirectTypedJsonForStringBackedSkill()
    {
        JObject arguments = JObject.Parse("""{"userId":42}""");

        var normalized = Assert.IsType<string>(AssistantSkillArgumentNormalizer.Convert(
            arguments,
            namedValue: null,
            typeof(string)));

        Assert.Equal("""{"userId":42}""", normalized);
    }

    [Fact]
    public void ArgumentNormalizer_UnwrapsLegacySerializedInput()
    {
        JObject arguments = JObject.Parse("""{"input":"{\"userId\":42}"}""");

        var normalized = Assert.IsType<string>(AssistantSkillArgumentNormalizer.Convert(
            arguments,
            arguments["input"],
            typeof(string)));

        Assert.Equal("""{"userId":42}""", normalized);
    }

    [Fact]
    public void ArgumentNormalizer_UnwrapsLegacyObjectInput()
    {
        JObject arguments = JObject.Parse("""{"input":{"userId":42}}""");

        var normalized = Assert.IsType<string>(AssistantSkillArgumentNormalizer.Convert(
            arguments,
            arguments["input"],
            typeof(string)));

        Assert.Equal("""{"userId":42}""", normalized);
    }

    [Fact]
    public void ArgumentNormalizer_RepairsSurplusTrailingDelimiterInLegacySerializedInput()
    {
        JObject arguments = JObject.Parse("""{"input":"{\"userId\":42}}"}""");

        var normalized = Assert.IsType<string>(AssistantSkillArgumentNormalizer.Convert(
            arguments,
            arguments["input"],
            typeof(string)));

        Assert.Equal("""{"userId":42}""", normalized);
    }

    [Fact]
    public void FailedRequest_IdenticalPayloadIsBlockedButChangedPayloadIsAllowed()
    {
        var workflow = new AssistantToolWorkflow();
        ChatToolCall first = Call("call-1", "GetUserAvailabilitySkill", """{"userId":42}""");
        ChatToolCall same = Call("call-2", "GetUserAvailabilitySkill", """{"userId":42}""");
        ChatToolCall changed = Call("call-3", "GetUserAvailabilitySkill", """{"userId":"42"}""");

        workflow.Record(first, "\"externalUserId or userId is required.\"");

        Assert.True(workflow.IsDuplicateFailedRequest(same));
        Assert.False(workflow.IsDuplicateFailedRequest(changed));
        Assert.Contains("identical_failed_request", AssistantToolWorkflow.DuplicateFailureResult(same.FunctionName));
    }

    [Fact]
    public void ToolProvidedSafeCorrection_CreatesOneChangedRetry()
    {
        ChatToolCall original = Call(
            "call-1",
            "ReplaceWeeklyWorkoutSkill",
            """{"weeklyPlanId":7,"date":"2030-01-02","deferProviderSync":true}""");
        const string result =
            """{"success":false,"safeAutomaticCorrection":true,"correctedArguments":{"weeklyPlanId":7,"date":"2030-01-02"}}""";

        Assert.True(AssistantToolWorkflow.TryCreateSafeCorrection(original, result, out ChatToolCall? corrected));
        Assert.NotNull(corrected);
        Assert.DoesNotContain("deferProviderSync", corrected!.FunctionArguments.ToString());
        Assert.NotEqual(AssistantToolWorkflow.Fingerprint(original), AssistantToolWorkflow.Fingerprint(corrected));

        var workflow = new AssistantToolWorkflow();
        workflow.Record(corrected, """{"success":false,"errorCode":"still_invalid"}""");
        Assert.True(workflow.IsDuplicateFailedRequest(corrected));
    }

    [Fact]
    public void MutationClassifier_CoversPlanWritesButNotReads()
    {
        Assert.True(AssistantToolWorkflow.IsMutation("ReplaceWeeklyWorkoutSkill"));
        Assert.True(AssistantToolWorkflow.IsMutation("FinalizeWeeklyPlanSkill"));
        Assert.False(AssistantToolWorkflow.IsMutation("GetWeeklyPlanSummarySkill"));
    }

    [Fact]
    public void DependencyFailure_UsesStableCodeAndPreventsAFalseSuccessClaim()
    {
        string result = AssistantToolWorkflow.DependencyFailureResult("FinalizeWeeklyPlanSkill");

        Assert.True(AssistantToolWorkflow.IsFailure(result));
        Assert.Contains("\"outcome\":\"dependency_failed\"", result);
        Assert.Contains("\"errorCode\":\"prior_mutation_failed\"", result);
        Assert.Contains("was not attempted", result);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"goal\":\"Finish the event\"}]")]
    [InlineData("\"[]\"")]
    public void JsonArrayResult_IsSuccessfulData(string result)
    {
        Assert.False(AssistantToolWorkflow.IsFailure(result));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyToolResult_IsFailureBecauseNoOutcomeWasVerified(string? result)
    {
        Assert.True(AssistantToolWorkflow.IsFailure(result));
    }

    [Fact]
    public void MissingToolResult_ProducesAnExplicitUnverifiedFailure()
    {
        string result = AssistantToolWorkflow.MissingResultFailureResult("SetPowerZonesSkill");

        Assert.True(AssistantToolWorkflow.IsFailure(result));
        Assert.Contains("\"outcome\":\"missing_tool_result\"", result);
        Assert.Contains("\"operation\":\"SetPowerZonesSkill\"", result);
        Assert.DoesNotContain("succeeded", result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeResult_ConvertsEmptyToolResultsToTheUnverifiedFailure(string? result)
    {
        string normalized = AssistantToolWorkflow.NormalizeResult("SetPowerZonesSkill", result);

        Assert.Contains("\"outcome\":\"missing_tool_result\"", normalized);
    }

    [Fact]
    public async Task MultipleDateMutations_RunSequentiallyAndReturnPerCallResults()
    {
        var workflow = new AssistantToolWorkflow();
        ChatToolCall first = Call("call-1", "ReplaceWeeklyWorkoutSkill", """{"date":"2030-01-02"}""");
        ChatToolCall second = Call("call-2", "ReplaceWeeklyWorkoutSkill", """{"date":"2030-01-03"}""");
        var invocationOrder = new List<string>();
        bool invocationInProgress = false;

        IReadOnlyList<AssistantToolWorkflow.ToolExecution> results =
            await workflow.ExecuteSequentialAsync([first, second], async call =>
            {
                Assert.False(invocationInProgress);
                invocationInProgress = true;
                invocationOrder.Add(call.Id);
                await Task.Yield();
                invocationInProgress = false;
                return $$"""{"success":true,"date":"{{call.Id}}"}""";
            });

        Assert.Equal(["call-1", "call-2"], invocationOrder);
        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
            Assert.Equal(AssistantToolWorkflow.ToolExecutionDisposition.Invoked, result.Disposition));
        Assert.Contains("call-1", results[0].Result);
        Assert.Contains("call-2", results[1].Result);
    }

    [Fact]
    public async Task FailedReplacement_BlocksDependentFinalization()
    {
        var workflow = new AssistantToolWorkflow();
        ChatToolCall replacement = Call("call-1", "ReplaceWeeklyWorkoutSkill", """{"date":"2030-01-02"}""");
        ChatToolCall finalization = Call("call-2", "FinalizeWeeklyPlanSkill", """{"weeklyPlanId":7}""");
        int invocationCount = 0;

        IReadOnlyList<AssistantToolWorkflow.ToolExecution> results =
            await workflow.ExecuteSequentialAsync([replacement, finalization], call =>
            {
                invocationCount++;
                return Task.FromResult<string?>("""{"success":false,"errorCode":"persistence_failed"}""");
            });

        Assert.Equal(1, invocationCount);
        Assert.Equal(AssistantToolWorkflow.ToolExecutionDisposition.DependencyBlocked, results[1].Disposition);
        Assert.Contains("prior_mutation_failed", results[1].Result);
    }

    [Fact]
    public async Task FailedPrerequisiteAcrossBatches_BlocksOnlyItsDeclaredDependentOperation()
    {
        var workflow = new AssistantToolWorkflow(
        [
            new AssistantToolWorkflow.DependencyRule(
                "GetTrainingSetupStatusSkill",
                "GenerateWeeklyPlanSkill")
        ]);
        ChatToolCall setup = Call("call-1", "GetTrainingSetupStatusSkill", """{"externalUserId":"athlete"}""");
        ChatToolCall plan = Call("call-2", "GenerateWeeklyPlanSkill", """{"externalUserId":"athlete"}""");
        ChatToolCall unrelatedRead = Call("call-3", "GetWeeklyPlanSummarySkill", """{"externalUserId":"athlete"}""");
        var invoked = new List<string>();

        await workflow.ExecuteSequentialAsync([setup], call =>
            Task.FromResult<string?>("""{"success":false,"outcome":"failed"}"""));

        IReadOnlyList<AssistantToolWorkflow.ToolExecution> secondBatch =
            await workflow.ExecuteSequentialAsync([plan, unrelatedRead], call =>
            {
                invoked.Add(call.FunctionName);
                return Task.FromResult<string?>("""{"success":true}""");
            });

        Assert.Equal(["GetWeeklyPlanSummarySkill"], invoked);
        Assert.Equal(AssistantToolWorkflow.ToolExecutionDisposition.DependencyBlocked, secondBatch[0].Disposition);
        Assert.Equal(AssistantToolWorkflow.ToolExecutionDisposition.Invoked, secondBatch[1].Disposition);
    }

    [Fact]
    public async Task VerifiedPrerequisiteRetry_ClearsItsCrossBatchDependencyBlock()
    {
        var workflow = new AssistantToolWorkflow(
        [
            new AssistantToolWorkflow.DependencyRule(
                "GetTrainingSetupStatusSkill",
                "GenerateWeeklyPlanSkill")
        ]);
        ChatToolCall failedSetup = Call("call-1", "GetTrainingSetupStatusSkill", """{"externalUserId":"athlete","attempt":1}""");
        ChatToolCall recoveredSetup = Call("call-2", "GetTrainingSetupStatusSkill", """{"externalUserId":"athlete","attempt":2}""");
        ChatToolCall plan = Call("call-3", "GenerateWeeklyPlanSkill", """{"externalUserId":"athlete"}""");

        await workflow.ExecuteSequentialAsync([failedSetup], _ => Task.FromResult<string?>("""{"success":false}"""));
        await workflow.ExecuteSequentialAsync([recoveredSetup], _ => Task.FromResult<string?>("""{"success":true}"""));
        IReadOnlyList<AssistantToolWorkflow.ToolExecution> results =
            await workflow.ExecuteSequentialAsync([plan], _ => Task.FromResult<string?>("""{"success":true}"""));

        Assert.Equal(AssistantToolWorkflow.ToolExecutionDisposition.Invoked, Assert.Single(results).Disposition);
    }

    [Fact]
    public void DependencyRuleParser_IgnoresMalformedPairs()
    {
        var rules = AssistantToolWorkflow.ParseDependencyRules("GetSetup>GeneratePlan;invalid;>missing;").ToList();

        var rule = Assert.Single(rules);
        Assert.Equal("GetSetup", rule.PrerequisiteOperation);
        Assert.Equal("GeneratePlan", rule.DependentOperation);
    }

    [Theory]
    [InlineData(1, true, true)]
    [InlineData(2, true, false)]
    [InlineData(1, false, false)]
    public async Task ConfirmedRepairAllowsOneIdenticalReadOnlyForTheSameVerifiedActor(int repairActor, bool verified, bool allowed)
    {
        var workflow = RecoveryWorkflow();
        var read = Call("read", "Check", """{"userId":1}""");
        var repair = Call("repair", "Confirm", """{"userId":1}""");
        await workflow.ExecuteSequentialAsync([read], _ => Task.FromResult<string?>(Evidence("Check", 1, false, "needs_user_input")));
        var blockedPlan = await workflow.ExecuteSequentialAsync([Call("blocked-plan", "Plan", "{}")], _ => throw new Exception("Prerequisite not ready"));
        Assert.Equal(AssistantToolWorkflow.ToolExecutionDisposition.DependencyBlocked, blockedPlan[0].Disposition);
        await workflow.ExecuteSequentialAsync([repair], _ => Task.FromResult<string?>(Evidence("Confirm", repairActor, true, "confirmed", verified)));
        var invoked = 0;
        var retried = await workflow.ExecuteSequentialAsync([read], _ =>
        {
            invoked++;
            return Task.FromResult<string?>(Evidence("Check", 1, true, "ready"));
        });
        Assert.Equal(allowed ? 1 : 0, invoked);
        if (allowed)
        {
            Assert.Contains("ready", retried[0].Result);
            var plan = await workflow.ExecuteSequentialAsync([Call("plan", "Plan", "{}")], _ => Task.FromResult<string?>("{}"));
            Assert.Equal(AssistantToolWorkflow.ToolExecutionDisposition.Invoked, plan[0].Disposition);
        }
    }

    [Fact]
    public async Task FailedRecoveryCannotResetItsBudgetOrUnlockUnknownWrites()
    {
        var workflow = RecoveryWorkflow();
        var read = Call("read", "Check", """{"userId":1}""");
        var repair = Call("repair", "Confirm", """{"userId":1}""");
        await workflow.ExecuteSequentialAsync([read], _ => Task.FromResult<string?>(Evidence("Check", 1, false, "failed")));
        await workflow.ExecuteSequentialAsync([repair], _ => Task.FromResult<string?>(Evidence("Confirm", 1, true, "confirmed")));
        var count = 0;
        for (var i = 0; i < 3; i++)
        {
            await workflow.ExecuteSequentialAsync([read], _ => { count++; return Task.FromResult<string?>(Evidence("Check", 1, false, "failed")); });
            await workflow.ExecuteSequentialAsync([repair], _ => Task.FromResult<string?>(Evidence("Confirm", 1, true, "confirmed")));
        }
        Assert.Equal(1, count);
        var write = Call("write", "SetValue", "{}");
        await workflow.ExecuteSequentialAsync([write], _ => Task.FromResult<string?>("""{"success":false,"outcome":"unknown"}"""));
        await workflow.ExecuteSequentialAsync([write], _ => throw new Exception("Unknown write must not repeat"));
    }

    [Fact]
    public async Task RecoveryCannotChangeArgumentsOrBeUnlockedByAnUnrelatedRepair()
    {
        var workflow = RecoveryWorkflow();
        var read = Call("read", "Check", """{"userId":1}""");
        await workflow.ExecuteSequentialAsync([read], _ => Task.FromResult<string?>(Evidence("Check", 1, false, "needs_user_input")));
        await workflow.ExecuteSequentialAsync([Call("unrelated", "Other", "{}")], _ => Task.FromResult<string?>(Evidence("Other", 1, true, "confirmed")));
        var blocked = await workflow.ExecuteSequentialAsync([read], _ => throw new Exception("Unrelated repair must not unlock the read"));
        Assert.Equal(AssistantToolWorkflow.ToolExecutionDisposition.DependencyBlocked, blocked[0].Disposition);
        await workflow.ExecuteSequentialAsync([Call("repair", "Confirm", "{}")], _ => Task.FromResult<string?>(Evidence("Confirm", 1, true, "confirmed")));
        await workflow.ExecuteSequentialAsync([Call("changed", "Check", """{"userId":2}""")], _ => throw new Exception("Actor arguments must not change"));
        var recovered = await workflow.ExecuteSequentialAsync([read], _ => Task.FromResult<string?>(Evidence("Check", 1, true, "ready")));
        Assert.True(recovered[0].IsRecoveryRead);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":1,\"operation\":\"Check\",\"success\":false,\"outcome\":\"failed\"}")]
    [InlineData("{\"schemaVersion\":1,\"operation\":\"Check\",\"actorId\":\"1\",\"success\":false,\"outcome\":\"failed\"}")]
    public async Task RecoveryRequiresCanonicalActorEvidenceFromTheOriginalRead(string failedRead)
    {
        var workflow = RecoveryWorkflow();
        var read = Call("read", "Check", """{"userId":1}""");
        await workflow.ExecuteSequentialAsync([read], _ => Task.FromResult<string?>(failedRead));
        await workflow.ExecuteSequentialAsync([Call("repair", "Confirm", "{}")], _ => Task.FromResult<string?>(Evidence("Confirm", 1, true, "confirmed")));
        await workflow.ExecuteSequentialAsync([read], _ => throw new Exception("Unscoped read must not gain recovery credit"));
    }

    static AssistantToolWorkflow RecoveryWorkflow() => new(
        AssistantToolWorkflow.ParseDependencyRules("Check>Plan"),
        AssistantToolWorkflow.ParseDependencyRules("Check>Confirm"));

    static string Evidence(string operation, int actor, bool success, string outcome, bool verified = true) =>
        Newtonsoft.Json.JsonConvert.SerializeObject(new { schemaVersion = 1, operation, actorId = actor,
            success, outcome, verified, persisted = success && operation != "Check", ready = success && operation == "Check" });

    static ChatToolCall Call(string id, string name, string arguments) =>
        ChatToolCall.CreateFunctionToolCall(id, name, BinaryData.FromString(arguments));
}
