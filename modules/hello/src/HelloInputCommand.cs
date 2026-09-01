namespace Lumio.GameRuntime.Hello;

/// <summary>Immutable hello input command as submitted by a client role; wire InputCommand without messageType.</summary>
/// <param name="Sender">Client role owning the command (browser|bot).</param>
/// <param name="Sequence">Per-sender monotonic sequence, starting at 1.</param>
/// <param name="Kind">Command kind; only "hello" exists in this milestone.</param>
/// <param name="Payload">UTF-8 text payload; non-empty and at most 4096 bytes.</param>
/// <param name="PayloadSha256">Lowercase-hex SHA-256 of the payload UTF-8 bytes, computed by the sender.</param>
/// <param name="SentAtMs">Sender UTC epoch milliseconds at send time.</param>
public sealed record HelloInputCommand(
    string Sender,
    ulong Sequence,
    string Kind,
    string Payload,
    string PayloadSha256,
    long SentAtMs);
