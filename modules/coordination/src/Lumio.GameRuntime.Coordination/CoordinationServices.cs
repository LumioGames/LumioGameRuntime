using Lumio.GameRuntime.Command;

namespace Lumio.GameRuntime.Coordination;

public sealed class CoordinationServices : ICoordinationServices
{
    internal CoordinationServices(
        SessionRevisionVectorStore revisions,
        CrossWorldCoordinator transactions,
        CommandPreflightValidator commandPreflight)
    {
        Revisions = revisions;
        Transactions = transactions;
        CommandPreflight = commandPreflight;
    }

    public SessionRevisionVectorStore Revisions { get; }

    public CrossWorldCoordinator Transactions { get; }

    public CommandPreflightValidator CommandPreflight { get; }

    public SessionRevisionVectorView ReadRevision() => Revisions.Read();

    public TxnPrepareResult PrepareTxn(in TxnPrepareRequest request) => Transactions.PrepareTxn(in request);

    public TxnCommitResult CommitTxn(CrossWorldPreparedTxn prepared) => Transactions.CommitTxn(prepared);

    public TxnTransitionResult AbortTxn(string txnId, string reason) => Transactions.AbortTxn(txnId, reason);

    public TxnCommitResult QueryResult(TxnId txnId) => Transactions.QueryResult(txnId);

    public SnapshotCutOpenResult BeginSnapshotCut(in SnapshotCutRequest request) => Transactions.BeginSnapshotCut(in request);
}

public interface ICoordinationServices
{
    SessionRevisionVectorView ReadRevision();

    TxnPrepareResult PrepareTxn(in TxnPrepareRequest request);

    TxnCommitResult CommitTxn(CrossWorldPreparedTxn prepared);

    TxnTransitionResult AbortTxn(string txnId, string reason);

    TxnCommitResult QueryResult(TxnId txnId);

    SnapshotCutOpenResult BeginSnapshotCut(in SnapshotCutRequest request);
}
