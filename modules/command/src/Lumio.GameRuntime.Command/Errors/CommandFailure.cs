using System;

namespace Lumio.GameRuntime.Command;

public enum CommandFailureClass
{
    Rejected,
    Retryable,
    Fatal
}

public sealed record CommandFailure(
    CommandFailureClass Class,
    string GeneratedErrorId,
    string Detail,
    string? CanonicalEvidence = null)
{
    public Command? FirstFailingCommand { get; init; }

    public CommandConflictEvidence? Conflict { get; init; }

    public ulong? FailingLocalSequence => FirstFailingCommand?.SortKey.LocalSequence;

    public static CommandFailure Rejected(string errorId, string detail, string? evidence = null) =>
        new(CommandFailureClass.Rejected, errorId, detail, evidence);

    public static CommandFailure Retryable(string errorId, string detail, string? evidence = null) =>
        new(CommandFailureClass.Retryable, errorId, detail, evidence);

    public static CommandFailure Fatal(string errorId, string detail, string? evidence = null) =>
        new(CommandFailureClass.Fatal, errorId, detail, evidence);

    public CommandFailure WithFirstCommand(Command command) => this with { FirstFailingCommand = command };

    public CommandFailure WithConflict(Command first, Command second) => this with
    {
        FirstFailingCommand = first,
        Conflict = new CommandConflictEvidence(first, second, GeneratedErrorId)
    };
}

public sealed record CommandConflictEvidence(
    Command First,
    Command Second,
    string GeneratedErrorId)
{
    public string FirstDigest => First.CanonicalDigestHex;

    public string SecondDigest => Second.CanonicalDigestHex;
}

internal static class CommandValidation
{
    internal static bool IsStructuralPhase(Lumio.Gen.ContractTypes.ProcessorDescriptorPhase phase) =>
        phase is Lumio.Gen.ContractTypes.ProcessorDescriptorPhase.ApplyInputs or
            Lumio.Gen.ContractTypes.ProcessorDescriptorPhase.ProcessorPlan or
            Lumio.Gen.ContractTypes.ProcessorDescriptorPhase.CrossWorldPrepare or
            Lumio.Gen.ContractTypes.ProcessorDescriptorPhase.CommitDecision or
            Lumio.Gen.ContractTypes.ProcessorDescriptorPhase.GasAndEventFinalize;

    internal static bool IsIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
        if (!IsAsciiAlphaNumeric(value[0])) return false;
        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            if (!(IsAsciiAlphaNumeric(c) || c is '.' or '_' or ':' or '-')) return false;
        }

        return true;
    }

    internal static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}
