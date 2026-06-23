using System;
using Mono.Cecil;

namespace MonoWeaver.Patterns;

/// <summary>
/// 创建不加载 CLR 程序集的 metadata symbol。
/// </summary>
public static class CilSymbols
{
    /// <summary>
    /// 以程序集简单名为前缀创建一组 named symbol。
    /// </summary>
    public static CilAssemblySpec In(string assemblySimpleName)
        => new(assemblySimpleName);

    public static CilTypeSpec Type(TypeReference type) => CilTypeSpec.From(type);
    public static CilMethodSpec Method(MethodReference method) => CilMethodSpec.From(method);
    public static CilFieldSpec Field(FieldReference field) => CilFieldSpec.From(field);
}

/// <summary>
/// 仅用于 metadata identity 的程序集前缀；不会触发 Assembly.Load。
/// </summary>
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
