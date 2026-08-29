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
}

/// <summary>
/// 匹配任意符合 type 约束的表达式。
/// </summary>
public sealed class AnyPatternNode : PatternNode
{
    internal AnyPatternNode(CilTypeSpec resultType) : base(resultType) { }

    /// <summary>以哪个 pattern 对象的身份捕获此节点的匹配；由拥有它的 pattern 在构造时设置。</summary>
    public ExpressionPattern? Capture { get; internal set; }
}

/// <summary>
/// 匹配 argument 读取。
/// </summary>
public sealed class ArgumentPatternNode : PatternNode
{
    internal ArgumentPatternNode(bool isThis, int? index, CilTypeSpec resultType, string? parameterName = null)
        : base(resultType)
    {
        IsThis = isThis;
        Index = index;
        ParameterName = parameterName;
    }

    public bool IsThis { get; }
    public int? Index { get; }

    /// <summary>按目标方法的参数名约束；null 表示不按名约束。</summary>
    public string? ParameterName { get; }

    /// <summary>以哪个 pattern 对象的身份捕获此节点的匹配；null 表示只匹配不捕获。</summary>
    public ExpressionPattern? Capture { get; internal set; }
}

/// <summary>
/// 匹配 local 读取。
/// </summary>
public sealed class LocalPatternNode : PatternNode
{
    internal LocalPatternNode(int? index, CilTypeSpec resultType, ValuePattern? definition = null)
        : base(resultType)
    {
        Index = index;
        Definition = definition;
    }

    public int? Index { get; }

    /// <summary>以哪个 pattern 对象的身份捕获此节点的匹配；null 表示只匹配不捕获。</summary>
    public ExpressionPattern? Capture { get; internal set; }

    /// <summary>该 local 的定义约束（来自 Cil.Local(definedBy)），matcher 在绑定此节点时就地验证。</summary>
    public ValuePattern? Definition { get; }
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
/// 匹配字段写入（stfld/stsfld）。Instance 为 null 表示 static field。
/// </summary>
public sealed class FieldStorePatternNode : PatternNode
{
    internal FieldStorePatternNode(CilFieldSpec field, PatternNode? instance, PatternNode value)
        : base(CilTypeSpec.Void)
    {
        Field = field ?? throw new ArgumentNullException(nameof(field));
        Instance = instance;
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public CilFieldSpec Field { get; }
    public PatternNode? Instance { get; }
    public PatternNode Value { get; }
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
/// 标记并捕获一个具体匹配片段。由 pattern 内嵌产生，以片段 pattern 对象的身份捕获。
/// </summary>
public sealed class MarkPatternNode : PatternNode
{
    internal MarkPatternNode(ExpressionPattern capture, PatternNode inner) : base(inner.ResultType)
    {
        Capture = capture ?? throw new ArgumentNullException(nameof(capture));
        Inner = inner;
    }

    public ExpressionPattern Capture { get; }
    public PatternNode Inner { get; }
}

/// <summary>pattern 树的遍历。节点不可变、可在多个 pattern 间共享。</summary>
internal static class PatternNodeTree
{
    /// <summary>按位置访问树中每个节点；共享子树会按出现位置访问多次。</summary>
    public static void Walk(PatternNode root, Action<PatternNode> visit)
    {
        var stack = new Stack<PatternNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            visit(node);
            foreach (var child in Children(node))
                stack.Push(child);
        }
    }

    private static IEnumerable<PatternNode> Children(PatternNode node)
    {
        switch (node)
        {
            case FieldPatternNode field:
                if (field.Instance is not null)
                    yield return field.Instance;
                break;
            case FieldStorePatternNode fieldStore:
                if (fieldStore.Instance is not null)
                    yield return fieldStore.Instance;
                yield return fieldStore.Value;
                break;
            case NewArrayPatternNode newArray:
                foreach (var length in newArray.Lengths)
                    yield return length;
                break;
            case ArrayElementPatternNode element:
                yield return element.Array;
                yield return element.Index;
                break;
            case ArrayLengthPatternNode length:
                yield return length.Array;
                break;
            case ArrayStorePatternNode store:
                yield return store.Array;
                yield return store.Index;
                yield return store.Value;
                break;
            case CallPatternNode call:
                if (call.Instance is not null)
                    yield return call.Instance;
                foreach (var argument in call.Arguments)
                    yield return argument;
                break;
            case UnaryPatternNode unary:
                yield return unary.Operand;
                break;
            case BinaryPatternNode binary:
                yield return binary.Left;
                yield return binary.Right;
                break;
            case MarkPatternNode mark:
                yield return mark.Inner;
                break;
        }
    }
}
