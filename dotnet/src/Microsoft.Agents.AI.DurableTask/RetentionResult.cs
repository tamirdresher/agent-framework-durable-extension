// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Agents.AI.DurableTask;

internal enum RetentionOutcome
{
    NoAction,
    Evicted,
    ForcedDeliveryEviction,
    FailedProtectedState,
}

internal sealed record RetentionResult(
    int NormallyRemovedEntryCount,
    int NormallyRemovedMessageCount,
    int ForcedRemovedEntryCount,
    int ForcedRemovedMessageCount,
    int InitialSizeBytes,
    int SizeAfterNormalEvictionBytes,
    int FinalSizeBytes,
    bool FailedProtectedState)
{
    public int RemovedEntryCount =>
        this.NormallyRemovedEntryCount + this.ForcedRemovedEntryCount;

    public int RemovedMessageCount =>
        this.NormallyRemovedMessageCount + this.ForcedRemovedMessageCount;

    public bool ForcedDeliveryWindowOverride => this.ForcedRemovedEntryCount > 0;

    public RetentionOutcome Outcome => this.FailedProtectedState
        ? RetentionOutcome.FailedProtectedState
        : this.ForcedDeliveryWindowOverride
            ? RetentionOutcome.ForcedDeliveryEviction
            : this.RemovedEntryCount > 0 || this.FinalSizeBytes < this.InitialSizeBytes
                ? RetentionOutcome.Evicted
                : RetentionOutcome.NoAction;
}
