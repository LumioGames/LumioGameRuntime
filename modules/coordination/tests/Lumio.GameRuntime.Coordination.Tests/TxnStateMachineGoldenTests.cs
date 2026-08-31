using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Lumio.GameRuntime.Coordination.Tests;

public sealed class TxnStateMachineGoldenTests
{
    private static readonly string[] ParticipantNames = { "NotStarted", "Unknown", "Applied", "Failed" };

    [Fact]
    public void OnlyFrozenTransactionTransitionsAreAccepted()
    {
        TxnRecord happy = Record("txn-happy");
        Assert.True(happy.TryTransition(CrossWorldTxnState.Prepared).Succeeded);
        Assert.Equal("CapabilityMissing",
            happy.TryTransition(CrossWorldTxnState.CommitIntent).Failure?.GeneratedErrorId);
        TxnAuthorityTestData.MarkIntent(happy);
        Assert.Equal("CapabilityMissing",
            happy.TryTransition(CrossWorldTxnState.Committed).Failure?.GeneratedErrorId);
        Assert.False(happy.TryTransition(CrossWorldTxnState.Prepared).Succeeded);

        TxnRecord invalid = Record("txn-invalid");
        Assert.True(invalid.TryTransition(CrossWorldTxnState.Prepared).Succeeded);
        Assert.False(invalid.TryTransition(CrossWorldTxnState.Indeterminate).Succeeded);
        Assert.Equal(CrossWorldTxnState.Prepared, invalid.State);
    }

    [Fact]
    public void ParticipantStateIsFourValuedAndNeverBoolean()
    {
        Assert.Equal(ParticipantNames, Enum.GetNames<TxnParticipantState>());
        Assert.DoesNotContain(typeof(TxnRecord).GetProperties(), property => property.PropertyType == typeof(bool) && property.Name.Contains("Participant", StringComparison.Ordinal));
    }

    private static TxnRecord Record(string txnId) =>
        new("session", txnId, 1UL, "command", new SessionRevisionVectorView(1UL, 1UL, 1UL,
            new Dictionary<string, ulong>(), 1UL, 1UL, 1UL), 10UL, string.Concat("digest-", txnId));
}
