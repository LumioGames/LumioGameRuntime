using Lumio.GameRuntime.GeneratedContracts;

namespace Lumio.GameRuntime.Config;

/// <summary>Tick-stable identity of one immutable config snapshot.</summary>
/// <param name="Value">Opaque snapshot identity assigned at construct/stage time.</param>
public readonly record struct ConfigSnapshotId(ulong Value);

/// <summary>Generated architecture schema epoch carried by a snapshot.</summary>
/// <param name="Value">Epoch integer from the generated contract manifest.</param>
public readonly record struct SchemaEpoch(int Value)
{
    /// <summary>Epoch declared by the locked generated contract manifest.</summary>
    public static SchemaEpoch FromGeneratedContracts() =>
        new(GeneratedContractManifest.SchemaEpoch);
}

/// <summary>Logical tick identity consumed at the config barrier. Not a Host clock.</summary>
/// <param name="Value">Monotonic logical tick number.</param>
public readonly record struct TickId(ulong Value)
{
    /// <summary>Construct from a canonical unsigned integer.</summary>
    public static TickId FromUInt64(ulong value) => new(value);
}

/// <summary>
/// Tick-local immutable config read surface. Implementations must not return
/// mutable dictionaries/arrays or alias caller-owned input storage.
/// </summary>
public interface IConfigSnapshotView
{
    /// <summary>Identity pinned for this snapshot.</summary>
    ConfigSnapshotId SnapshotId { get; }

    /// <summary>Generated schema epoch recorded at construct time.</summary>
    SchemaEpoch SchemaEpoch { get; }

    /// <summary>Open a typed reader for a table id. Missing tables return false.</summary>
    /// <param name="tableName">Generated table id.</param>
    /// <param name="reader">Reader bound to this snapshot's immutable storage.</param>
    /// <returns>True when the table exists.</returns>
    bool TryOpenTable(string tableName, out ConfigTableReader reader);
}
