// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.DurableTask.State;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace Microsoft.Agents.AI.DurableTask.Tests.Unit;

public sealed class AgentEntityHistoryTests
{
    private static readonly TimeSpan s_stageTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task EntityExecutionUsesDurableProviderAndPersistsSessionAsync()
    {
        RecordingChatClient client = new();
        ChatClientAgent agent = new(client, name: "agent");
        DurableAgentState initialState = CreateStateWithExchange("old", "old request", "old response");

        DurableAgentState persisted = await RunEntityAsync(
            agent,
            initialState,
            new RunRequest("new request") { CorrelationId = "new" });

        Assert.Equal(["old request", "old response", "new request"], client.LastMessages.Select(message => message.Text));
        Assert.Equal(4, persisted.Data.ConversationHistory.Count);
        Assert.NotNull(persisted.Data.Session);
        Assert.DoesNotContain(
            nameof(InMemoryChatHistoryProvider),
            persisted.Data.Session.Value.GetProperty("stateBag").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task WrappedAgentDoesNotReplayTranscriptOutsideProviderAsync()
    {
        RecordingChatClient client = new();
        ChatClientAgent chatAgent = new(client, name: "agent");
        AIAgent wrappedAgent = new TestDelegatingAgent(chatAgent);
        DurableAgentState initialState = CreateStateWithExchange("old", "old request", "old response");

        DurableAgentState persisted = await RunEntityAsync(
            wrappedAgent,
            initialState,
            new RunRequest("new request") { CorrelationId = "new" });

        Assert.Equal(3, client.LastMessages.Count);
        Assert.Equal(4, persisted.Data.ConversationHistory.Count);
    }

    [Fact]
    public async Task CustomProviderOwnsTranscriptAndEntityStoresMetadataRequestAndDeliveryResponseAsync()
    {
        RecordingChatClient client = new();
        RecordingHistoryProvider provider = new();
        ChatClientAgent agent = new(
            client,
            new ChatClientAgentOptions
            {
                Name = "agent",
                ChatHistoryProvider = provider,
            });

        DurableAgentState persisted = await RunEntityAsync(
            agent,
            new DurableAgentState(),
            new RunRequest("new request") { CorrelationId = "new" });

        Assert.Equal(1, provider.StoreCount);
        Assert.Collection(
            persisted.Data.ConversationHistory,
            entry =>
            {
                DurableAgentStateRequest storedRequest = Assert.IsType<DurableAgentStateRequest>(entry);
                DurableAgentStateMessage message = Assert.Single(storedRequest.Messages);
                Assert.Empty(message.Contents);
                Assert.False(string.IsNullOrWhiteSpace(message.MessageId));
                Assert.NotNull(message.CreatedAt);
            },
            entry => Assert.IsType<DurableAgentStateResponse>(entry));
        Assert.True(
            persisted.Data.Session?.GetProperty("stateBag").TryGetProperty("external-history", out _) is true);
    }

    [Fact]
    public async Task ServiceManagedConversationStoresIdentityMetadataRequestAndDeliveryResponseAsync()
    {
        RecordingChatClient client = new();
        ChatClientAgent agent = new(client, name: "agent");
        AgentSession serviceSession = await agent.CreateSessionAsync("service-id");
        DurableAgentState initialState = new();
        initialState.Data.Session = await agent.SerializeSessionAsync(serviceSession);

        DurableAgentState persisted = await RunEntityAsync(
            agent,
            initialState,
            new RunRequest("new request") { CorrelationId = "new" });

        Assert.Equal(["new request"], client.LastMessages.Select(message => message.Text));
        Assert.Equal(2, persisted.Data.ConversationHistory.Count);
        Assert.Empty(Assert.IsType<DurableAgentStateRequest>(persisted.Data.ConversationHistory[0]).Messages[0].Contents);
        Assert.Equal(
            "service-id",
            persisted.Data.Session?.GetProperty("conversationId").GetString());
    }

    [Fact]
    public async Task FirstServiceManagedTurnDoesNotLeaveEntityOwnedTranscriptAsync()
    {
        RecordingChatClient client = new() { ResponseConversationId = "service-id" };
        ChatClientAgent agent = new(client, name: "agent");

        DurableAgentState persisted = await RunEntityAsync(
            agent,
            new DurableAgentState(),
            new RunRequest("new request") { CorrelationId = "new" });

        Assert.Equal(2, persisted.Data.ConversationHistory.Count);
        DurableAgentStateRequest request =
            Assert.IsType<DurableAgentStateRequest>(persisted.Data.ConversationHistory[0]);
        Assert.Empty(Assert.Single(request.Messages).Contents);
        Assert.IsType<DurableAgentStateResponse>(persisted.Data.ConversationHistory[1]);
        Assert.Equal(
            "service-id",
            persisted.Data.Session?.GetProperty("conversationId").GetString());
    }

    [Fact]
    public async Task ServiceManagedPerCallDeclarationIsIgnoredWhenPerCallPersistenceIsDisabledAsync()
    {
        RecordingChatClient client = new();
        ChatClientAgent agent = new(client, name: "agent");
        DurableAgentState state = CreateStateWithExchange("old", "old request", "old response");

        DurableAgentState persisted = await RunEntityAsync(
            agent,
            state,
            new RunRequest("new request") { CorrelationId = "new" },
            options => options.SetServiceManagedPerServiceCallHistory("AGENT"));

        Assert.Equal(["old request", "old response", "new request"], client.LastMessages.Select(message => message.Text));
        Assert.Equal(4, persisted.Data.ConversationHistory.Count);
    }

    [Fact]
    public async Task PerServiceCallServiceOwnershipExcludesLocalProviderTranscriptStateAsync()
    {
        RecordingChatClient client = new() { ResponseConversationId = "service-id" };
#pragma warning disable MAAI001
        ChatClientAgent agent = new(
            client,
            new ChatClientAgentOptions
            {
                Name = "agent",
                RequirePerServiceCallChatHistoryPersistence = true,
            });
#pragma warning restore MAAI001

        DurableAgentState persisted = await RunEntityAsync(
            agent,
            new DurableAgentState(),
            new RunRequest("new request") { CorrelationId = "new" },
            options => options.SetServiceManagedPerServiceCallHistory("agent"));

        Assert.Equal(["new request"], client.LastMessages.Select(message => message.Text));
        Assert.Equal(2, persisted.Data.ConversationHistory.Count);
        DurableAgentStateMessage requestMessage =
            Assert.IsType<DurableAgentStateRequest>(persisted.Data.ConversationHistory[0]).Messages[0];
        Assert.Empty(requestMessage.Contents);
        Assert.False(string.IsNullOrWhiteSpace(requestMessage.MessageId));
        Assert.NotNull(requestMessage.CreatedAt);
        Assert.Equal("service-id", persisted.Data.Session?.GetProperty("conversationId").GetString());
        JsonElement serializedSession = persisted.Data.Session.GetValueOrDefault();
        Assert.DoesNotContain(
            nameof(InMemoryChatHistoryProvider),
            serializedSession.GetProperty("stateBag").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task AmbiguousPerServiceCallOwnershipFailsBeforeModelExecutionAsync()
    {
        RecordingChatClient client = new();
        RecordingHistoryProvider provider = new();
#pragma warning disable MAAI001
        ChatClientAgent agent = new(
            client,
            new ChatClientAgentOptions
            {
                Name = "agent",
                ChatHistoryProvider = provider,
                RequirePerServiceCallChatHistoryPersistence = true,
            });
#pragma warning restore MAAI001
        EntityHarness harness = CreateHarness(agent, new DurableAgentState());

        await Assert.ThrowsAsync<DurableAgentHistoryOwnershipNotSupportedException>(
            () => harness.RunAsync(new RunRequest("new request") { CorrelationId = "new" }));

        Assert.Equal(0, client.InvocationCount);
        Assert.Equal(0, provider.LoadCount);
        Assert.Equal(0, provider.StoreCount);
        Assert.False(harness.StateWasPersisted);
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("external")]
    [InlineData("service")]
    public async Task StatefulCompactionFailsBeforeEntityExecutionAsync(string ownership)
    {
        RecordingChatClient client = new();
        ChatHistoryProvider? historyProvider = ownership == "external"
            ? new RecordingHistoryProvider()
            : null;
        ChatClientAgent agent = new(
            client,
            new ChatClientAgentOptions
            {
                Name = "agent",
                ChatHistoryProvider = historyProvider,
                AIContextProviders =
                [
                    new CompactionProvider(
                        new SlidingWindowCompactionStrategy(_ => true)),
                ],
            });
        DurableAgentState state = new();
        if (ownership == "service")
        {
            state.Data.Session = await agent.SerializeSessionAsync(
                await agent.CreateSessionAsync("service-id"));
        }

        Assert.Throws<DurableAgentCompactionNotSupportedException>(
            () => CreateHarness(agent, state));
        Assert.Equal(0, client.InvocationCount);
    }

    [Fact]
    public async Task FactoryAgentValidationRunsOnceBeforeSessionOrModelSideEffectsAsync()
    {
        RecordingChatClient client = new();
        ChatClientAgent agent = new(
            client,
            new ChatClientAgentOptions
            {
                Name = "agent",
                AIContextProviders =
                [
                    new CompactionProvider(
                        new SlidingWindowCompactionStrategy(_ => true)),
                ],
            });
        int factoryInvocationCount = 0;
        EntityHarness harness = CreateHarness(
            agent,
            new DurableAgentState(),
            registerWithFactory: true,
            onFactoryInvoked: () => factoryInvocationCount++);

        await Assert.ThrowsAsync<DurableAgentCompactionNotSupportedException>(
            () => harness.RunAsync(new RunRequest("new request") { CorrelationId = "new" }));

        Assert.Equal(1, factoryInvocationCount);
        Assert.Equal(0, client.InvocationCount);
        Assert.False(harness.StateWasPersisted);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(DurableAgentHistoryReplayMode.PreloadEntityHistory, true)]
    [InlineData(DurableAgentHistoryReplayMode.CurrentRequestOnly, false)]
    public async Task AgentWithoutContextPipelineAppliesReplayModeAndStorageSemanticsAsync(
        DurableAgentHistoryReplayMode? replayMode,
        bool expectsPreloadedHistory)
    {
        RecordingAgent agent = new("agent");
        DurableAgentState initialState = CreateStateWithExchange("old", "old request", "old response");
        initialState.Data.ConversationHistory.Add(
            new DurableAgentStateErrorResponse
            {
                CorrelationId = "failed",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                Messages =
                [
                    DurableAgentStateMessage.FromChatMessage(
                        new ChatMessage(ChatRole.Assistant, "must not replay")),
                ],
            });
        initialState.Data.ConversationHistory.Add(
            new DurableAgentStateResponse
            {
                CorrelationId = "reasoning",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                Messages =
                [
                    new DurableAgentStateMessage
                    {
                        Role = ChatRole.Assistant.Value,
                        Contents =
                        [
                            new DurableAgentStateTextReasoningContent { Text = "private reasoning" },
                            new DurableAgentStateTextContent { Text = "visible answer" },
                        ],
                    },
                    new DurableAgentStateMessage
                    {
                        Role = ChatRole.Assistant.Value,
                        Contents =
                        [
                            new DurableAgentStateTextReasoningContent { Text = "reasoning only" },
                        ],
                    },
                ],
            });

        DurableAgentState persisted = await RunEntityAsync(
            agent,
            initialState,
            new RunRequest("new request") { CorrelationId = "new" },
            options =>
            {
                if (replayMode.HasValue)
                {
                    options.SetHistoryReplayMode("agent", replayMode.Value);
                }
            });

        Assert.Equal(
            expectsPreloadedHistory
                ? ["old request", "old response", "visible answer", "new request"]
                : ["new request"],
            agent.LastMessages.Select(message => message.Text));
        Assert.DoesNotContain(
            agent.LastMessages.SelectMany(message => message.Contents),
            content => content is TextReasoningContent);
        Assert.Equal(6, persisted.Data.ConversationHistory.Count);
        DurableAgentStateRequest storedRequest =
            Assert.IsType<DurableAgentStateRequest>(persisted.Data.ConversationHistory[^2]);
        DurableAgentStateMessage storedMessage = Assert.Single(storedRequest.Messages);
        if (expectsPreloadedHistory)
        {
            Assert.Equal("new request", Assert.IsType<DurableAgentStateTextContent>(
                Assert.Single(storedMessage.Contents)).Text);
        }
        else
        {
            Assert.Empty(storedMessage.Contents);
        }

        Assert.IsType<DurableAgentStateResponse>(persisted.Data.ConversationHistory[^1]);
        Assert.NotNull(persisted.Data.Session);
    }

    [Fact]
    public async Task AgentWithoutContextPipelineDoesNotReplayErrorResponsesAsync()
    {
        RecordingAgent agent = new("agent");
        DurableAgentState initialState = CreateStateWithExchange("old", "old request", "old response");
        initialState.Data.ConversationHistory.Add(
            new DurableAgentStateErrorResponse
            {
                CorrelationId = "failed",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                Messages =
                [
                    DurableAgentStateMessage.FromChatMessage(
                        new ChatMessage(ChatRole.Assistant, "must not replay")),
                ],
            });

        _ = await RunEntityAsync(
            agent,
            initialState,
            new RunRequest("new request") { CorrelationId = "new" });

        Assert.Equal(["old request", "old response", "new request"], agent.LastMessages.Select(message => message.Text));
    }

    [Fact]
    public async Task AgentWithoutContextPipelineFiltersReasoningFromReplayAsync()
    {
        RecordingAgent agent = new("agent");
        DurableAgentState initialState = new();
        initialState.Data.ConversationHistory.Add(
            new DurableAgentStateResponse
            {
                CorrelationId = "old",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                Messages =
                [
                    new DurableAgentStateMessage
                    {
                        Role = ChatRole.Assistant.Value,
                        Contents =
                        [
                            new DurableAgentStateTextReasoningContent { Text = "reasoning only" },
                        ],
                    },
                    new DurableAgentStateMessage
                    {
                        Role = ChatRole.Assistant.Value,
                        Contents =
                        [
                            new DurableAgentStateTextReasoningContent { Text = "private reasoning" },
                            new DurableAgentStateTextContent { Text = "visible answer" },
                        ],
                    },
                ],
            });

        _ = await RunEntityAsync(
            agent,
            initialState,
            new RunRequest("new request") { CorrelationId = "new" });

        Assert.Equal(["visible answer", "new request"], agent.LastMessages.Select(message => message.Text));
        Assert.DoesNotContain(
            agent.LastMessages.SelectMany(message => message.Contents),
            content => content is TextReasoningContent);
    }

    [Fact]
    public async Task EntityPreservesButDoesNotCreateCompactionEntriesAsync()
    {
        RecordingChatClient client = new();
        ChatClientAgent agent = new(client, name: "agent");
        DurableAgentState initialState = new();
        initialState.Data.ConversationHistory.Add(
            new DurableAgentStateCompaction
            {
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                Messages =
                [
                    DurableAgentStateMessage.FromChatMessage(
                        new ChatMessage(ChatRole.Assistant, "shared summary")),
                ],
            });

        DurableAgentState persisted = await RunEntityAsync(
            agent,
            initialState,
            new RunRequest("new request") { CorrelationId = "new" });

        Assert.Equal(["shared summary", "new request"], client.LastMessages.Select(message => message.Text));
        DurableAgentStateCompaction compaction =
            Assert.Single(persisted.Data.ConversationHistory.OfType<DurableAgentStateCompaction>());
        Assert.Equal("shared summary", compaction.Messages[0].ToChatMessage().Text);
        Assert.Equal(3, persisted.Data.ConversationHistory.Count);
    }

    [Fact]
    public async Task WrappedServerManagedSessionSurvivesColdEntityInvocationAsync()
    {
        RecordingChatClient client = new();
        ChatClientAgent chatAgent = new(client, name: "agent");
        AIAgent wrappedAgent = new TestDelegatingAgent(chatAgent);
        AgentSession serviceSession = await chatAgent.CreateSessionAsync("service-conversation");
        serviceSession.StateBag.SetValue("opaque-server-state", "preserved");
        DurableAgentState initialState = new()
        {
            Data =
            {
                Session = await wrappedAgent.SerializeSessionAsync(serviceSession),
            },
        };

        DurableAgentState persisted = await RunEntityAsync(
            wrappedAgent,
            initialState,
            new RunRequest("new request") { CorrelationId = "new" });
        AgentSession restored = await wrappedAgent.DeserializeSessionAsync(
            persisted.Data.Session!.Value);
        ChatClientAgentSession restoredTyped = Assert.IsType<ChatClientAgentSession>(restored);

        Assert.Equal(["new request"], client.LastMessages.Select(message => message.Text));
        Assert.Equal("service-conversation", restoredTyped.ConversationId);
        Assert.Equal("preserved", restoredTyped.StateBag.GetValue<string>("opaque-server-state"));
    }

    [Fact]
    public async Task LegacyMessageIdsPersistAcrossProviderLoadAndColdReloadAsync()
    {
        const string Json = """
            {
              "schemaVersion": "1.1.0",
              "data": {
                "conversationHistory": [
                  {
                    "$type": "request",
                    "correlationId": "old",
                    "createdAt": "2026-07-27T12:34:50+00:00",
                    "messages": [
                      {
                        "role": "user",
                        "contents": [{ "$type": "text", "text": "old request" }]
                      },
                      {
                        "role": "user",
                        "messageId": "producer-id",
                        "contents": [{ "$type": "text", "text": "preserved" }]
                      }
                    ]
                  },
                  {
                    "$type": "response",
                    "correlationId": "old",
                    "createdAt": "2026-07-27T12:34:51+00:00",
                    "messages": [
                      {
                        "role": "assistant",
                        "contents": []
                      },
                      {
                        "role": "assistant",
                        "contents": [{ "$type": "text", "text": "old response" }]
                      }
                    ]
                  }
                ]
              }
            }
            """;
        DurableAgentState initialState = Assert.IsType<DurableAgentState>(
            JsonSerializer.Deserialize(Json, DurableAgentStateJsonContext.Default.DurableAgentState));
        RecordingChatClient client = new();
        ChatClientAgent agent = new(client, name: "agent");

        DurableAgentState firstWrite = await RunEntityAsync(
            agent,
            initialState,
            new RunRequest("first new request") { CorrelationId = "new-1" });
        string serialized = JsonSerializer.Serialize(
            firstWrite,
            DurableAgentStateJsonContext.Default.DurableAgentState);
        DurableAgentState coldState = Assert.IsType<DurableAgentState>(
            JsonSerializer.Deserialize(serialized, DurableAgentStateJsonContext.Default.DurableAgentState));
        string?[] firstIds = firstWrite.Data.ConversationHistory
            .Take(2)
            .SelectMany(entry => entry.Messages)
            .Select(message => message.MessageId)
            .ToArray();

        DurableAgentState secondWrite = await RunEntityAsync(
            agent,
            coldState,
            new RunRequest("second new request") { CorrelationId = "new-2" });
        string?[] secondIds = secondWrite.Data.ConversationHistory
            .Take(2)
            .SelectMany(entry => entry.Messages)
            .Select(message => message.MessageId)
            .ToArray();

        string?[] expectedIds =
            ["durable_request_old_0", "producer-id", "durable_response_old_0", "durable_response_old_1"];
        Assert.Equal(expectedIds, firstIds);
        Assert.Equal(firstIds, secondIds);
        Assert.Contains("\"messageId\":\"durable_response_old_1\"", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailureAfterConversationFinalizationDoesNotMutateHydratedStateAsync()
    {
        FailingSerializationAgent agent = new("agent");
        DurableAgentState initialState = CreateStateWithExchange("old", "old request", "old response");
        EntityHarness harness = CreateHarness(agent, initialState);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.RunAsync(new RunRequest("new request") { CorrelationId = "new" }));

        Assert.True(agent.ExecutionCompleted);
        Assert.False(harness.StateWasPersisted);
        Assert.Equal(2, initialState.Data.ConversationHistory.Count);
        Assert.DoesNotContain(
            initialState.Data.ConversationHistory,
            entry => entry.CorrelationId == "new");
    }

    [Fact]
    public async Task AutoRetentionRunsOnCompletedEntityExecutionAsync()
    {
        RecordingChatClient client = new();
        ChatClientAgent agent = new(client, name: "agent");
        DurableAgentState initialState = CreateLargeState();

        DurableAgentState persisted = await RunEntityAsync(
            agent,
            initialState,
            new RunRequest(new string('n', 500)) { CorrelationId = "new" },
            options =>
            {
                options.HistoryRetentionMode = DurableAgentHistoryRetentionMode.Auto;
                options.MaxStateBytes = 1_800;
            });

        Assert.NotNull(persisted.Data.Truncation);
        Assert.DoesNotContain(persisted.Data.ConversationHistory, entry => entry.CorrelationId == "oldest");
        Assert.Contains(persisted.Data.ConversationHistory, entry => entry.CorrelationId == "new");
    }

    [Fact]
    public async Task OversizedProtectedStateFailsWithoutPersistenceAsync()
    {
        RecordingChatClient client = new();
        ChatClientAgent agent = new(client, name: "agent");
        EntityHarness harness = CreateHarness(
            agent,
            new DurableAgentState(),
            options =>
            {
                options.HistoryRetentionMode = DurableAgentHistoryRetentionMode.Auto;
                options.MaxStateBytes = 500;
            });

        await Assert.ThrowsAsync<DurableAgentStateSizeLimitExceededException>(
            () => harness.RunAsync(
                new RunRequest(new string('x', 2_000)) { CorrelationId = "new" }));

        Assert.False(harness.StateWasPersisted);
    }

    [Fact]
    public async Task ProviderLoadFailureDoesNotInvokeModelOrCommitWorkingStateAsync()
    {
        InvalidOperationException expected = new("provider load failed");
        RecordingHistoryProvider provider = new() { LoadException = expected };
        RecordingChatClient client = new();
        ChatClientAgent agent = CreateAgentWithProvider(client, provider);
        DurableAgentState initialState = CreateStateWithExchange("old", "old request", "old response");
        string originalState = SerializeState(initialState);
        EntityHarness harness = CreateHarness(agent, initialState);

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.RunAsync(new RunRequest("new request") { CorrelationId = "new" }));

        Assert.Same(expected, actual);
        Assert.Equal(1, provider.LoadCount);
        Assert.Equal(0, provider.StoreCount);
        Assert.Equal(0, client.InvocationCount);
        Assert.False(harness.StateWasPersisted);
        Assert.Equal(originalState, SerializeState(initialState));
        Assert.Contains(harness.Logs, entry => ReferenceEquals(expected, entry.Exception));
    }

    [Fact]
    public async Task ProviderStoreFailureDoesNotCommitWorkingStateAsync()
    {
        InvalidOperationException expected = new("provider store failed");
        RecordingHistoryProvider provider = new() { StoreException = expected };
        RecordingChatClient client = new();
        ChatClientAgent agent = CreateAgentWithProvider(client, provider);
        DurableAgentState initialState = CreateStateWithExchange("old", "old request", "old response");
        string originalState = SerializeState(initialState);
        EntityHarness harness = CreateHarness(agent, initialState);

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.RunAsync(new RunRequest("new request") { CorrelationId = "new" }));

        Assert.Same(expected, actual);
        Assert.Equal(1, provider.LoadCount);
        Assert.Equal(1, provider.StoreCount);
        Assert.Equal(1, client.InvocationCount);
        Assert.False(harness.StateWasPersisted);
        Assert.Equal(originalState, SerializeState(initialState));
        Assert.Contains(harness.Logs, entry => ReferenceEquals(expected, entry.Exception));
    }

    [Fact]
    public async Task ProviderLoadCancellationPropagatesWithoutCommitOrWrappingAsync()
    {
        using TestHostApplicationLifetime lifetime = new();
        RecordingHistoryProvider provider = new()
        {
            WaitForLoadCancellation = true,
        };
        RecordingChatClient client = new();
        ChatClientAgent agent = CreateAgentWithProvider(client, provider);
        DurableAgentState initialState = CreateStateWithExchange("old", "old request", "old response");
        string originalState = SerializeState(initialState);
        EntityHarness harness = CreateHarness(
            agent,
            initialState,
            applicationLifetime: lifetime);

        Task runTask = harness.RunAsync(new RunRequest("new request") { CorrelationId = "new" });
        try
        {
            await WaitForProviderStageAsync(
                provider.LoadStarted.Task,
                runTask,
                "history provider load callback");
            lifetime.StopApplication();
            OperationCanceledException actual =
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => runTask.WaitAsync(s_testTimeout));

            Assert.Same(provider.LoadCancellationException, actual);
            Assert.Equal(lifetime.ApplicationStopping, provider.LoadCancellationToken);
            Assert.Equal(1, provider.LoadCount);
            Assert.Equal(0, provider.StoreCount);
            Assert.Equal(0, client.InvocationCount);
            Assert.False(harness.StateWasPersisted);
            Assert.Equal(originalState, SerializeState(initialState));
        }
        finally
        {
            lifetime.StopApplication();
            await JoinRunTaskAsync(runTask, "history provider load cancellation");
        }
    }

    [Fact]
    public async Task ProviderStoreCancellationPropagatesWithoutPartialCommitOrWrappingAsync()
    {
        using TestHostApplicationLifetime lifetime = new();
        RecordingHistoryProvider provider = new()
        {
            WaitForStoreCancellation = true,
        };
        RecordingChatClient client = new();
        ChatClientAgent agent = CreateAgentWithProvider(client, provider);
        DurableAgentState initialState = CreateStateWithExchange("old", "old request", "old response");
        string originalState = SerializeState(initialState);
        EntityHarness harness = CreateHarness(
            agent,
            initialState,
            applicationLifetime: lifetime);

        Task runTask = harness.RunAsync(new RunRequest("new request") { CorrelationId = "new" });
        try
        {
            await WaitForProviderStageAsync(
                provider.StoreStarted.Task,
                runTask,
                "history provider store callback");
            lifetime.StopApplication();
            OperationCanceledException actual =
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => runTask.WaitAsync(s_testTimeout));

            Assert.Same(provider.StoreCancellationException, actual);
            Assert.Equal(lifetime.ApplicationStopping, client.LastCancellationToken);
            Assert.Equal(lifetime.ApplicationStopping, provider.LoadCancellationToken);
            Assert.Equal(lifetime.ApplicationStopping, provider.StoreCancellationToken);
            Assert.Equal(1, provider.LoadCount);
            Assert.Equal(1, provider.StoreCount);
            Assert.Equal(1, client.InvocationCount);
            Assert.False(harness.StateWasPersisted);
            Assert.Equal(originalState, SerializeState(initialState));
        }
        finally
        {
            lifetime.StopApplication();
            await JoinRunTaskAsync(runTask, "history provider store cancellation");
        }
    }

    private static async Task<DurableAgentState> RunEntityAsync(
        AIAgent agent,
        DurableAgentState state,
        RunRequest request,
        Action<DurableAgentsOptions>? configure = null)
    {
        EntityHarness harness = CreateHarness(agent, state, configure);
        await harness.RunAsync(request);
        return Assert.IsType<DurableAgentState>(harness.PersistedState);
    }

    private static EntityHarness CreateHarness(
        AIAgent agent,
        DurableAgentState state,
        Action<DurableAgentsOptions>? configure = null,
        bool registerWithFactory = false,
        Action? onFactoryInvoked = null,
        IHostApplicationLifetime? applicationLifetime = null)
    {
        AgentSessionId sessionId = new(agent.Name!, "session");
        DurableAgentsOptions options = new()
        {
            DefaultTimeToLive = null,
        };
        if (registerWithFactory)
        {
            options.AddAIAgentFactory(
                agent.Name!,
                _ =>
                {
                    onFactoryInvoked?.Invoke();
                    return agent;
                });
        }
        else
        {
            options.AddAIAgent(agent);
        }

        configure?.Invoke(options);

        ListLoggerProvider loggerProvider = new();
        Dictionary<Type, object> services = new()
        {
            [typeof(DurableTaskClient)] = new Mock<DurableTaskClient>("test").Object,
            [typeof(ILoggerFactory)] = new ListLoggerFactory(loggerProvider),
            [typeof(DurableAgentsOptions)] = options,
            [typeof(IReadOnlyDictionary<string, Func<IServiceProvider, AIAgent>>)] = options.GetAgentFactories(),
            [typeof(IHostApplicationLifetime)] = applicationLifetime ??
                Mock.Of<IHostApplicationLifetime>(
                    lifetime => lifetime.ApplicationStopping == CancellationToken.None),
        };
        IServiceProvider serviceProvider = new DictionaryServiceProvider(services);

        Mock<TaskEntityContext> context = new();
        context.SetupGet(value => value.Id).Returns(sessionId);
        Mock<TaskEntityState> entityState = new();
        entityState.Setup(value => value.GetState(typeof(DurableAgentState))).Returns(state);
        object? persistedState = null;
        entityState.Setup(value => value.SetState(It.IsAny<object?>()))
            .Callback<object?>(value => persistedState = value);

        Mock<TaskEntityOperation> operation = new();
        operation.SetupGet(value => value.Name).Returns(nameof(AgentEntity.Run));
        operation.SetupGet(value => value.Context).Returns(context.Object);
        operation.SetupGet(value => value.State).Returns(entityState.Object);
        operation.SetupGet(value => value.HasInput).Returns(true);

        AgentEntity entity = new(serviceProvider);
        return new EntityHarness(
            entity,
            operation,
            loggerProvider,
            () => persistedState);
    }

    private static ChatClientAgent CreateAgentWithProvider(
        RecordingChatClient client,
        RecordingHistoryProvider provider) =>
        new(
            client,
            new ChatClientAgentOptions
            {
                Name = "agent",
                ChatHistoryProvider = provider,
            });

    private static string SerializeState(DurableAgentState state) =>
        JsonSerializer.Serialize(
            state,
            DurableAgentStateJsonContext.Default.DurableAgentState);

    private static async Task WaitForProviderStageAsync(
        Task stageTask,
        Task runTask,
        string stageDescription)
    {
        Task completedTask;
        try
        {
            completedTask = await Task.WhenAny(stageTask, runTask).WaitAsync(s_stageTimeout);
        }
        catch (TimeoutException exception)
        {
            throw new Xunit.Sdk.XunitException(
                $"Timed out after {s_stageTimeout} waiting to reach the {stageDescription}.",
                exception);
        }

        if (ReferenceEquals(completedTask, runTask))
        {
            try
            {
                await runTask;
            }
            catch (Exception exception)
            {
                throw new Xunit.Sdk.XunitException(
                    $"The entity run failed before reaching the {stageDescription}.",
                    exception);
            }

            throw new Xunit.Sdk.XunitException(
                $"The entity run completed before reaching the {stageDescription}.");
        }

        await stageTask;
    }

    private static async Task JoinRunTaskAsync(Task runTask, string scenarioDescription)
    {
        try
        {
            await runTask.WaitAsync(s_testTimeout);
        }
        catch (OperationCanceledException) when (runTask.IsCanceled)
        {
            // The test body asserts the propagated cancellation; cleanup only joins the same task.
        }
        catch (TimeoutException exception)
        {
            throw new Xunit.Sdk.XunitException(
                $"Timed out after {s_testTimeout} joining the entity run during cleanup for {scenarioDescription}; the test may have leaked a running task.",
                exception);
        }
        catch (Exception exception)
        {
            throw new Xunit.Sdk.XunitException(
                $"The entity run faulted unexpectedly during cleanup for {scenarioDescription}.",
                exception);
        }
    }

    private static DurableAgentState CreateStateWithExchange(
        string correlationId,
        string request,
        string response)
    {
        DurableAgentState state = new();
        AddExchange(state, correlationId, request, response, DateTimeOffset.UtcNow.AddMinutes(-5));
        return state;
    }

    private static DurableAgentState CreateLargeState()
    {
        DurableAgentState state = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        AddExchange(state, "oldest", new string('a', 600), new string('b', 600), now.AddMinutes(-10));
        AddExchange(state, "middle", new string('c', 600), new string('d', 600), now.AddMinutes(-5));
        return state;
    }

    private static void AddExchange(
        DurableAgentState state,
        string correlationId,
        string request,
        string response,
        DateTimeOffset createdAt)
    {
        state.Data.ConversationHistory.Add(
            new DurableAgentStateRequest
            {
                CorrelationId = correlationId,
                CreatedAt = createdAt,
                Messages =
                [
                    DurableAgentStateMessage.FromChatMessage(
                        new ChatMessage(ChatRole.User, request) { CreatedAt = createdAt }),
                ],
            });
        state.Data.ConversationHistory.Add(
            new DurableAgentStateResponse
            {
                CorrelationId = correlationId,
                CreatedAt = createdAt,
                Messages =
                [
                    DurableAgentStateMessage.FromChatMessage(
                        new ChatMessage(ChatRole.Assistant, response) { CreatedAt = createdAt }),
                ],
            });
    }

    private sealed class EntityHarness(
        AgentEntity entity,
        Mock<TaskEntityOperation> operation,
        ListLoggerProvider loggerProvider,
        Func<object?> persistedState)
    {
        public object? PersistedState => persistedState();

        public bool StateWasPersisted => this.PersistedState is not null;

        public IReadOnlyList<LogRecord> Logs => loggerProvider.Records;

        public async Task<AgentResponse> RunAsync(RunRequest request)
        {
            operation.Setup(value => value.GetInput(typeof(RunRequest))).Returns(request);
            object? result = await ((ITaskEntity)entity).RunAsync(operation.Object);
            return Assert.IsType<AgentResponse>(result);
        }
    }

    private sealed class TestDelegatingAgent(AIAgent innerAgent) : DelegatingAIAgent(innerAgent);

    private sealed class RecordingAgent(string name) : AIAgent
    {
        public override string? Name => name;

        public List<ChatMessage> LastMessages { get; private set; } = [];

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default) => new(new RecordingSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            new(JsonSerializer.SerializeToElement(new { stateBag = session.StateBag.Serialize() }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            new(new RecordingSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.LastMessages = messages.ToList();
            await Task.Yield();
            yield return new AgentResponseUpdate(ChatRole.Assistant, "response");
        }

        private sealed class RecordingSession : AgentSession;
    }

    private sealed class FailingSerializationAgent(string name) : AIAgent
    {
        public override string? Name => name;

        public bool ExecutionCompleted { get; private set; }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default) => new(new FailingSerializationSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("session serialization failed");

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            new(new FailingSerializationSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.ExecutionCompleted = true;
            await Task.Yield();
            yield return new AgentResponseUpdate(ChatRole.Assistant, "response");
        }

        private sealed class FailingSerializationSession : AgentSession;
    }

    private sealed class RecordingHistoryProvider : ChatHistoryProvider
    {
        public override IReadOnlyList<string> StateKeys => ["external-history"];

        public Exception? LoadException { get; init; }

        public Exception? StoreException { get; init; }

        public bool WaitForLoadCancellation { get; init; }

        public bool WaitForStoreCancellation { get; init; }

        public TaskCompletionSource<bool> LoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> StoreStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LoadCount { get; private set; }

        public int StoreCount { get; private set; }

        public CancellationToken LoadCancellationToken { get; private set; }

        public CancellationToken StoreCancellationToken { get; private set; }

        public OperationCanceledException? LoadCancellationException { get; private set; }

        public OperationCanceledException? StoreCancellationException { get; private set; }

        protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            this.LoadCount++;
            this.LoadCancellationToken = cancellationToken;
            if (this.WaitForLoadCancellation)
            {
                this.LoadStarted.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken)
                        .WaitAsync(s_stageTimeout, CancellationToken.None);
                }
                catch (OperationCanceledException exception)
                {
                    this.LoadCancellationException = exception;
                    throw;
                }
                catch (TimeoutException exception)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"Timed out after {s_stageTimeout} waiting for ApplicationStopping during provider load.",
                        exception);
                }
            }

            if (this.LoadException is not null)
            {
                throw this.LoadException;
            }

            return [];
        }

        protected override async ValueTask StoreChatHistoryAsync(
            InvokedContext context,
            CancellationToken cancellationToken = default)
        {
            this.StoreCount++;
            this.StoreCancellationToken = cancellationToken;
            context.Session!.StateBag.SetValue(
                "external-history",
                new ExternalHistoryState { Count = this.StoreCount });
            if (this.WaitForStoreCancellation)
            {
                this.StoreStarted.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken)
                        .WaitAsync(s_stageTimeout, CancellationToken.None);
                }
                catch (OperationCanceledException exception)
                {
                    this.StoreCancellationException = exception;
                    throw;
                }
                catch (TimeoutException exception)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"Timed out after {s_stageTimeout} waiting for ApplicationStopping during provider store.",
                        exception);
                }
            }

            if (this.StoreException is not null)
            {
                throw this.StoreException;
            }
        }

        private sealed class ExternalHistoryState
        {
            public int Count { get; set; }
        }
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public Exception? Exception { get; init; }

        public string? ResponseConversationId { get; init; }

        public int InvocationCount { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public List<ChatMessage> LastMessages { get; private set; } = [];

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.InvocationCount++;
            this.LastCancellationToken = cancellationToken;
            this.LastMessages = messages.ToList();
            if (this.Exception is not null)
            {
                throw this.Exception;
            }

            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "response")
            {
                ConversationId = this.ResponseConversationId ?? options?.ConversationId,
            };
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _applicationStopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => this._applicationStopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => this._applicationStopping.Cancel();

        public void Dispose() => this._applicationStopping.Dispose();
    }

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        public List<LogRecord> Records { get; } = [];

        public ILogger CreateLogger(string categoryName) => new ListLogger(this.Records);

        public void Dispose()
        {
        }
    }

    private sealed class ListLoggerFactory : ILoggerFactory
    {
        private readonly ListLoggerProvider _provider;

        public ListLoggerFactory(ListLoggerProvider provider)
        {
            this._provider = provider;
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => this._provider.CreateLogger(categoryName);

        public void Dispose() => this._provider.Dispose();
    }

    private sealed class DictionaryServiceProvider(IReadOnlyDictionary<Type, object> services) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            services.TryGetValue(serviceType, out object? service) ? service : null;
    }

    private sealed class ListLogger(List<LogRecord> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            records.Add(new LogRecord(logLevel, eventId, exception, formatter(state, exception)));
        }
    }

    private sealed record LogRecord(
        LogLevel Level,
        EventId EventId,
        Exception? Exception,
        string Message);
}
