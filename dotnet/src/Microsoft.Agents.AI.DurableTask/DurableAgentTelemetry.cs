// Copyright (c) Microsoft. All rights reserved.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

namespace Microsoft.Agents.AI.DurableTask;

/// <summary>
/// Provides telemetry identifiers for durable agents.
/// </summary>
public static class DurableAgentTelemetry
{
    /// <summary>
    /// Gets the name of the meter that emits durable-agent metrics.
    /// </summary>
    public const string MeterName = "Microsoft.Agents.AI.DurableTask";

    internal const string EvictedMessagesInstrumentName =
        "durable.agent.history.evicted.messages";
    internal const string EvictedEntriesInstrumentName =
        "durable.agent.history.evicted.entries";
    internal const string ReclaimedBytesInstrumentName =
        "durable.agent.history.reclaimed.bytes";
    internal const string StateSizeBeforeInstrumentName =
        "durable.agent.history.state.size.before";
    internal const string StateSizeAfterInstrumentName =
        "durable.agent.history.state.size.after";
    internal const string RetentionOperationsInstrumentName =
        "durable.agent.history.retention.operations";

    internal const string AgentNameTagName = "agent.name";
    internal const string OutcomeTagName = "outcome";
    internal const string ReasonTagName = "reason";

    internal const string NoActionOutcome = "no_action";
    internal const string EvictedOutcome = "evicted";
    internal const string ForcedDeliveryEvictionOutcome = "forced_delivery_eviction";
    internal const string FailedProtectedStateOutcome = "failed_protected_state";

    internal const string PressureReason = "pressure";
    internal const string DeliveryProtectionOverrideReason = "delivery_protection_override";

    private static readonly Meter s_meter = new(
        MeterName,
        typeof(DurableAgentTelemetry).Assembly.GetName().Version?.ToString());
    private static readonly Counter<long> s_evictedMessages =
        s_meter.CreateCounter<long>(
            EvictedMessagesInstrumentName,
            unit: "{message}",
            description: "Number of messages removed from durable agent history.");
    private static readonly Counter<long> s_evictedEntries =
        s_meter.CreateCounter<long>(
            EvictedEntriesInstrumentName,
            unit: "{entry}",
            description: "Number of entries removed from durable agent history.");
    private static readonly Counter<long> s_reclaimedBytes =
        s_meter.CreateCounter<long>(
            ReclaimedBytesInstrumentName,
            unit: "By",
            description: "Net serialized durable-state bytes reclaimed by history retention.");
    private static readonly Histogram<long> s_stateSizeBefore =
        s_meter.CreateHistogram<long>(
            StateSizeBeforeInstrumentName,
            unit: "By",
            description: "Serialized durable-agent state size before a pressure-retention attempt.");
    private static readonly Histogram<long> s_stateSizeAfter =
        s_meter.CreateHistogram<long>(
            StateSizeAfterInstrumentName,
            unit: "By",
            description: "Serialized durable-agent state size after a pressure-retention attempt.");
    private static readonly Counter<long> s_retentionOperations =
        s_meter.CreateCounter<long>(
            RetentionOperationsInstrumentName,
            unit: "{operation}",
            description: "Number of automatic durable-agent history retention checks by outcome.");

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Telemetry must never affect durable agent execution.")]
    [SuppressMessage(
        "Roslynator",
        "RCS1075:Avoid empty catch clause that catches System.Exception",
        Justification = "Telemetry must never affect durable agent execution.")]
    internal static void RecordNoAction(string agentName)
    {
        if (!s_retentionOperations.Enabled)
        {
            return;
        }

        try
        {
            TagList tags = default;
            tags.Add(AgentNameTagName, agentName);
            tags.Add(OutcomeTagName, NoActionOutcome);
            s_retentionOperations.Add(1, tags);
        }
        catch (Exception)
        {
            // Metrics are best-effort operational telemetry.
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Telemetry must never affect durable agent execution.")]
    [SuppressMessage(
        "Roslynator",
        "RCS1075:Avoid empty catch clause that catches System.Exception",
        Justification = "Telemetry must never affect durable agent execution.")]
    internal static void RecordRetentionAttempt(
        string agentName,
        RetentionResult result)
    {
        if (!s_evictedMessages.Enabled &&
            !s_evictedEntries.Enabled &&
            !s_reclaimedBytes.Enabled &&
            !s_stateSizeBefore.Enabled &&
            !s_stateSizeAfter.Enabled &&
            !s_retentionOperations.Enabled)
        {
            return;
        }

        try
        {
            string outcome = result.Outcome switch
            {
                RetentionOutcome.Evicted => EvictedOutcome,
                RetentionOutcome.ForcedDeliveryEviction => ForcedDeliveryEvictionOutcome,
                RetentionOutcome.FailedProtectedState => FailedProtectedStateOutcome,
                _ => NoActionOutcome,
            };

            if (s_stateSizeBefore.Enabled || s_stateSizeAfter.Enabled)
            {
                TagList sizeTags = default;
                sizeTags.Add(AgentNameTagName, agentName);
                sizeTags.Add(OutcomeTagName, outcome);
                s_stateSizeBefore.Record(result.InitialSizeBytes, sizeTags);
                s_stateSizeAfter.Record(result.FinalSizeBytes, sizeTags);
            }

            RecordEviction(
                agentName,
                PressureReason,
                result.NormallyRemovedEntryCount,
                result.NormallyRemovedMessageCount,
                Math.Max(0, result.InitialSizeBytes - result.SizeAfterNormalEvictionBytes));
            RecordEviction(
                agentName,
                DeliveryProtectionOverrideReason,
                result.ForcedRemovedEntryCount,
                result.ForcedRemovedMessageCount,
                Math.Max(0, result.SizeAfterNormalEvictionBytes - result.FinalSizeBytes));

            if (s_retentionOperations.Enabled)
            {
                TagList operationTags = default;
                operationTags.Add(AgentNameTagName, agentName);
                operationTags.Add(OutcomeTagName, outcome);
                s_retentionOperations.Add(1, operationTags);
            }
        }
        catch (Exception)
        {
            // Metrics are best-effort operational telemetry.
        }
    }

    private static void RecordEviction(
        string agentName,
        string reason,
        int evictedEntries,
        int evictedMessages,
        int reclaimedBytes)
    {
        if (evictedEntries <= 0 && evictedMessages <= 0 && reclaimedBytes <= 0)
        {
            return;
        }

        TagList tags = default;
        tags.Add(AgentNameTagName, agentName);
        tags.Add(ReasonTagName, reason);
        if (evictedEntries > 0)
        {
            s_evictedEntries.Add(evictedEntries, tags);
        }

        if (evictedMessages > 0)
        {
            s_evictedMessages.Add(evictedMessages, tags);
        }

        if (reclaimedBytes > 0)
        {
            s_reclaimedBytes.Add(reclaimedBytes, tags);
        }
    }
}
