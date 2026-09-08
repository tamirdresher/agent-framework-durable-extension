# Durable Foundry Managed Agent

This sample wraps a server-managed, versioned Microsoft Foundry agent as a Microsoft Agent Framework `FoundryAgent`, registers it as a durable `AIAgent`, and proves conversation continuity across a host restart.

## Architecture

```text
Console application
  |
  | keyed AIAgent proxy + DurableAgentSession
  v
Durable Task Scheduler
  |-- owns invocation, retry, durable entity state, and entity lifetime
  |-- persists the opaque inner AgentSession returned by FoundryAgent
  v
FoundryAgent -> ChatClientAgent -> Foundry Responses API
  |-- owns the versioned agent definition
  `-- owns the server-side conversation and its transcript
```

The application:

1. Creates a uniquely named Foundry agent version with `CreateAgentVersionAsync`.
2. Wraps that exact version with `AIProjectClient.AsAIAgent`, producing a `FoundryAgent`.
3. Registers the `FoundryAgent` with `ConfigureDurableAgents`.
4. Resolves the keyed durable `AIAgent` proxy and creates a `DurableAgentSession`.
5. Sends a generated marker on the first turn. Foundry establishes the service conversation, and the durable entity persists the inner `ChatClientAgent` session containing that conversation identity.
6. Stops the host, creates a fresh `FoundryAgent` wrapper and host, and sends a second turn using the same `DurableAgentSession`. The durable entity restores the opaque inner session, so Foundry resumes the same server conversation and can return the marker.
7. Stops the host before deleting the exact server agent version in `finally`.

The host restart is intentional: it demonstrates that continuity comes from durable entity state, not from an in-memory `FoundryAgent` or `AgentSession`.

## History ownership

After Foundry establishes a conversation, Foundry owns the transcript. Durable Task persists the opaque inner session identity plus delivery metadata; it does not replay a duplicate entity transcript to the model service. The restored-session and first-turn metadata behavior is covered by `AgentEntityHistoryTests.ServiceManagedConversationStoresIdentityMetadataRequestAndDeliveryResponseAsync` and `AgentEntityHistoryTests.FirstServiceManagedTurnDoesNotLeaveEntityOwnedTranscriptAsync`.

This versioned `FoundryAgent` path does **not** enable `ChatClientAgentOptions.RequirePerServiceCallChatHistoryPersistence`, so the sample intentionally does not call `SetServiceManagedPerServiceCallHistory`. That declaration is only needed when a service-backed `ChatClientAgent` explicitly enables per-service-call persistence; it is otherwise ignored.

For arbitrary server-hosted `AIAgent` implementations that do not expose a discoverable `ChatClientAgent` pipeline, configure `DurableAgentHistoryReplayMode.CurrentRequestOnly` explicitly. This sample does not need that fallback because `FoundryAgent` exposes its inner `ChatClientAgent` through `GetService<ChatClientAgent>()`.

## Prerequisites and configuration

- .NET 10 SDK
- Azure CLI authenticated with `az login`
- Permission to create, invoke, and delete agents in the Foundry project
- A Durable Task Scheduler endpoint, such as the local DTS emulator

Set:

```powershell
$env:FOUNDRY_PROJECT_ENDPOINT = "https://<resource>.services.ai.azure.com/api/projects/<project>"
$env:FOUNDRY_MODEL = "<OpenAI-model-deployment>"
$env:DURABLE_TASK_SCHEDULER_CONNECTION_STRING = "Endpoint=http://localhost:8080;TaskHub=default;Authentication=None"
```

Under this sample's model policy, configure an OpenAI model deployment that is supported by Foundry prompt agents and the Responses API. No credentials are stored in tracked files. `DefaultAzureCredential` is convenient for local development; production applications should prefer a specific credential such as `ManagedIdentityCredential` to avoid unintended credential probing and latency.

## Run

Start the DTS emulator, then:

```powershell
cd dotnet\samples\DurableAgents\ConsoleApps\08_FoundryManagedAgent
dotnet run --framework net10.0
```

A successful run prints both responses and `Conversation continuity check: PASS`. Model wording is nondeterministic, so the smoke check only verifies that the randomly generated marker appears in both turns.

## Cleanup

The durable host is stopped before cleanup. Because the sample creates a unique server agent name, its `finally` block deletes only the exact version it created. Cleanup failures are reported and cause a failed process exit when there was no earlier failure; they never hide the original sample failure.

## Known limitation

A caller still cannot seed a new `DurableAgentSession` from an existing, pre-durable Foundry conversation. That unsupported scenario is isolated in the [server-managed agent reproduction branch](https://github.com/microsoft/agent-framework-durable-extension/tree/tamirdresher-microsoft-server-managed-agent-repro).
