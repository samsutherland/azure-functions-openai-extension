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

    static AssistantToolResponsePolicy Observe(string result)
    {
        var policy = new AssistantToolResponsePolicy("Write");
        policy.ObserveCalls([Call()]);
        policy.ObserveResults([new(Call(), result, AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        return policy;
    }

    static ChatToolCall Call() => ChatToolCall.CreateFunctionToolCall("call", "Write", BinaryData.FromString("{}"));
}
