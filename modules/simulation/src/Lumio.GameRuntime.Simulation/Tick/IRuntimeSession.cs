using System;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Simulation.Session;

namespace Lumio.GameRuntime.Simulation.Tick;

/// <summary>Session-owned logical tick input. Wraps the existing HostTickRequest protocol.</summary>
public readonly record struct TickInput
{
    public TickInput(in HostTickRequest request)
    {
        Request = request;
    }

    public HostTickRequest Request { get; }

    public static implicit operator TickInput(HostTickRequest request) => new(in request);
}

/// <summary>Host-facing session surface: identity, lifecycle state, and a single RunTick entry.</summary>
public interface IRuntimeSession : IDisposable
{
    string SessionId { get; }

    WorldId WorldId { get; }

    SimulationSessionState State { get; }

    TickRunResult RunTick(in TickInput input);
}
