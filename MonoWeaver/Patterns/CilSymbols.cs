using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using MonoWeaver.Utils;

namespace MonoWeaver.Patterns;

/// <summary>创建不加载 CLR 程序集的 metadata symbol。</summary>
public static class CilSymbols
{
    /// <summary>以程序集简单名为前缀创建一组 named symbol。</summary>
    public static CilAssemblySpec In(string assemblySimpleName)
        => new(assemblySimpleName);

    public static CilTypeSpec Type(TypeReference type) => CilTypeSpec.From(type);
    public static CilMethodSpec Method(MethodReference method) => CilMethodSpec.From(method);
    public static CilFieldSpec Field(FieldReference field) => CilFieldSpec.From(field);
}

/// <summary>仅用于 metadata identity 的程序集前缀；不会触发 Assembly.Load。</summary>
public sealed class CilAssemblySpec
{
    internal CilAssemblySpec(string simpleName)
    {
        if (string.IsNullOrWhiteSpace(simpleName))
            throw new ArgumentException("An assembly simple name is required.", nameof(simpleName));
        SimpleName = simpleName.Trim();
    }

    public string SimpleName { get; }

    public CilTypeSpec Type(string metadataFullName, bool isValueType = false)
        => CilTypeSpec.Named(metadataFullName, SimpleName, isValueType);
}

/// <summary>
/// 从已经由 Mono.Cecil 打开的 module 中解析精确符号。所有 Require helper 都拒绝
/// 0 个或多个候选，避免字符串 API 静默选错重载。
/// </summary>
public static class CecilSymbolExtensions
{
    public static TypeDefinition RequireType(this ModuleDefinition module, string metadataFullName)
    {
        if (module is null)
            throw new ArgumentNullException(nameof(module));
        if (string.IsNullOrWhiteSpace(metadataFullName))
            throw new ArgumentException("A metadata type full name is required.", nameof(metadataFullName));

        var normalized = CilTypeSpec.NormalizeFullName(metadataFullName);
        var matches = EnumerateTypes(module.Types)
            .Where(type => string.Equals(CilTypeSpec.NormalizeFullName(type.FullName), normalized,
                StringComparison.Ordinal))
            .ToArray();
        return RequireSingle(matches, $"type '{normalized}' in module '{module.Name}'");
    }

    public static MethodDefinition RequireMethod(this TypeDefinition type, string name,
        params TypeReference[] parameterTypes)
        => RequireMethod(type, name, genericArity: 0, parameterTypes);

    public static MethodDefinition RequireMethod(this TypeDefinition type, string name, int genericArity,
        params TypeReference[] parameterTypes)
    {
        if (type is null)
            throw new ArgumentNullException(nameof(type));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A metadata method name is required.", nameof(name));
        if (genericArity < 0)
            throw new ArgumentOutOfRangeException(nameof(genericArity));
        if (parameterTypes is null)
            throw new ArgumentNullException(nameof(parameterTypes));
        if (parameterTypes.Any(static typeReference => typeReference is null))
            throw new ArgumentException("Parameter types cannot contain null.", nameof(parameterTypes));

        var matches = type.Methods.Where(method =>
        {
            if (!string.Equals(method.Name, name, StringComparison.Ordinal)
                || method.GenericParameters.Count != genericArity
                || method.Parameters.Count != parameterTypes.Length)
            {
                return false;
            }

            for (var i = 0; i < parameterTypes.Length; i++)
            {
                if (!method.Parameters[i].ParameterType.IsSameWith(parameterTypes[i]))
                    return false;
            }

            return true;
        }).ToArray();

        return RequireSingle(matches,
            $"method '{type.FullName}::{name}' with the requested signature");
    }

    public static MethodDefinition RequireMethod(this TypeDefinition type, CilMethodSpec signature)
    {
        if (type is null)
            throw new ArgumentNullException(nameof(type));
        if (signature is null)
            throw new ArgumentNullException(nameof(signature));

        var matches = type.Methods.Where(signature.Matches).ToArray();
        return RequireSingle(matches, $"method '{signature}'");
    }

    public static FieldDefinition RequireField(this TypeDefinition type, string name)
    {
        if (type is null)
            throw new ArgumentNullException(nameof(type));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A metadata field name is required.", nameof(name));

        var matches = type.Fields
            .Where(field => string.Equals(field.Name, name, StringComparison.Ordinal))
            .ToArray();
        return RequireSingle(matches, $"field '{type.FullName}::{name}'");
    }

    public static FieldDefinition RequireField(this TypeDefinition type, CilFieldSpec signature)
    {
        if (type is null)
            throw new ArgumentNullException(nameof(type));
        if (signature is null)
            throw new ArgumentNullException(nameof(signature));

        var matches = type.Fields.Where(signature.Matches).ToArray();
        return RequireSingle(matches, $"field '{signature}'");
    }

    private static IEnumerable<TypeDefinition> EnumerateTypes(IEnumerable<TypeDefinition> roots)
    {
        foreach (var type in roots)
        {
            yield return type;
            foreach (var nested in EnumerateTypes(type.NestedTypes))
                yield return nested;
        }
    }

    private static T RequireSingle<T>(IReadOnlyList<T> matches, string description)
    {
        if (matches.Count == 1)
            return matches[0];
        if (matches.Count == 0)
            throw new MissingMemberException($"Could not find {description}.");
        throw new AmbiguousMatchException(
            $"Found {matches.Count} candidates for {description}; provide an exact signature.");
    }
}
