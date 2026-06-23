using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace MonoWeaver.Patterns;

public abstract class CilPatternNode
{
    protected CilPatternNode(CilTypeSpec resultType)
    {
        ResultType = resultType ?? throw new ArgumentNullException(nameof(resultType));
    }

    /// <summary>此 node 产生的 metadata type 约束。</summary>
    public CilTypeSpec ResultType { get; }
}

/// <summary>匹配任意符合 type 约束的表达式。</summary>
public sealed class AnyPatternNode : CilPatternNode
{
    internal AnyPatternNode(string captureName, CilTypeSpec resultType) : base(resultType)
    {
        CaptureName = captureName;
    }

    public string CaptureName { get; }
}

/// <summary>匹配 argument 读取。</summary>
public sealed class ArgumentPatternNode : CilPatternNode
{
    internal ArgumentPatternNode(bool isThis, int? index, string? captureName, CilTypeSpec resultType) : base(resultType)
    {
        IsThis = isThis;
        Index = index;
        CaptureName = captureName;
    }

    public bool IsThis { get; }
    public int? Index { get; }
    public string? CaptureName { get; }
}

/// <summary>匹配 local 读取。</summary>
public sealed class LocalPatternNode : CilPatternNode
{
    internal LocalPatternNode(int? index, string? captureName, CilTypeSpec resultType) : base(resultType)
    {
        Index = index;
        CaptureName = captureName;
    }

    public int? Index { get; }
    public string? CaptureName { get; }
}

/// <summary>匹配常量。</summary>
public sealed class ConstantPatternNode : CilPatternNode
{
    internal ConstantPatternNode(object? value, CilTypeSpec resultType) : base(resultType)
    {
        Value = value;
    }

    public object? Value { get; }
}

/// <summary>匹配字段读取。</summary>
public sealed class FieldPatternNode : CilPatternNode
{
    internal FieldPatternNode(CilFieldSpec field, CilPatternNode? instance) : base(field.FieldType)
    {
        Field = field ?? throw new ArgumentNullException(nameof(field));
        Instance = instance;
    }

    public CilFieldSpec Field { get; }
    public CilPatternNode? Instance { get; }
}

/// <summary>匹配一维数组创建。</summary>
public sealed class NewArrayPatternNode : CilPatternNode
{
    internal NewArrayPatternNode(CilTypeSpec elementType, IReadOnlyList<CilPatternNode> lengths, CilTypeSpec resultType)
        : base(resultType)
    {
        ElementType = elementType ?? throw new ArgumentNullException(nameof(elementType));
        Lengths = lengths ?? throw new ArgumentNullException(nameof(lengths));
    }

    public CilTypeSpec ElementType { get; }
    public IReadOnlyList<CilPatternNode> Lengths { get; }
}

/// <summary>匹配数组元素读取。</summary>
public sealed class ArrayElementPatternNode : CilPatternNode
{
    internal ArrayElementPatternNode(CilPatternNode array, CilPatternNode index, CilTypeSpec resultType)
        : base(resultType)
    {
        Array = array;
        Index = index;
    }

    public CilPatternNode Array { get; }
    public CilPatternNode Index { get; }
}

/// <summary>匹配数组长度读取。</summary>
public sealed class ArrayLengthPatternNode : CilPatternNode
{
    internal ArrayLengthPatternNode(CilPatternNode array, CilTypeSpec resultType)
        : base(resultType)
    {
        Array = array;
    }

    public CilPatternNode Array { get; }
}

/// <summary>匹配数组元素写入。</summary>
public sealed class ArrayStorePatternNode : CilPatternNode
{
    internal ArrayStorePatternNode(CilPatternNode array, CilPatternNode index, CilPatternNode value)
        : base(CilTypeSpec.Void)
    {
        Array = array;
        Index = index;
        Value = value;
    }

    public CilPatternNode Array { get; }
    public CilPatternNode Index { get; }
    public CilPatternNode Value { get; }
}

/// <summary>匹配函数调用。</summary>
public sealed class CallPatternNode : CilPatternNode
{
    internal CallPatternNode(CilMethodSpec method, CilPatternNode? instance,
        IReadOnlyList<CilPatternNode> arguments, CilTypeSpec? resultType = null)
        : base(resultType ?? method.ReturnType)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Instance = instance;
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
    }

    public CilMethodSpec Method { get; }
    public CilPatternNode? Instance { get; }
    public IReadOnlyList<CilPatternNode> Arguments { get; }
}

/// <summary>匹配一元表达式。</summary>
public sealed class UnaryPatternNode : CilPatternNode
{
    internal UnaryPatternNode(ExpressionType operation, CilPatternNode operand, CilMethodSpec? method,
        CilTypeSpec resultType)
        : base(resultType)
    {
        Operation = operation;
        Operand = operand;
        Method = method;
    }

    public ExpressionType Operation { get; }
    public CilPatternNode Operand { get; }
    public CilMethodSpec? Method { get; }
}

/// <summary>匹配二元表达式。</summary>
public sealed class BinaryPatternNode : CilPatternNode
{
    internal BinaryPatternNode(ExpressionType operation, CilPatternNode left, CilPatternNode right,
        CilMethodSpec? method, CilTypeSpec resultType) : base(resultType)
    {
        Operation = operation;
        Left = left;
        Right = right;
        Method = method;
    }

    public ExpressionType Operation { get; }
    public CilPatternNode Left { get; }
    public CilPatternNode Right { get; }
    public CilMethodSpec? Method { get; }
}

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
