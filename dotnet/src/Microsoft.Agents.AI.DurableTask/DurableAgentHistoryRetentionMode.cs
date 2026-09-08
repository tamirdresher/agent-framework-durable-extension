// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Agents.AI.DurableTask;

/// <summary>
/// Controls how durable agent conversation state is retained.
/// </summary>
public enum DurableAgentHistoryRetentionMode
{
    /// <summary>
    /// Never removes conversation entries. Persistence can fail when the backend state limit is reached.
    /// </summary>
    KeepAll,

    /// <summary>
    /// Removes the oldest eligible exchanges when serialized entity state reaches the configured high watermark.
    /// </summary>
    /// <remarks>
    /// Recent delivery responses are preferred during the first pass but can be evicted under hard pressure.
    /// The newest exchange and system messages are never evicted; if they cannot fit below the safe write
    /// threshold, the operation fails instead of persisting oversized state.
    /// </remarks>
    Auto,
}
