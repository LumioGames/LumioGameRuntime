namespace Lumio.GameRuntime.Hello;

/// <summary>Rejection codes produced by the hello runtime; each maps to an errorCodes string of lumio.hello-wire.v1.</summary>
public enum HelloRuntimeErrorCode
{
    /// <summary>Sender is not one of the contract roles (browser|bot).</summary>
    UnknownRole,

    /// <summary>Command violates the hello envelope shape (kind, payload presence or size).</summary>
    BadEnvelope,

    /// <summary>payloadSha256 does not equal the lowercase-hex SHA-256 of the payload UTF-8 bytes.</summary>
    BadPayloadHash,

    /// <summary>sequence is not strictly greater than the highest sequence already seen for the sender.</summary>
    DuplicateSequence,

    /// <summary>The internal ingress queue is at capacity.</summary>
    QueueFull,
}

/// <summary>A single authoritative rejection reason; never thrown, always returned.</summary>
/// <param name="Code">Machine-readable code to surface on the wire.</param>
public readonly record struct HelloRuntimeError(HelloRuntimeErrorCode Code)
{
    /// <summary>Wire error code string defined by the errorCodes vocabulary of lumio.hello-wire.v1.</summary>
    public string WireCode => Code switch
    {
        HelloRuntimeErrorCode.UnknownRole => "unknown_role",
        HelloRuntimeErrorCode.BadEnvelope => "bad_envelope",
        HelloRuntimeErrorCode.BadPayloadHash => "bad_payload_hash",
        HelloRuntimeErrorCode.DuplicateSequence => "duplicate_sequence",
        HelloRuntimeErrorCode.QueueFull => "queue_full",
        _ => throw new System.ArgumentOutOfRangeException(nameof(Code)),
    };
}
