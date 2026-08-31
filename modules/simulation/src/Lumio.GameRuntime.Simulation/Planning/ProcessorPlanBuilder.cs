using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Lumio.GameRuntime.Simulation.Phases;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Simulation.Planning;

public sealed class ProcessorPlanBuilder
{
    public ProcessorPlanBuildResult TryBuild(IEnumerable<ProcessorDescriptor> descriptors) => Build(descriptors);

    public static ProcessorPlanBuildResult Build(IEnumerable<ProcessorDescriptor> descriptors)
    {
        if (descriptors is null) return Reject("InvalidArgument", "Processor descriptors are required.");
        var values = descriptors.ToList();
        if (values.Any(value => value is null)) return Reject("ManifestMalformed", "A processor descriptor is null.");
        values.Sort(CompareCanonical);

        var byId = new Dictionary<string, ProcessorDescriptor>(StringComparer.Ordinal);
        foreach (ProcessorDescriptor descriptor in values)
        {
            string? validation = ValidateDescriptor(descriptor);
            if (validation is not null) return Reject("ManifestMalformed", validation, descriptor.ProcessorId);
            if (byId.ContainsKey(descriptor.ProcessorId)) return Reject("ManifestMalformed", "ProcessorId must be unique.", descriptor.ProcessorId);
            byId.Add(descriptor.ProcessorId, descriptor);
        }

        for (var i = 0; i < values.Count; i++)
        {
            for (var j = i + 1; j < values.Count; j++)
            {
                if (HasReadWriteConflict(values[i], values[j]))
                    return Reject("InternalInvariant", "Processor read/write sets conflict.", values[i].ProcessorId, values[j].ProcessorId);
            }
        }

        var outgoing = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var indegree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ProcessorDescriptor descriptor in values)
        {
            outgoing.Add(descriptor.ProcessorId, new SortedSet<string>(StringComparer.Ordinal));
            indegree.Add(descriptor.ProcessorId, 0);
        }

        foreach (ProcessorDescriptor descriptor in values)
        {
            foreach (string dependency in descriptor.After ?? Array.Empty<string>())
            {
                if (!byId.TryGetValue(dependency, out ProcessorDescriptor? target))
                    return Reject("ManifestMalformed", "An after dependency is unknown.", descriptor.ProcessorId, dependency);
                ProcessorPlanBuildResult? failure = AddDependency(target, descriptor, outgoing, indegree);
                if (failure is ProcessorPlanBuildResult result) return result;
            }

            foreach (string dependency in descriptor.Before ?? Array.Empty<string>())
            {
                if (!byId.TryGetValue(dependency, out ProcessorDescriptor? target))
                    return Reject("ManifestMalformed", "A before dependency is unknown.", descriptor.ProcessorId, dependency);
                ProcessorPlanBuildResult? failure = AddDependency(descriptor, target, outgoing, indegree);
                if (failure is ProcessorPlanBuildResult result) return result;
            }
        }

        var ready = new SortedSet<string>(new ProcessorIdComparer(byId));
        foreach (KeyValuePair<string, int> item in indegree) if (item.Value == 0) ready.Add(item.Key);
        var ordered = new List<ProcessorDescriptor>(values.Count);
        while (ready.Count > 0)
        {
            string id = ready.Min!;
            ready.Remove(id);
            ordered.Add(byId[id]);
            foreach (string next in outgoing[id])
            {
                indegree[next]--;
                if (indegree[next] == 0) ready.Add(next);
            }
        }

        if (ordered.Count != values.Count)
        {
            IReadOnlyList<string> cycle = FindCanonicalCycle(outgoing);
            return Reject("InternalInvariant", "Processor dependencies contain a cycle.", cycle.ToArray());
        }

        var byPhase = new Dictionary<TickPhase, IReadOnlyList<ProcessorInvocation>>();
        foreach (IGrouping<ProcessorDescriptorPhase, ProcessorDescriptor> group in ordered.GroupBy(value => value.Phase))
        {
            var invocations = new List<ProcessorInvocation>();
            foreach (ProcessorDescriptor descriptor in group) invocations.Add(new ProcessorInvocation(descriptor));
            byPhase.Add((TickPhase)group.Key, new ReadOnlyCollection<ProcessorInvocation>(invocations));
        }

        var plan = new ProcessorPlan(ordered, byPhase, ComputeHash(ordered));
        return new ProcessorPlanBuildResult(ProcessorPlanBuildStatus.Built, plan, null);
    }

    private static ProcessorPlanBuildResult? AddDependency(
        ProcessorDescriptor before,
        ProcessorDescriptor after,
        IDictionary<string, SortedSet<string>> outgoing,
        IDictionary<string, int> indegree)
    {
        if (before.ProcessorId == after.ProcessorId)
            return Reject("InternalInvariant", "A processor cannot depend on itself.", before.ProcessorId);
        if ((int)before.Phase > (int)after.Phase)
            return Reject("InternalInvariant", "A dependency contradicts generated phase order.", before.ProcessorId, after.ProcessorId);
        if ((int)before.Phase < (int)after.Phase) return null;
        if (outgoing[before.ProcessorId].Add(after.ProcessorId)) indegree[after.ProcessorId]++;
        return null;
    }

    private static string? ValidateDescriptor(ProcessorDescriptor descriptor)
    {
        if (!SimulationValidation.IsIdentifier(descriptor.ProcessorId)) return "ProcessorId is invalid.";
        if (!Enum.IsDefined(typeof(ProcessorDescriptorRole), descriptor.Role)) return "Processor role is unknown.";
        if (!Enum.IsDefined(typeof(ProcessorDescriptorPhase), descriptor.Phase)) return "Processor phase is unknown.";
        if (!Enum.IsDefined(typeof(ProcessorDescriptorDeterminismClass), descriptor.DeterminismClass)) return "Determinism class is unknown.";
        if (descriptor.MayEmitStructuralCommands && !AllowsStructuralCommands(descriptor.Phase)) return "Structural commands may only be emitted in ADR-030 business phases.";
        if (string.IsNullOrWhiteSpace(descriptor.Query) || descriptor.Query.Length > 512) return "Query is invalid.";
        if (descriptor.ReadSet is null || descriptor.WriteSet is null) return "ReadSet and WriteSet are required.";
        if (!ValidateUniqueIdentifiers(descriptor.ReadSet) || !ValidateUniqueIdentifiers(descriptor.WriteSet)) return "ReadSet or WriteSet is invalid.";
        if (descriptor.Before is not null && !ValidateUniqueIdentifiers(descriptor.Before)) return "Before dependencies are invalid.";
        if (descriptor.After is not null && !ValidateUniqueIdentifiers(descriptor.After)) return "After dependencies are invalid.";
        if (descriptor.Budget is null || descriptor.Budget.MaxMicros == 0 || descriptor.Budget.MaxCommands == 0) return "Processor budget is invalid.";
        if (string.IsNullOrWhiteSpace(descriptor.DiagnosticName) || descriptor.DiagnosticName.Length is < 2 or > 128) return "DiagnosticName is invalid.";
        if (!SimulationValidation.IsDiagnosticName(descriptor.DiagnosticName)) return "DiagnosticName is invalid.";
        return null;
    }

    private static bool AllowsStructuralCommands(ProcessorDescriptorPhase phase) =>
        phase is ProcessorDescriptorPhase.ApplyInputs
            or ProcessorDescriptorPhase.ProcessorPlan
            or ProcessorDescriptorPhase.CrossWorldPrepare
            or ProcessorDescriptorPhase.CommitDecision
            or ProcessorDescriptorPhase.GasAndEventFinalize;

    private static bool ValidateUniqueIdentifiers(IReadOnlyList<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in values)
            if (!SimulationValidation.IsIdentifier(value) || !seen.Add(value)) return false;
        return true;
    }

    private static bool HasReadWriteConflict(ProcessorDescriptor left, ProcessorDescriptor right)
    {
        if (left.Phase != right.Phase) return false;
        var leftWrites = new HashSet<string>(left.WriteSet, StringComparer.Ordinal);
        var rightWrites = new HashSet<string>(right.WriteSet, StringComparer.Ordinal);
        if (leftWrites.Overlaps(rightWrites)) return true;
        if (leftWrites.Overlaps(right.ReadSet)) return true;
        return rightWrites.Overlaps(left.ReadSet);
    }

    private static int CompareCanonical(ProcessorDescriptor? left, ProcessorDescriptor? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        int phase = left.Phase.CompareTo(right.Phase);
        return phase != 0 ? phase : StringComparer.Ordinal.Compare(left.ProcessorId, right.ProcessorId);
    }

    private static IReadOnlyList<string> FindCanonicalCycle(IDictionary<string, SortedSet<string>> outgoing)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();
        foreach (string start in outgoing.Keys.OrderBy(value => value, StringComparer.Ordinal))
        {
            IReadOnlyList<string>? result = Visit(start, outgoing, visiting, visited, path);
            if (result is not null) return result;
        }

        return Array.Empty<string>();
    }

    private static IReadOnlyList<string>? Visit(
        string current,
        IDictionary<string, SortedSet<string>> outgoing,
        ISet<string> visiting,
        ISet<string> visited,
        IList<string> path)
    {
        if (visiting.Contains(current))
        {
            var index = path.IndexOf(current);
            var cycle = new List<string>();
            for (var i = index; i < path.Count; i++) cycle.Add(path[i]);
            cycle.Add(current);
            return cycle;
        }

        if (visited.Contains(current)) return null;
        visiting.Add(current);
        path.Add(current);
        foreach (string next in outgoing[current])
        {
            IReadOnlyList<string>? result = Visit(next, outgoing, visiting, visited, path);
            if (result is not null) return result;
        }

        path.RemoveAt(path.Count - 1);
        visiting.Remove(current);
        visited.Add(current);
        return null;
    }

    private static string ComputeHash(IEnumerable<ProcessorDescriptor> descriptors)
    {
        var builder = new StringBuilder();
        foreach (ProcessorDescriptor value in descriptors)
        {
            Append(builder, value.ProcessorId);
            Append(builder, ((int)value.Role).ToString(CultureInfo.InvariantCulture));
            Append(builder, ((int)value.Phase).ToString(CultureInfo.InvariantCulture));
            Append(builder, value.Query);
            AppendList(builder, value.ReadSet);
            AppendList(builder, value.WriteSet);
            Append(builder, value.MayEmitStructuralCommands ? "1" : "0");
            AppendList(builder, value.Before ?? Array.Empty<string>());
            AppendList(builder, value.After ?? Array.Empty<string>());
            Append(builder, ((int)value.DeterminismClass).ToString(CultureInfo.InvariantCulture));
            Append(builder, value.Budget.MaxMicros.ToString(CultureInfo.InvariantCulture));
            Append(builder, value.Budget.MaxCommands.ToString(CultureInfo.InvariantCulture));
            Append(builder, value.DiagnosticName);
        }

        return SimulationHash.Sha256Hex(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static void AppendList(StringBuilder builder, IEnumerable<string> values)
    {
        foreach (string value in values.OrderBy(item => item, StringComparer.Ordinal)) Append(builder, value);
        builder.Append("0:");
    }

    private static void Append(StringBuilder builder, string value)
    {
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }

    private static ProcessorPlanBuildResult Reject(string errorId, string detail, params string[] path) =>
        new(ProcessorPlanBuildStatus.Rejected, null, new ProcessorPlanFailure(errorId, detail, path));

    private sealed class ProcessorIdComparer : IComparer<string>
    {
        private readonly IReadOnlyDictionary<string, ProcessorDescriptor> _byId;

        internal ProcessorIdComparer(IReadOnlyDictionary<string, ProcessorDescriptor> byId) => _byId = byId;

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            int phase = _byId[left].Phase.CompareTo(_byId[right].Phase);
            return phase != 0 ? phase : StringComparer.Ordinal.Compare(left, right);
        }
    }
}
