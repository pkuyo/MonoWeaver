using System;
using System.Collections.Generic;
using System.Reflection;

namespace MonoWeaver.Patterns;

/// <summary>解析后的 expression pattern 中所有 node 的 base type。</summary>
public abstract class CilPatternNode
{
    protected CilPatternNode(Type resultType)
    {
        ResultType = resultType ?? throw new ArgumentNullException(nameof(resultType));
    }

    /// <summary>此 node 产生的 nominal CLR type。</summary>
    public Type ResultType { get; }
}

/// <summary>匹配任意 compatible nominal type 的 expression。</summary>
public sealed class AnyPatternNode : CilPatternNode
{
    internal AnyPatternNode(string captureName, Type resultType) : base(resultType)
    {
        CaptureName = captureName;
    }

    public string CaptureName { get; }
}

/// <summary>匹配 instance argument 或显式 method parameter。</summary>
public sealed class ArgumentPatternNode : CilPatternNode
{
    internal ArgumentPatternNode(bool isThis, int? index, string? captureName, Type resultType) : base(resultType)
    {
        IsThis = isThis;
        Index = index;
        CaptureName = captureName;
    }

    public bool IsThis { get; }
    public int? Index { get; }
    public string? CaptureName { get; }
}

/// <summary>匹配 local-variable load。</summary>
public sealed class LocalPatternNode : CilPatternNode
{
    internal LocalPatternNode(int? index, string? captureName, Type resultType) : base(resultType)
    {
        Index = index;
        CaptureName = captureName;
    }

    public int? Index { get; }
    public string? CaptureName { get; }
}

/// <summary>匹配 literal constant。</summary>
public sealed class ConstantPatternNode : CilPatternNode
{
    internal ConstantPatternNode(object? value, Type resultType) : base(resultType)
    {
        Value = value;
    }

    public object? Value { get; }
}

/// <summary>匹配 field read。</summary>
public sealed class FieldPatternNode : CilPatternNode
{
    internal FieldPatternNode(FieldInfo field, CilPatternNode? instance) : base(field.FieldType)
    {
        Field = field;
        Instance = instance;
    }

    public FieldInfo Field { get; }
    public CilPatternNode? Instance { get; }
}

/// <summary>匹配 constructor、static method、instance method 或 property getter call。</summary>
public sealed class CallPatternNode : CilPatternNode
{
    internal CallPatternNode(MethodBase method, CilPatternNode? instance, IReadOnlyList<CilPatternNode> arguments, Type resultType)
        : base(resultType)
    {
        Method = method;
        Instance = instance;
        Arguments = arguments;
    }

    public MethodBase Method { get; }
    public CilPatternNode? Instance { get; }
    public IReadOnlyList<CilPatternNode> Arguments { get; }
}

/// <summary>匹配 unary operation。</summary>
public sealed class UnaryPatternNode : CilPatternNode
{
    internal UnaryPatternNode(System.Linq.Expressions.ExpressionType operation, CilPatternNode operand, MethodInfo? method, Type resultType)
        : base(resultType)
    {
        Operation = operation;
        Operand = operand;
        Method = method;
    }

    public System.Linq.Expressions.ExpressionType Operation { get; }
    public CilPatternNode Operand { get; }
    public MethodInfo? Method { get; }
}

/// <summary>匹配有顺序的 binary expression。</summary>
public sealed class BinaryPatternNode : CilPatternNode
{
    internal BinaryPatternNode(System.Linq.Expressions.ExpressionType operation, CilPatternNode left, CilPatternNode right,
        MethodInfo? method, Type resultType) : base(resultType)
    {
        Operation = operation;
        Left = left;
        Right = right;
        Method = method;
    }

    public System.Linq.Expressions.ExpressionType Operation { get; }
    public CilPatternNode Left { get; }
    public CilPatternNode Right { get; }
    public MethodInfo? Method { get; }
}

/// <summary>标记精确的 nested occurrence，同时保留 surrounding pattern 作为 context。</summary>
public sealed class MarkPatternNode : CilPatternNode
{
    internal MarkPatternNode(string captureName, CilPatternNode inner) : base(inner.ResultType)
    {
        CaptureName = captureName;
        Inner = inner;
    }

    public string CaptureName { get; }
    public CilPatternNode Inner { get; }
}
