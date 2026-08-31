using System;
using System.Reflection;
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Ecs.Tests;

internal static class EcsTestRegistration
{
    internal static ComponentTypeRegistrationResult Register(
        EcsWorld world,
        ComponentTypeDefinition? definition)
    {
        MethodInfo registration = AssertRegistrationMethod();
        ParameterInfo[] parameters = registration.GetParameters();
        object?[] arguments;
        if (parameters.Length == 1)
        {
            arguments = new object?[] { definition };
        }
        else
        {
            Type capabilityType = parameters[0].ParameterType;
            FieldInfo capabilityField = Array.Find(
                typeof(EcsWorld).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
                field => field.FieldType == capabilityType) ??
                throw new InvalidOperationException("World component-registration capability is missing.");
            arguments = new[] { capabilityField.GetValue(world), definition };
        }

        return (ComponentTypeRegistrationResult)(registration.Invoke(world, arguments) ??
            throw new InvalidOperationException("Component registration returned null."));
    }

    internal static MethodInfo AssertRegistrationMethod() =>
        Array.Find(
            typeof(EcsWorld).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic),
            static method => method.Name == "RegisterComponentType") ??
        throw new InvalidOperationException("Component registration method is missing.");
}
