// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ClientModel;
using Azure;
using Azure.Data.Tables;
using Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;
using Microsoft.Azure.WebJobs.Extensions.OpenAI.Models;
using Moq;
using OpenAI.Chat;
using Xunit;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Tests.Assistants;

public partial class DefaultAssistantServiceTests
{
    [Fact]
    public async Task PrerequisiteRecovery_RepairsOnceAndRetiresStaleConfirmationInStoredAndReturnedMessages()
    {
        const string id = "recovery-test";
        const string readName = "GetTrainingSetupStatusSkill";
        const string repairName = "ConfirmTrainingSetupSkill";
        const string planName = "GenerateWeeklyPlanSkill";
        ChatToolCall Call(string callId, string name) => ChatToolCall.CreateFunctionToolCall(callId, name, BinaryData.FromString("{\"userId\":1}"));
        var attribute = new AssistantPostAttribute(id, "Yes, that goal and schedule are right")
        {
            ToolWorkflowDependencies = readName + ">" + planName,
            ToolResponsePrerequisites = readName,
            ToolWorkflowRecoveryReads = readName + ">" + repairName
        };
        var completions = new Queue<ChatCompletion>(new[]
        {
            Completion("Everything is saved and synced.", Call("read-1", readName)),
            Completion("Plan saved.", Call("plan-blocked", planName)),
            Completion("Confirmation saved.", Call("repair", repairName)),
            Completion("Setup checked.", Call("read-2", readName)),
            Completion("Plan saved.", Call("plan-retry", planName)),
            Completion("Everything is saved and synced.")
        });
        var chat = new Mock<ChatClient>();
        chat.Setup(c => c.CompleteChatAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ClientResult.FromValue(completions.Dequeue(), new Mock<System.ClientModel.Primitives.PipelineResponse>().Object));
        var factory = this.mockOpenAIClientFactory.Object;
        var cache = (System.Collections.Concurrent.ConcurrentDictionary<string, (ChatClient, string, string)>)typeof(OpenAIClientFactory)
            .GetField("chatClients", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(factory)!;
        cache[""] = cache["OpenAI"] = (chat.Object, "synthetic", "synthetic");
        int reads = 0;
        this.mockSkillInvoker.Setup(i => i.InvokeAsync(It.IsAny<ChatToolCall>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatToolCall call, CancellationToken _) =>
            {
                bool ready = call.FunctionName != readName || ++reads > 1;
                // AssistantSkillManager JSON-serializes the string returned by API skills.
                string payload = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    schemaVersion = 1, operation = call.FunctionName, actorId = 1, verified = true,
                    success = ready, outcome = !ready ? "needs_user_input" : call.FunctionName == repairName ? "confirmed" : "ready",
                    ready, persisted = call.FunctionName == repairName, nextQuestion = ready ? null : "Old confirmation?"
                });
                return Newtonsoft.Json.JsonConvert.SerializeObject(payload);
            });
        this.mockTableClient.Setup(t => t.QueryAsync<TableEntity>(It.IsAny<string>(), null, null, It.IsAny<CancellationToken>()))
            .Returns(MockAsyncPageable<TableEntity>.Create(new[] { new TableEntity(id, AssistantStateEntity.FixedRowKeyValue)
            {
                ["Exists"] = true, ["CreatedAt"] = DateTime.UtcNow.AddDays(-1), ["LastUpdatedAt"] = DateTime.UtcNow.AddDays(-1),
                ["TotalMessages"] = 0, ["TotalTokens"] = 0
            } }));
        var stored = new List<ChatMessageTableEntity>();
        this.mockTableClient.Setup(t => t.SubmitTransactionAsync(It.IsAny<IEnumerable<TableTransactionAction>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<TableTransactionAction>, CancellationToken>((actions, _) => stored.AddRange(actions.Select(a => a.Entity).OfType<ChatMessageTableEntity>()))
            .ReturnsAsync(Response.FromValue<IReadOnlyList<Response>>(Array.Empty<Response>(), new Mock<Response>().Object));
        var service = new DefaultAssistantService(factory, this.mockAzureComponentFactory.Object, this.mockConfiguration.Object,
            this.mockSkillInvoker.Object, this.mockLoggerFactory.Object);
        typeof(DefaultAssistantService).GetField("tableClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(service, this.mockTableClient.Object);

        var state = await service.PostMessageAsync(attribute, CancellationToken.None);

        Assert.Equal(2, reads);
        this.mockSkillInvoker.Verify(i => i.InvokeAsync(It.Is<ChatToolCall>(call => call.FunctionName == planName), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(state.RecentMessages, message => message.Content == "Your saved setup is ready.");
        Assert.All(stored.Where(message => message.Role == "Assistant"), message =>
        {
            Assert.DoesNotContain("Old confirmation?", message.Content);
            Assert.DoesNotContain("saved and synced", message.Content);
        });
        Assert.Equal(stored.SelectMany(message => message.ToolCalls ?? Array.Empty<ChatToolCall>()).Select(call => call.Id),
            stored.Where(message => message.Role == "Tool").Select(message => message.Name));
    }
}
