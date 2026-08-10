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

    static ChatToolCall Call(string id, string name, string arguments) =>
        ChatToolCall.CreateFunctionToolCall(id, name, BinaryData.FromString(arguments));
}
