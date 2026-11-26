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
