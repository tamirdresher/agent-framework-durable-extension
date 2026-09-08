// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Agents.AI.DurableTask.State;

/// <summary>
/// Locates the unique terminal response for a durable request correlation.
/// </summary>
internal static class DurableAgentStateTerminalResponseLookup
{
    /// <summary>
    /// Returns the unique response or error response for <paramref name="correlationId"/>.
    /// </summary>
    public static DurableAgentStateResponse? FindUniqueTerminalResponse(
        IEnumerable<DurableAgentStateEntry> history,
        string correlationId)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(correlationId);

        DurableAgentStateResponse? match = null;
        int matchCount = 0;
        foreach (DurableAgentStateEntry entry in history)
        {
            if (entry is DurableAgentStateResponse response &&
                string.Equals(response.CorrelationId, correlationId, StringComparison.Ordinal))
            {
                match ??= response;
                matchCount++;
            }
        }

        return matchCount switch
        {
            0 => null,
            1 => match,
            _ => throw new DurableAgentStateCorruptionException(correlationId, matchCount),
        };
    }
}
