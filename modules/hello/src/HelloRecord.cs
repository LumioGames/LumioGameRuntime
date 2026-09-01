namespace Lumio.GameRuntime.Hello;

/// <summary>Authoritative committed hello record; field set matches sharedTypes.HelloRecord of lumio.hello-wire.v1.</summary>
/// <param name="Sender">Client role whose command produced this record.</param>
/// <param name="Sequence">Sequence of the committed command.</param>
/// <param name="Kind">Command kind ("hello").</param>
/// <param name="Payload">Committed payload text.</param>
/// <param name="PayloadSha256">Verified lowercase-hex SHA-256 of the payload UTF-8 bytes.</param>
/// <param name="TickId">Authoritative tick that committed the record.</param>
/// <param name="Revision">Global revision after committing the record.</param>
/// <param name="OriginSentAtMs">sentAtMs of the originating command.</param>
/// <param name="CommittedAtMs">UTC epoch milliseconds at the committing tick.</param>
public sealed record HelloRecord(
    string Sender,
    ulong Sequence,
    string Kind,
    string Payload,
    string PayloadSha256,
    ulong TickId,
    ulong Revision,
    long OriginSentAtMs,
    long CommittedAtMs);
