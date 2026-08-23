using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using MonoWeaver.Patterns;
// --8<-- [start:typecheck-usings]
using MonoWeaver.Utils;
// --8<-- [end:typecheck-usings]

namespace MonoWeaver.DocSamples;

public static class TypeCheckSamples
{
    public static void ThreeChecks(TypeReference actual, TypeReference expected)
    {
        // --8<-- [start:three-checks]
        bool exactlySame = actual.IsSameWith(expected);
        bool canAssign = actual.IsAssignableTo(expected);
        bool sameMeaning = expected.IsAssignableFrom(actual);
        // --8<-- [end:three-checks]
        _ = (exactlySame, canAssign, sameMeaning);
    }

    public static void ExactOverload(TypeDefinition type, TypeReference enemyType)
    {
        // --8<-- [start:exact-overload]
        var overload = type.Methods.Single(method =>
            method.Name == "SetTarget" &&
            method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.IsSameWith(enemyType));
        // --8<-- [end:exact-overload]
        _ = overload;
    }

    public static void AssignabilityCheck(ValueTarget captured, MethodReference callback)
    {
        // --8<-- [start:assignability]
        TypeReference gameValueType = captured.ValueType;
        TypeReference callbackParameterType = callback.Parameters[0].ParameterType;

        if (!gameValueType.IsAssignableTo(callbackParameterType))
            throw new InvalidOperationException("The callback parameter cannot receive this game value.");
        // --8<-- [end:assignability]
    }

    public static void AccessChecks(MethodDefinition method, MethodDefinition targetMethod,
        FieldDefinition targetField, TypeReference targetType)
    {
        // --8<-- [start:access-checks]
        TypeReference callerType = method.DeclaringType;

        bool canCall = callerType.CanAccess(targetMethod);
        bool canReadField = callerType.CanAccess(targetField);
        bool canUseType = callerType.CanAccess(targetType);
        // --8<-- [end:access-checks]
        _ = (canCall, canReadField, canUseType);
    }

    public static void Helpers(TypeReference type, TypeReference left, TypeReference right,
        TypeReference genericType, MethodReference genericMethod)
    {
        // --8<-- [start:type-helpers]
        var plainType = type.StripType();
        var parent = type.BaseType();
        var commonParent = CecilTypeSystem.FindCommonBaseType(left, right);

        var interfaces = new List<TypeReference>();
        CecilTypeSystem.CollectAllInterfaces(type, interfaces);

        bool typeArgumentsOk = genericType.CheckConstraints();
        bool methodArgumentsOk = genericMethod.CheckConstraints();
        // --8<-- [end:type-helpers]
        _ = (plainType, parent, commonParent, typeArgumentsOk, methodArgumentsOk);
    }

    public static void StackCompatibility(TypeReference from, TypeReference to)
    {
        // --8<-- [start:stack-assignable]
        bool compatibleOnRuntimeStack = from.IsILStackAssignableTo(to);
        // --8<-- [end:stack-assignable]
        _ = compatibleOnRuntimeStack;
    }
}
