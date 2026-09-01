using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lumio.GameRuntime.Hello;

namespace Lumio.GameRuntime.HelloEntry;

/// <summary>Fixed native entry point that drives the hello runtime over a UTF-8 JSON op protocol.</summary>
/// <remarks>
/// Single-threaded calling model: the host must serialize calls; the runtime instance is process-global state.
/// Input and output are single JSON objects in UTF-8; the op vocabulary and field names follow lumio.hello-wire.v1.
/// Clocks: "tick" receives nowMs from the caller, so no wall clock is read inside this entry.
/// </remarks>
public static class HelloEntry
{
    private const int EntrySuccess = 0;
    private const int EntryInvalidInput = 1;
    private const int EntryBufferTooSmall = 2;
    private const int EntryRuntimeFailure = 3;

    private static HelloRuntime? s_runtime;

    private static HelloRuntime Runtime => s_runtime ??= new HelloRuntime();

    /// <summary>Native entry point "lumio_hello_entry": executes one op and writes one JSON response.</summary>
    /// <param name="input">Pointer to the UTF-8 request JSON.</param>
    /// <param name="inputLength">Byte length of the request.</param>
    /// <param name="output">Pointer to the caller-provided response buffer.</param>
    /// <param name="outputCapacity">Byte capacity of the response buffer.</param>
    /// <param name="bytesWritten">Receives the number of response bytes written; set to 0 when nothing is written.</param>
    /// <returns>0 success; 1 invalid input; 2 buffer too small; 3 runtime failure.</returns>
    [UnmanagedCallersOnly(EntryPoint = "lumio_hello_entry")]
    public static unsafe int LumioHelloEntry(byte* input, int inputLength, byte* output, int outputCapacity, int* bytesWritten)
    {
        if (bytesWritten is null)
        {
            return EntryInvalidInput;
        }

        bytesWritten[0] = 0;
        if (inputLength < 0 || outputCapacity < 0 || (inputLength > 0 && input is null) || (outputCapacity > 0 && output is null))
        {
            return EntryInvalidInput;
        }

        int returnCode;
        byte[] response;
        try
        {
            (returnCode, response) = Execute(input, inputLength);
        }
        catch (Exception)
        {
            // Native boundary: no managed exception may ever escape into the host process.
            returnCode = EntryRuntimeFailure;
            response = BuildErrorResponse("runtime_failure");
        }

        if (response.Length > outputCapacity)
        {
            return EntryBufferTooSmall;
        }

        response.AsSpan().CopyTo(new Span<byte>(output, response.Length));
        bytesWritten[0] = response.Length;
        return returnCode;
    }

    private static unsafe (int ReturnCode, byte[] Response) Execute(byte* input, int inputLength)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(new ReadOnlySpan<byte>(input, inputLength).ToArray());
            return Dispatch(document.RootElement);
        }
        catch (JsonException)
        {
            return (EntryInvalidInput, BuildErrorResponse("bad_envelope"));
        }
    }

    private static (int ReturnCode, byte[] Response) Dispatch(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("op", out JsonElement op)
            || op.ValueKind != JsonValueKind.String)
        {
            return (EntryInvalidInput, BuildErrorResponse("bad_envelope"));
        }

        return op.GetString() switch
        {
            "enqueue" => ExecuteEnqueue(root),
            "tick" => ExecuteTick(root),
            "snapshot" => ExecuteSnapshot(),
            "shutdown" => ExecuteShutdown(),
            _ => (EntryInvalidInput, BuildErrorResponse("bad_envelope")),
        };
    }

    private static (int ReturnCode, byte[] Response) ExecuteEnqueue(JsonElement root)
    {
        if (!TryReadString(root, "sender", out string? sender)
            || !TryReadNumber(root, "sequence", out ulong sequence)
            || !TryReadString(root, "kind", out string? kind)
            || !TryReadString(root, "payload", out string? payload)
            || !TryReadString(root, "payloadSha256", out string? payloadSha256)
            || !TryReadNumber(root, "sentAtMs", out long sentAtMs))
        {
            return (EntryInvalidInput, BuildErrorResponse("bad_envelope"));
        }

        HelloInputCommand command = new(sender!, sequence, kind!, payload!, payloadSha256!, sentAtMs);
        HelloRuntimeError? error = Runtime.Enqueue(command);
        return error is null
            ? (EntrySuccess, BuildOkResponse())
            : (EntrySuccess, BuildErrorResponse(error.Value.WireCode));
    }

    private static (int ReturnCode, byte[] Response) ExecuteTick(JsonElement root)
    {
        if (!TryReadNumber(root, "nowMs", out long nowMs))
        {
            return (EntryInvalidInput, BuildErrorResponse("bad_envelope"));
        }

        HelloDelta[] deltas = Runtime.Tick(nowMs);
        HelloFullSnapshot snapshot = Runtime.Snapshot();

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteNumber("tickId", snapshot.TickId);
            writer.WriteNumber("revision", snapshot.Revision);
            writer.WriteStartArray("deltas");
            foreach (HelloDelta delta in deltas)
            {
                writer.WriteStartObject();
                WriteHelloFields(
                    writer,
                    delta.Sender,
                    delta.Sequence,
                    delta.Kind,
                    delta.Payload,
                    delta.PayloadSha256,
                    delta.TickId,
                    delta.Revision,
                    delta.OriginSentAtMs,
                    delta.CommittedAtMs);
                writer.WriteNumber("commandSequence", delta.CommandSequence);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return (EntrySuccess, buffer.WrittenSpan.ToArray());
    }

    private static (int ReturnCode, byte[] Response) ExecuteSnapshot()
    {
        HelloFullSnapshot snapshot = Runtime.Snapshot();

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteNumber("tickId", snapshot.TickId);
            writer.WriteNumber("revision", snapshot.Revision);
            writer.WriteStartArray("helloLog");
            foreach (HelloRecord record in snapshot.HelloLog)
            {
                writer.WriteStartObject();
                WriteHelloFields(
                    writer,
                    record.Sender,
                    record.Sequence,
                    record.Kind,
                    record.Payload,
                    record.PayloadSha256,
                    record.TickId,
                    record.Revision,
                    record.OriginSentAtMs,
                    record.CommittedAtMs);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return (EntrySuccess, buffer.WrittenSpan.ToArray());
    }

    private static (int ReturnCode, byte[] Response) ExecuteShutdown()
    {
        // Shutdown destroys the runtime; the next op lazily builds a fresh one, mirroring the
        // process-level "destroy Runtime/CLR/SDK resources" semantics of the wire contract.
        s_runtime = null;
        return (EntrySuccess, BuildOkResponse());
    }

    private static bool TryReadString(JsonElement root, string name, out string? value)
    {
        if (root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryReadNumber(JsonElement root, string name, out ulong value)
    {
        if (root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetUInt64(out value);
        }

        value = 0;
        return false;
    }

    private static bool TryReadNumber(JsonElement root, string name, out long value)
    {
        if (root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt64(out value);
        }

        value = 0;
        return false;
    }

    private static void WriteHelloFields(
        Utf8JsonWriter writer,
        string sender,
        ulong sequence,
        string kind,
        string payload,
        string payloadSha256,
        ulong tickId,
        ulong revision,
        long originSentAtMs,
        long committedAtMs)
    {
        writer.WriteString("sender", sender);
        writer.WriteNumber("sequence", sequence);
        writer.WriteString("kind", kind);
        writer.WriteString("payload", payload);
        writer.WriteString("payloadSha256", payloadSha256);
        writer.WriteNumber("tickId", tickId);
        writer.WriteNumber("revision", revision);
        writer.WriteNumber("originSentAtMs", originSentAtMs);
        writer.WriteNumber("committedAtMs", committedAtMs);
    }

    private static byte[] BuildOkResponse()
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] BuildErrorResponse(string code)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", false);
            writer.WriteString("code", code);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
