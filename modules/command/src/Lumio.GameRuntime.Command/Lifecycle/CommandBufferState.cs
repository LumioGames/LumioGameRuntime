using System;

namespace Lumio.GameRuntime.Command;

public enum CommandBufferState
{
    Open,
    Sealed,
    Merged,
    Prepared,
    Applied,
    Discarded,
    Faulted
}

public enum CommandAppendStatus
{
    Accepted,
    Rejected,
    Retryable,
    Fatal
}

public readonly record struct CommandAppendResult(
    CommandAppendStatus Status,
    ulong LocalSequence,
    string? GeneratedErrorId)
{
    public bool IsAccepted => Status == CommandAppendStatus.Accepted;

    public static CommandAppendResult Accepted(ulong sequence) =>
        new(CommandAppendStatus.Accepted, sequence, null);

    public static CommandAppendResult Rejected(string errorId) =>
        new(CommandAppendStatus.Rejected, 0UL, errorId);
}

public readonly record struct CommandBufferTransitionResult(
    bool Succeeded,
    CommandBufferState State,
    string? GeneratedErrorId)
{
    public static CommandBufferTransitionResult Success(CommandBufferState state) =>
        new(true, state, null);

    public static CommandBufferTransitionResult Failure(CommandBufferState state, string errorId) =>
        new(false, state, errorId);
}
