using System;
using System.Reflection;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Gas;
using Lumio.GameRuntime.GeneratedContracts;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Gas.Tests;

public sealed class TypeHandlePropertyTests
{
    [Fact]
    public void TypeIdInstanceIdAndHandleAreDistinctIdentities()
    {
        Assert.NotEqual(typeof(AbilityTypeId), typeof(AbilityInstanceId));
        Assert.NotEqual(typeof(AbilityTypeId), typeof(AbilityHandle));
        Assert.NotEqual(typeof(AbilityInstanceId), typeof(AbilityHandle));
        Assert.NotEqual(typeof(EffectTypeId), typeof(EffectInstanceId));
        Assert.NotEqual(typeof(EffectTypeId), typeof(EffectHandle));
        Assert.NotEqual(typeof(AbilityHandle), typeof(EffectHandle));
        Assert.Null(FindConversion(typeof(AbilityTypeId), typeof(AbilityHandle)));
        Assert.Null(FindConversion(typeof(AbilityInstanceId), typeof(AbilityHandle)));
        Assert.Null(FindConversion(typeof(EffectTypeId), typeof(EffectHandle)));
    }

    [Fact]
    public void HandlesFromDifferentWorldsWithTheSameInstanceIdAreNeverEqualOrResolvable()
    {
        using GasWorldContext first = GasTestHarness.Running(100UL);
        using GasWorldContext second = GasTestHarness.Running(200UL);
        var instance = new AbilityInstanceId(9UL);

        AbilityHandleResult firstIssued = first.CreateAbilityHandle(instance);
        AbilityHandleResult secondIssued = second.CreateAbilityHandle(instance);

        Assert.True(firstIssued.Succeeded);
        Assert.True(secondIssued.Succeeded);
        Assert.NotEqual(firstIssued.Handle, secondIssued.Handle);
        Assert.NotEqual(firstIssued.Handle.GetHashCode(), secondIssued.Handle.GetHashCode());
        Assert.Equal(first.WorldId, firstIssued.Handle.WorldId);
        Assert.Equal(second.WorldId, secondIssued.Handle.WorldId);

        GasResolveResult crossed = first.TryResolveAbility(secondIssued.Handle, out AbilityInstanceId resolved);
        Assert.False(crossed.Resolved);
        Assert.Equal("WrongContext", crossed.GeneratedErrorId);
        Assert.Equal(default, resolved);
        Assert.True(first.TryResolveAbility(firstIssued.Handle, out AbilityInstanceId own).Resolved);
        Assert.Equal(instance, own);
    }

    [Fact]
    public void RetireAndReuseAdvancesGenerationAndLeavesStaleHandlesPermanentlyInvalid()
    {
        using GasWorldContext context = GasTestHarness.Running(101UL);
        var instance = new AbilityInstanceId(4UL);
        AbilityHandleResult first = context.CreateAbilityHandle(instance);
        Assert.True(first.Succeeded);

        GasRetireResult retired = context.RetireAbility(first.Handle);
        AbilityHandleResult reuse = context.CreateAbilityHandle(instance);

        Assert.True(retired.Succeeded);
        Assert.True(reuse.Succeeded);
        Assert.Equal(first.Handle.InstanceId, reuse.Handle.InstanceId);
        Assert.Equal(first.Handle.WorldId, reuse.Handle.WorldId);
        Assert.True(reuse.Handle.Generation > first.Handle.Generation);
        Assert.NotEqual(first.Handle, reuse.Handle);

        GasResolveResult stale = context.TryResolveAbility(first.Handle, out AbilityInstanceId staleId);
        Assert.False(stale.Resolved);
        Assert.Equal("InvalidHandle", stale.GeneratedErrorId);
        Assert.Equal(default, staleId);
        Assert.True(context.TryResolveAbility(reuse.Handle, out AbilityInstanceId live).Resolved);
        Assert.Equal(instance, live);
        Assert.False(context.RetireAbility(first.Handle).Succeeded);
    }

    [Fact]
    public void LongRetireReuseSequenceKeepsEveryRetiredHandleStale()
    {
        using GasWorldContext context = GasTestHarness.Running(102UL);
        var instance = new AbilityInstanceId(12UL);
        AbilityHandle[] retired = new AbilityHandle[64];

        for (int i = 0; i < retired.Length; i++)
        {
            AbilityHandleResult issued = context.CreateAbilityHandle(instance);
            Assert.True(issued.Succeeded);
            retired[i] = issued.Handle;
            Assert.True(context.RetireAbility(issued.Handle).Succeeded);
            Assert.False(context.TryResolveAbility(issued.Handle, out _).Resolved);
        }

        AbilityHandleResult live = context.CreateAbilityHandle(instance);
        Assert.True(live.Succeeded);
        for (int i = 0; i < retired.Length; i++)
        {
            Assert.False(context.TryResolveAbility(retired[i], out _).Resolved);
            Assert.NotEqual(retired[i], live.Handle);
        }
    }

    [Fact]
    public void HandleEqualityAndHashUseWorldInstanceAndGenerationNeverObjectAddress()
    {
        var handle = new AbilityHandle(new WorldId(3UL), new AbilityInstanceId(7UL), 11U);
        var copy = new AbilityHandle(new WorldId(3UL), new AbilityInstanceId(7UL), 11U);
        var otherGen = new AbilityHandle(new WorldId(3UL), new AbilityInstanceId(7UL), 12U);
        object boxed = handle;
        object boxedCopy = copy;

        Assert.Equal(handle, copy);
        Assert.Equal(handle.GetHashCode(), copy.GetHashCode());
        Assert.True(boxed.Equals(boxedCopy));
        Assert.False(ReferenceEquals(boxed, boxedCopy));
        Assert.NotEqual(handle, otherGen);
        Assert.All(typeof(AbilityHandle).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field => Assert.True(field.FieldType.IsValueType, field.Name));
        Assert.All(typeof(EffectHandle).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field => Assert.True(field.FieldType.IsValueType, field.Name));
    }

    [Fact]
    public void DefaultHandleDoesNotResolve()
    {
        using GasWorldContext context = GasTestHarness.Running(103UL);
        GasResolveResult ability = context.TryResolveAbility(default, out _);
        GasResolveResult effect = context.TryResolveEffect(default, out _);
        Assert.False(ability.Resolved);
        Assert.Equal("InvalidHandle", ability.GeneratedErrorId);
        Assert.False(effect.Resolved);
        Assert.Equal("InvalidHandle", effect.GeneratedErrorId);
    }

    [Fact]
    public void CompatibleDuplicateRegistrationReturnsAlreadyRegistered()
    {
        using GasWorldContext context = GasTestHarness.ContextAt(110UL, GasFrameworkState.Registered);
        GasTypeDescriptor descriptor = GasTestHarness.Descriptor("ability.bolt", 1U, 9);
        AbilityTypeId typeId = new(21U);

        GasRegistrationResult first = context.RegisterAbility(typeId, descriptor);
        GasRegistrationResult duplicate = context.RegisterAbility(typeId, descriptor);

        Assert.Equal(GasRegistrationStatus.Registered, first.Status);
        Assert.Equal(GasRegistrationStatus.AlreadyRegistered, duplicate.Status);
        Assert.Null(duplicate.GeneratedErrorId);
        Assert.True(context.Types.TryGetAbility(typeId, out GasTypeDescriptor stored));
        Assert.Equal(descriptor.SchemaId, stored.SchemaId);
        Assert.Equal(descriptor.SchemaVersion, stored.SchemaVersion);
    }

    [Fact]
    public void SameTypeIdWithDifferentSchemaOrVersionIsFatal()
    {
        using GasWorldContext context = GasTestHarness.ContextAt(111UL, GasFrameworkState.Registered);
        AbilityTypeId typeId = new(22U);
        Assert.Equal(GasRegistrationStatus.Registered,
            context.RegisterAbility(typeId, GasTestHarness.Descriptor("ability.bolt", 1U, 1)).Status);

        GasRegistrationResult schemaConflict = context.RegisterAbility(
            typeId,
            GasTestHarness.Descriptor("ability.spear", 1U, 1));
        GasRegistrationResult versionConflict = context.RegisterAbility(
            typeId,
            GasTestHarness.Descriptor("ability.bolt", 2U, 1));
        GasRegistrationResult hashConflict = context.RegisterAbility(
            typeId,
            GasTestHarness.Descriptor("ability.bolt", 1U, 2));

        Assert.Equal(GasRegistrationStatus.Fatal, schemaConflict.Status);
        Assert.Equal("PackageIdentityConflict", schemaConflict.GeneratedErrorId);
        Assert.Equal(GasRegistrationStatus.Fatal, versionConflict.Status);
        Assert.Equal("PackageIdentityConflict", versionConflict.GeneratedErrorId);
        Assert.Equal(GasRegistrationStatus.Fatal, hashConflict.Status);
        Assert.Equal("PackageIdentityConflict", hashConflict.GeneratedErrorId);
        Assert.Contains("PackageIdentityConflict", Catalog.StableErrorIds, StringComparer.Ordinal);
        Assert.True(context.Types.TryGetAbility(typeId, out GasTypeDescriptor kept));
        Assert.Equal("ability.bolt", kept.SchemaId);
        Assert.Equal(1U, kept.SchemaVersion);
    }

    [Fact]
    public void RegistryIsFrozenAfterReady()
    {
        using GasWorldContext context = GasTestHarness.ContextAt(112UL, GasFrameworkState.Registered);
        Assert.Equal(GasRegistrationStatus.Registered,
            context.RegisterAbility(new AbilityTypeId(1U), GasTestHarness.Descriptor("a", 1U, 1)).Status);
        Assert.True(context.MarkReady().Succeeded);
        Assert.True(context.Types.IsFrozen);

        GasRegistrationResult afterReady = context.RegisterAbility(
            new AbilityTypeId(2U),
            GasTestHarness.Descriptor("b", 1U, 1));
        Assert.Equal(GasRegistrationStatus.Rejected, afterReady.Status);
        Assert.Equal("InvalidArgument", afterReady.GeneratedErrorId);
        Assert.False(context.Types.TryGetAbility(new AbilityTypeId(2U), out _));
    }

    [Fact]
    public void ModuleCatalogIsTheSameInstanceAsWorldContextAndFreezesOnReady()
    {
        GasModule module = GasModule.Create();
        AbilityTypeId typeId = new(40U);
        GasTypeDescriptor descriptor = GasTestHarness.Descriptor("ability.shared", 1U, 4);
        Assert.Equal(
            GasRegistrationStatus.Registered,
            module.Services.Types.RegisterAbility(typeId, descriptor).Status);

        using GasWorldContext context = module.CreateWorldContext(
            new WorldId(118UL),
            new RecordingGasEcsProjectionPort());

        Assert.Same(module.Services.Types, context.Types);
        Assert.Same(module.Types, context.Types);
        Assert.True(context.Types.TryGetAbility(typeId, out GasTypeDescriptor stored));
        Assert.Equal("ability.shared", stored.SchemaId);
        Assert.Equal(1U, stored.SchemaVersion);

        Assert.True(context.Register().Succeeded);
        Assert.True(context.MarkReady().Succeeded);
        Assert.True(module.Types.IsFrozen);
        Assert.True(module.Services.Types.IsFrozen);

        GasRegistrationResult afterReady = module.Types.RegisterAbility(
            new AbilityTypeId(41U),
            GasTestHarness.Descriptor("ability.late", 1U, 5));
        Assert.Equal(GasRegistrationStatus.Rejected, afterReady.Status);
        Assert.Equal("InvalidArgument", afterReady.GeneratedErrorId);
        Assert.False(module.Types.TryGetAbility(new AbilityTypeId(41U), out _));
    }

    [Fact]
    public void ModuleWorldsShareCatalogAndKeepIndependentHandleTables()
    {
        GasModule module = GasModule.Create();
        using GasWorldContext first = module.CreateWorldContext(
            new WorldId(119UL),
            new RecordingGasEcsProjectionPort());
        using GasWorldContext second = module.Services.CreateWorldContext(
            new WorldId(120UL),
            new RecordingGasEcsProjectionPort());

        Assert.Same(first.Types, second.Types);
        Assert.True(first.Register().Succeeded);
        Assert.True(first.MarkReady().Succeeded);
        Assert.True(first.Start().Succeeded);
        Assert.True(second.Register().Succeeded);
        Assert.True(second.MarkReady().Succeeded);
        Assert.True(second.Start().Succeeded);

        var instance = new AbilityInstanceId(7UL);
        AbilityHandleResult issuedFirst = first.CreateAbilityHandle(instance);
        AbilityHandleResult issuedSecond = second.CreateAbilityHandle(instance);
        Assert.True(issuedFirst.Succeeded);
        Assert.True(issuedSecond.Succeeded);
        Assert.NotEqual(issuedFirst.Handle, issuedSecond.Handle);

        Assert.True(first.RetireAbility(issuedFirst.Handle).Succeeded);
        Assert.False(first.TryResolveAbility(issuedFirst.Handle, out _).Resolved);
        Assert.True(second.TryResolveAbility(issuedSecond.Handle, out AbilityInstanceId live).Resolved);
        Assert.Equal(instance, live);
    }

    [Fact]
    public void CanonicalEnumerationDoesNotDependOnRegistrationOrder()
    {
        using GasWorldContext left = GasTestHarness.ContextAt(113UL, GasFrameworkState.Registered);
        using GasWorldContext right = GasTestHarness.ContextAt(114UL, GasFrameworkState.Registered);
        GasTypeDescriptor a = GasTestHarness.Descriptor("a", 1U, 1);
        GasTypeDescriptor b = GasTestHarness.Descriptor("b", 1U, 2);
        GasTypeDescriptor c = GasTestHarness.Descriptor("c", 1U, 3);

        Assert.Equal(GasRegistrationStatus.Registered, left.RegisterAbility(new AbilityTypeId(30U), c).Status);
        Assert.Equal(GasRegistrationStatus.Registered, left.RegisterAbility(new AbilityTypeId(10U), a).Status);
        Assert.Equal(GasRegistrationStatus.Registered, left.RegisterAbility(new AbilityTypeId(20U), b).Status);
        Assert.Equal(GasRegistrationStatus.Registered, right.RegisterAbility(new AbilityTypeId(10U), a).Status);
        Assert.Equal(GasRegistrationStatus.Registered, right.RegisterAbility(new AbilityTypeId(20U), b).Status);
        Assert.Equal(GasRegistrationStatus.Registered, right.RegisterAbility(new AbilityTypeId(30U), c).Status);

        Assert.Equal(left.Types.EnumerateAbilitiesCanonical(), right.Types.EnumerateAbilitiesCanonical());
        Assert.Equal(
            new[] { new AbilityTypeId(10U), new AbilityTypeId(20U), new AbilityTypeId(30U) },
            left.Types.EnumerateAbilitiesCanonical());
    }

    [Fact]
    public void StaleEpochAndDefaultTypeIdAreRejected()
    {
        using GasWorldContext context = GasTestHarness.ContextAt(115UL, GasFrameworkState.Registered);
        var stale = new GasTypeDescriptor(
            "ability.bolt",
            1U,
            GeneratedContractManifest.SchemaEpoch + 1,
            new byte[] { 1 });

        GasRegistrationResult epoch = context.RegisterAbility(new AbilityTypeId(5U), stale);
        GasRegistrationResult zero = context.RegisterAbility(default, GasTestHarness.Descriptor("ability.bolt", 1U, 1));

        Assert.Equal(GasRegistrationStatus.Rejected, epoch.Status);
        Assert.Equal("StaleEpoch", epoch.GeneratedErrorId);
        Assert.Equal(GasRegistrationStatus.Rejected, zero.Status);
        Assert.Equal("InvalidArgument", zero.GeneratedErrorId);
    }

    [Fact]
    public void EffectHandlesFollowTheSameWorldBoundGenerationRules()
    {
        using GasWorldContext first = GasTestHarness.Running(116UL);
        using GasWorldContext second = GasTestHarness.Running(117UL);
        var instance = new EffectInstanceId(44UL);

        EffectHandleResult a = first.CreateEffectHandle(instance);
        EffectHandleResult b = second.CreateEffectHandle(instance);
        Assert.True(a.Succeeded);
        Assert.True(b.Succeeded);
        Assert.NotEqual(a.Handle, b.Handle);
        Assert.True(first.RetireEffect(a.Handle).Succeeded);
        EffectHandleResult reuse = first.CreateEffectHandle(instance);
        Assert.True(reuse.Succeeded);
        Assert.True(reuse.Handle.Generation > a.Handle.Generation);
        Assert.False(first.TryResolveEffect(a.Handle, out _).Resolved);
        Assert.Equal("WrongContext", second.TryResolveEffect(a.Handle, out _).GeneratedErrorId);
    }

    [Fact]
    public void NoProcessWideHandleCounterExists()
    {
        FieldInfo[] staticFields = typeof(GasWorldContext)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(staticFields, field =>
            field.FieldType == typeof(int) ||
            field.FieldType == typeof(uint) ||
            field.FieldType == typeof(long) ||
            field.FieldType == typeof(ulong));
    }

    private static MethodInfo? FindConversion(Type from, Type to)
    {
        foreach (MethodInfo method in from.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if ((method.Name == "op_Implicit" || method.Name == "op_Explicit") &&
                method.ReturnType == to)
            {
                return method;
            }
        }

        return null;
    }
}
