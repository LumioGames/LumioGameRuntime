using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Lumio.GameRuntime.Coordination;

internal sealed class SessionCoordinationContext
{
    private static readonly ConditionalWeakTable<SessionRevisionVectorStore, SessionCoordinationContext> Contexts = new();
    private readonly object _gate = new();
    private long _nextToken;
    private long _activeToken;
    private string? _activeIdentity;
    private string? _sessionId;
    private string? _gameReleaseId;

    private SessionCoordinationContext(SessionRevisionVectorStore revisions)
    {
        Revisions = revisions;
    }

    internal SessionRevisionVectorStore Revisions { get; }

    internal static SessionCoordinationContext For(SessionRevisionVectorStore revisions)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(revisions);
#else
        if (revisions is null) throw new ArgumentNullException(nameof(revisions));
#endif
        return Contexts.GetValue(revisions, static store => new SessionCoordinationContext(store));
    }

    internal bool TryEnter(
        TxnIdentity identity,
        out TxnAuthorityOperation operation,
        out CoordinationFailure? failure)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(identity);
#else
        if (identity is null) throw new ArgumentNullException(nameof(identity));
#endif
        lock (_gate)
        {
            if (_sessionId is not null &&
                (!string.Equals(_sessionId, identity.SessionId, StringComparison.Ordinal) ||
                 !string.Equals(_gameReleaseId, identity.GameReleaseId, StringComparison.Ordinal)))
            {
                operation = null!;
                failure = CoordinationFailure.Fatal(
                    "InternalInvariant",
                    "A session revision store cannot be shared by different session identities.");
                return false;
            }

            if (_activeToken != 0)
            {
                operation = null!;
                failure = CoordinationFailure.Retryable(
                    "QueueFull",
                    string.Equals(_activeIdentity, identity.DigestHex, StringComparison.Ordinal)
                        ? "The transaction authority operation is already in flight."
                        : "Another transaction authority operation is in flight for this session.");
                return false;
            }

            long token;
            try { token = checked(++_nextToken); }
            catch (OverflowException)
            {
                operation = null!;
                failure = CoordinationFailure.Fatal("InternalInvariant", "Authority operation tokens were exhausted.");
                return false;
            }

            _sessionId ??= identity.SessionId;
            _gameReleaseId ??= identity.GameReleaseId;
            _activeToken = token;
            _activeIdentity = identity.DigestHex;
            operation = new TxnAuthorityOperation(this, identity, token);
            failure = null;
            return true;
        }
    }

    internal bool IsActive(TxnAuthorityOperation operation)
    {
        lock (_gate)
        {
            return operation is not null && ReferenceEquals(operation.Context, this) &&
                operation.Token == _activeToken &&
                string.Equals(operation.Identity.DigestHex, _activeIdentity, StringComparison.Ordinal) &&
                !operation.IsDisposed;
        }
    }

    internal void Exit(TxnAuthorityOperation operation)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(operation.Context, this) || operation.Token != _activeToken) return;
            _activeToken = 0;
            _activeIdentity = null;
        }
    }
}

internal sealed class TxnAuthorityOperation : IDisposable
{
    private int _disposed;

    internal TxnAuthorityOperation(SessionCoordinationContext context, TxnIdentity identity, long token)
    {
        Context = context;
        Identity = identity;
        Token = token;
    }

    internal SessionCoordinationContext Context { get; }

    internal TxnIdentity Identity { get; }

    internal long Token { get; }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal bool Owns(SessionRevisionVectorStore revisions) =>
        ReferenceEquals(Context.Revisions, revisions) && Context.IsActive(this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) Context.Exit(this);
    }
}
