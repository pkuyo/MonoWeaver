using System;
using System.Reflection;
using Mono.Cecil;

namespace MonoWeaver.Utils;

public static partial class CecilHelper
{
    /// <summary>
    /// 运行时 <see cref="Type"/> 与 Cecil 类型按名字身份比较（<see cref="CecilTypeSystem.TypeSig.SameName"/>）：
    /// 不比程序集，不 Resolve，不 ImportReference。两边各自缓存一次签名，之后每次比较只是两次查表。
    /// </summary>
    public static bool TypeMatches(TypeReference? cecilType, Type runtimeType)
    {
        if (cecilType is null || runtimeType is null)
            return false;
        return CecilTypeSystem.TypeSig.Create(cecilType).SameName(CecilTypeSystem.TypeSig.Create(runtimeType));
    }

    public static bool TypeMatches(TypeReference? candidate, TypeReference? expected)
        => candidate is not null && expected is not null && candidate.IsSameWith(expected);

    /// <summary>
    /// 把签名中引用 declaring type 泛型参数的部分（如 Nullable`1 成员签名里的 !0）
    /// 用 declaringType 的泛型实参实例化；无法实例化时原样返回。
    /// </summary>
    public static TypeReference InflateDeclaringGenerics(TypeReference type, TypeReference? declaringType)
    {
        if (declaringType is not GenericInstanceType genericInstance)
            return type;

        switch (type)
        {
            case GenericParameter { Type: GenericParameterType.Type } parameter
                when parameter.Position >= 0 && parameter.Position < genericInstance.GenericArguments.Count:
                return genericInstance.GenericArguments[parameter.Position];

            case ArrayType array:
            {
                var element = InflateDeclaringGenerics(array.ElementType, declaringType);
                return ReferenceEquals(element, array.ElementType) ? type : new ArrayType(element, array.Rank);
            }

            case ByReferenceType byReference:
            {
                var element = InflateDeclaringGenerics(byReference.ElementType, declaringType);
                return ReferenceEquals(element, byReference.ElementType) ? type : new ByReferenceType(element);
            }

            case GenericInstanceType nested:
            {
                var changed = false;
                var inflated = new GenericInstanceType(nested.ElementType);
                foreach (var argument in nested.GenericArguments)
                {
                    var inflatedArgument = InflateDeclaringGenerics(argument, declaringType);
                    changed |= !ReferenceEquals(inflatedArgument, argument);
                    inflated.GenericArguments.Add(inflatedArgument);
                }
                return changed ? inflated : type;
            }

            default:
                return type;
        }
    }

    public static bool MethodMatches(MethodReference candidate, MethodBase expected)
    {
        if (candidate is null)
            throw new ArgumentNullException(nameof(candidate));
        if (expected is null)
            throw new ArgumentNullException(nameof(expected));

        var candidateElement = candidate is GenericInstanceMethod gim ? gim.ElementMethod : candidate;
        var expectedElement = expected.IsGenericMethod ? ((MethodInfo)expected).GetGenericMethodDefinition() : expected;

        if (!string.Equals(candidateElement.Name, expectedElement.Name, StringComparison.Ordinal))
            return false;
        if (candidateElement.HasThis == expectedElement.IsStatic)
            return false;
        if (expectedElement.DeclaringType is null
            || !TypeMatches(candidateElement.DeclaringType, expectedElement.DeclaringType))
            return false;

        var expectedParameters = expectedElement.GetParameters();
        if (candidateElement.Parameters.Count != expectedParameters.Length)
            return false;

        //methodref 挂在 GenericInstanceType 上时，参数/返回签名仍是开放的 !0；先按 declaring type 实例化再比较
        var candidateDeclaringType = candidateElement.DeclaringType;
        for (var i = 0; i < expectedParameters.Length; i++)
        {
            var candidateParameter = InflateDeclaringGenerics(
                candidateElement.Parameters[i].ParameterType, candidateDeclaringType);
            if (!TypeMatches(candidateParameter, expectedParameters[i].ParameterType))
                return false;
        }

        if (expectedElement is MethodInfo expectedMethod)
        {
            if (!TypeMatches(InflateDeclaringGenerics(candidateElement.ReturnType, candidateDeclaringType),
                    expectedMethod.ReturnType))
                return false;
        }
        else if (candidateElement.ReturnType.MetadataType != MetadataType.Void)
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

    public static bool MethodMatches(MethodReference candidate, MethodReference expected)
    {
        if (candidate is null)
            throw new ArgumentNullException(nameof(candidate));
        if (expected is null)
            throw new ArgumentNullException(nameof(expected));

        var candidateElement = candidate is GenericInstanceMethod candidateGeneric
            ? candidateGeneric.ElementMethod
            : candidate;
        var expectedElement = expected is GenericInstanceMethod expectedGeneric
            ? expectedGeneric.ElementMethod
            : expected;

        if (!string.Equals(candidateElement.Name, expectedElement.Name, StringComparison.Ordinal)
            || candidateElement.HasThis != expectedElement.HasThis
            || candidateElement.ExplicitThis != expectedElement.ExplicitThis
            || candidateElement.CallingConvention != expectedElement.CallingConvention
            || !TypeMatches(candidateElement.DeclaringType, expectedElement.DeclaringType)
            || candidateElement.GenericParameters.Count != expectedElement.GenericParameters.Count
            || candidateElement.Parameters.Count != expectedElement.Parameters.Count
            || !TypeMatches(candidateElement.ReturnType, expectedElement.ReturnType))
        {
            return false;
        }

        for (var i = 0; i < candidateElement.Parameters.Count; i++)
        {
            if (!TypeMatches(candidateElement.Parameters[i].ParameterType, expectedElement.Parameters[i].ParameterType))
                return false;
        }

        if (expected is GenericInstanceMethod expectedInstance)
        {
            if (candidate is not GenericInstanceMethod candidateInstance
                || candidateInstance.GenericArguments.Count != expectedInstance.GenericArguments.Count)
            {
                return false;
            }

            for (var i = 0; i < expectedInstance.GenericArguments.Count; i++)
            {
                if (!TypeMatches(candidateInstance.GenericArguments[i], expectedInstance.GenericArguments[i]))
                    return false;
            }
        }

        return true;
    }

    public static bool FieldMatches(FieldReference candidate, FieldInfo expected)
    {
        if (candidate is null)
            throw new ArgumentNullException(nameof(candidate));
        if (expected is null)
            throw new ArgumentNullException(nameof(expected));

        return string.Equals(candidate.Name, expected.Name, StringComparison.Ordinal)
               && expected.DeclaringType is not null
               && TypeMatches(candidate.DeclaringType, expected.DeclaringType)
               && TypeMatches(candidate.FieldType, expected.FieldType);
    }

    public static bool FieldMatches(FieldReference candidate, FieldReference expected)
    {
        if (candidate is null)
            throw new ArgumentNullException(nameof(candidate));
        if (expected is null)
            throw new ArgumentNullException(nameof(expected));

        return string.Equals(candidate.Name, expected.Name, StringComparison.Ordinal)
               && TypeMatches(candidate.DeclaringType, expected.DeclaringType)
               && TypeMatches(candidate.FieldType, expected.FieldType);
    }
}
