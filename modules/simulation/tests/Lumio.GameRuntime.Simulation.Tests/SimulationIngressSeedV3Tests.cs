using System;
using Lumio.GameRuntime.Simulation.Session;
using Lumio.GameRuntime.Simulation.Tick;
using Xunit;

namespace Lumio.GameRuntime.Simulation.Tests;

public sealed class SimulationIngressSeedV3Tests
{
    [Fact]
    public void NullSessionIdReturnsStableRejectionAndSameTickCanRecover()
    {
        using var session = RunningSession(SimulationSessionOptions.Default("null-session"));
        var malformed = new OpaqueIngressView(null!, 1, 1, 1, new byte[] { 1 });
        TickRunResult? result = null;

        Exception? exception = Record.Exception(() => result = session.RunTick(Request(new[] { malformed })));

        Assert.Null(exception);
        Assert.Equal(TickRunStatus.Rejected, result!.Status);
        Assert.Equal("ManifestMalformed", result.GeneratedErrorId);
        Assert.Equal(SimulationSessionState.Running, session.State);
        Assert.Equal(TickRunStatus.Succeeded, session.RunTick(Request()).Status);
    }

    [Fact]
    public void NullPayloadReturnsStableRejectionAndNoCommit()
    {
        using var session = RunningSession(SimulationSessionOptions.Default("null-payload"));
        var malformed = new OpaqueIngressView("null-payload", 1, 1, 1, null!);

        TickRunResult result = session.RunTick(Request(new[] { malformed }));

        Assert.Equal(TickRunStatus.Rejected, result.Status);
        Assert.Equal("ManifestMalformed", result.GeneratedErrorId);
        Assert.False(result.IsCommitted);
        Assert.Equal(SimulationSessionState.Running, session.State);
    }

    [Fact]
    public void InvalidUtf16IdentifierReturnsStableRejection()
    {
        using var session = RunningSession(SimulationSessionOptions.Default("invalid-utf"));
        var malformed = new OpaqueIngressView("\uD800", 1, 1, 1, new byte[] { 1 });

        TickRunResult result = session.RunTick(Request(new[] { malformed }));

        Assert.Equal(TickRunStatus.Rejected, result.Status);
        Assert.Equal("ManifestMalformed", result.GeneratedErrorId);
        Assert.Equal(SimulationSessionState.Running, session.State);
    }

    [Fact]
    public void IngressCapacityAndByteLimitsRejectBeforeHashing()
    {
        SimulationSessionOptions options = SimulationSessionOptions.Default("ingress-limits") with
        {
            IngressCapacity = 1,
            IngressBytes = 2
        };
        using var session = RunningSession(options);
        var tooLarge = new OpaqueIngressView("ingress-limits", 1, 1, 1, new byte[] { 1, 2, 3 });

        TickRunResult bytes = session.RunTick(Request(new[] { tooLarge }));

        Assert.Equal(TickRunStatus.Rejected, bytes.Status);
        Assert.Equal("CapacityExceeded", bytes.GeneratedErrorId);

        var one = new OpaqueIngressView("ingress-limits", 1, 1, 1, new byte[] { 1 });
        var two = new OpaqueIngressView("ingress-limits", 2, 1, 1, new byte[] { 2 });
        TickRunResult count = session.RunTick(Request(new[] { one, two }));
        Assert.Equal(TickRunStatus.Rejected, count.Status);
        Assert.Equal("CapacityExceeded", count.GeneratedErrorId);
        Assert.Equal(SimulationSessionState.Running, session.State);
    }

    [Fact]
    public void ConfiguredSeedOverridesUnspecifiedRequestAndRejectsExplicitMismatch()
    {
        ulong observedSeed = 0;
        SimulationSessionOptions options = SimulationSessionOptions.Default("seed-authority") with { Seed = 111 };
        using var session = RunningSession(
            options,
            TickTestExecutors.CompleteComposition((_, context) => observedSeed = context.Determinism.Seed));

        TickRunResult first = session.RunTick(new HostTickRequest(1, session.Epoch.Value, Array.Empty<OpaqueIngressView>()));

        Assert.Equal(TickRunStatus.Succeeded, first.Status);
        Assert.Equal((ulong)111, observedSeed);

        TickRunResult mismatch = session.RunTick(new HostTickRequest(2, session.Epoch.Value, 222, 1, Array.Empty<OpaqueIngressView>()));
        Assert.Equal(TickRunStatus.Rejected, mismatch.Status);
        Assert.Equal("InvalidArgument", mismatch.GeneratedErrorId);
        Assert.Equal(SimulationSessionState.Running, session.State);
    }

    [Fact]
    public void ExplicitAndImplicitAuthoritativeSeedHaveSameReplayIdentity()
    {
        SimulationSessionOptions options = SimulationSessionOptions.Default("seed-replay") with { Seed = 111 };
        using var implicitSeed = RunningSession(options);
        using var explicitSeed = RunningSession(options);

        TickRunResult implicitResult = implicitSeed.RunTick(
            new HostTickRequest(1, implicitSeed.Epoch.Value, Array.Empty<OpaqueIngressView>()));
        TickRunResult explicitResult = explicitSeed.RunTick(
            new HostTickRequest(1, explicitSeed.Epoch.Value, 111, 1, Array.Empty<OpaqueIngressView>()));

        Assert.Equal(TickRunStatus.Succeeded, implicitResult.Status);
        Assert.Equal(TickRunStatus.Succeeded, explicitResult.Status);
        Assert.Equal(implicitResult.RequestHashHex, explicitResult.RequestHashHex);
        Assert.Equal(implicitResult.StateHashHex, explicitResult.StateHashHex);
    }

    private static SimulationSession RunningSession(
        SimulationSessionOptions options,
        TickExecutorComposition? composition = null)
    {
        var session = SimulationModule.Create().CreateSession(
            options,
            composition ?? TickTestExecutors.CompleteComposition());
        SessionEpoch epoch = session.Epoch;
        Assert.True(session.Initialize(epoch).Succeeded);
        Assert.True(session.Prime(epoch).Succeeded);
        Assert.True(session.Start(epoch).Succeeded);
        return session;
    }

    private static HostTickRequest Request(OpaqueIngressView[]? inputs = null) =>
        new(1, 1, inputs ?? Array.Empty<OpaqueIngressView>());
}
