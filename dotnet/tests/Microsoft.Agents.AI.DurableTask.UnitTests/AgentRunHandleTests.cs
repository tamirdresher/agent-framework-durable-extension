// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Agents.AI.DurableTask.State;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.Entities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Microsoft.Agents.AI.DurableTask.Tests.Unit;

public sealed class AgentRunHandleTests
{
    private static readonly AgentSessionId s_sessionId = new("agent", "session");

    [Fact]
    public async Task PollingReturnsUniqueSuccessfulResponseAsync()
    {
        DurableAgentState state = new();
        state.Data.ConversationHistory.Add(CreateResponse("correlation", "success"));

        AgentResponse response = await CreateHandle(state).ReadAgentResponseAsync();

        Assert.Equal("success", response.Text);
    }

    [Fact]
    public async Task PollingReturnsUniqueErrorResponseAsync()
    {
        DurableAgentState state = new();
        state.Data.ConversationHistory.Add(CreateErrorResponse("correlation", "failure"));

        AgentResponse response = await CreateHandle(state).ReadAgentResponseAsync();

        Assert.Equal("failure", response.Text);
    }

    [Fact]
    public async Task PollingDuplicateTerminalsThrowsImmediatelyAsync()
    {
        DurableAgentState state = new();
        state.Data.ConversationHistory.Add(CreateResponse("correlation", "first"));
        state.Data.ConversationHistory.Add(CreateResponse("correlation", "second"));
        int readCount = 0;

        DurableAgentStateCorruptionException exception =
            await Assert.ThrowsAsync<DurableAgentStateCorruptionException>(
                () => CreateHandle(state, () => readCount++).ReadAgentResponseAsync());

        Assert.Equal("correlation", exception.CorrelationId);
        Assert.Equal(2, exception.TerminalResponseCount);
        Assert.Equal(1, readCount);
    }

    [Fact]
    public async Task PollingWithoutTerminalRemainsPendingAsync()
    {
        DurableAgentState state = new();
        state.Data.ConversationHistory.Add(
            new DurableAgentStateRequest
            {
                CorrelationId = "correlation",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        using CancellationTokenSource cancellation = new();
        int readCount = 0;
        AgentRunHandle handle = CreateHandle(
            state,
            () =>
            {
                readCount++;
                cancellation.Cancel();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handle.ReadAgentResponseAsync(cancellation.Token));

        Assert.Equal(1, readCount);
    }

    [Fact]
    public async Task ClientRejectsInvalidCorrelationBeforeSignallingAsync()
    {
        Mock<DurableTaskClient> client = new(MockBehavior.Strict, "test");
        DefaultDurableAgentClient durableAgentClient =
            new(client.Object, NullLoggerFactory.Instance);
        RunRequest request = new("request") { CorrelationId = "" };

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => durableAgentClient.RunAgentAsync(s_sessionId, request));

        Assert.Equal("request", exception.ParamName);
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public void HandleRejectsInvalidCorrelationBeforePolling()
    {
        Mock<DurableTaskClient> client = new("test");

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new AgentRunHandle(
                client.Object,
                NullLogger.Instance,
                s_sessionId,
                " "));

        Assert.Equal("correlationId", exception.ParamName);
    }

    private static AgentRunHandle CreateHandle(DurableAgentState state, Action? onRead = null)
    {
        Mock<DurableEntityClient> entities = new("test");
        entities
            .Setup(client => client.GetEntityAsync<DurableAgentState>(
                s_sessionId,
                It.IsAny<CancellationToken>()))
            .Callback(onRead ?? (() => { }))
            .ReturnsAsync(new EntityMetadata<DurableAgentState>(s_sessionId, state));

        Mock<DurableTaskClient> client = new("test");
        client.SetupGet(value => value.Entities).Returns(entities.Object);
        return new AgentRunHandle(
            client.Object,
            NullLogger.Instance,
            s_sessionId,
            "correlation");
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
