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
    [InlineData("write_unknown")]
    [InlineData("write_saved")]
    [InlineData("status")]
    [InlineData("advice")]
    public async Task SavedDetailBoundarySuppressesInterimAndFinalModelClaims(string mode)
    {
        const string assistant = "saved-details";
        const string misleading = "Your saved recovery ride is 30 minutes.";
        var attribute = new AssistantPostAttribute(assistant, "What are the saved workout details for 2026-11-26?")
        {
            WorkflowContextOperation = "Context", WorkflowContextArguments = "private-header",
            ToolInternalOperations = "Context", ToolResponseOperations = "Write", ToolResponsePrerequisites = "GetTrainingSetupStatusSkill"
        };
        int loads = 0, requests = 0;
        var invoked = new List<ChatToolCall>();
        this.mockSkillInvoker.Setup(i => i.GetFunctionsDefinitions()).Returns(AssistantWorkflowContextTests.Tools());
        this.mockSkillInvoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatToolCall call, CancellationToken _) =>
            {
                if (call.FunctionName != "Context")
                {
                    invoked.Add(call);
                    return mode == "write_saved" ? "{\"schemaVersion\":1,\"operation\":\"Write\",\"verified\":true,\"persisted\":true,\"success\":true,\"outcome\":\"saved\",\"canTellAthleteSaved\":true}" : "{}";
                }
                loads++;
                var snapshot = JObject.Parse(AssistantWorkflowContextTests.Result("saved-details-snapshot", "Read", "Write"));
                snapshot["toolArgumentOverrides"] = new JObject { ["userId"] = 1 };
                snapshot["requireDetailsEvidence"] = mode != "advice" && (mode != "cleared" || loads == 1);
                snapshot["responseDetails"] = (mode == "unavailable" || mode == "cleared" && loads > 1) ? null : AssistantSavedWorkoutDetailsResponseTests.Facts();
                if (mode == "changed" && loads > 1)
                { snapshot["responseDetails"]!["durationSeconds"] = 3000; snapshot["responseDetails"]!["revision"] = "sha256:" + new string('b', 64); }
                if (mode == "selection") snapshot["responseDetails"] = JObject.FromObject(new { schemaVersion = 1, operation = "GetWorkflowContextSkill", source = "saved_workout_detail", actorId = 1, verified = false, outcome = "unavailable", reason = "date_required" });
                snapshot["requireOperationEvidence"] = mode == "status";
                snapshot["responsePrerequisite"] = AssistantWorkflowContextTests.Prerequisite(false, "What is your goal?");
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
                else if (requests == 1 && mode is "read" or "cleared" or "changed" or "write_unknown" or "write_saved" or "call_limit")
                {
                    var calls = Enumerable.Range(0, mode == "call_limit" ? 60 : 1).Select(i =>
                        ChatToolCall.CreateFunctionToolCall("call-" + i, mode.StartsWith("write_", StringComparison.Ordinal) ? "Write" : "Read", BinaryData.FromString("{}"))).ToArray();
                    reply = Completion(misleading, calls);
                }
                else reply = Completion(mode == "advice" ? "Keep tomorrow easy and review how you feel." : misleading);
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
            "advice" => "Keep tomorrow easy and review how you feel.",
            "status" => "Your saved goal and availability." + Environment.NewLine + Environment.NewLine + "What is your goal?",
            "write_saved" => "Your saved goal and availability." + Environment.NewLine + Environment.NewLine + "What is your goal?" + Environment.NewLine + Environment.NewLine + "Your change was saved and verified in the app. Device delivery is not confirmed.",
            "write_unknown" or "call_limit" => AssistantToolResponsePolicy.UnverifiedResponse,
            "changed" => AssistantSavedWorkoutDetailsResponseTests.Expected.Replace("2700 seconds (45 minutes)", "3000 seconds (50 minutes)", StringComparison.Ordinal),
            "selection" => "Which date should I check for the saved workout?",
            "unavailable" or "cleared" => AssistantSavedWorkoutDetailsResponse.UnavailableResponse,
            _ => AssistantSavedWorkoutDetailsResponseTests.Expected
        };
        Assert.Equal(expected, state.RecentMessages.Last().Content);
        Assert.Equal(expected, stored.Last(m => m.Role == "Assistant").Content);
        Assert.All(stored.Where(m => m.Role == "Assistant"), m => Assert.DoesNotContain("30 minutes", m.Content));
        Assert.DoesNotContain(stored, m => m.Content.Contains("saved-details-snapshot", StringComparison.Ordinal) || m.Content.Contains("private-header", StringComparison.Ordinal));
        var callsStored = stored.SelectMany(m => m.ToolCalls ?? Array.Empty<ChatToolCall>()).ToList();
        Assert.Equal(callsStored.Select(c => c.Id), stored.Where(m => m.Role == "Tool").Select(m => m.Name));
        Assert.DoesNotContain(callsStored, c => c.FunctionName == "Context");
        Assert.Equal(mode is "read" or "cleared" or "changed" or "write_unknown" or "write_saved" ? 1 : 0, invoked.Count);
        Assert.Equal(mode is "read" or "cleared" or "changed" or "write_unknown" or "write_saved" ? 2 : 1, requests);
        Assert.Equal(requests, loads);
    }
}
