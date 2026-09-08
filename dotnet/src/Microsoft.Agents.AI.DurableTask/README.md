# Microsoft.Agents.AI.DurableTask

The Microsoft Agent Framework provides a programming model for building agents and agent workflows in .NET. This package, the *Durable Task extension for the Agent Framework*, extends the Agent Framework programming model with the following capabilities:

- Stateful, durable execution of agents in distributed environments
- Automatic conversation history management
- Long-running agent workflows as "durable orchestrator" functions
- Tools and dashboards for managing and monitoring agents and agent workflows

These capabilities are implemented using foundational technologies from the Durable Task technology stack:

- [Durable Entities](https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-entities) for stateful, durable execution of agents
- [Durable Orchestrations](https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-orchestrations) for long-running agent workflows
- The [Durable Task Scheduler](https://learn.microsoft.com/azure/azure-functions/durable/durable-task-scheduler/choose-orchestration-framework) for managing durable task execution and observability at scale

This package can be used by itself or in conjunction with the `Microsoft.Agents.AI.Hosting.AzureFunctions` package, which provides additional features via Azure Functions integration.

## Install the package

From the command-line:

```bash
dotnet add package Microsoft.Agents.AI.DurableTask
```

Or directly in your project file:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Agents.AI.DurableTask" Version="[CURRENTVERSION]" />
</ItemGroup>
```

You can alternatively just reference the `Microsoft.Agents.AI.Hosting.AzureFunctions` package if you're hosting your agents and orchestrations in the Azure Functions .NET Isolated worker.

## Usage Examples

For a comprehensive tour of all the functionality, concepts, and APIs, check out the [.NET Durable Task samples](https://github.com/microsoft/agent-framework-durable-extension/tree/main/dotnet/samples).

### Conversation history and sessions

Durable `ChatClientAgent` instances use the normal Agent Framework history-provider pipeline. The default
`InMemoryChatHistoryProvider` is replaced for each entity invocation by a provider backed by durable entity
state. Wrapping the agent in `DelegatingAIAgent` is supported because the durable runtime discovers the
underlying `ChatClientAgent` through `GetService<T>()`.

The replacement provider is an adapter over the entity operation's isolated working state, not a second
persistence backend. Its history callback stages the request and response in that working list. The entity
then completes aggregate response metadata, serializes the session, applies TTL, and performs one durable-state
commit when the operation finishes successfully.

Custom history providers remain authoritative. Their session state is persisted, while the entity stores a
metadata-only request envelope and the response required for correlation-based polling. Service-managed
conversations similarly persist the service conversation ID and metadata-only request envelope without copying
request content into entity state. Agents that do not expose a `ChatClientAgent` context pipeline retain the
legacy full-history replay behavior by default; failed `errorResponse` entries from another runtime are not replayed.
Reasoning content is preserved in durable state but removed from model replay because it can be provider-specific,
rejected by another model API, or expose chain-of-thought. Messages that also contain replayable content retain that
content; reasoning-only and metadata-only envelopes are omitted from model input.
For a custom server-managed `AIAgent` whose serialized session owns remote continuation, disable entity replay
explicitly so the agent receives only the current request:

```csharp
services.ConfigureDurableAgents(options =>
{
    options.AddAIAgent(serverManagedAgent);
    options.SetHistoryReplayMode(
        serverManagedAgent.Name!,
        DurableAgentHistoryReplayMode.CurrentRequestOnly);
});
```

`PreloadEntityHistory` remains the default for backward compatibility with generic local agents. The setting is
consulted only when no `ChatClientAgent` is discoverable through the agent's `GetService<T>()` chain. Entity-level
tests run the default, explicit preload, and current-request-only modes against the same stored exchange. They verify
the exact model input, filtering of failed responses and reasoning content, and the corresponding full-request versus
metadata-only durable request record.

Provider callbacks run inside the same isolated working-state transaction as model execution, response finalization,
session serialization, and TTL processing. An ordinary exception or cancellation during provider load or
store/finalization is rethrown unchanged, and the working state is not committed. Load failures occur before model
execution; store failures can occur after the model has produced a response but still leave the original entity state
unchanged. The Agent Framework callback APIs receive the entity operation cancellation token; they do not expose a
separate cancellation source or a finer durable commit boundary. In normal hosted execution, that token is
`IHostApplicationLifetime.ApplicationStopping`. The entity currently logs all exceptions caught at this boundary,
including cancellation, before rethrowing.

Session serialization is owned by the registered agent's existing `SerializeSessionAsync` and
`DeserializeSessionAsync` contract. The durable extension stores the returned JSON opaquely and does not add
parallel serializer delegates. Custom agents and custom session types should implement those Agent Framework
methods by overriding `SerializeSessionCoreAsync` and `DeserializeSessionCoreAsync` so their invariants and
type registration remain authoritative.

Agent Framework's experimental per-service-call history persistence exposes whether the mode is enabled, but
its public history-provider callbacks do not identify which of the multiple service-call responses is the final
agent response. Durable agents therefore support this mode only when the model service owns the conversation,
which must be declared explicitly:

```csharp
services.ConfigureDurableAgents(options =>
{
    options.AddAIAgent(agent);
    options.SetServiceManagedPerServiceCallHistory(agent.Name!);
});
```

The declaration has no effect when `RequirePerServiceCallChatHistoryPersistence` is disabled. When it is enabled,
omitting the declaration fails before session restoration, provider callbacks, or model execution. A local provider
means the configured `ChatHistoryProvider` owns the transcript instead of the model service. In that path, Agent
Framework writes its internal `_agent_local_chat_history` sentinel into the same public `ConversationId` property
used for real service conversation IDs. A non-null ID therefore cannot distinguish local from service ownership.

Local per-service-call persistence is not supported for durable execution. The provider is called after every
service call in a tool loop, and its public callback context has no final-response marker. `AgentRunHandle` is the
polling caller: after signaling the durable entity, it repeatedly queries entity state by correlation ID and must
receive only the completed outer `AgentResponse`, never an intermediate tool-call response. Disable per-service-call
persistence for local/entity-owned history until Agent Framework exposes a public finality boundary.

Stateful `CompactionProvider` use in any ownership mode, or an `InMemoryChatHistoryProvider` reducer, fails before
model execution. Current .NET Agent Framework compaction
state contains full message copies, so persisting it would duplicate transcript content for entity-,
external-provider-, and service-owned conversations, while deleting it would lose compaction semantics.

## Feedback & Contributing

We welcome feedback and contributions in [our GitHub repo](https://github.com/microsoft/agent-framework-durable-extension).
