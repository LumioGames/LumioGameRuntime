using System;
using System.Collections.Generic;
using System.Linq;

namespace Lumio.GameRuntime.Command;

public readonly record struct CommandMergeResult(
    bool Succeeded,
    MergedCommandBatch? Batch,
    string? GeneratedErrorId)
{
    public CommandFailure? Failure { get; init; }

    public CommandMergeStatus Status { get; init; }

    public static CommandMergeResult Success(MergedCommandBatch batch) => new(true, batch, null)
    {
        Status = CommandMergeStatus.Merged
    };

    public static CommandMergeResult Rejected(string errorId) => new(false, null, errorId)
    {
        Status = CommandMergeStatus.Rejected,
        Failure = CommandFailure.Rejected(errorId, "Command buffers could not be merged.")
    };

    public static CommandMergeResult Rejected(string errorId, Command command, string detail) => new(false, null, errorId)
    {
        Status = CommandMergeStatus.Rejected,
        Failure = CommandFailure.Rejected(errorId, detail, command.CanonicalDigestHex).WithFirstCommand(command)
    };

    public static CommandMergeResult Retryable(string errorId) => new(false, null, errorId)
    {
        Status = CommandMergeStatus.Retryable,
        Failure = CommandFailure.Retryable(errorId, "Command buffers are not ready to merge.")
    };

    public static CommandMergeResult Fatal(string errorId, string detail = "Command buffers could not be merged safely.") => new(false, null, errorId)
    {
        Status = CommandMergeStatus.Fatal,
        Failure = CommandFailure.Fatal(errorId, detail)
    };
}

public enum CommandMergeStatus
{
    Merged,
    Rejected,
    Retryable,
    Fatal
}

public sealed class CommandMergeException : InvalidOperationException
{
    public CommandMergeException(string generatedErrorId)
        : base(generatedErrorId)
    {
        GeneratedErrorId = generatedErrorId;
    }

    public string GeneratedErrorId { get; }
}

public sealed class CommandBufferMerger
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822")]
    public CommandMergeResult TryMergeResult(ulong tickId, IEnumerable<SealedCommandBuffer> buffers) =>
        TryMerge(tickId, buffers);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822")]
    public MergedCommandBatch Merge(ulong tickId, IEnumerable<SealedCommandBuffer> buffers)
    {
        CommandMergeResult result = TryMerge(tickId, buffers);
        if (!result.Succeeded || result.Batch is null) throw new CommandMergeException(result.GeneratedErrorId ?? "InvalidArgument");
        return result.Batch;
    }

    public static CommandMergeResult TryMerge(ulong tickId, IEnumerable<SealedCommandBuffer> buffers)
    {
        if (buffers is null) return CommandMergeResult.Rejected("InvalidArgument");

        List<SealedCommandBuffer> source;
        try { source = buffers.ToList(); }
        catch (Exception ex)
        {
            return CommandMergeResult.Fatal("PanicBoundary", ex.Message);
        }

        var processorIds = new HashSet<string>(StringComparer.Ordinal);
        string? worldId = null;
        var commands = new List<Command>();
        foreach (SealedCommandBuffer buffer in source)
        {
            if (buffer is null || buffer.TickId != tickId)
            {
                return CommandMergeResult.Rejected("WrongContext");
            }

            if (!buffer.IsSealed) return CommandMergeResult.Retryable("ContextClosing");

            worldId ??= buffer.WorldId;
            if (!string.Equals(worldId, buffer.WorldId, StringComparison.Ordinal))
                return CommandMergeResult.Rejected("WrongContext");

            if (!processorIds.Add(buffer.ProcessorId))
            {
                return CommandMergeResult.Rejected("InvalidArgument");
            }

            ulong previous = 0UL;
            bool first = true;
            ulong computedBytes = 0UL;
            try
            {
                foreach (Command command in buffer.Commands)
                {
                    if (command is null || command.SortKey.Phase != buffer.Phase ||
                        !string.Equals(command.SortKey.ProcessorId, buffer.ProcessorId, StringComparison.Ordinal))
                    {
                        return CommandMergeResult.Rejected("WrongContext");
                    }

                    if ((first && command.SortKey.LocalSequence != 1UL) || (!first && command.SortKey.LocalSequence <= previous))
                    {
                        return CommandMergeResult.Rejected("InvalidArgument");
                    }

                    if (command.Kind is not (CommandKind.Create or CommandKind.Write or CommandKind.Destroy) ||
                        command.EstimatedBytes == 0UL)
                    {
                        return CommandMergeResult.Rejected("ManifestMalformed");
                    }

                    if (command.IsStructural &&
                        (!buffer.MayEmitStructuralCommands || !CommandValidation.IsStructuralPhase(buffer.Phase)))
                    {
                        return CommandMergeResult.Rejected("MessagePermissionDenied");
                    }

                    first = false;
                    previous = command.SortKey.LocalSequence;
                    computedBytes = checked(computedBytes + command.EstimatedBytes);
                    commands.Add(command);
                }
            }
            catch (OverflowException)
            {
                return CommandMergeResult.Fatal("CapacityExceeded", "Command byte accounting overflowed.");
            }

            if (computedBytes != buffer.Bytes) return CommandMergeResult.Rejected("InternalInvariant");
        }

        commands.Sort(static (left, right) => left.SortKey.CompareTo(right.SortKey));
        MergedCommandBatch batch = new(tickId, source, commands);
        try
        {
            foreach (SealedCommandBuffer buffer in source) buffer.MarkMerged();
        }
        catch (Exception ex)
        {
            return CommandMergeResult.Fatal("InternalInvariant", ex.Message);
        }
        return CommandMergeResult.Success(batch);
    }
}
