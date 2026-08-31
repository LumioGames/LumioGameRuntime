using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Lumio.GameRuntime.Simulation.Native;

public sealed class NativeCompletionBatch
{
    internal NativeCompletionBatch(ulong generation, IList<NativeCompletion> items, string canonicalHashHex)
    {
        Generation = generation;
        Items = new ReadOnlyCollection<NativeCompletion>(items);
        CanonicalHashHex = canonicalHashHex;
    }

    public ulong Generation { get; }

    public IReadOnlyList<NativeCompletion> Items { get; }

    public string CanonicalHashHex { get; }
}

public static class NativeCompletionMerger
{
    public static NativeCompletionBatch Merge(IEnumerable<NativeCompletion> completions, ulong generation)
    {
        if (completions is null) throw new ArgumentNullException(nameof(completions));
        var values = completions.Where(value => value.Generation == generation).Select(value => value.Snapshot()).ToList();
        values.Sort((left, right) =>
        {
            int job = StringComparer.Ordinal.Compare(left.JobId, right.JobId);
            return job != 0 ? job : StringComparer.Ordinal.Compare(left.Token, right.Token);
        });

        using var stream = new System.IO.MemoryStream();
        foreach (NativeCompletion value in values)
        {
            Write(stream, value.JobId);
            Write(stream, value.Token);
            byte[] payload = value.Payload.ToArray();
            WriteUInt64(stream, (ulong)payload.Length);
            stream.Write(payload, 0, payload.Length);
        }

        return new NativeCompletionBatch(generation, values, SimulationHash.Sha256Hex(stream.ToArray()));
    }

    private static void Write(System.IO.Stream stream, string value)
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
