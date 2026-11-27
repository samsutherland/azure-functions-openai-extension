// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using Newtonsoft.Json.Linq;
using Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;
using Xunit;
namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Tests.Assistants;
public class AssistantExistingStateResponseTests
{
    [Theory]
    [InlineData("actorId", "2")]
    [InlineData("schemaVersion", "2")]
    [InlineData("setup.confirmed", "false")]
    [InlineData("setup.ready", "false")]
    [InlineData("setup.currentFingerprintVersion", "\"unknown\"")]
    [InlineData("setup.currentFingerprint", "\"invented\"")]
    [InlineData("setup.confirmationEvidenceKind", "\"model\"")]
    [InlineData("setup.confirmationUtc", "\"not a timestamp\"")]
    [InlineData("plan.requestedDate", "\"2027-01-01\"")]
    [InlineData("plan.status", "\"Failed\"")]
    [InlineData("plan.completenessVerified", "false")]
    [InlineData("plan.weeklyPlanId", "0")]
    [InlineData("plan.workouts", "[]")]
    [InlineData("plan.workouts[0].revision", "\"invalid\"")]
    [InlineData("plan.workouts[0].date", "\"2026-12-09\"")]
    [InlineData("plan.workouts[0].weeklyPlanWorkoutId", "0")]
    public void InvalidObservationCannotAuthorizeFacts(string path, string value)
    {
        var facts = Facts(); facts.SelectToken(path)!.Replace(JToken.Parse(value));
        Assert.Null(AssistantExistingStateResponse.Validate(facts, new JValue(1)));
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("legacy_timestamp")]
    public void ObservedConfirmationPreservesExistingEvidenceSemantics(string kind)
    {
        var facts = Facts(); facts["setup"]!["confirmationEvidenceKind"] = kind;
        Assert.NotNull(AssistantExistingStateResponse.Validate(facts, new JValue(1)));
        Assert.Equal(Expected.Replace("\n", Environment.NewLine, StringComparison.Ordinal), AssistantExistingStateResponse.Render(facts));
        facts.Remove("plan"); facts["observationKind"] = "setup";
        Assert.NotNull(AssistantExistingStateResponse.Validate(facts, new JValue(1)));
        Assert.Equal("Your saved training setup is already marked confirmed.", AssistantExistingStateResponse.Render(facts));
    }

    [Fact]
    public void OmittedUnknownAndDuplicatePlanFactsCannotAuthorizeObservation()
    {
        var facts = Facts(); facts["plan"]!["workouts"]![0]!["arbitraryProse"] = "saved";
        Assert.Null(AssistantExistingStateResponse.Validate(facts, new JValue(1)));
        facts = Facts(); ((JArray)facts["plan"]!["workouts"]!).Add(facts["plan"]!["workouts"]![0]!.DeepClone());
        Assert.Null(AssistantExistingStateResponse.Validate(facts, new JValue(1)));
        facts = Facts(); facts["plan"]!["workouts"] = new JArray(Enumerable.Range(1,65).Select(i => new JObject { ["date"]="2026-12-02",["weeklyPlanWorkoutId"]=i,["revision"]="sha256:"+new string('a',64) }));
        Assert.Null(AssistantExistingStateResponse.Validate(facts, new JValue(1)));
    }

    internal const string Expected = "Your saved training setup is already marked confirmed.\n\nA saved plan covers 2026-12-02. No new save or provider delivery is confirmed.";
    internal static JObject Facts() => JObject.FromObject(new
    {
        schemaVersion=1, operation="GetWorkflowContextSkill", source="existing_training_state", actorId=1, verified=true, outcome="observed", observationKind="saved_plan",
        setup=new { ready=true, confirmed=true, confirmationEvidenceKind="legacy_timestamp", confirmationUtc="2026-11-26T10:00:00.0000000Z", currentFingerprint=new string('A',64), currentFingerprintVersion="setup-v1" },
        plan=new { weeklyPlanId=10, weekStartDate="2026-12-02",weekEndDate="2026-12-08",requestedDate="2026-12-02", status="Completed",completenessVerified=true,
            workouts=new[] { new {date="2026-12-02",weeklyPlanWorkoutId=20,revision="sha256:"+new string('a',64)} } }
    });
}
