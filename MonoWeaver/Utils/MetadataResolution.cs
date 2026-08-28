using System;
using Mono.Cecil;

namespace MonoWeaver.Utils;

/// <summary>
/// 元数据引用 → 定义 的统一入口。
/// </summary>
/// <remarks>
/// 所有 <c>.Resolve()</c> 都应经过这里：当前线程有显式 <see cref="IMetadataResolver"/>（校验器按解析范围设置）时用它，
/// 否则用引用所属模块自己的 resolver。任何解析异常都收敛为 null，由调用方决定是否报诊断。
/// </remarks>
internal static class MetadataResolution
{
    [ThreadStatic]
    private static IMetadataResolver? _current;

    /// <summary>当前线程正在使用的解析器；null 表示按模块自己的 resolver 解析。</summary>
    public static IMetadataResolver? Current => _current;

    /// <summary>在当前线程上启用指定解析器，Dispose 时恢复之前的值。</summary>
    public static Scope Enter(IMetadataResolver? resolver)
    {
        var previous = _current;
        _current = resolver;
        return new Scope(previous);
    }

    public readonly struct Scope(IMetadataResolver? previous) : IDisposable
    {
        public void Dispose() => _current = previous;
    }

    public static IMemberDefinition? TryResolve(MemberReference reference)
        => TryResolve(reference, out _);

    public static IMemberDefinition? TryResolve(MemberReference reference, out Exception? error)
    {
        error = null;
        switch (reference)
        {
            case TypeReference type:
                return TryResolve(type, out error);
            case MethodReference method:
                return TryResolve(method, out error);
            case FieldReference field:
                return TryResolve(field, out error);
            default:
                return null;
        }
    }

    public static TypeDefinition? TryResolve(TypeReference? reference)
        => TryResolve(reference, out _);

    public static TypeDefinition? TryResolve(TypeReference? reference, out Exception? error)
    {
        error = null;
        switch (reference)
        {
            case null:
            case GenericParameter:
                return null;
            case TypeDefinition definition:
                return definition;
        }

        var resolver = _current;
        try
        {
            return resolver is null ? reference.Resolve() : resolver.Resolve(reference);
        }
        catch (Exception e)
        {
            error = e;
            return null;
        }
    }

    public static MethodDefinition? TryResolve(MethodReference? reference)
        => TryResolve(reference, out _);

    public static MethodDefinition? TryResolve(MethodReference? reference, out Exception? error)
    {
        error = null;
        switch (reference)
        {
            case null:
                return null;
            case MethodDefinition definition:
                return definition;
        }

        var resolver = _current;
        try
        {
            return resolver is null ? reference.Resolve() : resolver.Resolve(reference);
        }
        catch (Exception e)
        {
            error = e;
            return null;
        }
    }

    public static FieldDefinition? TryResolve(FieldReference? reference)
        => TryResolve(reference, out _);

    public static FieldDefinition? TryResolve(FieldReference? reference, out Exception? error)
    {
        error = null;
        switch (reference)
        {
            case null:
                return null;
            case FieldDefinition definition:
                return definition;
        }

        var resolver = _current;
        try
        {
            return resolver is null ? reference.Resolve() : resolver.Resolve(reference);
        }
        catch (Exception e)
        {
            error = e;
            return null;
        }
    }
}
