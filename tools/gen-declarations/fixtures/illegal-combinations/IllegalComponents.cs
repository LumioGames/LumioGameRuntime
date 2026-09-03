using Lumio.GameRuntime.Ecs;

namespace Lumio.Tools.GenDeclarations.IllegalFixtures;

[EcsComponent]
public sealed class IllegalSharedState
{
    public string DeadField = string.Empty;
}

[EntityType(Mode.CS, World = true)]
[Has(typeof(WorldSaveComponent))]
public abstract class FirstWorldEntity { }

[EntityType(Mode.CS, World = true)]
[Has(typeof(WorldSaveComponent))]
public abstract class SecondWorldEntity { }

[EntityType(Mode.CS)]
[Has(typeof(WorldSaveComponent))]
public class ConcreteEntityType { }
