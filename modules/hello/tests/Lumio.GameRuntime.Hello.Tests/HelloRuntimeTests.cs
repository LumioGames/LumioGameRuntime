using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Lumio.GameRuntime.Hello;
using Xunit;

namespace Lumio.GameRuntime.Hello.Tests;

public sealed class HelloRuntimeTests
{
    private const string HelloWorldPayload = "Hello World";
    private const string HelloWorldSha256 = "a591a6d40bf420404a011733cfb7b190d62c65bf0bcda32b57b277d9ad9f146e";

    private static string Sha256Hex(string payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    private static HelloInputCommand Command(
        string sender = "browser",
        ulong sequence = 1,
        string kind = "hello",
        string payload = HelloWorldPayload,
        string? payloadSha256 = null,
        long sentAtMs = 1000) =>
        new(sender, sequence, kind, payload, payloadSha256 ?? Sha256Hex(payload), sentAtMs);

    private static HelloRuntimeErrorCode Reject(HelloRuntime runtime, HelloInputCommand command)
    {
        HelloRuntimeError? error = runtime.Enqueue(command);
        Assert.NotNull(error);
        return error!.Value.Code;
    }

    [Fact]
    public void EnqueueThenTickCommitsDeltaWithAuthoritativeFields()
    {
        var runtime = new HelloRuntime();

        Assert.Null(runtime.Enqueue(Command()));
        HelloDelta[] deltas = runtime.Tick(committedAtMs: 1234);

        HelloDelta delta = Assert.Single(deltas);
        Assert.Equal("browser", delta.Sender);
        Assert.Equal(1UL, delta.Sequence);
        Assert.Equal("hello", delta.Kind);
        Assert.Equal(HelloWorldPayload, delta.Payload);
        Assert.Equal(HelloWorldSha256, delta.PayloadSha256);
        Assert.Equal(1UL, delta.TickId);
        Assert.Equal(1UL, delta.Revision);
        Assert.Equal(1UL, delta.CommandSequence);
        Assert.Equal(1000L, delta.OriginSentAtMs);
        Assert.Equal(1234L, delta.CommittedAtMs);
    }

    [Fact]
    public void EnqueuedCommandStaysUncommittedUntilTick()
    {
        var runtime = new HelloRuntime();

        Assert.Null(runtime.Enqueue(Command()));
        HelloFullSnapshot beforeCommit = runtime.Snapshot();

        Assert.Equal(0UL, beforeCommit.TickId);
        Assert.Equal(0UL, beforeCommit.Revision);
        Assert.Empty(beforeCommit.HelloLog);
    }

    [Fact]
    public void EmptyTickReturnsNoDeltasAndKeepsCounters()
    {
        var runtime = new HelloRuntime();

        Assert.Empty(runtime.Tick(10));
        HelloFullSnapshot baseline = runtime.Snapshot();
        Assert.Equal(0UL, baseline.TickId);
        Assert.Equal(0UL, baseline.Revision);
        Assert.Empty(baseline.HelloLog);

        Assert.Null(runtime.Enqueue(Command()));
        Assert.Single(runtime.Tick(20));
        Assert.Empty(runtime.Tick(30));

        HelloFullSnapshot after = runtime.Snapshot();
        Assert.Equal(1UL, after.TickId);
        Assert.Equal(1UL, after.Revision);
        Assert.Single(after.HelloLog);
    }

    [Fact]
    public void InterleavedSendersCommitContiguousRevisionsInQueueOrder()
    {
        var runtime = new HelloRuntime();

        Assert.Null(runtime.Enqueue(Command(sender: "browser", sequence: 1, sentAtMs: 100)));
        Assert.Null(runtime.Enqueue(Command(sender: "bot", sequence: 1, sentAtMs: 200)));
        Assert.Null(runtime.Enqueue(Command(sender: "browser", sequence: 2, sentAtMs: 300)));
        Assert.Null(runtime.Enqueue(Command(sender: "bot", sequence: 2, sentAtMs: 400)));

        HelloDelta[] deltas = runtime.Tick(500);

        Assert.Equal(4, deltas.Length);
        Assert.Equal(new ulong[] { 1, 2, 3, 4 }, deltas.Select(delta => delta.Revision).ToArray());
        Assert.All(deltas, delta => Assert.Equal(1UL, delta.TickId));
        Assert.Equal(new string[] { "browser", "bot", "browser", "bot" }, deltas.Select(delta => delta.Sender).ToArray());
        Assert.Equal(new ulong[] { 1, 1, 2, 2 }, deltas.Select(delta => delta.Sequence).ToArray());
        Assert.Equal(new long[] { 100, 200, 300, 400 }, deltas.Select(delta => delta.OriginSentAtMs).ToArray());
    }

    [Fact]
    public void SnapshotHelloLogIsBoundedAndDropsTheOldestRecord()
    {
        var runtime = new HelloRuntime();

        for (ulong sequence = 1; sequence <= 33; sequence++)
        {
            Assert.Null(runtime.Enqueue(Command(sequence: sequence)));
        }

        Assert.Equal(33, runtime.Tick(9000).Length);
        HelloFullSnapshot snapshot = runtime.Snapshot();

        Assert.Equal(33UL, snapshot.Revision);
        Assert.Equal(1UL, snapshot.TickId);
        Assert.Equal(HelloRuntime.HelloLogCapacity, snapshot.HelloLog.Count);
        Assert.Equal(2UL, snapshot.HelloLog[0].Sequence);
        Assert.Equal(33UL, snapshot.HelloLog[^1].Sequence);
    }

    [Fact]
    public void DuplicateAndStaleSequencesAreRejected()
    {
        var runtime = new HelloRuntime();

        Assert.Null(runtime.Enqueue(Command(sequence: 5)));
        Assert.Equal(HelloRuntimeErrorCode.DuplicateSequence, Reject(runtime, Command(sequence: 5)));
        Assert.Equal(HelloRuntimeErrorCode.DuplicateSequence, Reject(runtime, Command(sequence: 4)));

        runtime.Tick(10);
        Assert.Equal(HelloRuntimeErrorCode.DuplicateSequence, Reject(runtime, Command(sequence: 5)));
        Assert.Null(runtime.Enqueue(Command(sequence: 6)));
    }

    [Fact]
    public void MismatchedPayloadHashIsRejected()
    {
        var runtime = new HelloRuntime();

        Assert.Equal(
            HelloRuntimeErrorCode.BadPayloadHash,
            Reject(runtime, Command(payloadSha256: new string('a', 64))));
        Assert.Equal(
            HelloRuntimeErrorCode.BadPayloadHash,
            Reject(runtime, Command(payloadSha256: HelloWorldSha256.ToUpperInvariant())));
    }

    [Fact]
    public void UnknownSenderRoleIsRejected()
    {
        var runtime = new HelloRuntime();

        Assert.Equal(HelloRuntimeErrorCode.UnknownRole, Reject(runtime, Command(sender: "alice")));
        Assert.Equal(HelloRuntimeErrorCode.UnknownRole, Reject(runtime, Command() with { Sender = null! }));
    }

    [Fact]
    public void EmptyPayloadIsRejected()
    {
        var runtime = new HelloRuntime();

        Assert.Equal(HelloRuntimeErrorCode.BadEnvelope, Reject(runtime, Command(payload: "")));
    }

    [Fact]
    public void OversizedPayloadIsRejected()
    {
        var runtime = new HelloRuntime();

        Assert.Null(runtime.Enqueue(Command(payload: new string('a', 4096))));
        Assert.Equal(HelloRuntimeErrorCode.BadEnvelope, Reject(runtime, Command(payload: new string('a', 4097))));
        Assert.Equal(HelloRuntimeErrorCode.BadEnvelope, Reject(runtime, Command(payload: new string('é', 2049))));
    }

    [Fact]
    public void WrongKindIsRejected()
    {
        var runtime = new HelloRuntime();

        Assert.Equal(HelloRuntimeErrorCode.BadEnvelope, Reject(runtime, Command(kind: "goodbye")));
    }

    [Fact]
    public void QueueCapacityIsEnforcedAndDrainingFreesIt()
    {
        var runtime = new HelloRuntime();

        for (ulong sequence = 1; sequence <= (ulong)HelloRuntime.IngressQueueCapacity; sequence++)
        {
            Assert.Null(runtime.Enqueue(Command(sequence: sequence)));
        }

        Assert.Equal(
            HelloRuntimeErrorCode.QueueFull,
            Reject(runtime, Command(sequence: (ulong)HelloRuntime.IngressQueueCapacity + 1)));

        Assert.Equal(HelloRuntime.IngressQueueCapacity, runtime.Tick(10).Length);
        Assert.Null(runtime.Enqueue(Command(sequence: (ulong)HelloRuntime.IngressQueueCapacity + 1)));
    }
}
