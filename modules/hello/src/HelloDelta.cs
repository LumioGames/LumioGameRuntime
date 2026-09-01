namespace Lumio.GameRuntime.Hello;

/// <summary>Committed delta broadcast shape; wire Delta without messageType: HelloRecord fields plus commandSequence.</summary>
/// <param name="Sender">Client role whose command produced the committed change.</param>
/// <param name="Sequence">Sequence of the committed command.</param>
/// <param name="Kind">Command kind ("hello").</param>
/// <param name="Payload">Committed payload text.</param>
/// <param name="PayloadSha256">Verified lowercase-hex SHA-256 of the payload UTF-8 bytes.</param>
/// <param name="TickId">Authoritative tick that committed the change.</param>
/// <param name="Revision">Global revision after committing the change.</param>
/// <param name="OriginSentAtMs">sentAtMs of the originating command.</param>
/// <param name="CommittedAtMs">UTC epoch milliseconds at the committing tick.</param>
/// <param name="CommandSequence">Sequence of the InputCommand that triggered this commit.</param>
public sealed record HelloDelta(
    string Sender,
    ulong Sequence,
    string Kind,
    string Payload,
    string PayloadSha256,
    ulong TickId,
    ulong Revision,
    long OriginSentAtMs,
    long CommittedAtMs,
    ulong CommandSequence);
