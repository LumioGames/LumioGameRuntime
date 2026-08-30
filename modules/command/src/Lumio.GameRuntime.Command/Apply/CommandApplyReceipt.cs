using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Command;

public enum CommandApplyStatus
{
    Applied,
    AlreadyApplied,
    Indeterminate,
    Faulted,
    InfrastructureFault
}

public readonly record struct CommandApplyReceipt(
    CommandApplyStatus Status,
    ulong TickId,
    ReadOnlyMemory<byte> CanonicalDigest,
    int AppliedCommandCount,
    string? GeneratedErrorId,
    CommandChangeSet? ChangeSet = null)
{
    public bool IsApplied => Status is CommandApplyStatus.Applied or CommandApplyStatus.AlreadyApplied;

    public bool IsIndeterminate => Status is CommandApplyStatus.Indeterminate or CommandApplyStatus.Faulted or CommandApplyStatus.InfrastructureFault;

    public string CanonicalDigestHex => CommandHashing.ToHex(CanonicalDigest.ToArray());

    public string ChangeSetHash => CanonicalDigestHex;
}

public sealed class CommandChangeSet
{
    private readonly IReadOnlyList<Command> _commands;

    public CommandChangeSet(ReadOnlyMemory<byte> canonicalDigest, IEnumerable<Command> commands)
    {
        CanonicalDigest = canonicalDigest.ToArray();
        _commands = new List<Command>(commands).AsReadOnly();
    }

    public ReadOnlyMemory<byte> CanonicalDigest { get; }

    public IReadOnlyList<Command> Commands => _commands;

    public string Hash => CommandHashing.ToHex(CanonicalDigest.ToArray());
}

public enum EcsCommandPortStatus
{
    Applied,
    AlreadyApplied,
    Rejected,
    InfrastructureFault,
    Indeterminate,
    Faulted
}

public readonly record struct EcsCommandPortResult(
    EcsCommandPortStatus Status,
    string? ResolvedEntityId,
    string? GeneratedErrorId)
{
    public static EcsCommandPortResult Applied(string? entityId = null) => new(EcsCommandPortStatus.Applied, entityId, null);

    public static EcsCommandPortResult AlreadyApplied(string? entityId = null) => new(EcsCommandPortStatus.AlreadyApplied, entityId, null);

    public static EcsCommandPortResult Rejected(string errorId) => new(EcsCommandPortStatus.Rejected, null, errorId);

    public static EcsCommandPortResult Fault(string errorId = "PanicBoundary") => new(EcsCommandPortStatus.InfrastructureFault, null, errorId);

    public static EcsCommandPortResult Indeterminate(string errorId = "PanicBoundary") =>
        new(EcsCommandPortStatus.Indeterminate, null, errorId);

    public static EcsCommandPortResult Faulted(string errorId = "PanicBoundary") =>
        new(EcsCommandPortStatus.Faulted, null, errorId);
}

public interface IEcsCommandCommitPort
{
    EcsCommandPortResult Apply(Command command, string? resolvedEntityId);
}
