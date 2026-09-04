using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Command;

/// <summary>Idempotent command apply boundary. WorldManager owns structural ECS commit.</summary>
public sealed class EcsCommandCommitExecutor
{
    private readonly object _gate = new();
    private readonly object _applyGate = new();
    private readonly Dictionary<string, CommandApplyReceipt> _receipts = new(StringComparer.Ordinal);
    private bool _faulted;
    private readonly Action<string>? _faultSink;
    private readonly IEcsCommandCommitPort _port;

    public EcsCommandCommitExecutor(IEcsCommandCommitPort? port = null, Action<string>? faultSink = null)
    {
        _port = port ?? FailClosedPort.Instance;
        _faultSink = faultSink;
    }
    public bool IsFaulted { get { lock (_gate) return _faulted; } }

    internal CommandApplyReceipt Apply(PreparedGameDelta prepared, CommandOperationLease operation, CommandModule owner)
    {
        if (operation is null || owner is null || !operation.PermitsApply(owner)) return FaultReceipt(prepared?.TickId ?? 0, prepared?.CanonicalDigest.ToArray() ?? Array.Empty<byte>(), "InvalidArgument");
        if (prepared is null || !prepared.VerifyForApply()) return FaultReceipt(prepared?.TickId ?? 0, prepared?.CanonicalDigest.ToArray() ?? Array.Empty<byte>(), "InternalInvariant");
        lock (_applyGate)
        {
            string key = prepared.IdempotencyKey;
            lock (_gate)
            {
                if (_receipts.TryGetValue(key, out CommandApplyReceipt existing))
                    return existing with { Status = existing.Status == CommandApplyStatus.Applied ? CommandApplyStatus.AlreadyApplied : existing.Status };
                if (_faulted) return FaultReceipt(prepared.TickId, prepared.CanonicalDigest, "PanicBoundary");
            }

            int applied = 0;
            try
            {
                foreach (Command command in prepared.Commands)
                {
                    string? resolved = null;
                    if (command.Kind != CommandKind.Create && command.DeferredTarget is DeferredEntityToken token)
                    {
                        if (!prepared.ResolutionPlan.TryResolve(token, prepared.TickId, out resolved))
                            return FaultReceipt(prepared.TickId, prepared.CanonicalDigest, "InternalInvariant");
                    }

                    EcsCommandPortResult result = _port.Apply(command, resolved);
                    if (result.Status is EcsCommandPortStatus.Rejected or EcsCommandPortStatus.InfrastructureFault or
                        EcsCommandPortStatus.Faulted or EcsCommandPortStatus.Indeterminate)
                        return FaultReceipt(prepared.TickId, prepared.CanonicalDigest, result.GeneratedErrorId ?? "PanicBoundary");

                    if (command.Kind == CommandKind.Create && command.DeferredTarget is DeferredEntityToken createToken)
                    {
                        if (result.ResolvedEntityId is not string resolvedEntityId ||
                            !prepared.ResolutionPlan.TrySet(createToken, resolvedEntityId, out _))
                            return FaultReceipt(prepared.TickId, prepared.CanonicalDigest, "InternalInvariant");
                    }

                    applied++;
                }

                prepared.Reservations.Commit();
                prepared.Batch.MarkApplied();
                var receipt = new CommandApplyReceipt(CommandApplyStatus.Applied, prepared.TickId,
                    prepared.CanonicalDigest.ToArray(), applied, null,
                    new CommandChangeSet(prepared.CanonicalDigest, prepared.Commands));
                lock (_gate) _receipts[key] = receipt;
                return receipt;
            }
            catch (Exception)
            {
                return FaultReceipt(prepared.TickId, prepared.CanonicalDigest, "PanicBoundary");
            }
        }
    }

    private CommandApplyReceipt FaultReceipt(ulong tick, ReadOnlyMemory<byte> digest, string error)
    {
        var receipt = new CommandApplyReceipt(CommandApplyStatus.InfrastructureFault, tick, digest.ToArray(), 0, error);
        lock (_gate) { _faulted = true; _receipts[string.Concat(tick.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", CommandHashing.ToHex(digest.ToArray()))] = receipt; }
        try { _faultSink?.Invoke(error); } catch (Exception) { }
        return receipt;
    }

    private sealed class FailClosedPort : IEcsCommandCommitPort
    {
        internal static readonly FailClosedPort Instance = new();
        public EcsCommandPortResult Apply(Command command, string? resolvedEntityId) =>
            EcsCommandPortResult.Fault("CapabilityMissing");
    }
}
