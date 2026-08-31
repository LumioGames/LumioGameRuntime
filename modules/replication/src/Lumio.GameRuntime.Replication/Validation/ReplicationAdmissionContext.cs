using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Lumio.GameRuntime.Replication.Validation;

/// <summary>
/// Immutable admission facts supplied by the connection/session owner before an
/// envelope is parsed or queued.
/// </summary>
public class ReplicationAdmissionContext
{
    public ReplicationAdmissionContext(
        string role,
        IReadOnlyList<string>? claims,
        ulong connectionGeneration,
        string admittedSessionId,
        string admittedProductId,
        string admittedGameReleaseId,
        string admittedRole,
        IReadOnlyList<string>? admittedClaims,
        ulong admittedConnectionGeneration)
        : this(
            null,
            null,
            null,
            null,
            role,
            claims,
            connectionGeneration,
            admittedSessionId,
            admittedProductId,
            admittedGameReleaseId,
            admittedRole,
            admittedClaims,
            admittedConnectionGeneration)
    {
    }

    /// <summary>Constructs a context carrying the complete generated gate input set.</summary>
    public ReplicationAdmissionContext(
        string? sessionId,
        string? productId,
        string? gameReleaseId,
        string? messageId,
        string? role,
        IReadOnlyList<string>? claims,
        ulong connectionGeneration,
        string? admittedSessionId,
        string? admittedProductId,
        string? admittedGameReleaseId,
        string? admittedRole,
        IReadOnlyList<string>? admittedClaims,
        ulong admittedConnectionGeneration)
    {
        SessionId = sessionId;
        ProductId = productId;
        GameReleaseId = gameReleaseId;
        MessageId = messageId;
        Role = role;
        Claims = Copy(claims);
        ConnectionGeneration = connectionGeneration;
        AdmittedSessionId = admittedSessionId;
        AdmittedProductId = admittedProductId;
        AdmittedGameReleaseId = admittedGameReleaseId;
        AdmittedRole = admittedRole;
        AdmittedClaims = Copy(admittedClaims);
        AdmittedConnectionGeneration = admittedConnectionGeneration;
    }

    public string? SessionId { get; }
    public string? ProductId { get; }
    public string? GameReleaseId { get; }
    public string? MessageId { get; }
    public string? Role { get; }
    public IReadOnlyList<string>? Claims { get; }
    public ulong ConnectionGeneration { get; }
    public string? AdmittedSessionId { get; }
    public string? AdmittedProductId { get; }
    public string? AdmittedGameReleaseId { get; }
    public string? AdmittedRole { get; }
    public IReadOnlyList<string>? AdmittedClaims { get; }
    public ulong AdmittedConnectionGeneration { get; }

    private static IReadOnlyList<string>? Copy(IReadOnlyList<string>? values)
    {
        if (values is null) return null;
        var copy = new string[values.Count];
        for (var index = 0; index < values.Count; index++) copy[index] = values[index]!;
        return new ReadOnlyCollection<string>(copy);
    }
}
