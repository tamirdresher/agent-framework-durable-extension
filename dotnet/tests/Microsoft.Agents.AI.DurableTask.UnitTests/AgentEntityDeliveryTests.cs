// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using Microsoft.Agents.AI.DurableTask.State;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace Microsoft.Agents.AI.DurableTask.Tests.Unit;

public sealed class AgentEntityDeliveryTests
{
    [Fact]
    public async Task ReusedSuccessfulCorrelationReturnsWithoutComparingContentOrConstructingAgentAsync()
    {
        DurableAgentState initialState = CreateStateWithResponse(
            "duplicate",
            "response");
        int factoryInvocationCount = 0;
        EntityHarness harness = CreateHarness(
            new RecordingAgent("agent"),
            initialState,
            registerWithFactory: true,
            onFactoryInvoked: () => factoryInvocationCount++);

        AgentResponse response = await harness.RunAsync(
            new RunRequest("different logical request") { CorrelationId = "duplicate" });

        Assert.Equal("response", response.Text);
        Assert.Equal(0, factoryInvocationCount);
        Assert.Same(initialState, harness.PersistedState);
    }

    [Fact]
    public async Task ReusedErrorCorrelationWithEmptyMessagesReturnsBeforeValidationAsync()
    {
        DurableAgentState initialState = CreateStateWithResponse(
            "duplicate",
            "persisted failure",
            isError: true);
        RecordingAgent agent = new("agent");
        EntityHarness harness = CreateHarness(agent, initialState);

        AgentResponse response = await harness.RunAsync(
            new RunRequest([]) { CorrelationId = "duplicate" });

        Assert.Equal("persisted failure", response.Text);
        Assert.Equal(0, agent.InvocationCount);
        Assert.Same(initialState, harness.PersistedState);
    }

    [Fact]
    public async Task DuplicateTerminalStateThrowsBeforeValidationAndAgentConstructionAsync()
    {
        DurableAgentState initialState = CreateStateWithResponse(
            "duplicate",
            "first");
        initialState.Data.ConversationHistory.Add(
            CreateResponse("duplicate", "second", isError: true));
        int factoryInvocationCount = 0;
        EntityHarness harness = CreateHarness(
            new RecordingAgent("agent"),
            initialState,
            registerWithFactory: true,
            onFactoryInvoked: () => factoryInvocationCount++);

        DurableAgentStateCorruptionException exception =
            await Assert.ThrowsAsync<DurableAgentStateCorruptionException>(
                () => harness.RunAsync(
                    new RunRequest([]) { CorrelationId = "duplicate" }));

        Assert.Equal(2, exception.TerminalResponseCount);
        Assert.Equal(0, factoryInvocationCount);
        Assert.False(harness.StateWasPersisted);
    }

    [Fact]
    public async Task InvalidCorrelationAndNewEmptyRequestFailBeforeAgentSideEffectsAsync()
    {
        int factoryInvocationCount = 0;
        EntityHarness harness = CreateHarness(
            new RecordingAgent("agent"),
            new DurableAgentState(),
            registerWithFactory: true,
            onFactoryInvoked: () => factoryInvocationCount++);

        ArgumentException correlationException = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.RunAsync(new RunRequest([]) { CorrelationId = "" }));
        ArgumentException messageException = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.RunAsync(new RunRequest([]) { CorrelationId = "new" }));

        Assert.Equal("request", correlationException.ParamName);
        Assert.Equal("request", messageException.ParamName);
        Assert.Equal(0, factoryInvocationCount);
        Assert.False(harness.StateWasPersisted);
    }

    [Fact]
    public async Task ModelFailureDoesNotMutateOrPersistHydratedStateAsync()
    {
        RecordingAgent agent = new("agent")
        {
            Exception = new InvalidOperationException("model failed"),
        };
        DurableAgentState initialState = CreateStateWithResponse(
            "old",
            "old response");
        EntityHarness harness = CreateHarness(agent, initialState);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.RunAsync(
                new RunRequest("new request") { CorrelationId = "new" }));

        Assert.False(harness.StateWasPersisted);
        Assert.Single(initialState.Data.ConversationHistory);
        Assert.DoesNotContain(
            initialState.Data.ConversationHistory,
            entry => entry.CorrelationId == "new");
    }

    [Fact]
    public async Task NewRequestUsesExistingHistoryAndCommitsRequestAndResponseAsync()
    {
        RecordingAgent agent = new("agent");
        DurableAgentState initialState = CreateStateWithResponse(
            "old",
            "old response");
        EntityHarness harness = CreateHarness(agent, initialState);

        AgentResponse response = await harness.RunAsync(
            new RunRequest("new request") { CorrelationId = "new" });

        Assert.Equal("response", response.Text);
        Assert.Equal(["old response", "new request"], agent.LastMessages.Select(message => message.Text));
        DurableAgentState persisted = Assert.IsType<DurableAgentState>(harness.PersistedState);
        Assert.Equal(3, persisted.Data.ConversationHistory.Count);
        Assert.Single(initialState.Data.ConversationHistory);
    }

    private static DurableAgentState CreateStateWithResponse(
        string correlationId,
        string text,
        bool isError = false)
    {
        DurableAgentState state = new();
        state.Data.ConversationHistory.Add(CreateResponse(correlationId, text, isError));
        return state;
    }

    private static DurableAgentStateResponse CreateResponse(
        string correlationId,
        string text,
        bool isError = false)
    {
        IReadOnlyList<DurableAgentStateMessage> messages =
        [
            DurableAgentStateMessage.FromChatMessage(
                new ChatMessage(ChatRole.Assistant, text)),
        ];
        return isError
            ? new DurableAgentStateErrorResponse
            {
                CorrelationId = correlationId,
                CreatedAt = DateTimeOffset.UtcNow,
                Messages = messages,
            }
            : new DurableAgentStateResponse
            {
                CorrelationId = correlationId,
                CreatedAt = DateTimeOffset.UtcNow,
                Messages = messages,
            };
    }

    private static EntityHarness CreateHarness(
        RecordingAgent agent,
        DurableAgentState state,
        bool registerWithFactory = false,
        Action? onFactoryInvoked = null)
    {
        AgentSessionId sessionId = new(agent.Name!, "session");
        DurableAgentsOptions options = new() { DefaultTimeToLive = null };
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

        Dictionary<Type, object> services = new()
        {
            [typeof(DurableTaskClient)] = new Mock<DurableTaskClient>("test").Object,
            [typeof(ILoggerFactory)] = Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            [typeof(DurableAgentsOptions)] = options,
            [typeof(IReadOnlyDictionary<string, Func<IServiceProvider, AIAgent>>)] =
                options.GetAgentFactories(),
            [typeof(IHostApplicationLifetime)] = Mock.Of<IHostApplicationLifetime>(
                lifetime => lifetime.ApplicationStopping == CancellationToken.None),
        };

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

        AgentEntity entity = new(new DictionaryServiceProvider(services));
        return new EntityHarness(entity, operation, () => persistedState);
    }

    private sealed class EntityHarness(
        AgentEntity entity,
        Mock<TaskEntityOperation> operation,
        Func<object?> persistedState)
    {
        public object? PersistedState => persistedState();

        public bool StateWasPersisted => this.PersistedState is not null;

        public async Task<AgentResponse> RunAsync(RunRequest request)
        {
            operation.Setup(value => value.GetInput(typeof(RunRequest))).Returns(request);
            object? result = await ((ITaskEntity)entity).RunAsync(operation.Object);
            return Assert.IsType<AgentResponse>(result);
        }
    }

    private sealed class RecordingAgent(string name) : AIAgent
    {
        public override string? Name => name;

        public Exception? Exception { get; init; }

        public int InvocationCount { get; private set; }

        public List<ChatMessage> LastMessages { get; private set; } = [];

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default) => new(new RecordingSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            new(JsonSerializer.SerializeToElement(new { }));

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
            this.InvocationCount++;
            this.LastMessages = messages.ToList();
            if (this.Exception is not null)
            {
                throw this.Exception;
            }

            await Task.Yield();
            yield return new AgentResponseUpdate(ChatRole.Assistant, "response");
        }

        private sealed class RecordingSession : AgentSession;
    }

    private sealed class DictionaryServiceProvider(IReadOnlyDictionary<Type, object> services) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            services.TryGetValue(serviceType, out object? service) ? service : null;
    }
}
