# Custom History Provider Sample

This sample demonstrates a durable Azure OpenAI agent whose cumulative conversation history is
stored by a custom JSON-file `ChatHistoryProvider`.

## Key Concepts Demonstrated

- Persisting the full transcript outside the durable entity.
- Restoring the same provider/session identity after a host restart.
- Growing external history beyond 1 MiB with many simulated 4 KiB records.
- Projecting only the newest 12 records, capped at 32 KiB of UTF-8 text, into model history.

The sample does not send one message larger than 1 MiB. A single oversized request or tool result
can exceed the Durable Task Scheduler message boundary before the provider can process it. The
seeded records represent ordinary conversation accumulated over time.

The durable entity retains metadata-only request envelopes plus final responses needed for durable
delivery; those responses and other metadata remain subject to durable retention. External provider
storage is not an atomic transaction with entity persistence.

This JSON implementation reads the full file before selecting the bounded model-history suffix.
Production stores should use suitable paging or indexing and must account for their own item-size,
retention, transaction, and eviction limits. They must also serialize every content type and message
property the application uses.

This is a sample storage provider, not a production design.

## Environment Setup

See the [README.md](../README.md) file in the parent directory for Foundry, authentication, and
Durable Task Scheduler setup.

## Running the Sample

```bash
cd dotnet/samples/DurableAgents/ConsoleApps/09_CustomHistoryProvider
dotnet run --framework net10.0
```

Enter a short marker. The sample creates more than 1 MiB of cumulative external history, asks the
agent to remember the marker, restarts the host, and verifies that the restored conversation recalls
the marker. The sample-local JSON file is deleted when the run finishes.

## Tests

```bash
dotnet test --project tests/09_CustomHistoryProvider.Tests.csproj
```

The tests cover external storage above 1 MiB, bounded model-history projection, provider identity
restoration, and framework-filtered history persistence without requiring Foundry or DTS. All
test-only fakes and storage helpers stay private to the test class.
