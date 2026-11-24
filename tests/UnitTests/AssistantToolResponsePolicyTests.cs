// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;
using OpenAI.Chat;
using Xunit;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Tests.Assistants;

public class AssistantToolResponsePolicyTests
{
    [Theory]
    [InlineData("{}")] // Legacy payloads cannot prove completion.
    [InlineData("not-json")]
    [InlineData("{\"schemaVersion\":1,\"operation\":\"Write\",\"success\":true,\"outcome\":\"saved\",\"verified\":false,\"persisted\":true}")]
    [InlineData("{\"schemaVersion\":1,\"operation\":\"Other\",\"success\":true,\"outcome\":\"saved\",\"verified\":true,\"persisted\":true}")]
    [InlineData("{\"schemaVersion\":1,\"operation\":\"Write\",\"success\":false,\"outcome\":\"failed\",\"userFacingStatus\":\"Saved and synced successfully!\"}")]
    public void UnverifiedResultsNeverRenderCompletionClaims(string result)
    {
        var policy = Observe(result);
        Assert.Equal(AssistantToolResponsePolicy.UnverifiedResponse, policy.Render());
    }

    [Fact]
    public void VerifiedSaveUsesFiniteTextAndDoesNotClaimProviderDelivery()
    {
        var policy = Observe("""{"schemaVersion":1,"operation":"Write","success":true,"outcome":"saved","verified":true,"persisted":true,"canTellAthleteSaved":true,"canTellAthleteSynced":false,"userFacingStatus":"Your watch has synced!"}""");
        Assert.Equal("Your change was saved and verified in the app. Device delivery is not confirmed.", policy.Render());
    }

    [Fact]
    public void UnknownResultAlongsideVerifiedSaveNeverSuggestsAllOperationsSucceeded()
    {
        var policy = Observe("""{"schemaVersion":1,"operation":"Write","success":true,"outcome":"saved","verified":true,"persisted":true,"canTellAthleteSaved":true}""");
        policy.ObserveResults([new(Call(), "{}", AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        Assert.Equal(AssistantToolResponsePolicy.UnverifiedResponse, policy.Render());
    }

    [Fact]
    public void UnconfiguredPolicyPreservesNormalChat()
    {
        var policy = new AssistantToolResponsePolicy(null);
        policy.ObserveCalls([Call()]);
        Assert.False(policy.IsActive);
    }

    [Fact]
    public void UnrelatedToolsDoNotActivatePolicy()
    {
        var policy = new AssistantToolResponsePolicy("Other");
        policy.ObserveCalls([Call()]);
        Assert.False(policy.IsActive);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"operation\":\"CheckSetup\",\"success\":true,\"verified\":true,\"outcome\":\"ready\",\"ready\":true}", false)]
    [InlineData("{\"schemaVersion\":1,\"operation\":\"CheckSetup\",\"success\":false,\"verified\":true,\"outcome\":\"needs_user_input\",\"nextQuestion\":\"Goal: climb confidently. Monday: 0 hours. Does that feel right?\"}", true)]
    [InlineData("{\"success\":false,\"outcome\":\"failed\"}", true)]
    [InlineData("not-json", true)]
    [InlineData("{\"schemaVersion\":1,\"operation\":\"Other\",\"success\":true,\"verified\":true,\"outcome\":\"ready\",\"ready\":true}", true)]
    public void PrerequisiteBoundaryOwnsOnlyIncompleteOrUnverifiedReads(string result, bool active)
    {
        var policy = new AssistantToolResponsePolicy(null, "CheckSetup");
        var call = ChatToolCall.CreateFunctionToolCall("check", "CheckSetup", BinaryData.FromString("{}"));
        policy.ObserveCalls([call]);
        Assert.False(policy.IsActive);
        policy.ObserveResults([new(call, result, AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        Assert.Equal(active, policy.IsActive);
        if (result.Contains("needs_user_input", StringComparison.Ordinal))
        {
            Assert.Equal("Goal: climb confidently. Monday: 0 hours. Does that feel right?", policy.Render());
        }
    }

    [Fact]
    public void MissingInputRendersSavedContextBeforeTheUnchangedQuestion()
    {
        var policy = Observe("""{"schemaVersion":1,"operation":"Write","success":false,"verified":true,"outcome":"needs_user_input","promptContext":"Goal: climb confidently.\nMonday: 0 hours.","nextQuestion":"Does that feel right?"}""");
        Assert.Equal("Goal: climb confidently.\nMonday: 0 hours." + Environment.NewLine + Environment.NewLine + "Does that feel right?", policy.Render());
    }

    static AssistantToolResponsePolicy Observe(string result)
    {
        var policy = new AssistantToolResponsePolicy("Write");
        policy.ObserveCalls([Call()]);
        policy.ObserveResults([new(Call(), result, AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        return policy;
    }

    static ChatToolCall Call() => ChatToolCall.CreateFunctionToolCall("call", "Write", BinaryData.FromString("{}"));
}
