using System;
using System.Text;
using System.Text.Json;
using Xunit;
using HelloEntryPoint = Lumio.GameRuntime.HelloEntry.HelloEntry;

namespace Lumio.GameRuntime.Hello.Tests;

public sealed class HelloEntryTests
{
    private const string HelloWorldPayload = "Hello World";
    private const string HelloWorldSha256 = "a591a6d40bf420404a011733cfb7b190d62c65bf0bcda32b57b277d9ad9f146e";
    private const byte GuardByte = 0xAA;
    private const int OutputBufferSize = 4096;

    private static readonly unsafe delegate* unmanaged<byte*, int, byte*, int, int*, int> Entry =
        &HelloEntryPoint.LumioHelloEntry;

    private static unsafe (int ReturnCode, int BytesWritten, byte[] Output) Call(string request, int outputCapacity = OutputBufferSize)
    {
        byte[] input = Encoding.UTF8.GetBytes(request);
        byte[] output = new byte[OutputBufferSize];
        Array.Fill(output, GuardByte);
        int bytesWritten = -1;
        int returnCode;
        fixed (byte* inputPointer = input)
        fixed (byte* outputPointer = output)
        {
            returnCode = Entry(inputPointer, input.Length, outputPointer, outputCapacity, &bytesWritten);
        }

        return (returnCode, bytesWritten, output);
    }

    private static JsonDocument ParseResponse(int bytesWritten, byte[] output)
    {
        Assert.InRange(bytesWritten, 1, OutputBufferSize);
        return JsonDocument.Parse(output.AsSpan(0, bytesWritten).ToArray());
    }

    [Fact]
    public void AllFourOpsRoundTripThroughRealJsonBytes()
    {
        // shutdown first so the scenario always starts on a destroyed runtime, whatever ran before.
        (int resetCode, int _, byte[] _) = Call("""{"op":"shutdown"}""");
        Assert.Equal(0, resetCode);

        (int enqueueCode, int enqueueWritten, byte[] enqueueOutput) = Call(
            $$"""{"op":"enqueue","sender":"browser","sequence":1,"kind":"hello","payload":"{{HelloWorldPayload}}","payloadSha256":"{{HelloWorldSha256}}","sentAtMs":1000}""");
        Assert.Equal(0, enqueueCode);
        using (JsonDocument enqueueResponse = ParseResponse(enqueueWritten, enqueueOutput))
        {
            Assert.True(enqueueResponse.RootElement.GetProperty("ok").GetBoolean());
        }

        (int tickCode, int tickWritten, byte[] tickOutput) = Call("""{"op":"tick","nowMs":2000}""");
        Assert.Equal(0, tickCode);
        using (JsonDocument tickResponse = ParseResponse(tickWritten, tickOutput))
        {
            Assert.True(tickResponse.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(1UL, tickResponse.RootElement.GetProperty("tickId").GetUInt64());
            Assert.Equal(1UL, tickResponse.RootElement.GetProperty("revision").GetUInt64());

            JsonElement delta = Assert.Single(tickResponse.RootElement.GetProperty("deltas").EnumerateArray());
            Assert.Equal("browser", delta.GetProperty("sender").GetString());
            Assert.Equal(1UL, delta.GetProperty("sequence").GetUInt64());
            Assert.Equal("hello", delta.GetProperty("kind").GetString());
            Assert.Equal(HelloWorldPayload, delta.GetProperty("payload").GetString());
            Assert.Equal(HelloWorldSha256, delta.GetProperty("payloadSha256").GetString());
            Assert.Equal(1UL, delta.GetProperty("tickId").GetUInt64());
            Assert.Equal(1UL, delta.GetProperty("revision").GetUInt64());
            Assert.Equal(1000L, delta.GetProperty("originSentAtMs").GetInt64());
            Assert.Equal(2000L, delta.GetProperty("committedAtMs").GetInt64());
            Assert.Equal(1UL, delta.GetProperty("commandSequence").GetUInt64());
        }

        (int snapshotCode, int snapshotWritten, byte[] snapshotOutput) = Call("""{"op":"snapshot"}""");
        Assert.Equal(0, snapshotCode);
        using (JsonDocument snapshotResponse = ParseResponse(snapshotWritten, snapshotOutput))
        {
            Assert.True(snapshotResponse.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(1UL, snapshotResponse.RootElement.GetProperty("tickId").GetUInt64());
            Assert.Equal(1UL, snapshotResponse.RootElement.GetProperty("revision").GetUInt64());

            JsonElement record = Assert.Single(snapshotResponse.RootElement.GetProperty("helloLog").EnumerateArray());
            Assert.Equal("browser", record.GetProperty("sender").GetString());
            Assert.Equal(1UL, record.GetProperty("sequence").GetUInt64());
            Assert.Equal(HelloWorldPayload, record.GetProperty("payload").GetString());
            Assert.Equal(HelloWorldSha256, record.GetProperty("payloadSha256").GetString());
            Assert.Equal(1UL, record.GetProperty("tickId").GetUInt64());
            Assert.Equal(1UL, record.GetProperty("revision").GetUInt64());
            Assert.Equal(1000L, record.GetProperty("originSentAtMs").GetInt64());
            Assert.Equal(2000L, record.GetProperty("committedAtMs").GetInt64());
        }

        (int shutdownCode, int shutdownWritten, byte[] shutdownOutput) = Call("""{"op":"shutdown"}""");
        Assert.Equal(0, shutdownCode);
        using (JsonDocument shutdownResponse = ParseResponse(shutdownWritten, shutdownOutput))
        {
            Assert.True(shutdownResponse.RootElement.GetProperty("ok").GetBoolean());
        }

        (int postShutdownCode, int postShutdownWritten, byte[] postShutdownOutput) = Call("""{"op":"snapshot"}""");
        Assert.Equal(0, postShutdownCode);
        using (JsonDocument empty = ParseResponse(postShutdownWritten, postShutdownOutput))
        {
            Assert.Equal(0UL, empty.RootElement.GetProperty("tickId").GetUInt64());
            Assert.Equal(0UL, empty.RootElement.GetProperty("revision").GetUInt64());
            Assert.Empty(empty.RootElement.GetProperty("helloLog").EnumerateArray());
        }
    }

    [Fact]
    public void MalformedJsonIsRejectedWithBadEnvelope()
    {
        (int returnCode, int bytesWritten, byte[] output) = Call("{ this is not json");

        Assert.Equal(1, returnCode);
        using JsonDocument response = ParseResponse(bytesWritten, output);
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("bad_envelope", response.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void UnknownOpIsRejectedWithBadEnvelope()
    {
        (int returnCode, int bytesWritten, byte[] output) = Call("""{"op":"frobnicate"}""");

        Assert.Equal(1, returnCode);
        using JsonDocument response = ParseResponse(bytesWritten, output);
        Assert.Equal("bad_envelope", response.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void UndersizedOutputBufferWritesNothing()
    {
        (int returnCode, int bytesWritten, byte[] output) = Call("""{"op":"snapshot"}""", outputCapacity: 4);

        Assert.Equal(2, returnCode);
        Assert.Equal(0, bytesWritten);
        Assert.Equal(GuardByte, output[0]);
        Assert.Equal(GuardByte, output[1]);
        Assert.Equal(GuardByte, output[2]);
        Assert.Equal(GuardByte, output[3]);
    }

    [Fact]
    public void DomainRejectionIsReportedAsOkFalseWithWireCode()
    {
        string request =
            $$"""{"op":"enqueue","sender":"bot","sequence":1,"kind":"hello","payload":"{{HelloWorldPayload}}","payloadSha256":"{{HelloWorldSha256}}","sentAtMs":100}""";

        (int firstCode, _, _) = Call(request);
        Assert.Equal(0, firstCode);

        (int secondCode, int bytesWritten, byte[] output) = Call(request);
        Assert.Equal(0, secondCode);
        using JsonDocument response = ParseResponse(bytesWritten, output);
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("duplicate_sequence", response.RootElement.GetProperty("code").GetString());
    }
}
