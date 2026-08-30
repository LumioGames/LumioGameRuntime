using System;
using System.Security.Cryptography;
using System.Text;
using Lumio.GameRuntime.Observability;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Command;

public readonly struct CommandLogRecordView
{
    public CommandLogRecordView(CommandLogRecord record, ReadOnlyMemory<byte> payload, in CorrelationView correlation)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        Payload = payload.ToArray();
        Correlation = correlation;
    }

    public CommandLogRecord Record { get; }

    public ReadOnlyMemory<byte> Payload { get; }

    public CorrelationView Correlation { get; }

    public string IdempotencyKey => Record.IdempotencyKey;

    public bool IsWellFormed =>
        !string.IsNullOrWhiteSpace(Record.IdempotencyKey) &&
        !string.IsNullOrWhiteSpace(Record.CommandId) &&
        Correlation.IsComplete;
}

public enum CommandEvidenceStatus
{
    Accepted,
    Retryable,
    Fatal,
    Rejected
}

public readonly record struct CommandEvidenceResult(
    CommandEvidenceStatus Status,
    ulong RecordSequence,
    bool AlreadyPresent,
    string? GeneratedErrorId)
{
    public bool IsAccepted => Status == CommandEvidenceStatus.Accepted;
}

public interface ICommandEvidencePort
{
    CommandEvidenceResult Append(in CommandLogRecordView record);
}

/// <summary>Adapter from the durable observability port; no command record is sent to diagnostics.</summary>
public sealed class DurableCommandEvidencePort : ICommandEvidencePort
{
    private readonly IDurableEvidencePort _durable;

    public DurableCommandEvidencePort(IDurableEvidencePort durable)
    {
        _durable = durable ?? throw new ArgumentNullException(nameof(durable));
    }

    public CommandEvidenceResult Append(in CommandLogRecordView record)
    {
        if (!record.IsWellFormed)
        {
            return new CommandEvidenceResult(CommandEvidenceStatus.Rejected, 0UL, false, "ManifestMalformed");
        }

        var durableRecord = new DurableRecordView(
            record.Record.IdempotencyKey,
            "command-log-record",
            record.Payload,
            record.Correlation);
        DurableEnqueueResult result = _durable.Enqueue(in durableRecord);
        return result.Status switch
        {
            DurableEnqueueStatus.Accepted => new CommandEvidenceResult(
                CommandEvidenceStatus.Accepted, result.RecordSequence, result.AlreadyPresent, null),
            DurableEnqueueStatus.Backpressured => new CommandEvidenceResult(
                CommandEvidenceStatus.Retryable, 0UL, false, result.GeneratedErrorId ?? "QueueFull"),
            DurableEnqueueStatus.Rejected => new CommandEvidenceResult(
                CommandEvidenceStatus.Rejected, 0UL, false, result.GeneratedErrorId ?? "ManifestMalformed"),
            _ => new CommandEvidenceResult(
                CommandEvidenceStatus.Fatal, 0UL, false, result.GeneratedErrorId ?? "ContextClosing")
        };
    }

    public CommandEvidenceResult Append(CommandLogRecord record, ReadOnlyMemory<byte> payload, in CorrelationView correlation)
    {
        var view = new CommandLogRecordView(record, payload, in correlation);
        return Append(in view);
    }
}

public static class CommandLogRecordFactory
{
    public static CommandLogRecord Create(
        string sessionId,
        string gameReleaseId,
        ulong tickId,
        string commandId,
        CommandLogRecordRecordKind kind,
        string idempotencyKey,
        ReadOnlyMemory<byte> payload = default,
        ulong recordSequence = 0UL,
        string? txnId = null,
        CommandLogRecordCommitState commitState = CommandLogRecordCommitState.Pending)
    {
        byte[] payloadBytes = payload.ToArray();
        byte[] payloadHashBytes = Hash(payloadBytes.Length == 0 ? Encoding.UTF8.GetBytes(string.Concat(commandId, "|", kind)) : payloadBytes);
        string payloadHash = ToHex(payloadHashBytes);
        string previousHash = new string('0', 64);
        string checksum = ToHex(Hash(Encoding.UTF8.GetBytes(string.Concat(recordSequence, "|", payloadHash, "|", idempotencyKey))));
        return new CommandLogRecord(
            1UL,
            recordSequence,
            previousHash,
            payloadHash,
            (ulong)payloadBytes.Length,
            checksum,
            commitState,
            CommandLogRecordDurabilityState.Durable,
            sessionId,
            gameReleaseId,
            tickId,
            txnId,
            commandId,
            kind,
            idempotencyKey);
    }

    private static byte[] Hash(byte[] bytes)
    {
#if NET10_0_OR_GREATER
        return SHA256.HashData(bytes);
#else
        using SHA256 sha = SHA256.Create();
        return sha.ComputeHash(bytes);
#endif
    }

    private static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) builder.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}
