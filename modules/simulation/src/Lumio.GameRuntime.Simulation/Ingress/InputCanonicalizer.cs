using System;
using System.Collections.Generic;
using System.Linq;
using Lumio.Gen.ContractTypes;
using Lumio.Gen.ProtocolPermissionValidator;

namespace Lumio.GameRuntime.Simulation.Ingress;

public static class InputCanonicalizer
{
    public static CanonicalInputBatch Canonicalize(ulong tickId, IEnumerable<OpaqueIngress> values)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        var ordered = values.Select(value => value.Snapshot()).ToList();
        ordered.Sort(Compare);
        using var stream = new System.IO.MemoryStream();
        WriteUInt64(stream, tickId);
        foreach (OpaqueIngress value in ordered)
        {
            WriteUInt64(stream, (ulong)(int)value.ArrivalClass);
            WriteString(stream, value.SessionId);
            WriteString(stream, value.CommandId);
            WriteUInt64(stream, value.ClientCommandSequence);
            WriteUInt64(stream, value.TargetTickId);
            WriteUInt64(stream, value.Generation);
            WriteUInt64(stream, (ulong)value.Length);
            byte[] payload = value.Payload;
            stream.Write(payload, 0, payload.Length);
        }

        return new CanonicalInputBatch(tickId, ordered, SimulationHash.Sha256Hex(stream.ToArray()));
    }

    public static bool TryValidate(in OpaqueIngress value, long maxBytes, out IngressEnqueueStatus status)
    {
        if (!SimulationValidation.IsIdentifier(value.SessionId) ||
            !SimulationValidation.IsIdentifier(value.CommandId) ||
            value.Payload is null ||
            value.Length <= 0)
        {
            status = IngressEnqueueStatus.Invalid;
            return false;
        }

        if (maxBytes <= 0 || value.Length > maxBytes)
        {
            status = IngressEnqueueStatus.Rejected;
            return false;
        }

        if (ProtocolGate.RegisteredMessageIds.Length == 0 || Catalog.StableErrorIds.Length == 0)
        {
            status = IngressEnqueueStatus.Invalid;
            return false;
        }

        status = IngressEnqueueStatus.Accepted;
        return true;
    }

    private static int Compare(OpaqueIngress left, OpaqueIngress right)
    {
        int arrival = left.ArrivalClass.CompareTo(right.ArrivalClass);
        if (arrival != 0) return arrival;
        int sequence = left.ClientCommandSequence.CompareTo(right.ClientCommandSequence);
        if (sequence != 0) return sequence;
        int command = StringComparer.Ordinal.Compare(left.CommandId, right.CommandId);
        if (command != 0) return command;
        int session = StringComparer.Ordinal.Compare(left.SessionId, right.SessionId);
        if (session != 0) return session;
        int targetTick = left.TargetTickId.CompareTo(right.TargetTickId);
        if (targetTick != 0) return targetTick;
        int generation = left.Generation.CompareTo(right.Generation);
        if (generation != 0) return generation;
        return CompareBytes(left.Payload, right.Payload);
    }

    private static int CompareBytes(byte[] left, byte[] right)
    {
        int length = Math.Min(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            int result = left[i].CompareTo(right[i]);
            if (result != 0) return result;
        }

        return left.Length.CompareTo(right.Length);
    }

    private static void WriteString(System.IO.Stream stream, string value)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
        WriteUInt64(stream, (ulong)bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteUInt64(System.IO.Stream stream, ulong value)
    {
        for (var shift = 0; shift < 64; shift += 8) stream.WriteByte((byte)(value >> shift));
    }
}
