// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using Microsoft.Agents.AI.DurableTask.State;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.Agents.AI.DurableTask.Tests.Unit;

public sealed class DurableAgentStateRetentionTests
{
    [Fact]
    public void KeepAllNeverDeletes()
    {
        DurableAgentState state = CreateLargeState();
        int originalCount = state.Data.ConversationHistory.Count;

        int removed = DurableAgentStateRetention.Enforce(
            state,
            DurableAgentHistoryRetentionMode.KeepAll,
            500,
            DateTimeOffset.UtcNow,
            NullLogger.Instance,
            new AgentSessionId("agent", "session"));

        Assert.Equal(0, removed);
        Assert.Equal(originalCount, state.Data.ConversationHistory.Count);
    }

    [Fact]
    public void AutoEvictsOldestExchangeAndRecordsBoundedEvidence()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DurableAgentState state = CreateLargeState(now);

        int removed = DurableAgentStateRetention.Enforce(
            state,
            DurableAgentHistoryRetentionMode.Auto,
            2_500,
            now,
            NullLogger.Instance,
            new AgentSessionId("agent", "session"));

        Assert.True(removed > 0);
        Assert.DoesNotContain(state.Data.ConversationHistory, entry => entry.CorrelationId == "oldest");
        Assert.Contains(state.Data.ConversationHistory, entry => entry.CorrelationId == "newest");
        Assert.NotNull(state.Data.Truncation);
        Assert.Equal(removed, state.Data.Truncation.EvictedMessageCount);
        Assert.True(
            DurableAgentStateRetention.GetSerializedSize(state) <
            2_500 * DurableAgentStateRetention.HighWatermark);
    }

    [Fact]
    public void AutoPreservesSystemExchange()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DurableAgentState state = CreateLargeState(now);
        state.Data.ConversationHistory.Insert(
            0,
            CreateRequest("system", ChatRole.System, new string('s', 500), now.AddMinutes(-10)));

        _ = DurableAgentStateRetention.Enforce(
            state,
            DurableAgentHistoryRetentionMode.Auto,
            3_200,
            now,
            NullLogger.Instance,
            new AgentSessionId("agent", "session"));

        Assert.Contains(state.Data.ConversationHistory, entry => entry.CorrelationId == "system");
    }

    [Fact]
    public void AutoPreservesNewestExchange()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DurableAgentState state = CreateLargeState(now);

        _ = DurableAgentStateRetention.Enforce(
            state,
            DurableAgentHistoryRetentionMode.Auto,
            2_500,
            now,
            NullLogger.Instance,
            new AgentSessionId("agent", "session"));

        Assert.Contains(state.Data.ConversationHistory, entry => entry.CorrelationId == "newest");
    }

    [Fact]
    public void AutoPreservesPollableResponseWhenOlderHistoryCanMeetBudget()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DurableAgentState state = new();
        AddExchange(state, "old", new string('o', 400), now.AddMinutes(-5));
        AddExchange(state, "pollable", new string('a', 400), now.AddSeconds(-30));
        AddExchange(state, "newest", new string('b', 400), now);
        DurableAgentState protectedOnly = new();
        AddExchange(protectedOnly, "pollable", new string('a', 400), now.AddSeconds(-30));
        AddExchange(protectedOnly, "newest", new string('b', 400), now);
        protectedOnly.Data.Truncation = new DurableAgentStateTruncation
        {
            EvictedMessageCount = 2,
            FirstEvictedAt = now,
            LastEvictedAt = now,
        };
        int protectedSize = DurableAgentStateRetention.GetSerializedSize(protectedOnly);
        int maxStateBytes = (int)Math.Ceiling(
            protectedSize /
            DurableAgentStateRetention.LowWatermark) + 1;

        _ = DurableAgentStateRetention.Enforce(
            state,
            DurableAgentHistoryRetentionMode.Auto,
            maxStateBytes,
            now,
            NullLogger.Instance,
            new AgentSessionId("agent", "session"));

        Assert.DoesNotContain(state.Data.ConversationHistory, entry => entry.CorrelationId == "old");
        Assert.Contains(state.Data.ConversationHistory, entry => entry.CorrelationId == "pollable");
        Assert.Contains(state.Data.ConversationHistory, entry => entry.CorrelationId == "newest");
    }

    [Fact]
    public void AutoSacrificesOlderPollableResponseWhenRequired()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DurableAgentState state = new();
        AddExchange(state, "pollable-oldest", new string('a', 500), now.AddSeconds(-30));
        AddExchange(state, "pollable-middle", new string('b', 500), now.AddSeconds(-20));
        AddExchange(state, "newest", new string('c', 500), now);
        RecordingLogger logger = new();

        int removed = DurableAgentStateRetention.Enforce(
            state,
            DurableAgentHistoryRetentionMode.Auto,
            2_200,
            now,
            logger,
            new AgentSessionId("agent", "session"));

        Assert.True(removed > 0);
        Assert.DoesNotContain(state.Data.ConversationHistory, entry => entry.CorrelationId == "pollable-oldest");
        Assert.Contains(state.Data.ConversationHistory, entry => entry.CorrelationId == "newest");
        Assert.Equal(removed, state.Data.Truncation?.EvictedMessageCount);
        Assert.Contains(new EventId(15), logger.EventIds);
        Assert.True(
            DurableAgentStateRetention.GetSerializedSize(state) <
            2_200 * DurableAgentStateRetention.HighWatermark);
    }

    [Fact]
    public void AutoFailsRatherThanPersistProtectedStateOverSafeThreshold()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DurableAgentState state = new();
        AddExchange(state, "newest", new string('x', 2_000), now);

        DurableAgentStateSizeLimitExceededException exception =
            Assert.Throws<DurableAgentStateSizeLimitExceededException>(
                () => DurableAgentStateRetention.Enforce(
                    state,
                    DurableAgentHistoryRetentionMode.Auto,
                    500,
                    now,
                    NullLogger.Instance,
                    new AgentSessionId("agent", "session")));

        Assert.True(exception.StateSizeBytes >= 500 * DurableAgentStateRetention.HighWatermark);
        Assert.Equal(500, exception.MaxStateBytes);
        Assert.Equal(2, state.Data.ConversationHistory.Count);
    }

    [Fact]
    public void AutoRemovesToolCallAndResultAtomicallyWithExchange()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DurableAgentState state = new();
        DurableAgentStateRequest request = CreateRequest("tools", ChatRole.User, new string('a', 400), now.AddMinutes(-10));
        DurableAgentStateResponse response = DurableAgentStateResponse.FromResponse(
            "tools",
            new AgentResponse(
                new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent("call", "tool"),
                        new FunctionResultContent("call", "result"),
                    ])
                {
                    CreatedAt = now.AddMinutes(-10),
                }));
        state.Data.ConversationHistory.Add(request);
        state.Data.ConversationHistory.Add(response);
        AddExchange(state, "newest", new string('b', 400), now);

        _ = DurableAgentStateRetention.Enforce(
            state,
            DurableAgentHistoryRetentionMode.Auto,
            2_000,
            now,
            NullLogger.Instance,
            new AgentSessionId("agent", "session"));

        Assert.DoesNotContain(state.Data.ConversationHistory, entry => entry.CorrelationId == "tools");
        Assert.DoesNotContain(
            state.Data.ConversationHistory.SelectMany(entry => entry.Messages).SelectMany(message => message.Contents),
            content => content is DurableAgentStateFunctionCallContent or DurableAgentStateFunctionResultContent);
    }

    [Fact]
    public void AutoEvictsToolCallAndResultAcrossDifferentCorrelations()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DurableAgentState state = new();
        state.Data.ConversationHistory.Add(
            CreateRequest("call", ChatRole.User, "invoke", now.AddMinutes(-10)));
        state.Data.ConversationHistory.Add(
            CreateToolCallResponse("call", "shared-call", new string('a', 2_000), now.AddMinutes(-10)));
        state.Data.ConversationHistory.Add(
            CreateToolResultRequest("result", "shared-call", new string('b', 2_000), now.AddMinutes(-9)));
        state.Data.ConversationHistory.Add(
            CreateResponse("result", "after tool", now.AddMinutes(-9)));
        AddExchange(state, "newest", new string('c', 400), now);

        _ = DurableAgentStateRetention.Enforce(
            state,
            DurableAgentHistoryRetentionMode.Auto,
            5_000,
            now,
            NullLogger.Instance,
            new AgentSessionId("agent", "session"));

        Assert.DoesNotContain(
            state.Data.ConversationHistory,
            entry => entry.CorrelationId is "call" or "result");
        Assert.False(ContainsToolCallId(state, "shared-call"));
        Assert.Contains(state.Data.ConversationHistory, entry => entry.CorrelationId == "newest");
        Assert.True(
            DurableAgentStateRetention.GetSerializedSize(state) <
            5_000 * DurableAgentStateRetention.HighWatermark);
    }

    [Fact]
    public void AutoTreatsInterleavedToolCallsAsOneConnectedComponent()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DurableAgentState state = new();
        state.Data.ConversationHistory.Add(
            CreateRequest("calls", ChatRole.User, "invoke", now.AddMinutes(-10)));
        state.Data.ConversationHistory.Add(
            CreateToolCallResponse(
                "calls",
                now.AddMinutes(-10),
                ("call-a", new string('a', 1_000)),
                ("call-b", new string('b', 1_000))));
        state.Data.ConversationHistory.Add(
            CreateToolResultRequest("result-a", "call-a", new string('c', 1_000), now.AddMinutes(-9)));
        state.Data.ConversationHistory.Add(
            CreateResponse("result-a", "after a", now.AddMinutes(-9)));
        state.Data.ConversationHistory.Add(
            CreateToolResultRequest("result-b", "call-b", new string('d', 1_000), now.AddMinutes(-8)));
        state.Data.ConversationHistory.Add(
            CreateResponse("result-b", "after b", now.AddMinutes(-8)));
        AddExchange(state, "newest", new string('e', 400), now);

        _ = DurableAgentStateRetention.Enforce(
            state,
            DurableAgentHistoryRetentionMode.Auto,
            5_000,
            now,
            NullLogger.Instance,
            new AgentSessionId("agent", "session"));

        Assert.DoesNotContain(
            state.Data.ConversationHistory,
            entry => entry.CorrelationId is "calls" or "result-a" or "result-b");
        Assert.False(ContainsToolCallId(state, "call-a"));
        Assert.False(ContainsToolCallId(state, "call-b"));
        Assert.Contains(state.Data.ConversationHistory, entry => entry.CorrelationId == "newest");
    }

    [Fact]
    public void AutoProtectsWholeToolComponentWhenResultIsInNewestExchange()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DurableAgentState state = new();
        AddExchange(state, "filler", new string('f', 7_000), now.AddMinutes(-20));
        state.Data.ConversationHistory.Add(
            CreateRequest("call", ChatRole.User, "invoke", now.AddMinutes(-10)));
        state.Data.ConversationHistory.Add(
            CreateToolCallResponse("call", "protected-call", new string('a', 500), now.AddMinutes(-10)));
        state.Data.ConversationHistory.Add(
            CreateToolResultRequest("newest", "protected-call", new string('b', 500), now));
        state.Data.ConversationHistory.Add(
            CreateResponse("newest", "final", now));

        _ = DurableAgentStateRetention.Enforce(
            state,
            DurableAgentHistoryRetentionMode.Auto,
            5_000,
            now,
            NullLogger.Instance,
            new AgentSessionId("agent", "session"));

        Assert.DoesNotContain(state.Data.ConversationHistory, entry => entry.CorrelationId == "filler");
        Assert.Contains(state.Data.ConversationHistory, entry => entry.CorrelationId == "call");
        Assert.Contains(state.Data.ConversationHistory, entry => entry.CorrelationId == "newest");
        Assert.True(ContainsToolCallId(state, "protected-call"));
    }

    [Fact]
    public void AutoTreatsDuplicateToolIdsAsOneConservativeGroup()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DurableAgentState state = new();
        AddExchange(state, "filler", new string('f', 7_000), now.AddMinutes(-20));
        state.Data.ConversationHistory.Add(
            CreateToolCallResponse("first", "duplicate", new string('a', 500), now.AddMinutes(-10)));
        state.Data.ConversationHistory.Add(
            CreateToolCallResponse("second", "duplicate", new string('b', 500), now.AddMinutes(-5)));
        state.Data.ConversationHistory.Add(
            CreateToolResultRequest("newest", "duplicate", "result", now));
        state.Data.ConversationHistory.Add(
            CreateResponse("newest", "final", now));

        _ = DurableAgentStateRetention.Enforce(
            state,
            DurableAgentHistoryRetentionMode.Auto,
            5_000,
            now,
            NullLogger.Instance,
            new AgentSessionId("agent", "session"));

        Assert.DoesNotContain(state.Data.ConversationHistory, entry => entry.CorrelationId == "filler");
        Assert.Contains(state.Data.ConversationHistory, entry => entry.CorrelationId == "first");
        Assert.Contains(state.Data.ConversationHistory, entry => entry.CorrelationId == "second");
        Assert.Contains(state.Data.ConversationHistory, entry => entry.CorrelationId == "newest");
    }

    [Fact]
    public void AutoDoesNotConnectOrphanedToolContentWithDifferentIds()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DurableAgentState state = new();
        state.Data.ConversationHistory.Add(
            CreateToolCallResponse("orphan-call", "call-only", new string('a', 5_000), now.AddMinutes(-10)));
        state.Data.ConversationHistory.Add(
            CreateToolResultRequest("orphan-result", "result-only", new string('b', 500), now.AddMinutes(-5)));
        state.Data.ConversationHistory.Add(
            CreateResponse("orphan-result", "after orphan", now.AddMinutes(-5)));
        AddExchange(state, "newest", new string('c', 400), now);

        _ = DurableAgentStateRetention.Enforce(
            state,
            DurableAgentHistoryRetentionMode.Auto,
            4_000,
            now,
            NullLogger.Instance,
            new AgentSessionId("agent", "session"));

        Assert.DoesNotContain(state.Data.ConversationHistory, entry => entry.CorrelationId == "orphan-call");
        Assert.Contains(state.Data.ConversationHistory, entry => entry.CorrelationId == "orphan-result");
        Assert.True(ContainsToolCallId(state, "result-only"));
    }

    [Fact]
    public void AutoDoesNotConnectToolContentWithMissingIds()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DurableAgentState state = new();
        state.Data.ConversationHistory.Add(
            CreateToolCallResponse("missing-call", string.Empty, new string('a', 5_000), now.AddMinutes(-10)));
        state.Data.ConversationHistory.Add(
            CreateToolResultRequest("missing-result", string.Empty, new string('b', 500), now.AddMinutes(-5)));
        state.Data.ConversationHistory.Add(
            CreateResponse("missing-result", "after orphan", now.AddMinutes(-5)));
        AddExchange(state, "newest", new string('c', 400), now);

        _ = DurableAgentStateRetention.Enforce(
            state,
            DurableAgentHistoryRetentionMode.Auto,
            4_000,
            now,
            NullLogger.Instance,
            new AgentSessionId("agent", "session"));

        Assert.DoesNotContain(state.Data.ConversationHistory, entry => entry.CorrelationId == "missing-call");
        Assert.Contains(state.Data.ConversationHistory, entry => entry.CorrelationId == "missing-result");
    }

    [Fact]
    public void SerializedSizeIncludesSessionAndTruncation()
    {
        DurableAgentState state = new();
        int emptySize = DurableAgentStateRetention.GetSerializedSize(state);
        state.Data.Session = JsonSerializer.SerializeToElement(new { conversationId = new string('c', 100) });
        state.Data.Truncation = new DurableAgentStateTruncation
        {
            EvictedMessageCount = 2,
            FirstEvictedAt = DateTimeOffset.UtcNow,
            LastEvictedAt = DateTimeOffset.UtcNow,
        };

        int completeSize = DurableAgentStateRetention.GetSerializedSize(state);

        Assert.True(completeSize > emptySize + 100);
    }

    [Fact]
    public void PublicRetentionModesAreOnlyKeepAllAndAuto()
    {
        Assert.Equal(
            [nameof(DurableAgentHistoryRetentionMode.KeepAll), nameof(DurableAgentHistoryRetentionMode.Auto)],
            Enum.GetNames<DurableAgentHistoryRetentionMode>());
    }

    [Fact]
    public void AutoCanEvictOlderCorrelationlessCompaction()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DurableAgentState state = new();
        state.Data.ConversationHistory.Add(
            new DurableAgentStateCompaction
            {
                CreatedAt = now.AddMinutes(-5),
                Messages =
                [
                    DurableAgentStateMessage.FromChatMessage(
                        new ChatMessage(ChatRole.Assistant, new string('a', 1_000))),
                ],
            });
        state.Data.ConversationHistory.Add(
            new DurableAgentStateCompaction
            {
                CreatedAt = now,
                Messages =
                [
                    DurableAgentStateMessage.FromChatMessage(
                        new ChatMessage(ChatRole.Assistant, "newest")),
                ],
            });

        int removed = DurableAgentStateRetention.Enforce(
            state,
            DurableAgentHistoryRetentionMode.Auto,
            1_200,
            now,
            NullLogger.Instance,
            new AgentSessionId("agent", "session"));

        Assert.Equal(1, removed);
        DurableAgentStateCompaction remaining =
            Assert.IsType<DurableAgentStateCompaction>(Assert.Single(state.Data.ConversationHistory));
        Assert.Equal("newest", remaining.Messages[0].ToChatMessage().Text);
    }

    private static DurableAgentState CreateLargeState(DateTimeOffset? now = null)
    {
        DateTimeOffset current = now ?? DateTimeOffset.UtcNow;
        DurableAgentState state = new();
        AddExchange(state, "oldest", new string('a', 500), current.AddMinutes(-10));
        AddExchange(state, "middle", new string('b', 500), current.AddMinutes(-5));
        AddExchange(state, "newest", new string('c', 500), current);
        return state;
    }

    private static void AddExchange(
        DurableAgentState state,
        string correlationId,
        string content,
        DateTimeOffset createdAt)
    {
        state.Data.ConversationHistory.Add(
            CreateRequest(correlationId, ChatRole.User, content, createdAt));
        state.Data.ConversationHistory.Add(
            new DurableAgentStateResponse
            {
                CorrelationId = correlationId,
                CreatedAt = createdAt,
                Messages =
                [
                    DurableAgentStateMessage.FromChatMessage(
                        new ChatMessage(ChatRole.Assistant, content) { CreatedAt = createdAt }),
                ],
            });
    }

    private static DurableAgentStateRequest CreateRequest(
        string correlationId,
        ChatRole role,
        string content,
        DateTimeOffset createdAt)
    {
        return new DurableAgentStateRequest
        {
            CorrelationId = correlationId,
            CreatedAt = createdAt,
            Messages =
            [
                DurableAgentStateMessage.FromChatMessage(
                    new ChatMessage(role, content) { CreatedAt = createdAt }),
            ],
        };
    }

    private static DurableAgentStateResponse CreateResponse(
        string correlationId,
        string content,
        DateTimeOffset createdAt)
    {
        return new DurableAgentStateResponse
        {
            CorrelationId = correlationId,
            CreatedAt = createdAt,
            Messages =
            [
                DurableAgentStateMessage.FromChatMessage(
                    new ChatMessage(ChatRole.Assistant, content) { CreatedAt = createdAt }),
            ],
        };
    }

    private static DurableAgentStateResponse CreateToolCallResponse(
        string correlationId,
        string callId,
        string payload,
        DateTimeOffset createdAt)
        => CreateToolCallResponse(correlationId, createdAt, (callId, payload));

    private static DurableAgentStateResponse CreateToolCallResponse(
        string correlationId,
        DateTimeOffset createdAt,
        params (string CallId, string Payload)[] calls)
    {
        List<AIContent> contents = calls
            .Select(call => (AIContent)new FunctionCallContent(
                call.CallId,
                "tool",
                new Dictionary<string, object?> { ["payload"] = call.Payload }))
            .ToList();
        return new DurableAgentStateResponse
        {
            CorrelationId = correlationId,
            CreatedAt = createdAt,
            Messages =
            [
                DurableAgentStateMessage.FromChatMessage(
                    new ChatMessage(ChatRole.Assistant, contents) { CreatedAt = createdAt }),
            ],
        };
    }

    private static DurableAgentStateRequest CreateToolResultRequest(
        string correlationId,
        string callId,
        object result,
        DateTimeOffset createdAt)
    {
        return new DurableAgentStateRequest
        {
            CorrelationId = correlationId,
            CreatedAt = createdAt,
            Messages =
            [
                DurableAgentStateMessage.FromChatMessage(
                    new ChatMessage(
                        ChatRole.Tool,
                        [new FunctionResultContent(callId, result)])
                    {
                        CreatedAt = createdAt,
                    }),
            ],
        };
    }

    private static bool ContainsToolCallId(DurableAgentState state, string callId)
    {
        return state.Data.ConversationHistory
            .SelectMany(entry => entry.Messages)
            .SelectMany(message => message.Contents)
            .Any(content => content switch
            {
                DurableAgentStateFunctionCallContent functionCall => functionCall.CallId == callId,
                DurableAgentStateFunctionResultContent functionResult => functionResult.CallId == callId,
                _ => false,
            });
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<EventId> EventIds { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            this.EventIds.Add(eventId);
        }
    }
}
