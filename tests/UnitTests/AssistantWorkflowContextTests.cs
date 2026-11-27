// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;
using Moq;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;
using Xunit;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Tests.Assistants;

public class AssistantWorkflowContextTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"schemaVersion\":2,\"operation\":\"Context\",\"success\":true,\"verified\":true,\"instructions\":\"current\",\"allowedToolNames\":[\"Read\"]}")]
    [InlineData("{\"schemaVersion\":1,\"operation\":\"Other\",\"success\":true,\"verified\":true,\"instructions\":\"current\",\"allowedToolNames\":[\"Read\"]}")]
    [InlineData("{\"schemaVersion\":1,\"operation\":\"Context\",\"success\":true,\"verified\":false,\"instructions\":\"current\",\"allowedToolNames\":[\"Read\"]}")]
    [InlineData("{\"schemaVersion\":1,\"operation\":\"Context\",\"success\":true,\"verified\":true,\"instructions\":\"current\",\"allowedToolNames\":[\"Context\"]}")]
    [InlineData("{\"schemaVersion\":1,\"operation\":\"Context\",\"success\":true,\"verified\":true,\"instructions\":\"current\",\"allowedToolNames\":[\"Read\",\"Read\"]}")]
    [InlineData("{\"schemaVersion\":1,\"operation\":\"Context\",\"success\":true,\"verified\":true,\"instructions\":\"current\",\"allowedToolNames\":[\"Unknown\"]}")]
    [InlineData("{\"schemaVersion\":1,\"operation\":\"Context\",\"success\":true,\"verified\":true,\"instructions\":\"current\",\"allowedToolNames\":[\"Read;Write\"]}")]
    public async Task UnverifiedOrInvalidContextFailsClosed(string result)
    {
        var invoker = new Mock<IAssistantSkillInvoker>();
        invoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        Assert.Null(await AssistantWorkflowContext.LoadAsync(invoker.Object, "Context", "opaque", "assistant", Tools(), CancellationToken.None));
    }

    [Fact]
    public async Task MalformedOperationEvidencePolicyFailsClosed()
    {
        var result = JObject.Parse(Result()); result["requireOperationEvidence"] = "true";
        var invoker = new Mock<IAssistantSkillInvoker>();
        invoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>())).ReturnsAsync(result.ToString());
        Assert.Null(await AssistantWorkflowContext.LoadAsync(invoker.Object, "Context", "opaque", "assistant", Tools(), CancellationToken.None));
    }

    [Fact]
    public async Task SnapshotArgumentsEncodeOpaqueIdentityWithoutUsingItAsJson()
    {
        const string opaque = "header-with-\\\"quotes\\\"";
        ChatToolCall? observed = null;
        var invoker = new Mock<IAssistantSkillInvoker>();
        invoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>()))
            .Callback<ChatToolCall, CancellationToken>((call, _) => observed = call).ReturnsAsync(Result());
        var context = await AssistantWorkflowContext.LoadAsync(invoker.Object, "Context", opaque, "assistant", Tools(), CancellationToken.None);
        Assert.NotNull(context);
        var args = JObject.Parse(JObject.Parse(observed!.FunctionArguments.ToString())["input"]!.Value<string>()!);
        Assert.Equal("assistant", args["assistantId"]);
        Assert.Equal(opaque, args["requestContext"]);
        Assert.Equal("current", context.Instructions);
        Assert.Equal(new[] { "Read" }, context.AllowedTools.Select(t => t.FunctionName));
    }

    [Fact]
    public async Task OversizedArgumentsNeverInvokeContextOperation()
    {
        var invoker = new Mock<IAssistantSkillInvoker>();
        Assert.Null(await AssistantWorkflowContext.LoadAsync(invoker.Object, "Context", new string('x', 16385), "assistant", Tools(), CancellationToken.None));
        invoker.Verify(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Theory]
    [InlineData("{\"userId\":2,\"externalUserId\":\"victim\"}")]
    [InlineData("{\"input\":{\"userId\":2,\"externalUserId\":\"victim\"}}")]
    public async Task TrustedIdentityOverridesBothDirectAndObjectWrappedSelectors(string arguments)
    {
        var result = JObject.Parse(Result());
        result["toolArgumentOverrides"] = new JObject { ["userId"] = 1 };
        var invoker = new Mock<IAssistantSkillInvoker>();
        invoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>())).ReturnsAsync(result.ToString());
        var context = await AssistantWorkflowContext.LoadAsync(invoker.Object, "Context", "opaque", "assistant", Tools(), CancellationToken.None);
        var call = context!.Canonicalize(ChatToolCall.CreateFunctionToolCall("write", "Write", BinaryData.FromString(arguments)));
        Assert.NotNull(call);
        var args = JObject.Parse(call.FunctionArguments.ToString());
        var payload = args["input"] is JToken input ? JObject.Parse(input.Value<string>()!) : args;
        Assert.Equal(1, payload["userId"]);
        Assert.Null(payload["externalUserId"]);
    }

    [Fact]
    public async Task EmptyToolAllowlistIsValidForOrdinaryChat()
    {
        var result = JObject.Parse(Result()); result["allowedToolNames"] = new JArray();
        var invoker = new Mock<IAssistantSkillInvoker>();
        invoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>())).ReturnsAsync(result.ToString());
        var context = await AssistantWorkflowContext.LoadAsync(invoker.Object, "Context", "opaque", "assistant", Tools(), CancellationToken.None);
        Assert.NotNull(context); Assert.Empty(context.AllowedTools);
    }

    [Theory]
    [InlineData("{\"userId\":\"2\"}")]
    [InlineData("{\"userId\":0}")]
    [InlineData("{\"externalUserId\":null}")]
    [InlineData("{\"externalUserId\":\"\"}")]
    [InlineData("{\"arbitrary\":1}")]
    public async Task MalformedTrustedSelectorsFailClosed(string overrides)
    {
        var result = JObject.Parse(Result()); result["toolArgumentOverrides"] = JObject.Parse(overrides);
        var invoker = new Mock<IAssistantSkillInvoker>();
        invoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>())).ReturnsAsync(result.ToString());
        Assert.Null(await AssistantWorkflowContext.LoadAsync(invoker.Object, "Context", "opaque", "assistant", Tools(), CancellationToken.None));
    }

    [Fact]
    public async Task OversizedSnapshotInstructionsFailClosed()
    {
        var invoker = new Mock<IAssistantSkillInvoker>();
        invoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result(new string('x', 16385)));
        Assert.Null(await AssistantWorkflowContext.LoadAsync(invoker.Object, "Context", "opaque", "assistant", Tools(), CancellationToken.None));
    }

    [Theory]
    [InlineData("actorId", "2")]
    [InlineData("schemaVersion", "2")]
    [InlineData("operation", "\"Other\"")]
    [InlineData("verified", "false")]
    [InlineData("ready", "true")]
    [InlineData("nextQuestion", "1")]
    public async Task MalformedOrOtherActorPrerequisiteRejectsContext(string property, string value)
    {
        var result = JObject.Parse(Result()); result["toolArgumentOverrides"] = new JObject { ["userId"] = 1 };
        var read = Prerequisite(false, "Current question?"); read[property] = JToken.Parse(value);
        result["responsePrerequisite"] = read;
        var invoker = new Mock<IAssistantSkillInvoker>();
        invoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>())).ReturnsAsync(result.ToString());
        Assert.Null(await AssistantWorkflowContext.LoadAsync(invoker.Object, "Context", "opaque", "assistant", Tools(), CancellationToken.None));
    }

    [Theory]
    [InlineData("actorId", "2")]
    [InlineData("operation", "\"ReplaceWeeklyWorkoutSkill\"")]
    [InlineData("existingPlanPreserved", "false")]
    [InlineData("weeklyPlanId", "0")]
    [InlineData("userFacingStatus", "oversized")]
    [InlineData("conflictEvidenceVerified", "false")]
    [InlineData("persisted", "true")]
    [InlineData("completenessViolations", "[]")]
    public async Task InvalidCurrentPlanConflictRejectsContext(string property, string value)
    {
        var result = JObject.Parse(Result()); result["toolArgumentOverrides"] = new JObject { ["userId"] = 1 };
        var rejection = AssistantScopedConflictResponseTests.Result("power_target_missing");
        rejection[property] = value == "oversized" ? new JValue(new string('x', 32769)) : JToken.Parse(value); result["responseConflict"] = rejection;
        var invoker = new Mock<IAssistantSkillInvoker>();
        invoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>())).ReturnsAsync(result.ToString());
        Assert.Null(await AssistantWorkflowContext.LoadAsync(invoker.Object, "Context", "opaque", "assistant", Tools(), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrustedOperationBindingsReplaceModelValuesBeforeInvocation(bool wrapped)
    {
        var result = JObject.Parse(Result("current", "Write"));
        result["toolArgumentOverrides"] = new JObject { ["userId"] = 1 };
        result["toolOperationArgumentOverrides"] = JObject.Parse("{\"Write\":{\"regenerate\":false,\"regenerationReason\":null,\"weekStart\":\"2026-12-02\",\"reuseOnly\":true}}");
        var invoker = new Mock<IAssistantSkillInvoker>();
        invoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>())).ReturnsAsync(result.ToString());
        var context = await AssistantWorkflowContext.LoadAsync(invoker.Object, "Context", "opaque", "assistant", Tools(), CancellationToken.None);
        Assert.NotNull(context);
        var payload = JObject.Parse("{\"USERID\":99,\"REGENERATE\":true,\"regenerationReason\":\"replace all\",\"weekStart\":\"2027-01-01\"}");
        var args = wrapped ? new JObject { ["input"] = payload.ToString() } : payload;
        var call = context.Canonicalize(ChatToolCall.CreateFunctionToolCall("actual-call", "Write", BinaryData.FromString(args.ToString())));
        Assert.NotNull(call); Assert.Equal("actual-call", call.Id);
        var actual = JObject.Parse(call.FunctionArguments.ToString());
        if (wrapped) actual = JObject.Parse(actual["input"]!.Value<string>()!);
        Assert.Equal(1, actual["userId"]); Assert.Null(actual["USERID"]);
        Assert.False(actual["regenerate"]!.Value<bool>()); Assert.Null(actual["REGENERATE"]);
        Assert.Equal(JTokenType.Null, actual["regenerationReason"]!.Type);
        Assert.Equal("2026-12-02", actual["weekStart"]); Assert.True(actual["reuseOnly"]!.Value<bool>());
    }

    [Theory]
    [InlineData("{\"Unknown\":{\"reuseOnly\":true}}")]
    [InlineData("{\"Context\":{\"reuseOnly\":true}}")]
    [InlineData("{\"Write\":{\"userId\":1}}")]
    [InlineData("{\"Write\":{\"EXTERNALUSERID\":\"victim\"}}")]
    [InlineData("{\"Write\":{\"reuseOnly\":{\"nested\":true}}}")]
    [InlineData("{\"Write\":{\"reuseOnly\":[true]}}")]
    [InlineData("{\"Write\":{\"reuseOnly\":true,\"REUSEONLY\":false}}")]
    [InlineData("{\"Write\":{\"reuseOnly\":true},\"write\":{\"reuseOnly\":false}}")]
    [InlineData("{\"Write\":{\"bad.key\":true}}")]
    [InlineData("{\"Write\":{\"fraction\":1.5}}")]
    public async Task MalformedOperationBindingsFailClosed(string bindings)
    {
        var result = JObject.Parse(Result("current", "Write")); result["toolOperationArgumentOverrides"] = JObject.Parse(bindings);
        var invoker = new Mock<IAssistantSkillInvoker>();
        invoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>())).ReturnsAsync(result.ToString());
        Assert.Null(await AssistantWorkflowContext.LoadAsync(invoker.Object, "Context", "opaque", "assistant", Tools(), CancellationToken.None));
    }

    [Fact]
    public async Task TrustedBindingsCannotOverrideInternalOperationsOrHideDuplicateJsonKeys()
    {
        var result = JObject.Parse(Result("current", "Hidden")); result["toolOperationArgumentOverrides"] = JObject.Parse("{\"Hidden\":{\"reuseOnly\":true}}");
        var invoker = new Mock<IAssistantSkillInvoker>();
        invoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>())).ReturnsAsync(result.ToString());
        Assert.Null(await AssistantWorkflowContext.LoadAsync(invoker.Object, "Context", "opaque", "assistant", Tools(), CancellationToken.None, internalOperationNames: "Hidden"));
        var duplicate = Result("current", "Write").TrimEnd().TrimEnd('}') + ",\"toolOperationArgumentOverrides\":{\"Write\":{\"reuseOnly\":true,\"reuseOnly\":false}}}";
        invoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>())).ReturnsAsync(duplicate);
        Assert.Null(await AssistantWorkflowContext.LoadAsync(invoker.Object, "Context", "opaque", "assistant", Tools(), CancellationToken.None));
    }

    [Fact]
    public async Task BindingsAreBoundedAndDoNotSurviveTheNextSnapshot()
    {
        var result = JObject.Parse(Result("current", "Write"));
        result["toolOperationArgumentOverrides"] = new JObject { ["Write"] = new JObject { ["reason"] = new string('x',1025) } };
        var invoker = new Mock<IAssistantSkillInvoker>();
        invoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>())).ReturnsAsync(result.ToString());
        Assert.Null(await AssistantWorkflowContext.LoadAsync(invoker.Object,"Context","opaque","assistant",Tools(),CancellationToken.None));
        result["toolOperationArgumentOverrides"] = JObject.Parse("{\"Write\":{\"regenerate\":false}}");
        invoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>())).ReturnsAsync(result.ToString());
        var first = await AssistantWorkflowContext.LoadAsync(invoker.Object,"Context","opaque","assistant",Tools(),CancellationToken.None);
        result.Remove("toolOperationArgumentOverrides");
        invoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>())).ReturnsAsync(result.ToString());
        var next = await AssistantWorkflowContext.LoadAsync(invoker.Object,"Context","opaque","assistant",Tools(),CancellationToken.None);
        var call = ChatToolCall.CreateFunctionToolCall("write","Write", BinaryData.FromString("{\"regenerate\":true}"));
        Assert.False(JObject.Parse(first!.Canonicalize(call)!.FunctionArguments.ToString())["regenerate"]!.Value<bool>());
        Assert.True(JObject.Parse(next!.Canonicalize(call)!.FunctionArguments.ToString())["regenerate"]!.Value<bool>());
    }

    internal static JObject Prerequisite(bool ready, string question) => JObject.FromObject(new
    {
        schemaVersion = 1, operation = "GetTrainingSetupStatusSkill", actorId = 1, verified = true,
        success = ready, ready, outcome = ready ? "ready" : "needs_user_input",
        nextQuestion = ready ? null : question, promptContext = ready ? null : "Your saved goal and availability."
    });

    internal static IList<ChatTool> Tools() => new List<ChatTool>
    {
        ChatTool.CreateFunctionTool("Context", "Read context", BinaryData.FromString("{}")),
        ChatTool.CreateFunctionTool("Read", "Read data", BinaryData.FromString("{}")),
        ChatTool.CreateFunctionTool("Write", "Write data", BinaryData.FromString("{}")),
        ChatTool.CreateFunctionTool("Hidden", "Other data", BinaryData.FromString("{}"))
    };
    internal static string Result(string instructions = "current", params string[] allowed) => JObject.FromObject(new
    {
        schemaVersion = 1, operation = "Context", success = true, verified = true, instructions,
        allowedToolNames = allowed.Length == 0 ? new[] { "Read" } : allowed
    }).ToString();
}
