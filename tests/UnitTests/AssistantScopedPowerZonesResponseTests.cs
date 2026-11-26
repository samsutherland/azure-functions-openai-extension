// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;
using Xunit;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Tests.Assistants;

public class AssistantScopedPowerZonesResponseTests
{
    [Theory]
    [InlineData("actorId", "0")]
    [InlineData("actorId", "2")]
    [InlineData("userId", "null")]
    [InlineData("userId", "2")]
    [InlineData("ftpOverrideWatts", "251")]
    [InlineData("ftpOverrideWatts", "null")]
    [InlineData("ftpOverrideWatts", "\"250\"")]
    public void SaveRequiresSameCanonicalActorAndRequestedReadbackValue(string property, string value)
    {
        var result = Result(); result[property] = JToken.Parse(value);
        Assert.Equal(AssistantToolResponsePolicy.UnverifiedResponse, Observe(result, JToken.FromObject(250)).Render());
    }

    [Fact]
    public void TrustedRequestActorMustMatchTheCanonicalResult()
    {
        Assert.Equal(AssistantToolResponsePolicy.UnverifiedResponse, Observe(Result(), JToken.FromObject(250), 2).Render());
    }

    [Theory]
    [InlineData("250")]
    [InlineData("\"250\"")]
    [InlineData("null")]
    public void ExactSaveOrExplicitClearSupportsLegacyExternalIdentityRequests(string requested)
    {
        var result = Result();
        var input = JToken.Parse(requested);
        result["ftpOverrideWatts"] = input.Type == JTokenType.Null ? JValue.CreateNull() : JToken.FromObject(250);
        Assert.Equal("Your change was saved and verified in the app. Device delivery is not confirmed.", Observe(result, input).Render());
    }

    static JObject Result() => JObject.FromObject(new
    {
        schemaVersion = 1, operation = "SetPowerZonesSkill", actorId = 1, userId = 1,
        success = true, outcome = "saved", verified = true, persisted = true, canTellAthleteSaved = true,
        ftpOverrideWatts = 250
    });
    static AssistantToolResponsePolicy Observe(JObject result, JToken requested, int? trustedActor = null)
    {
        var input = new JObject { ["externalUserId"] = "legacy-alias", ["ftpOverrideWatts"] = requested.DeepClone() };
        if (trustedActor.HasValue) input["userId"] = trustedActor.Value;
        var call = ChatToolCall.CreateFunctionToolCall("ftp", "SetPowerZonesSkill", BinaryData.FromString(new JObject { ["input"] = input.ToString() }.ToString()));
        var policy = new AssistantToolResponsePolicy("SetPowerZonesSkill");
        policy.ObserveResults([new(call, result.ToString(), AssistantToolWorkflow.ToolExecutionDisposition.Invoked)]);
        return policy;
    }
}
