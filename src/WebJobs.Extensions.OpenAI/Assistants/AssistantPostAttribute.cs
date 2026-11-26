// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Azure.WebJobs.Description;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;

[Binding]
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class AssistantPostAttribute : AssistantBaseAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AssistantPostAttribute"/> class.
    /// </summary>
    /// <param name="id">The assistant identifier.</param>
    /// <param name="userMessage">The user message.</param>
    public AssistantPostAttribute(string id, string userMessage)
    {
        this.Id = id;
        this.UserMessage = userMessage;
    }

    /// <summary>
    /// Gets the ID of the assistant to update.
    /// </summary>
    [AutoResolve]
    public string Id { get; }

    /// <summary>
    /// Gets or sets the user message to OpenAI.
    /// </summary>
    [AutoResolve]
    public string UserMessage { get; }

    /// <summary>
    /// Configuration section name for the table settings for chat storage.
    /// </summary>
    public string? ChatStorageConnectionSetting { get; set; }

    /// <summary>
    /// Table collection name for chat storage.
    /// </summary>
    [AutoResolve]
    public string CollectionName { get; set; } = "ChatState";

    /// <summary>
    /// Optional registered read operation that authenticates the opaque request context
    /// and returns version-1 ephemeral instructions, allowed tools and trusted identity overrides.
    /// Null preserves existing behavior. Invalid or unavailable context fails closed.
    /// </summary>
    [AutoResolve]
    public string? WorkflowContextOperation { get; set; }

    /// <summary>
    /// Opaque server-bound request context for the internal context operation. Never
    /// populated from model arguments, exposed to the model or stored in chat history.
    /// </summary>
    [AutoResolve]
    public string? WorkflowContextArguments { get; set; }

    /// <summary>
    /// Gets or sets declared tool dependencies as a semicolon-separated list of
    /// <c>prerequisite&gt;dependent</c> operation pairs.
    /// </summary>
    public string? ToolWorkflowDependencies { get; set; }

    /// <summary>
    /// Semicolon-separated read&gt;repair pairs. A verified, persisted confirmation
    /// for the same actor permits one identical read retry per processing invocation.
    /// Configure only read operations; unknown writes are never retried by this rule.
    /// </summary>
    public string? ToolWorkflowRecoveryReads { get; set; }


    /// <summary>
    /// Gets or sets the semicolon-separated tool names whose turns use verified
    /// result responses instead of model text. Null preserves normal chat rendering.
    /// Only version 1 operation outcomes can establish completion.
    /// </summary>
    public string? ToolResponseOperations { get; set; }

    /// <summary>
    /// Gets or sets semicolon-separated prerequisite-read names. Incomplete,
    /// failed or unverified reads use deterministic responses; verified ready
    /// reads preserve normal coaching and downstream operation rendering.
    /// </summary>
    public string? ToolResponsePrerequisites { get; set; }


}
