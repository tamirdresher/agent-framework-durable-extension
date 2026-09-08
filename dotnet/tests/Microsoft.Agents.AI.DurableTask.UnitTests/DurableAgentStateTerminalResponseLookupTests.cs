// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using Microsoft.Agents.AI.DurableTask.State;
using Microsoft.Extensions.AI;

namespace Microsoft.Agents.AI.DurableTask.Tests.Unit;

public sealed class DurableAgentStateTerminalResponseLookupTests
{
    [Fact]
    public void NonTerminalEntriesWithMatchingCorrelationAreIgnored()
    {
        DurableAgentStateResponse expected = CreateResponse("same", "terminal");
        DurableAgentStateEntry[] history =
        [
            new DurableAgentStateRequest
            {
                CorrelationId = "same",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            new DurableAgentStateCompaction
            {
                CorrelationId = "same",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            expected,
        ];

        DurableAgentStateResponse? actual =
            DurableAgentStateTerminalResponseLookup.FindUniqueTerminalResponse(history, "same");

        Assert.Same(expected, actual);
    }

    [Fact]
    public void NoTerminalResponseReturnsNull()
    {
        DurableAgentStateEntry[] history =
        [
            new DurableAgentStateRequest
            {
                CorrelationId = "pending",
                CreatedAt = DateTimeOffset.UtcNow,
            },
        ];

        Assert.Null(
            DurableAgentStateTerminalResponseLookup.FindUniqueTerminalResponse(
                history,
                "pending"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SerializedDuplicateTerminalOrderAlwaysThrowsCorruption(bool reverseOrder)
    {
        DurableAgentState state = new();
        DurableAgentStateResponse success = CreateResponse("duplicate", "success");
        DurableAgentStateErrorResponse error = CreateErrorResponse("duplicate", "failure");
        state.Data.ConversationHistory.Add(reverseOrder ? error : success);
        state.Data.ConversationHistory.Add(reverseOrder ? success : error);

        string json = JsonSerializer.Serialize(
            state,
            DurableAgentStateJsonContext.Default.DurableAgentState);
        DurableAgentState restored = Assert.IsType<DurableAgentState>(
            JsonSerializer.Deserialize(
                json,
                DurableAgentStateJsonContext.Default.DurableAgentState));

        DurableAgentStateCorruptionException exception =
            Assert.Throws<DurableAgentStateCorruptionException>(
                () => DurableAgentStateTerminalResponseLookup.FindUniqueTerminalResponse(
                    restored.Data.ConversationHistory,
                    "duplicate"));

        Assert.Equal("duplicate", exception.CorrelationId);
        Assert.Equal(2, exception.TerminalResponseCount);
        Assert.DoesNotContain("success", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("failure", exception.Message, StringComparison.Ordinal);
    }

    private static DurableAgentStateResponse CreateResponse(string correlationId, string text)
    {
        return new DurableAgentStateResponse
        {
            CorrelationId = correlationId,
            CreatedAt = DateTimeOffset.UtcNow,
            Messages =
            [
                DurableAgentStateMessage.FromChatMessage(
                    new ChatMessage(ChatRole.Assistant, text)),
            ],
        };
    }

    private static DurableAgentStateErrorResponse CreateErrorResponse(string correlationId, string text)
    {
        return new DurableAgentStateErrorResponse
        {
            CorrelationId = correlationId,
            CreatedAt = DateTimeOffset.UtcNow,
            Messages =
            [
                DurableAgentStateMessage.FromChatMessage(
                    new ChatMessage(ChatRole.Assistant, text)),
            ],
        };
    }
}
