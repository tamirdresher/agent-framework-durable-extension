// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Agents.AI.DurableTask.State;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.AI.DurableTask;

internal class AgentEntity(IServiceProvider services, CancellationToken cancellationToken = default) : TaskEntity<DurableAgentState>
{
    private readonly IServiceProvider _services = services;
    private readonly DurableTaskClient _client = services.GetRequiredService<DurableTaskClient>();
    private readonly ILoggerFactory _loggerFactory = services.GetRequiredService<ILoggerFactory>();
    private readonly IAgentResponseHandler? _messageHandler = services.GetService<IAgentResponseHandler>();
    private readonly DurableAgentsOptions _options = services.GetRequiredService<DurableAgentsOptions>();
    // Entity operations rehydrate and execute once rather than replaying like orchestrations, and
    // TaskEntityContext has no deterministic clock. Use wall-clock UTC through an injectable source.
    private readonly TimeProvider _timeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;
    private readonly CancellationToken _cancellationToken = cancellationToken != default
        ? cancellationToken
        : services.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;

    public Task<AgentResponse> RunAgentAsync(RunRequest request)
    {
        return this.Run(request);
    }

    // IDE1006 and VSTHRD200 disabled to allow method name to match the common cross-platform entity operation name.
#pragma warning disable IDE1006
#pragma warning disable VSTHRD200
    public async Task<AgentResponse> Run(RunRequest request)
#pragma warning restore VSTHRD200
#pragma warning restore IDE1006
    {
        ArgumentNullException.ThrowIfNull(request);

        AgentSessionId sessionId = this.Context.Id;
        // Logger category is Microsoft.DurableTask.Agents.{registeredAgentName}.{sessionId}
        ILogger logger = this.GetLogger(sessionId.Name, sessionId.Key);

        string correlationId = request.CorrelationId;
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException(
                "A non-empty correlation ID is required to run a durable agent request.",
                nameof(request));
        }

        DurableAgentStateResponse? existingResponse;
        try
        {
            existingResponse = DurableAgentStateTerminalResponseLookup.FindUniqueTerminalResponse(
                this.State.Data.ConversationHistory,
                correlationId);
        }
        catch (DurableAgentStateCorruptionException exception)
        {
            logger.LogTerminalResponseStateCorruption(
                exception,
                sessionId,
                correlationId,
                exception.TerminalResponseCount.GetValueOrDefault());
            throw;
        }

        if (existingResponse is not null)
        {
            // Durable signals are delivered at least once. The correlation ID is the idempotency key, so a
            // retained terminal response is reused without comparing request content. Callers must not reuse
            // the ID for a different logical request while that terminal response is retained.
            return existingResponse.ToResponse();
        }

        if (request.Messages is not { Count: > 0 })
        {
            throw new ArgumentException(
                "At least one message is required for a new durable agent request.",
                nameof(request));
        }

        AIAgent agent = this.GetAgent(sessionId);
        bool serviceManagedPerServiceCallHistory =
            this._options.IsServiceManagedPerServiceCallHistory(sessionId.Name);
        ValidatedDurableAgentHistoryConfiguration validatedHistoryConfiguration =
            DurableAgentHistoryOwnershipResolver.ValidateRunConfiguration(
                agent,
                serviceManagedPerServiceCallHistory);

        // TaskEntity hydrates State with the backend-owned reference. Provider callbacks, session state,
        // finalization, and retention all mutate objects, so a deep working copy preserves rollback when
        // any later phase fails. Assigning State only at the end is not sufficient without this isolation.
        DurableAgentState workingState = this.State.Clone();

        foreach (ChatMessage msg in request.Messages)
        {
            logger.LogAgentRequest(sessionId, msg.Role, msg.Text);
        }

        // Set the current agent context for the duration of the agent run. This will be exposed
        // to any tools that are invoked by the agent.
        DurableAgentContext agentContext = new(
            entityContext: this.Context,
            client: this._client,
            lifetime: this._services.GetRequiredService<IHostApplicationLifetime>(),
            services: this._services);
        DurableAgentContext.SetCurrent(agentContext);

        try
        {
            AgentSession session = await DurableAgentSessionState.RestoreAsync(
                agent,
                workingState.Data.Session,
                this._cancellationToken).ConfigureAwait(false);
            (DurableAgentHistoryOwnership ownership, ChatClientAgent? chatClientAgent) =
                DurableAgentHistoryOwnershipResolver.Resolve(
                    session,
                    validatedHistoryConfiguration);
            bool entityOwnedHistory = ownership == DurableAgentHistoryOwnership.Entity;
            DurableAgentHistoryReplayMode historyReplayMode =
                this._options.GetHistoryReplayMode(sessionId.Name);

            // The provider is bound per invocation because it needs this operation's working state and
            // correlation ID. A registration-time provider cannot safely bind either value.
            DurableChatHistoryProvider? durableHistoryProvider = entityOwnedHistory
                ? new(workingState.Data.ConversationHistory, request, logger)
                : null;
            EntityAgentWrapper agentWrapper = new(
                agent,
                this.Context,
                request,
                this._services,
                durableHistoryProvider);

            IEnumerable<ChatMessage> inputMessages = BuildAgentInputMessages(
                workingState,
                request,
                ownership,
                historyReplayMode);

            // Start the agent response stream
            IAsyncEnumerable<AgentResponseUpdate> responseStream = agentWrapper.RunStreamingAsync(
                inputMessages,
                session,
                options: null,
                this._cancellationToken);

            AgentResponse response;
            if (this._messageHandler is null)
            {
                // If no message handler is provided, we can just get the full response at once.
                // This is expected to be the common case for non-interactive agents.
                response = await responseStream.ToAgentResponseAsync(this._cancellationToken);
            }
            else
            {
                List<AgentResponseUpdate> responseUpdates = [];

                // To support interactive chat agents, we need to stream the responses to an IAgentMessageHandler.
                // The user-provided message handler can be implemented to send the responses to the user.
                // We assume that only non-empty text updates are useful for the user.
                async IAsyncEnumerable<AgentResponseUpdate> StreamResultsAsync()
                {
                    await foreach (AgentResponseUpdate update in responseStream)
                    {
                        // We need the full response further down, so we piece it together as we go.
                        responseUpdates.Add(update);

                        // Yield the update to the message handler.
                        yield return update;
                    }
                }

                await this._messageHandler.OnStreamingResponseUpdateAsync(StreamResultsAsync(), this._cancellationToken);
                response = responseUpdates.ToAgentResponse();
            }

            FinalizeConversationEntries(
                workingState,
                request,
                response,
                ownership,
                historyReplayMode,
                durableHistoryProvider,
                logger);

            workingState.Data.Session = await SerializeSessionWithoutDuplicateHistoryAsync(
                agent,
                session,
                chatClientAgent,
                ownership,
                this._cancellationToken).ConfigureAwait(false);

            string responseText = response.Text;

            if (!string.IsNullOrEmpty(responseText))
            {
                logger.LogAgentResponse(
                    sessionId,
                    response.Messages.FirstOrDefault()?.Role ?? ChatRole.Assistant,
                    responseText,
                    response.Usage?.InputTokenCount,
                    response.Usage?.OutputTokenCount,
                    response.Usage?.TotalTokenCount);
            }

            DateTime? deletionCheckExpiration =
                this.UpdateExpiration(workingState, sessionId, logger);
            this.ApplyRetentionAndCommit(
                workingState,
                sessionId,
                logger,
                deletionCheckExpiration);
            return response;
        }
        catch (Exception exception)
        {
            logger.LogDurableAgentExecutionFailed(exception, sessionId);
            throw;
        }
        finally
        {
            // Clear the current agent context
            DurableAgentContext.ClearCurrent();
        }
    }

    /// <summary>
    /// Checks if the entity has expired and deletes it if so, otherwise reschedules the deletion check.
    /// </summary>
    /// <remarks>
    /// This method is called by the durable task runtime when a <c>CheckAndDeleteIfExpired</c> signal is received.
    /// </remarks>
    public void CheckAndDeleteIfExpired(AgentEntityDeletionCheck? scheduledCheck = null)
    {
        AgentSessionId sessionId = this.Context.Id;
        ILogger logger = this.GetLogger(sessionId.Name, sessionId.Key);

        DateTime currentTime = this._timeProvider.GetUtcNow().UtcDateTime;
        DateTime? expirationTime = this.State.Data.ExpirationTimeUtc;

        logger.LogTTLDeletionCheck(sessionId, expirationTime, currentTime);

        // A delayed signal can outlive a deleted entity. TaskEntity initializes missing state
        // before dispatch, so delete that otherwise-empty placeholder instead of recreating it.
        if (!expirationTime.HasValue && IsEmptyInitializedState(this.State))
        {
            this.State = null!;
            return;
        }

        if (!this._options.ContainsAgent(sessionId.Name) ||
            !this._options.GetTimeToLive(sessionId.Name).HasValue)
        {
            // Configuration can change while a durable delayed signal is outstanding.
            if (expirationTime.HasValue)
            {
                logger.LogTTLExpirationTimeCleared(sessionId);
                this.State.Data.ExpirationTimeUtc = null;
            }

            return;
        }

        if (!expirationTime.HasValue)
        {
            return;
        }

        if (currentTime >= expirationTime.Value)
        {
            logger.LogTTLEntityExpired(sessionId, expirationTime.Value);
            this.State = null!;
            return;
        }

        // Later interactions normally extend expiration and let the earlier signal move the chain
        // forward. A shorter TTL schedules an earlier signal; its older, later counterpart is stale.
        if (scheduledCheck is null ||
            scheduledCheck.ExpectedExpirationTimeUtc <= expirationTime.Value)
        {
            this.ScheduleDeletionCheck(sessionId, logger, expirationTime.Value);
        }
    }

    private static bool IsEmptyInitializedState(DurableAgentState state)
    {
        return state.Data.ConversationHistory.Count == 0 &&
            state.Data.Session is null &&
            state.Data.IngestedPositions is null &&
            state.Data.Truncation is null &&
            state.Data.ExpirationTimeUtc is null &&
            state.Data.ExtensionData is null &&
            state.Data.UnknownProperties is null &&
            state.ExtensionData is null &&
            state.UnknownProperties is null;
    }

    private void ScheduleDeletionCheck(
        AgentSessionId sessionId,
        ILogger logger,
        DateTime expirationTime)
    {
        DateTime currentTime = this._timeProvider.GetUtcNow().UtcDateTime;
        TimeSpan minimumDelay = this._options.MinimumTimeToLiveSignalDelay;

        // To avoid excessive scheduling, we schedule the deletion check for no less than the minimum delay.
        DateTime scheduledTime = expirationTime > currentTime.Add(minimumDelay)
            ? expirationTime
            : currentTime.Add(minimumDelay);

        logger.LogTTLDeletionScheduled(sessionId, scheduledTime);

        // Schedule a signal to self to check for expiration
        this.Context.SignalEntity(
            this.Context.Id,
            nameof(CheckAndDeleteIfExpired), // self-signal
            new AgentEntityDeletionCheck(expirationTime),
            options: new SignalEntityOptions { SignalTime = scheduledTime });
    }

    private static IEnumerable<ChatMessage> BuildAgentInputMessages(
        DurableAgentState workingState,
        RunRequest request,
        DurableAgentHistoryOwnership ownership,
        DurableAgentHistoryReplayMode historyReplayMode)
    {
        if (ownership != DurableAgentHistoryOwnership.NoContextPipeline ||
            historyReplayMode == DurableAgentHistoryReplayMode.CurrentRequestOnly)
        {
            // A MAF history/context pipeline or a server-owned opaque session supplies prior context.
            // Passing stored history here as well would duplicate messages.
            return request.Messages;
        }

        // Legacy generic AIAgents have no discoverable context pipeline. In the backward-compatible
        // preload mode, the entity manually replays prior durable history before the current request.
        return DurableAgentStateReplay.GetMessages(
                workingState.Data.ConversationHistory,
                request.CorrelationId)
            .Concat(request.Messages);
    }

    private static void FinalizeConversationEntries(
        DurableAgentState workingState,
        RunRequest request,
        AgentResponse response,
        DurableAgentHistoryOwnership ownership,
        DurableAgentHistoryReplayMode historyReplayMode,
        DurableChatHistoryProvider? durableHistoryProvider,
        ILogger logger)
    {
        if (durableHistoryProvider?.HasStagedTurn is true)
        {
            // Provider callbacks already staged the entity-owned request and response. Replace only
            // the staged response so aggregate usage and response metadata are retained once.
            durableHistoryProvider.CompleteStagedResponse(response);
            return;
        }

        bool entityReplaysHistory =
            ownership == DurableAgentHistoryOwnership.NoContextPipeline &&
            historyReplayMode == DurableAgentHistoryReplayMode.PreloadEntityHistory;
        workingState.Data.ConversationHistory.Add(
            entityReplaysHistory
                ? DurableAgentStateRequest.FromRunRequest(request, logger)
                : DurableAgentStateRequest.FromRunRequestMetadata(request));

        // External providers, services, and opaque session-managed agents own their transcript.
        // The entity still records the completed outer response for at-least-once delivery/polling.
        workingState.Data.ConversationHistory.Add(
            DurableAgentStateResponse.FromResponse(request.CorrelationId, response, logger));
    }

    private static ValueTask<System.Text.Json.JsonElement> SerializeSessionWithoutDuplicateHistoryAsync(
        AIAgent agent,
        AgentSession session,
        ChatClientAgent? chatClientAgent,
        DurableAgentHistoryOwnership ownership,
        CancellationToken cancellationToken)
    {
        // InMemoryChatHistoryProvider state can contain a full transcript already retained by the
        // entity. Exclude only that provider's declared keys; custom, compaction, and opaque
        // server-session state remains authoritative and is preserved.
        IEnumerable<string> excludedStateKeys =
            chatClientAgent?.ChatHistoryProvider is InMemoryChatHistoryProvider inMemoryHistoryProvider &&
            ownership is DurableAgentHistoryOwnership.Entity or DurableAgentHistoryOwnership.Service
                ? inMemoryHistoryProvider.StateKeys
                : [];

        return DurableAgentSessionState.SerializeAsync(
            agent,
            session,
            excludedStateKeys,
            cancellationToken);
    }

    private DateTime? UpdateExpiration(
        DurableAgentState workingState,
        AgentSessionId sessionId,
        ILogger logger)
    {
        TimeSpan? timeToLive = this._options.GetTimeToLive(sessionId.Name);
        DateTime? previousExpirationTime = workingState.Data.ExpirationTimeUtc;
        if (!timeToLive.HasValue)
        {
            if (previousExpirationTime.HasValue)
            {
                logger.LogTTLExpirationTimeCleared(sessionId);
                workingState.Data.ExpirationTimeUtc = null;
            }

            return null;
        }

        DateTime newExpirationTime =
            this._timeProvider.GetUtcNow().UtcDateTime.Add(timeToLive.Value);
        workingState.Data.ExpirationTimeUtc = newExpirationTime;
        logger.LogTTLExpirationTimeUpdated(sessionId, newExpirationTime);

        // The first turn starts one delayed-check chain. Extended expirations are picked up by the
        // earlier check; only a shortened expiration needs a new earlier signal.
        return !previousExpirationTime.HasValue ||
            newExpirationTime < previousExpirationTime.Value
                ? newExpirationTime
                : null;
    }

    private void ApplyRetentionAndCommit(
        DurableAgentState workingState,
        AgentSessionId sessionId,
        ILogger logger,
        DateTime? deletionCheckExpiration)
    {
        _ = DurableAgentStateRetention.Enforce(
            workingState,
            this._options.HistoryRetentionMode,
            this._options.MaxStateBytes,
            this._timeProvider.GetUtcNow(),
            logger,
            sessionId);

        if (deletionCheckExpiration.HasValue)
        {
            // Pass the working-copy value explicitly: this.State still refers to the original state
            // until the operation commits.
            this.ScheduleDeletionCheck(sessionId, logger, deletionCheckExpiration.Value);
        }

        // This setter performs no backend I/O. TaskEntity writes the replacement state only after
        // this async operation completes successfully; an exception before then leaves storage unchanged.
        this.State = workingState;
    }

    private AIAgent GetAgent(AgentSessionId sessionId)
    {
        IReadOnlyDictionary<string, Func<IServiceProvider, AIAgent>> agents =
            this._services.GetRequiredService<IReadOnlyDictionary<string, Func<IServiceProvider, AIAgent>>>();
        if (!agents.TryGetValue(sessionId.Name, out Func<IServiceProvider, AIAgent>? agentFactory))
        {
            throw new InvalidOperationException($"Agent '{sessionId.Name}' not found");
        }

        return agentFactory(this._services);
    }

    private ILogger GetLogger(string agentName, string sessionKey)
    {
        return this._loggerFactory.CreateLogger($"Microsoft.DurableTask.Agents.{agentName}.{sessionKey}");
    }
}

internal sealed record AgentEntityDeletionCheck(DateTime ExpectedExpirationTimeUtc);
