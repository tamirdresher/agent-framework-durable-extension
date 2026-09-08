// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using Microsoft.Agents.AI.DurableTask.State;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.AI.DurableTask;

/// <summary>
/// Applies deterministic pressure retention to durable agent state.
/// </summary>
internal static class DurableAgentStateRetention
{
    internal const double HighWatermark = 0.85;
    internal const double LowWatermark = 0.70;
    internal static readonly TimeSpan DeliveryWindow = TimeSpan.FromSeconds(60);

    public static int GetSerializedSize(DurableAgentState state)
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            state,
            DurableAgentStateJsonContext.Default.DurableAgentState).Length;
    }

    public static int Enforce(
        DurableAgentState state,
        DurableAgentHistoryRetentionMode mode,
        int maxStateBytes,
        DateTimeOffset now,
        ILogger logger,
        AgentSessionId sessionId)
    {
        int highWatermark = (int)(maxStateBytes * HighWatermark);
        int initialSize = GetSerializedSize(state);
        if (mode == DurableAgentHistoryRetentionMode.KeepAll)
        {
            return 0;
        }

        if (initialSize < highWatermark)
        {
            DurableAgentTelemetry.RecordNoAction(sessionId.Name);
            return 0;
        }

        int lowWatermark = (int)(maxStateBytes * LowWatermark);
        int normallyRemovedEntries = 0;
        int normallyRemovedMessages = 0;
        while (GetSerializedSize(state) > lowWatermark)
        {
            List<DurableAgentStateEntry>? group = FindOldestEligibleExchange(
                state.Data.ConversationHistory,
                now,
                honorDeliveryWindow: true);
            if (group is null)
            {
                break;
            }

            int removedFromGroup = group.Sum(entry => entry.Messages.Count);
            normallyRemovedEntries += group.Count;
            normallyRemovedMessages += removedFromGroup;
            foreach (DurableAgentStateEntry entry in group)
            {
                _ = state.Data.ConversationHistory.Remove(entry);
            }

            RecordTruncation(state, removedFromGroup, now);
        }

        int finalSize = GetSerializedSize(state);
        int sizeAfterNormalEviction = finalSize;
        int forcedRemovedEntries = 0;
        int forcedRemovedMessages = 0;
        while (finalSize >= highWatermark && finalSize > lowWatermark)
        {
            List<DurableAgentStateEntry>? group = FindOldestEligibleExchange(
                state.Data.ConversationHistory,
                now,
                honorDeliveryWindow: false);
            if (group is null)
            {
                break;
            }

            int removedFromGroup = group.Sum(entry => entry.Messages.Count);
            forcedRemovedEntries += group.Count;
            forcedRemovedMessages += removedFromGroup;
            foreach (DurableAgentStateEntry entry in group)
            {
                _ = state.Data.ConversationHistory.Remove(entry);
            }

            RecordTruncation(state, removedFromGroup, now);
            finalSize = GetSerializedSize(state);
        }

        int removedMessages = normallyRemovedMessages + forcedRemovedMessages;
        bool failedProtectedState = finalSize >= highWatermark;
        RetentionResult result = new(
            normallyRemovedEntries,
            normallyRemovedMessages,
            forcedRemovedEntries,
            forcedRemovedMessages,
            initialSize,
            sizeAfterNormalEviction,
            finalSize,
            failedProtectedState);
        DurableAgentTelemetry.RecordRetentionAttempt(sessionId.Name, result);

        if (removedMessages > 0)
        {
            logger.LogDurableHistoryTruncated(
                sessionId,
                initialSize,
                maxStateBytes,
                removedMessages,
                finalSize);
        }

        if (forcedRemovedMessages > 0)
        {
            logger.LogDurableHistoryDeliveryResponsesEvicted(
                sessionId,
                forcedRemovedMessages,
                DeliveryWindow.TotalSeconds,
                finalSize,
                maxStateBytes);
        }

        if (failedProtectedState)
        {
            logger.LogDurableHistoryStillOverBudget(
                sessionId,
                finalSize,
                maxStateBytes);
            throw new DurableAgentStateSizeLimitExceededException(finalSize, maxStateBytes);
        }

        return result.RemovedMessageCount;
    }

    private static List<DurableAgentStateEntry>? FindOldestEligibleExchange(
        IList<DurableAgentStateEntry> history,
        DateTimeOffset now,
        bool honorDeliveryWindow)
    {
        DurableAgentStateEntry? newestCorrelatedEntry =
            history.LastOrDefault(entry => entry.CorrelationId is not null);
        IReadOnlyList<DurableAgentStateEntry> newestExchange = newestCorrelatedEntry is not null
            ? history.Where(entry => entry.CorrelationId == newestCorrelatedEntry.CorrelationId).ToList()
            : history.Count > 0
                ? [history[^1]]
                : [];
        HashSet<DurableAgentStateEntry> newestEntries = [.. newestExchange];
        DateTimeOffset deliveryCutoff = now - DeliveryWindow;

        foreach (List<DurableAgentStateEntry> group in BuildAtomicGroups(history))
        {
            if (group.Any(newestEntries.Contains) ||
                group.Any(entry => entry.Messages.Any(message => message.Role == ChatRole.System.ToString())) ||
                (honorDeliveryWindow && group.Any(IsRecentDeliveryResponse)))
            {
                continue;
            }

            return group;
        }

        return null;

        bool IsRecentDeliveryResponse(DurableAgentStateEntry entry) =>
            entry is DurableAgentStateResponse &&
            entry.CreatedAt > deliveryCutoff;
    }

    private static List<List<DurableAgentStateEntry>> BuildAtomicGroups(
        IList<DurableAgentStateEntry> history)
    {
        int[] parents = Enumerable.Range(0, history.Count).ToArray();
        Dictionary<string, int> correlationOwners = new(StringComparer.Ordinal);
        Dictionary<string, int> toolCallOwners = new(StringComparer.Ordinal);

        for (int index = 0; index < history.Count; index++)
        {
            DurableAgentStateEntry entry = history[index];
            if (entry.CorrelationId is not null)
            {
                UnionWithOwner(correlationOwners, entry.CorrelationId, index);
            }

            HashSet<string> entryToolCallIds = new(StringComparer.Ordinal);
            foreach (DurableAgentStateContent content in entry.Messages.SelectMany(message => message.Contents))
            {
                string? callId = content switch
                {
                    DurableAgentStateFunctionCallContent functionCall => functionCall.CallId,
                    DurableAgentStateFunctionResultContent functionResult => functionResult.CallId,
                    _ => null,
                };

                if (!string.IsNullOrWhiteSpace(callId) && entryToolCallIds.Add(callId))
                {
                    UnionWithOwner(toolCallOwners, callId, index);
                }
            }
        }

        Dictionary<int, List<DurableAgentStateEntry>> components = [];
        List<int> roots = [];
        for (int index = 0; index < history.Count; index++)
        {
            int root = Find(index);
            if (!components.TryGetValue(root, out List<DurableAgentStateEntry>? component))
            {
                component = [];
                components[root] = component;
                roots.Add(root);
            }

            component.Add(history[index]);
        }

        return roots.ConvertAll(root => components[root]);

        void UnionWithOwner(Dictionary<string, int> owners, string key, int index)
        {
            if (owners.TryGetValue(key, out int owner))
            {
                Union(owner, index);
            }
            else
            {
                owners[key] = index;
            }
        }

        int Find(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }

            return index;
        }

        void Union(int first, int second)
        {
            int firstRoot = Find(first);
            int secondRoot = Find(second);
            if (firstRoot == secondRoot)
            {
                return;
            }

            if (firstRoot < secondRoot)
            {
                parents[secondRoot] = firstRoot;
            }
            else
            {
                parents[firstRoot] = secondRoot;
            }
        }
    }

    private static void RecordTruncation(
        DurableAgentState state,
        int removedMessages,
        DateTimeOffset now)
    {
        DurableAgentStateTruncation truncation = state.Data.Truncation ??= new()
        {
            FirstEvictedAt = now,
        };

        truncation.EvictedMessageCount += removedMessages;
        truncation.LastEvictedAt = now;
    }
}
