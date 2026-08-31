using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;

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
            WriteString(stream, value.SessionId);
            WriteUInt64(stream, value.ClientCommandSequence);
            WriteUInt64(stream, value.TargetTickId);
            WriteUInt64(stream, value.Generation);
            WriteUInt64(stream, (ulong)value.Length);
            byte[] payload = value.Payload;
            stream.Write(payload, 0, payload.Length);
        }

        return new CanonicalInputBatch(tickId, ordered, SimulationHash.Sha256Hex(stream.ToArray()));
    }

    private static int Compare(OpaqueIngress left, OpaqueIngress right)
    {
        int session = StringComparer.Ordinal.Compare(left.SessionId, right.SessionId);
        if (session != 0) return session;
        int sequence = left.ClientCommandSequence.CompareTo(right.ClientCommandSequence);
        if (sequence != 0) return sequence;
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
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
        WriteUInt64(stream, (ulong)bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteUInt64(System.IO.Stream stream, ulong value)
    {
        for (var shift = 0; shift < 64; shift += 8) stream.WriteByte((byte)(value >> shift));
    }
}
