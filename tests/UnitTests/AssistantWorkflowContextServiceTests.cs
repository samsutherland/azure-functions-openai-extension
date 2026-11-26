// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ClientModel;
using Azure;
using Azure.Data.Tables;
using Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;
using Microsoft.Azure.WebJobs.Extensions.OpenAI.Models;
using Moq;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;
using Xunit;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Tests.Assistants;

public partial class DefaultAssistantServiceTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task ContextRefreshesAfterToolsWithoutStoringSnapshotsAndBlocksHiddenCalls(bool failRefresh, bool hidden, bool internalOnly)
    {
        const string assistant = "context-test";
        var attribute = new AssistantPostAttribute(assistant, "Change my plan")
        {
            WorkflowContextOperation = internalOnly ? null : "Context", WorkflowContextArguments = "secret-header",
            ToolResponseOperations = "Write;Hidden"
        };
        typeof(AssistantPostAttribute).GetProperty("ToolInternalOperations")?.SetValue(attribute, "Context");
        int contextCalls = 0, modelCalls = 0;
        var invoked = new List<ChatToolCall>();
        this.mockSkillInvoker.Setup(i => i.GetFunctionsDefinitions()).Returns(AssistantWorkflowContextTests.Tools());
        this.mockSkillInvoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatToolCall call, CancellationToken _) =>
            {
                if (call.FunctionName != "Context")
                {
                    invoked.Add(call);
                    return "{\"schemaVersion\":1,\"operation\":\"Write\",\"verified\":true,\"persisted\":true,\"success\":true,\"outcome\":\"saved\",\"canTellAthleteSaved\":true}";
                }
                contextCalls++;
                var input = JObject.Parse(JObject.Parse(call.FunctionArguments.ToString())["input"]!.Value<string>()!);
                Assert.Equal("secret-header", input["requestContext"]);
                Assert.Equal(assistant, input["assistantId"]);
                Assert.Equal("Change my plan", input["userMessage"]);
                if (failRefresh && contextCalls > 1) return "{}";
                var result = JObject.Parse(AssistantWorkflowContextTests.Result("snapshot-" + contextCalls, "Write"));
                result["toolArgumentOverrides"] = new JObject { ["userId"] = 1, ["externalUserId"] = "owner" };
                return result.ToString();
            });
        var chatClient = new Mock<ChatClient>();
        chatClient.Setup(c => c.CompleteChatAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<ChatMessage> messages, ChatCompletionOptions options, CancellationToken _) =>
            {
                modelCalls++;
                if (internalOnly) Assert.DoesNotContain(options.Tools, t => t.FunctionName == "Context");
                else
                {
                    Assert.Equal(new[] { "Write" }, options.Tools.Select(t => t.FunctionName));
                    Assert.Contains(messages, m => m is SystemChatMessage && m.Content.Any(c => c.Text == "snapshot-" + modelCalls));
                }
                Assert.DoesNotContain(messages, m => m.Content.Any(c => c.Text?.Contains("secret-header", StringComparison.Ordinal) == true));
                ChatCompletion response = modelCalls == 1 ? Completion("Everything is saved on your watch!",
                    ChatToolCall.CreateFunctionToolCall("write", internalOnly ? "Context" : hidden ? "Hidden" : "Write",
                        BinaryData.FromString("{\"input\":\"{\\\"userId\\\":2,\\\"externalUserId\\\":\\\"victim\\\",\\\"date\\\":\\\"2026-11-26\\\"}\"}")))
                    : Completion("Everything is saved on your watch!");
                return ClientResult.FromValue(response, new Mock<System.ClientModel.Primitives.PipelineResponse>().Object);
            });
        var factory = this.mockOpenAIClientFactory.Object;
        var cache = (System.Collections.Concurrent.ConcurrentDictionary<string, (ChatClient, string, string)>)
            typeof(OpenAIClientFactory).GetField("chatClients", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(factory)!;
        cache[""] = (chatClient.Object, "synthetic", "synthetic");
        cache["OpenAI"] = (chatClient.Object, "synthetic", "synthetic");
        var stored = new List<ChatMessageTableEntity>();
        this.mockTableClient.Setup(t => t.QueryAsync<TableEntity>(It.IsAny<string>(), null, null, It.IsAny<CancellationToken>()))
            .Returns(MockAsyncPageable<TableEntity>.Create(new[]
            {
                new TableEntity(assistant, AssistantStateEntity.FixedRowKeyValue) { ["Exists"] = true,
                    ["CreatedAt"] = DateTime.UtcNow.AddDays(-1), ["LastUpdatedAt"] = DateTime.UtcNow.AddDays(-1), ["TotalMessages"] = 0, ["TotalTokens"] = 0 }
            }));
        this.mockTableClient.Setup(t => t.SubmitTransactionAsync(It.IsAny<IEnumerable<TableTransactionAction>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<TableTransactionAction>, CancellationToken>((actions, _) => stored.AddRange(actions.Select(a => a.Entity).OfType<ChatMessageTableEntity>()))
            .ReturnsAsync(Response.FromValue<IReadOnlyList<Response>>(Array.Empty<Response>(), new Mock<Response>().Object));
        var service = new DefaultAssistantService(factory, this.mockAzureComponentFactory.Object, this.mockConfiguration.Object,
            this.mockSkillInvoker.Object, this.mockLoggerFactory.Object);
        typeof(DefaultAssistantService).GetField("tableClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(service, this.mockTableClient.Object);
        var state = await service.PostMessageAsync(attribute, CancellationToken.None);
        string expected = hidden || internalOnly ? AssistantWorkflowContext.BlockedResponse : failRefresh ? AssistantWorkflowContext.UnavailableResponse
            : "Your change was saved and verified in the app. Device delivery is not confirmed.";
        Assert.Contains(state.RecentMessages, m => m.Content == expected);
        Assert.Contains(stored, m => m.Role == "Assistant" && m.Content == expected);
        Assert.All(stored.Where(m => m.Role == "Assistant"), m => Assert.DoesNotContain("on your watch", m.Content));
        Assert.DoesNotContain(stored, m => m.Content.Contains("snapshot-", StringComparison.Ordinal) || m.Content.Contains("secret-header", StringComparison.Ordinal));
        Assert.Equal(internalOnly ? 0 : hidden ? 1 : 2, contextCalls);
        Assert.Equal(internalOnly || hidden || failRefresh ? 1 : 2, modelCalls);
        Assert.Equal(internalOnly || hidden ? 0 : 1, invoked.Count);
        if (!hidden && !internalOnly)
        {
            var actual = JObject.Parse(JObject.Parse(invoked.Single().FunctionArguments.ToString())["input"]!.Value<string>()!);
            Assert.Equal(1, actual["userId"]); Assert.Equal("owner", actual["externalUserId"]);
        }
        var calls = stored.SelectMany(m => m.ToolCalls ?? Array.Empty<ChatToolCall>()).ToList();
        Assert.Equal(calls.Select(c => c.Id), stored.Where(m => m.Role == "Tool").Select(m => m.Name));
        if (!internalOnly) Assert.DoesNotContain(calls, c => c.FunctionName == "Context");
        if (!hidden && !internalOnly) Assert.Equal(invoked.Single().FunctionArguments.ToString(), calls.Single().FunctionArguments.ToString());
        var telemetry = this.mockLogger.Invocations.Where(i => i.Method.Name == "Log").Select(i => i.Arguments[2]?.ToString() ?? "")
            .Where(message => message.StartsWith("Workflow context", StringComparison.Ordinal)).ToList();
        if (!internalOnly) Assert.Contains(telemetry, message => message.Contains("policyVersion=workflow-context-v1", StringComparison.Ordinal)
            && message.Contains("selectedToolCount=1", StringComparison.Ordinal) && message.Contains("latencyMs=", StringComparison.Ordinal));
        Assert.All(telemetry, message => { Assert.DoesNotContain("secret-header", message); Assert.DoesNotContain("snapshot-", message); });
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task BlankContextConfigurationPreservesExistingFlow(string operation)
    {
        this.mockTableClient.Setup(t => t.QueryAsync<TableEntity>(It.IsAny<string>(), null, null, It.IsAny<CancellationToken>()))
            .Returns(MockAsyncPageable<TableEntity>.Create(Array.Empty<TableEntity>()));
        var service = new DefaultAssistantService(this.mockOpenAIClientFactory.Object, this.mockAzureComponentFactory.Object,
            this.mockConfiguration.Object, this.mockSkillInvoker.Object, this.mockLoggerFactory.Object);
        typeof(DefaultAssistantService).GetField("tableClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(service, this.mockTableClient.Object);
        var state = await service.PostMessageAsync(new AssistantPostAttribute("assistant", "hello")
            { WorkflowContextOperation = operation, WorkflowContextArguments = "header" }, CancellationToken.None);
        Assert.False(state.Exists); Assert.Empty(state.RecentMessages);
        this.mockSkillInvoker.Verify(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>()), Times.Never());
        this.mockTableClient.Verify(t => t.QueryAsync<TableEntity>(It.IsAny<string>(), null, null, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task UnavailablePreflightNeverLoadsOrMutatesChat()
    {
        this.mockSkillInvoker.Setup(i => i.GetFunctionsDefinitions()).Returns(AssistantWorkflowContextTests.Tools());
        this.mockSkillInvoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>())).ReturnsAsync("{}");
        var service = new DefaultAssistantService(this.mockOpenAIClientFactory.Object, this.mockAzureComponentFactory.Object,
            this.mockConfiguration.Object, this.mockSkillInvoker.Object, this.mockLoggerFactory.Object);
        var state = await service.PostMessageAsync(new AssistantPostAttribute("victim-assistant", "attack")
            { WorkflowContextOperation = "Context", WorkflowContextArguments = "untrusted-header" }, CancellationToken.None);
        Assert.False(state.Exists);
        Assert.Equal(AssistantWorkflowContext.UnavailableResponse, Assert.Single(state.RecentMessages).Content);
        this.mockTableClient.Verify(t => t.QueryAsync<TableEntity>(It.IsAny<string>(), null, null, It.IsAny<CancellationToken>()), Times.Never());
        this.mockTableClient.Verify(t => t.SubmitTransactionAsync(It.IsAny<IEnumerable<TableTransactionAction>>(), It.IsAny<CancellationToken>()), Times.Never());
    }
}
