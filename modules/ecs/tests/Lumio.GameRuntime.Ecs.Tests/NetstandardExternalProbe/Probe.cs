using System;
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Ecs.NetstandardProbe;

/// <summary>External netstandard2.1 consumer of the public ECS surface.</summary>
public static class Probe
{
    /// <summary>Touches the public types Client assemblies are expected to reference.</summary>
    public static string Touch()
    {
        _ = typeof(WorldManager);
        _ = typeof(World);
        _ = typeof(Component);
        _ = typeof(Sync<string>);
        _ = typeof(SyncList<int>);
        _ = typeof(SyncDict<string, int>);
        _ = typeof(IGeneratedComponent);
        _ = typeof(ISyncHost);
        _ = typeof(IPersistWriter);
        _ = typeof(IPersistReader);
        _ = typeof(EcsComponentAttribute);
        _ = typeof(PersistAttribute);
        _ = typeof(EntityTypeAttribute);
        _ = typeof(HasAttribute);
        _ = typeof(ServerRpcAttribute);
        _ = typeof(ClientRpcAttribute);
        _ = typeof(NetEntityId);
        _ = typeof(WorldMessage);
        _ = typeof(WorldChangeMessage);
        _ = typeof(WelcomeMessage);
        _ = typeof(InputCommandMessage);
        var generated = new ProbeComponent();
        _ = generated as IGeneratedComponent;
        return typeof(WorldManager).Assembly.GetName().Name ?? "Lumio.GameRuntime.Ecs";
    }

    /// <summary>Proves a gameplay assembly can implement generated protocol types without InternalsVisibleTo.</summary>
    private sealed class ProbeComponent : Component, IGeneratedComponent
    {
        public Sync<string> Name = new(Scope.Room, Authority.Owner);

        public void BindFields(ISyncHost host) =>
            Name = Name.Bound(host, this, 0, "ProbeComponent.name");

        public void InvokePostAttribute()
        {
        }

        public void InvokeFieldChanging(int ordinal, object? oldValue, object? newValue, ChangeReason reason)
        {
        }

        public void InvokeFieldChanged(int ordinal, object? oldValue, object? newValue, ChangeReason reason)
        {
        }

        public bool DispatchClientWrite(in SyncWrite write) => true;

        public void DispatchServerRpc(string method, object?[] args)
        {
        }

        public void DispatchClientRpc(string method, object?[] args)
        {
        }

        public void CapturePersist(IPersistWriter writer) => writer.WriteString("ProbeComponent.name", Name.Value);

        public void CaptureSync(IPersistWriter writer)
        {
        }

        public void RestorePersist(IPersistReader reader)
        {
            if (reader.TryReadString("ProbeComponent.name", out string value))
                Name.SetSilent(value);
        }

        public object? ReadField(string fieldId) => Name.Value;

        public void WriteField(string fieldId, object? value, bool silent)
        {
            if (silent) Name.SetSilent((string)value!);
            else Name.Value = (string)value!;
        }
    }
}
