using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace MonoWeaver.Patterns;

public abstract class PatternNode
{
    protected PatternNode(CilTypeSpec resultType)
    {
        ResultType = resultType ?? throw new ArgumentNullException(nameof(resultType));
    }

    /// <summary>
    /// 此 node 产生的 metadata type 约束。
    /// </summary>
    public CilTypeSpec ResultType { get; }

    /// <summary>
    /// 是否为根节点
    /// </summary>
    public bool IsRoot { get; set; }
}

/// <summary>
/// 匹配任意符合 type 约束的表达式。
/// </summary>
public sealed class AnyPatternNode : PatternNode
{
    internal AnyPatternNode(string captureName, CilTypeSpec resultType) : base(resultType)
    {
        CaptureName = captureName;
    }

    public string CaptureName { get; }
}

/// <summary>
/// 匹配 argument 读取。
/// </summary>
public sealed class ArgumentPatternNode : PatternNode
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

/// <summary>
/// 匹配 local 读取。
/// </summary>
public sealed class LocalPatternNode : PatternNode
{
    internal LocalPatternNode(int? index, string? captureName, CilTypeSpec resultType) : base(resultType)
    {
        Index = index;
        CaptureName = captureName;
    }

    public int? Index { get; }
    public string? CaptureName { get; }
}

/// <summary>
/// 匹配常量。
/// </summary>
public sealed class ConstantPatternNode : PatternNode
{
    internal ConstantPatternNode(object? value, CilTypeSpec resultType) : base(resultType)
    {
        Value = value;
    }

    public object? Value { get; }
}

/// <summary>
/// 匹配字段读取。
/// </summary>
public sealed class FieldPatternNode : PatternNode
{
    internal FieldPatternNode(CilFieldSpec field, PatternNode? instance) : base(field.FieldType)
    {
        Field = field ?? throw new ArgumentNullException(nameof(field));
        Instance = instance;
    }

    public CilFieldSpec Field { get; }
    public PatternNode? Instance { get; }
}

/// <summary>
/// 匹配一维数组创建。
/// </summary>
public sealed class NewArrayPatternNode : PatternNode
{
    internal NewArrayPatternNode(CilTypeSpec elementType, IReadOnlyList<PatternNode> lengths, CilTypeSpec resultType)
        : base(resultType)
    {
        ElementType = elementType ?? throw new ArgumentNullException(nameof(elementType));
        Lengths = lengths ?? throw new ArgumentNullException(nameof(lengths));
    }

    public CilTypeSpec ElementType { get; }
    public IReadOnlyList<PatternNode> Lengths { get; }
}

/// <summary>
/// 匹配数组元素读取。
/// </summary>
public sealed class ArrayElementPatternNode : PatternNode
{
    internal ArrayElementPatternNode(PatternNode array, PatternNode index, CilTypeSpec resultType)
        : base(resultType)
    {
        Array = array;
        Index = index;
    }

    public PatternNode Array { get; }
    public PatternNode Index { get; }
}

/// <summary>
/// 匹配数组长度读取。
/// </summary>
public sealed class ArrayLengthPatternNode : PatternNode
{
    internal ArrayLengthPatternNode(PatternNode array, CilTypeSpec resultType)
        : base(resultType)
    {
        Array = array;
    }

    public PatternNode Array { get; }
}

/// <summary>
/// 匹配数组元素写入。
/// </summary>
public sealed class ArrayStorePatternNode : PatternNode
{
    internal ArrayStorePatternNode(PatternNode array, PatternNode index, PatternNode value)
        : base(CilTypeSpec.Void)
    {
        Array = array;
        Index = index;
        Value = value;
    }

    public PatternNode Array { get; }
    public PatternNode Index { get; }
    public PatternNode Value { get; }
}

/// <summary>
/// 匹配函数调用。
/// </summary>
public sealed class CallPatternNode : PatternNode
{
    internal CallPatternNode(CilMethodSpec method, PatternNode? instance,
        IReadOnlyList<PatternNode> arguments, CilTypeSpec? resultType = null)
        : base(resultType ?? method.ReturnType)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Instance = instance;
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
    }

    public CilMethodSpec Method { get; }
    public PatternNode? Instance { get; }
    public IReadOnlyList<PatternNode> Arguments { get; }
}

/// <summary>
/// 匹配一元表达式。
/// </summary>
public sealed class UnaryPatternNode : PatternNode
{
    internal UnaryPatternNode(ExpressionType operation, PatternNode operand, CilMethodSpec? method,
        CilTypeSpec resultType)
        : base(resultType)
    {
        Operation = operation;
        Operand = operand;
        Method = method;
    }

    public ExpressionType Operation { get; }
    public PatternNode Operand { get; }
    public CilMethodSpec? Method { get; }
}

/// <summary>
/// 匹配二元表达式。
/// </summary>
public sealed class BinaryPatternNode : PatternNode
{
    internal BinaryPatternNode(ExpressionType operation, PatternNode left, PatternNode right,
        CilMethodSpec? method, CilTypeSpec resultType) : base(resultType)
    {
        Operation = operation;
        Left = left;
        Right = right;
        Method = method;
    }

    public ExpressionType Operation { get; }
    public PatternNode Left { get; }
    public PatternNode Right { get; }
    public CilMethodSpec? Method { get; }
}

/// <summary>
/// 标记并捕获一个具体匹配片段。
/// </summary>
public sealed class MarkPatternNode : PatternNode
{
    internal MarkPatternNode(string captureName, PatternNode inner) : base(inner.ResultType)
    {
        CaptureName = captureName;
        Inner = inner;
    }

    public string CaptureName { get; }
    public PatternNode Inner { get; }
}
