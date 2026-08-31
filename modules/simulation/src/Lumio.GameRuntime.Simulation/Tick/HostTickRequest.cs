using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Lumio.GameRuntime.Simulation.Ingress;
using Lumio.GameRuntime.Simulation.Session;

namespace Lumio.GameRuntime.Simulation.Tick;

/// <summary>Host-provided logical tick request. Payloads remain opaque to the simulation boundary.</summary>
public readonly record struct HostTickRequest
{
    private readonly OpaqueIngressView[] _inputs;
    private readonly bool _hasExplicitSeed;

    public HostTickRequest(ulong tickId, ulong epoch, IReadOnlyList<OpaqueIngressView> inputs)
        : this(
            tickId,
            epoch,
            0,
            Lumio.GameRuntime.GeneratedContracts.GeneratedContractManifest.SchemaEpoch,
            inputs,
            TickExecutionControl.ForTick(tickId),
            false)
    {
    }

    public HostTickRequest(ulong tickId, ulong epoch, ulong seed, int schemaEpoch, IReadOnlyList<OpaqueIngressView> inputs)
        : this(tickId, epoch, seed, schemaEpoch, inputs, TickExecutionControl.ForTick(tickId), true)
    {
    }

    public HostTickRequest(
        ulong tickId,
        ulong epoch,
        ulong seed,
        int schemaEpoch,
        IReadOnlyList<OpaqueIngressView> inputs,
        TickExecutionControl executionControl)
        : this(tickId, epoch, seed, schemaEpoch, inputs, executionControl, true)
    {
    }

    private HostTickRequest(
        ulong tickId,
        ulong epoch,
        ulong seed,
        int schemaEpoch,
        IReadOnlyList<OpaqueIngressView> inputs,
        TickExecutionControl executionControl,
        bool hasExplicitSeed)
    {
        TickId = tickId;
        Epoch = new SessionEpoch(epoch);
        Seed = seed;
        SchemaEpoch = schemaEpoch;
        ExecutionControl = executionControl;
        _hasExplicitSeed = hasExplicitSeed;
        _inputs = inputs is null ? Array.Empty<OpaqueIngressView>() : inputs.Select(Snapshot).ToArray();
    }

    public ulong TickId { get; }

    public SessionEpoch Epoch { get; }

    public ulong Seed { get; }

    public int SchemaEpoch { get; }

    public TickExecutionControl ExecutionControl { get; }

    public IReadOnlyList<OpaqueIngressView> Inputs => _inputs ?? Array.Empty<OpaqueIngressView>();

    public string ComputeCanonicalHashHex()
    {
        CanonicalInputBatch batch = CanonicalizeInputs();
        using SHA256 sha = SHA256.Create();
        byte[] seed = Encoding.UTF8.GetBytes(string.Concat(Epoch.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", Seed.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", SchemaEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", batch.CanonicalHashHex));
        return ToHex(sha.ComputeHash(seed));
    }

    internal CanonicalInputBatch CanonicalizeInputs()
    {
        var values = new List<OpaqueIngress>();
        foreach (OpaqueIngressView input in Inputs) values.Add(input.ToIngress());
        return InputCanonicalizer.Canonicalize(TickId, values);
    }

    public bool IsWellFormed => TickId != 0 && Epoch.IsValid && SchemaEpoch >= 0;

    internal bool HasExplicitSeed => _hasExplicitSeed;

    internal HostTickRequest WithAuthoritativeSeed(ulong seed) =>
        new(TickId, Epoch.Value, seed, SchemaEpoch, Inputs, ExecutionControl, true);

    internal bool TryValidateIngress(int maxItems, long maxBytes, out string errorId, out string detail)
    {
        if (Inputs.Count > maxItems)
        {
            errorId = "CapacityExceeded";
            detail = "The captured ingress item limit was exceeded.";
            return false;
        }

        long bytes = 0;
        foreach (OpaqueIngressView input in Inputs)
        {
            if (!SimulationValidation.IsIdentifier(input.SessionId) ||
                input.ClientCommandSequence == 0 ||
                input.TargetTickId == 0 ||
                input.Generation == 0 ||
                input.PayloadWasNull)
            {
                errorId = "ManifestMalformed";
                detail = "Captured ingress contains an invalid identity or payload.";
                return false;
            }

            byte[] payload = input.Payload;
            if (payload.Length > maxBytes - bytes)
            {
                errorId = "CapacityExceeded";
                detail = "The captured ingress byte limit was exceeded.";
                return false;
            }

            bytes += payload.Length;
        }

        errorId = string.Empty;
        detail = string.Empty;
        return true;
    }

    private static OpaqueIngressView Snapshot(OpaqueIngressView value) =>
        new(value.SessionId, value.ClientCommandSequence, value.TargetTickId, value.Generation, value.PayloadWasNull ? null! : value.Payload);

    private static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) builder.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}
