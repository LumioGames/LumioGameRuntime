using System.Collections.Generic;

namespace Lumio.GameRuntime.Hello;

/// <summary>Full authoritative hello state; wire FullSnapshot without messageType/sessionId.</summary>
/// <param name="TickId">Current authoritative tick counter.</param>
/// <param name="Revision">Current revision, 0 on the empty baseline.</param>
/// <param name="HelloLog">Most recent committed records, bounded to <see cref="HelloRuntime.HelloLogCapacity"/> entries.</param>
public sealed record HelloFullSnapshot(ulong TickId, ulong Revision, IReadOnlyList<HelloRecord> HelloLog);
