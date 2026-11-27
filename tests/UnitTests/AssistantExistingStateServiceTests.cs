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
    [InlineData("no_tool")]
    [InlineData("read")]
    [InlineData("truncated")]
    [InlineData("call_limit")]
    [InlineData("unavailable")]
    [InlineData("cleared")]
    [InlineData("changed")]
    [InlineData("selection")]
    [InlineData("incidental_setup")]
    [InlineData("mixed_setup")]
    [InlineData("mixed_call")]
    [InlineData("bindings")]
    [InlineData("write_unknown")]
    [InlineData("write_saved")]
    [InlineData("status")]
    [InlineData("advice")]
    public async Task ExistingStateBoundaryUsesOnlyFreshObservedFacts(string mode)
    {
        const string assistant = "existing-state";
        const string misleading = "I confirmed your setup and regenerated your plan.";
        var attribute = new AssistantPostAttribute(assistant, "Keep my already confirmed setup and use the existing plan for 2026-12-02.")
        {
            WorkflowContextOperation = "Context", WorkflowContextArguments = "private-header",
            ToolInternalOperations = "Context", ToolResponseOperations = "Write", ToolResponsePrerequisites = "GetTrainingSetupStatusSkill"
        };
        int loads = 0, requests = 0;
        var invoked = new List<ChatToolCall>();
        var tools = AssistantWorkflowContextTests.Tools();
        tools.Add(ChatTool.CreateFunctionTool("GetTrainingSetupStatusSkill", "Read setup", BinaryData.FromString("{}")));
        tools.Add(ChatTool.CreateFunctionTool("AddManualActivitySkill", "Log activity", BinaryData.FromString("{}")));
        this.mockSkillInvoker.Setup(i => i.GetFunctionsDefinitions()).Returns(tools);
        this.mockSkillInvoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatToolCall call, CancellationToken _) =>
            {
                if (call.FunctionName != "Context")
                {
                    invoked.Add(call);
                    if (call.FunctionName == "GetTrainingSetupStatusSkill") return AssistantWorkflowContextTests.Prerequisite(false, "What is your goal?").ToString();
                    return mode == "write_saved" ? "{\"schemaVersion\":1,\"operation\":\"Write\",\"verified\":true,\"persisted\":true,\"success\":true,\"outcome\":\"saved\",\"canTellAthleteSaved\":true}" : "{}";
                }
                loads++;
                var snapshot = JObject.Parse(AssistantWorkflowContextTests.Result("existing-state-snapshot", "Read", "Write", "GetTrainingSetupStatusSkill", "AddManualActivitySkill"));
                snapshot["toolArgumentOverrides"] = new JObject { ["userId"] = 1 };
                snapshot["responseObservation"] = (mode == "unavailable" || mode == "cleared" && loads > 1) ? null : AssistantExistingStateResponseTests.Facts();
                if (mode == "changed" && loads > 1)
                { snapshot["responseObservation"]!["plan"]!["requestedDate"] = "2026-12-03"; }
                if (mode == "selection") snapshot["responseObservation"] = JObject.FromObject(new { schemaVersion = 1, operation = "GetWorkflowContextSkill", source = "existing_training_state", actorId = 1, verified = false, outcome = "unavailable", reason = "date_required" });
                snapshot["requireObservationEvidence"] = mode is not ("advice" or "incidental_setup" or "mixed_setup" or "mixed_call") && (mode != "cleared" || loads == 1);
                snapshot["requireOperationEvidence"] = mode is "status" or "mixed_setup";
                snapshot["responsePrerequisite"] = AssistantWorkflowContextTests.Prerequisite(mode is not ("status" or "write_saved" or "incidental_setup" or "mixed_setup" or "mixed_call"), "What is your goal?");
                if (mode == "bindings") snapshot["toolOperationArgumentOverrides"] = JObject.Parse("{\"Write\":{\"regenerate\":false,\"reuseOnly\":true,\"weekStart\":\"2026-12-02\"}}");
                return snapshot.ToString();
            });
        var chat = new Mock<ChatClient>();
        chat.Setup(c => c.CompleteChatAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<ChatMessage> _, ChatCompletionOptions options, CancellationToken token) =>
            {
                requests++;
                ChatCompletion reply;
                if (mode == "truncated") reply = OpenAIChatModelFactory.ChatCompletion(id: "truncated", finishReason: ChatFinishReason.Length,
                    content: new ChatMessageContent(misleading), usage: OpenAIChatModelFactory.ChatTokenUsage(1, 1, 2));
                else if (requests == 1 && mode is "read" or "cleared" or "changed" or "write_unknown" or "write_saved" or "call_limit" or "incidental_setup" or "mixed_setup" or "mixed_call" or "bindings")
                {
                    var calls = Enumerable.Range(0, mode == "call_limit" ? 60 : 1).Select(i =>
                        ChatToolCall.CreateFunctionToolCall("call-" + i, mode is "incidental_setup" or "mixed_setup" ? "GetTrainingSetupStatusSkill" : mode.StartsWith("write_", StringComparison.Ordinal) || mode == "bindings" ? "Write" : "Read", BinaryData.FromString(mode == "bindings" ? "{\"input\":\"{\\\"regenerate\\\":true,\\\"weekStart\\\":\\\"2027-01-01\\\"}\"}" : "{}"))).ToArray();
                    if (mode == "incidental_setup") calls = calls.Append(ChatToolCall.CreateFunctionToolCall("manual", "AddManualActivitySkill", BinaryData.FromString("{}"))).ToArray();
                    if (mode == "mixed_call") calls = new[] { ChatToolCall.CreateFunctionToolCall("setup", "GetTrainingSetupStatusSkill", BinaryData.FromString("{}")), ChatToolCall.CreateFunctionToolCall("write", "Write", BinaryData.FromString("{}")) };
                    reply = Completion(mode == "incidental_setup" ? "Checking your activity." : misleading, calls);
                }
                else reply = Completion(mode is "advice" or "incidental_setup" ? "Keep tomorrow easy and review how you feel." : misleading);
                return ClientResult.FromValue(reply, new Mock<System.ClientModel.Primitives.PipelineResponse>().Object);
            });
        var factory = this.mockOpenAIClientFactory.Object;
        var cache = (System.Collections.Concurrent.ConcurrentDictionary<string, (ChatClient, string, string)>)
            typeof(OpenAIClientFactory).GetField("chatClients", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(factory)!;
        cache[""] = (chat.Object, "synthetic", "synthetic"); cache["OpenAI"] = (chat.Object, "synthetic", "synthetic");
        this.mockTableClient.Setup(t => t.QueryAsync<TableEntity>(It.IsAny<string>(), null, null, It.IsAny<CancellationToken>()))
            .Returns(MockAsyncPageable<TableEntity>.Create(new[] { new TableEntity(assistant, AssistantStateEntity.FixedRowKeyValue)
                { ["Exists"] = true, ["CreatedAt"] = DateTime.UtcNow.AddDays(-1), ["LastUpdatedAt"] = DateTime.UtcNow.AddDays(-1), ["TotalMessages"] = 0, ["TotalTokens"] = 0 } }));
        var stored = new List<ChatMessageTableEntity>();
        this.mockTableClient.Setup(t => t.SubmitTransactionAsync(It.IsAny<IEnumerable<TableTransactionAction>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<TableTransactionAction>, CancellationToken>((actions, _) => stored.AddRange(actions.Select(a => a.Entity).OfType<ChatMessageTableEntity>()))
            .ReturnsAsync(Response.FromValue<IReadOnlyList<Response>>(Array.Empty<Response>(), new Mock<Response>().Object));
        var service = new DefaultAssistantService(factory, this.mockAzureComponentFactory.Object, this.mockConfiguration.Object,
            this.mockSkillInvoker.Object, this.mockLoggerFactory.Object);
        typeof(DefaultAssistantService).GetField("tableClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(service, this.mockTableClient.Object);
        var state = await service.PostMessageAsync(attribute, CancellationToken.None);
        string expected = mode switch
        {
            "advice" or "incidental_setup" => "Keep tomorrow easy and review how you feel.",
            "status" or "mixed_setup" => "Your saved goal and availability." + Environment.NewLine + Environment.NewLine + "What is your goal?",
            "write_saved" => "Your saved goal and availability." + Environment.NewLine + Environment.NewLine + "What is your goal?" + Environment.NewLine + Environment.NewLine + "Your change was saved and verified in the app. Device delivery is not confirmed.",
            "write_unknown" or "call_limit" or "bindings" or "mixed_call" => AssistantToolResponsePolicy.UnverifiedResponse,
            "changed" => AssistantExistingStateResponseTests.Expected.Replace("2026-12-02", "2026-12-03", StringComparison.Ordinal),
            "selection" => AssistantToolResponsePolicy.UnverifiedResponse,
            "unavailable" or "cleared" => AssistantToolResponsePolicy.UnverifiedResponse,
            _ => AssistantExistingStateResponseTests.Expected
        };
        Assert.Equal(expected, state.RecentMessages.Last().Content);
        Assert.Equal(expected, stored.Last(m => m.Role == "Assistant").Content);
        Assert.All(stored.Where(m => m.Role == "Assistant"), m => Assert.DoesNotContain("regenerated", m.Content));
        Assert.DoesNotContain(stored, m => m.Content.Contains("existing-state-snapshot", StringComparison.Ordinal) || m.Content.Contains("private-header", StringComparison.Ordinal));
        var callsStored = stored.SelectMany(m => m.ToolCalls ?? Array.Empty<ChatToolCall>()).ToList();
        Assert.Equal(callsStored.Select(c => c.Id), stored.Where(m => m.Role == "Tool").Select(m => m.Name));
        Assert.DoesNotContain(callsStored, c => c.FunctionName == "Context");
        if (mode == "bindings")
        {
            var actual = JObject.Parse(JObject.Parse(invoked.Single().FunctionArguments.ToString())["input"]!.Value<string>()!);
            Assert.False(actual["regenerate"]!.Value<bool>()); Assert.True(actual["reuseOnly"]!.Value<bool>()); Assert.Equal("2026-12-02", actual["weekStart"]);
            Assert.Equal(invoked.Single().FunctionArguments.ToString(), callsStored.Single().FunctionArguments.ToString());
        }
        Assert.Equal(mode is "incidental_setup" or "mixed_call" ? 2 : mode is "read" or "cleared" or "changed" or "write_unknown" or "write_saved" or "mixed_setup" or "bindings" ? 1 : 0, invoked.Count);
        Assert.Equal(mode is "read" or "cleared" or "changed" or "write_unknown" or "write_saved" or "incidental_setup" or "mixed_setup" or "mixed_call" or "bindings" ? 2 : 1, requests);
        Assert.Equal(requests, loads);
    }
}
