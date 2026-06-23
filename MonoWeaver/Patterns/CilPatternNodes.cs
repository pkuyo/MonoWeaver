using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace MonoWeaver.Patterns;


public abstract class CilPatternNode
{
    protected CilPatternNode(Type resultType)
    {
        ResultType = resultType ?? throw new ArgumentNullException(nameof(resultType));
    }

    /// <summary>此 node 产生的 nominal CLR type。</summary>
    public Type ResultType { get; }
}

/// <summary>
/// 匹配任意符合Type约束的表达式。
/// </summary>
public sealed class AnyPatternNode : CilPatternNode
{
    internal AnyPatternNode(string captureName, Type resultType) : base(resultType)
    {
        CaptureName = captureName;
    }

    public string CaptureName { get; }
}

/// <summary>
/// 匹配arg读取
/// </summary>
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

/// <summary>
/// 匹配local读取。
/// </summary>
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

/// <summary>
/// 匹配常量。
/// </summary>
public sealed class ConstantPatternNode : CilPatternNode
{
    internal ConstantPatternNode(object? value, Type resultType) : base(resultType)
    {
        Value = value;
    }

    public object? Value { get; }
}

/// <summary>
/// 匹配字段读取。
/// </summary>
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

/// <summary>
/// 匹配一维数组创建。
/// </summary>
public sealed class NewArrayPatternNode : CilPatternNode
{
    internal NewArrayPatternNode(Type elementType, IReadOnlyList<CilPatternNode> lengths, Type resultType)
        : base(resultType)
    {
        ElementType = elementType;
        Lengths = lengths;
    }

    public Type ElementType { get; }
    public IReadOnlyList<CilPatternNode> Lengths { get; }
}

/// <summary>
/// 匹配数组元素读取。
/// </summary>
public sealed class ArrayElementPatternNode : CilPatternNode
{
    internal ArrayElementPatternNode(CilPatternNode array, CilPatternNode index, Type resultType)
        : base(resultType)
    {
        Array = array;
        Index = index;
    }

    public CilPatternNode Array { get; }
    public CilPatternNode Index { get; }
}

/// <summary>
/// 匹配数组长度读取。
/// </summary>
public sealed class ArrayLengthPatternNode : CilPatternNode
{
    internal ArrayLengthPatternNode(CilPatternNode array, Type resultType)
        : base(resultType)
    {
        Array = array;
    }

    public CilPatternNode Array { get; }
}

/// <summary>
/// 匹配数组元素写入。
/// </summary>
public sealed class ArrayStorePatternNode : CilPatternNode
{
    internal ArrayStorePatternNode(CilPatternNode array, CilPatternNode index, CilPatternNode value)
        : base(typeof(void))
    {
        Array = array;
        Index = index;
        Value = value;
    }

    public CilPatternNode Array { get; }
    public CilPatternNode Index { get; }
    public CilPatternNode Value { get; }
}

/// <summary>
/// 匹配函数调用。
/// </summary>
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

/// <summary>
/// 匹配一元表达式。
/// </summary>
public sealed class UnaryPatternNode : CilPatternNode
{
    internal UnaryPatternNode(ExpressionType operation, CilPatternNode operand, MethodInfo? method, Type resultType)
        : base(resultType)
    {
        Operation = operation;
        Operand = operand;
        Method = method;
    }

    public ExpressionType Operation { get; }
    public CilPatternNode Operand { get; }
    public MethodInfo? Method { get; }
}

/// <summary>
/// 匹配二元表达式。
/// </summary>
public sealed class BinaryPatternNode : CilPatternNode
{
    internal BinaryPatternNode(ExpressionType operation, CilPatternNode left, CilPatternNode right,
        MethodInfo? method, Type resultType) : base(resultType)
    {
        Operation = operation;
        Left = left;
        Right = right;
        Method = method;
    }

    public ExpressionType Operation { get; }
    public CilPatternNode Left { get; }
    public CilPatternNode Right { get; }
    public MethodInfo? Method { get; }
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
