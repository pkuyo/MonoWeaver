using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

namespace MonoWeaver.Utils;

internal static class CecilIdentity
{
    public static bool TypeMatches(TypeReference? cecilType, Type runtimeType)
    {
        if (cecilType is null)
            return false;
        if (runtimeType.IsGenericParameter)
            return cecilType is GenericParameter gp && gp.Position == runtimeType.GenericParameterPosition;
        return cecilType.IsSameWith(cecilType.Module.ImportReference(runtimeType));
    }

    public static bool MethodMatches(MethodReference candidate, MethodBase expected)
    {
        var candidateElement = candidate is GenericInstanceMethod gim ? gim.ElementMethod : candidate;
        var expectedElement = expected.IsGenericMethod ? ((MethodInfo)expected).GetGenericMethodDefinition() : expected;

        if (!string.Equals(candidateElement.Name, expectedElement.Name, StringComparison.Ordinal))
            return false;
        if (expectedElement.DeclaringType is null
            || !TypeMatches(candidateElement.DeclaringType, expectedElement.DeclaringType))
            return false;

        var expectedParameters = expectedElement.GetParameters();
        if (candidateElement.Parameters.Count != expectedParameters.Length)
            return false;

        for (var i = 0; i < expectedParameters.Length; i++)
        {
            if (!TypeMatches(candidateElement.Parameters[i].ParameterType, expectedParameters[i].ParameterType))
                return false;
        }

        if (expectedElement is MethodInfo expectedMethod)
        {
            if (!TypeMatches(candidateElement.ReturnType, expectedMethod.ReturnType))
                return false;
        }
        else if (!candidateElement.ReturnType.MetadataType.Equals(MetadataType.Void))
        {
            return false;
        }

        if (expected is MethodInfo constructed && constructed.IsGenericMethod && !constructed.IsGenericMethodDefinition)
        {
            if (candidate is not GenericInstanceMethod candidateGeneric)
                return false;
            var expectedArguments = constructed.GetGenericArguments();
            if (candidateGeneric.GenericArguments.Count != expectedArguments.Length)
                return false;
            for (var i = 0; i < expectedArguments.Length; i++)
            {
                if (!TypeMatches(candidateGeneric.GenericArguments[i], expectedArguments[i]))
                    return false;
            }
        }

        return true;
    }

    public static bool FieldMatches(FieldReference candidate, FieldInfo expected)
    {
        return string.Equals(candidate.Name, expected.Name, StringComparison.Ordinal)
               && expected.DeclaringType is not null
               && TypeMatches(candidate.DeclaringType, expected.DeclaringType)
               && TypeMatches(candidate.FieldType, expected.FieldType);
    }
}
