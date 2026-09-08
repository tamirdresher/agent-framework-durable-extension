// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.AI.DurableTask.State;

/// <summary>
/// Represents a single message within a durable agent state entry.
/// </summary>
internal sealed class DurableAgentStateMessage
{
    /// <summary>
    /// Gets the name of the author of this message.
    /// </summary>
    [JsonPropertyName("authorName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthorName { get; init; }

    /// <summary>
    /// Gets the timestamp when this message was created.
    /// </summary>
    [JsonPropertyName("createdAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>
    /// Gets the stable message identifier.
    /// </summary>
    [JsonPropertyName("messageId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageId { get; set; }

    /// <summary>
    /// Gets message-level additional properties from the schema's <c>extensionData</c> property.
    /// </summary>
    [JsonPropertyName("extensionData")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; init; }

    /// <summary>
    /// Gets the contents of this message.
    /// </summary>
    [JsonPropertyName("contents")]
    public IReadOnlyList<DurableAgentStateContent> Contents { get; init; } = [];

    /// <summary>
    /// Gets the role of the message sender (e.g., "user", "assistant", "system").
    /// </summary>
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    /// <summary>
    /// Gets unknown message properties that are outside the declared schema.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? UnknownProperties { get; set; }

    /// <summary>
    /// Creates a <see cref="DurableAgentStateMessage"/> from a <see cref="ChatMessage"/>.
    /// </summary>
    /// <param name="message">The <see cref="ChatMessage"/> to convert.</param>
    /// <param name="generatedMessageId">The stable identifier to use when the message does not already have one.</param>
    /// <param name="logger">The logger used to report safe unknown-content fallbacks.</param>
    /// <returns>A <see cref="DurableAgentStateMessage"/> representing the original message.</returns>
    public static DurableAgentStateMessage FromChatMessage(
        ChatMessage message,
        string? generatedMessageId = null,
        ILogger? logger = null)
        => FromChatMessage(message, generatedMessageId, includeContents: true, logger: logger);

    /// <summary>
    /// Creates a metadata-only durable message without transcript content.
    /// </summary>
    public static DurableAgentStateMessage FromChatMessageMetadata(
        ChatMessage message,
        string generatedMessageId,
        DateTimeOffset createdAt)
        => FromChatMessage(message, generatedMessageId, includeContents: false, createdAt);

    private static DurableAgentStateMessage FromChatMessage(
        ChatMessage message,
        string? generatedMessageId,
        bool includeContents,
        DateTimeOffset? defaultCreatedAt = null,
        ILogger? logger = null)
    {
        Dictionary<string, JsonElement>? additionalProperties = message.AdditionalProperties?
            .ToDictionary(
                pair => pair.Key,
                pair => JsonSerializer.SerializeToElement(
                    pair.Value,
                    DurableAgentJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object))));

        return new DurableAgentStateMessage()
        {
            CreatedAt = message.CreatedAt ?? defaultCreatedAt,
            AuthorName = message.AuthorName,
            MessageId = message.MessageId ?? generatedMessageId,
            AdditionalProperties = additionalProperties,
            Role = message.Role.ToString(),
            Contents = includeContents
                ? message.Contents.Select(content =>
                    DurableAgentStateContent.FromAIContent(content, logger)).ToList()
                : []
        };
    }

    /// <summary>
    /// Converts this <see cref="DurableAgentStateMessage"/> to a <see cref="ChatMessage"/>.
    /// </summary>
    /// <returns>A <see cref="ChatMessage"/> representing this message.</returns>
    public ChatMessage ToChatMessage()
    {
        AdditionalPropertiesDictionary? additionalProperties = this.AdditionalProperties is null
            ? null
            : new AdditionalPropertiesDictionary(
                this.AdditionalProperties.Select(pair =>
                    new KeyValuePair<string, object?>(pair.Key, pair.Value)));

        return new ChatMessage()
        {
            CreatedAt = this.CreatedAt,
            AuthorName = this.AuthorName,
            MessageId = this.MessageId,
            AdditionalProperties = additionalProperties,
            Contents = this.Contents.Select(c => c.ToAIContent()).ToList(),
            Role = new(this.Role)
        };
    }

    /// <summary>
    /// Converts this message to model context, omitting provider-specific reasoning content.
    /// </summary>
    /// <remarks>
    /// Reasoning is retained in durable storage for schema fidelity but is not replayed because it
    /// can be provider-specific, rejected by another API, or expose chain-of-thought. A message with
    /// other content keeps that content; a reasoning-only or metadata-only message has no model
    /// payload and returns <see langword="null"/>.
    /// </remarks>
    public ChatMessage? ToReplayableChatMessage()
    {
        List<DurableAgentStateContent> replayableContents =
            this.Contents.Where(content => content is not DurableAgentStateTextReasoningContent).ToList();
        if (replayableContents.Count == 0)
        {
            return null;
        }

        AdditionalPropertiesDictionary? additionalProperties = this.AdditionalProperties is null
            ? null
            : new AdditionalPropertiesDictionary(
                this.AdditionalProperties.Select(pair =>
                    new KeyValuePair<string, object?>(pair.Key, pair.Value)));

        return new ChatMessage
        {
            CreatedAt = this.CreatedAt,
            AuthorName = this.AuthorName,
            MessageId = this.MessageId,
            AdditionalProperties = additionalProperties,
            Contents = replayableContents.ConvertAll(static content => content.ToAIContent()),
            Role = new(this.Role),
        };
    }
}
