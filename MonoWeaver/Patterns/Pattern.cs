using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace MonoWeaver.Patterns;

/// <summary>
/// 控制 matcher 在匹配时是否把 compiler-generated local 视为 transparent。
/// matcher 会保持保守：有歧义的 local 永远不会成为 match。
/// </summary>
public enum TemporaryNormalization
{
    /// <summary>永不从 local read 追踪到它的 definition。</summary>
    None,

    /// <summary>只有在恰好一个 store 能到达该 load，且 local 没有被取地址时，才追踪该 local。</summary>
    UniqueDefinitions
}

/// <summary>将 pattern 匹配到方法时使用的选项。</summary>
public sealed class PatternOptions
{
    public TemporaryNormalization TemporaryNormalization { get; set; } = TemporaryNormalization.UniqueDefinitions;
    public bool IgnoreCallOpcodeDifference { get; set; } = true;
    public bool IgnoreTransparentControlFlow { get; set; } = true;
}

/// <summary>
/// expression-pattern 的公共基类。具体 pattern kind 由派生类型在编译期表达。
/// pattern 对象身份即捕获身份：节点以 <see cref="ExpressionPattern"/> 引用标记"以谁的身份捕获"，
/// 匹配结果按同一个对象取回。节点不可变，可在多个 pattern 间共享；"根"是相对一次匹配而言的，由 matcher 判定。
/// </summary>
public abstract class ExpressionPattern
{
    private static readonly IReadOnlyDictionary<string, ExpressionPattern> NoParameters =
        new Dictionary<string, ExpressionPattern>();

    private protected ExpressionPattern(PatternNode root, PatternOptions? options, string? displayName = null,
        IReadOnlyDictionary<string, ExpressionPattern>? parameters = null)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        Options = options ?? new PatternOptions();
        DisplayName = displayName;
        Parameters = parameters ?? NoParameters;
        ValidateShapeCaptures();
    }

    public PatternNode Root { get; }
    public PatternOptions Options { get; }

    /// <summary>
    /// lambda 参数名 → 该参数对应的 leaf pattern。普通类型参数是按目标参数名匹配的 <see cref="CilArg"/>
    /// （<c>__this</c> 是 <see cref="CilThis"/>），leaf 类型参数是对应的 pattern 局部 leaf。
    /// 匹配结果用 <c>match.Arg(name)</c> / <c>match.This()</c> / <c>match.Local(name)</c> 按此表取回。
    /// </summary>
    public IReadOnlyDictionary<string, ExpressionPattern> Parameters { get; }

    /// <summary>按 lambda 参数名取 leaf；没有该参数时抛出说明性的异常。</summary>
    public ExpressionPattern Parameter(string name)
    {
        if (name is null)
            throw new ArgumentNullException(nameof(name));
        if (Parameters.TryGetValue(name, out var leaf))
            return leaf;
        throw new KeyNotFoundException(Parameters.Count == 0
            ? $"Pattern '{this}' has no lambda parameters."
            : $"Pattern '{this}' has no lambda parameter named '{name}'. Available: {string.Join(", ", Parameters.Keys)}.");
    }

    /// <summary>可选显示名，仅用于诊断输出，不参与匹配。</summary>
    public string? DisplayName { get; }

    public override string ToString()
        => DisplayName ?? $"{GetType().Name.Split('`')[0]}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this):x8}";

    /// <summary>
    /// 嵌入到另一棵 pattern 树时使用的节点。leaf pattern 的根已以自己的身份捕获，直接共享该节点
    /// （再套 Mark 会与自身重复捕获）；其余形状类 pattern 包一层 Mark，以 pattern 对象身份捕获整个片段。
    /// </summary>
    internal PatternNode CreateEmbedNode()
        => RootIsOwnCaptureNode ? Root : new MarkPatternNode(this, Root);

    private bool RootIsOwnCaptureNode => Root switch
    {
        LocalPatternNode local => ReferenceEquals(local.Capture, this),
        ArgumentPatternNode argument => ReferenceEquals(argument.Capture, this),
        AnyPatternNode any => ReferenceEquals(any.Capture, this),
        _ => false,
    };

    /// <summary>
    /// 构造期校验：形状类捕获（通配、片段）不绑定身份，同一对象在树里出现两次即拒绝。
    /// 循环嵌入不需要检测：pattern 构造后不可变，无法包含自己。
    /// </summary>
    private void ValidateShapeCaptures()
    {
        //ExpressionPattern 不重写 Equals，默认比较器即引用相等。
        var seen = new HashSet<ExpressionPattern>();
        PatternNodeTree.Walk(Root, node =>
        {
            switch (node)
            {
                case AnyPatternNode { Capture: { } any } when !seen.Add(any):
                    throw new InvalidOperationException(
                        $"Wildcard '{any}' occurs more than once in this pattern. A wildcard does not bind an identity, so repetition is undefined; create a separate Cil.Any for each position.");
                case MarkPatternNode mark when !seen.Add(mark.Capture):
                    throw new InvalidOperationException(
                        $"Pattern fragment '{mark.Capture}' is embedded more than once in this pattern. A fragment does not bind an identity; create a separate pattern object for each position.");
            }
        });
    }
}

/// <summary>匹配一个会在当前 occurrence 产生值的表达式。</summary>
public class ValuePattern : ExpressionPattern
{
    internal ValuePattern(PatternNode root, PatternOptions? options, string? displayName = null,
        IReadOnlyDictionary<string, ExpressionPattern>? parameters = null)
        : base(root, options, displayName, parameters) { }

    /// <summary>此 pattern 作为片段嵌入 CilExpr 树。</summary>
    public CilExpr Expr => this;

    public static implicit operator CilExpr(ValuePattern pattern)
        => new((pattern ?? throw new ArgumentNullException(nameof(pattern))).CreateEmbedNode());
}

/// <summary>由强类型 lambda 构造的 value pattern。</summary>
public class ValuePattern<T> : ValuePattern
{
    internal ValuePattern(PatternNode root, PatternOptions? options, string? displayName = null)
        : base(root, options, displayName) { }

    internal ValuePattern(ParsedLambda parsed, PatternOptions? options)
        : base(parsed.Root, options, displayName: null, parsed.Parameters) { }

    /// <summary>
    /// 在 pattern lambda 中引用此 pattern 的显式写法。当 T 是 object 或 interface 时，
    /// C# 会用内建引用转换而绕过隐式转换算子，此时必须用 .Value；直接调用是错误用法。
    /// </summary>
    public T Value
        => throw new InvalidOperationException(
            "A pattern object can only be embedded inside a lambda passed to Cil.Value/Cil.Effect/Cil.Condition.");

    /// <summary>仅供在 pattern lambda 中把此 pattern 作为片段嵌入；直接调用是错误用法。</summary>
    public static implicit operator T(ValuePattern<T> pattern)
        => throw new InvalidOperationException(
            "A pattern object can only be embedded inside a lambda passed to Cil.Value/Cil.Effect/Cil.Condition.");
}

/// <summary>匹配一个完整的无结果 effect，包括 void expression 或被 pop 的 value expression。</summary>
public sealed class EffectPattern : ExpressionPattern
{
    internal EffectPattern(PatternNode root, PatternOptions? options)
        : base(root, options) { }

    internal EffectPattern(ParsedLambda parsed, PatternOptions? options)
        : base(parsed.Root, options, displayName: null, parsed.Parameters) { }
}

/// <summary>匹配一个会决定 true/false 控制流的 Boolean condition。</summary>
public sealed class ConditionPattern : ExpressionPattern
{
    internal ConditionPattern(PatternNode root, PatternOptions? options)
        : base(root, options) { }

    internal ConditionPattern(ParsedLambda parsed, PatternOptions? options)
        : base(parsed.Root, options, displayName: null, parsed.Parameters) { }

    /// <summary>此 pattern 作为片段嵌入 CilExpr 树。</summary>
    public CilExpr Expr => this;

    /// <summary>在 pattern lambda 中引用此条件片段的显式写法（整个 body 就是它时使用）；直接调用是错误用法。</summary>
    public bool Value
        => throw new InvalidOperationException(
            "A pattern object can only be embedded inside a lambda passed to Cil.Value/Cil.Effect/Cil.Condition.");

    /// <summary>仅供在 pattern lambda 中把此 pattern 作为条件片段嵌入；直接调用是错误用法。</summary>
    public static implicit operator bool(ConditionPattern pattern)
        => throw new InvalidOperationException(
            "A pattern object can only be embedded inside a lambda passed to Cil.Value/Cil.Effect/Cil.Condition.");

    public static implicit operator CilExpr(ConditionPattern pattern)
        => new((pattern ?? throw new ArgumentNullException(nameof(pattern))).CreateEmbedNode());
}

/// <summary>
/// 零结构通配 leaf：匹配任意符合类型约束的表达式。它就是 Root 为 AnyPatternNode 的 ValuePattern，
/// 不绑定身份，每个外层 pattern 只能出现一次；独立成类只为参数位声明可写出类型名。
/// </summary>
public sealed class CilAny<T> : ValuePattern<T>
{
    internal CilAny(AnyPatternNode node, string? displayName)
        : base(node, options: null, displayName)
    {
        node.Capture = this;
    }
}

/// <summary>
/// 绑定类 leaf：匹配 local 读取，命中时绑定到具体 VariableDefinition。
/// 同一对象重复出现表示同一个 local（合一）；可挂 definedBy 定义约束或 index 约束。
/// </summary>
public class CilLocal : ValuePattern
{
    internal CilLocal(LocalPatternNode node, string? displayName)
        : base(node, options: null, displayName)
    {
        node.Capture = this;
        LeafNode = node;
    }

    internal LocalPatternNode LeafNode { get; }
    public int? Index => LeafNode.Index;
    public ValuePattern? DefinedBy => LeafNode.Definition;
}

/// <summary>CLR 类型便捷形态的 Local leaf。</summary>
public sealed class CilLocal<T> : CilLocal
{
    internal CilLocal(LocalPatternNode node, string? displayName)
        : base(node, displayName) { }

    /// <summary>
    /// 在 pattern lambda 中引用此 pattern 的显式写法。当 T 是 object 或 interface 时，
    /// C# 会用内建引用转换而绕过隐式转换算子，此时必须用 .Value；直接调用是错误用法。
    /// </summary>
    public T Value
        => throw new InvalidOperationException(
            "A pattern object can only be embedded inside a lambda passed to Cil.Value/Cil.Effect/Cil.Condition.");

    /// <summary>仅供在 pattern lambda 中引用；直接调用是错误用法。</summary>
    public static implicit operator T(CilLocal<T> pattern)
        => throw new InvalidOperationException(
            "A pattern object can only be embedded inside a lambda passed to Cil.Value/Cil.Effect/Cil.Condition.");
}

/// <summary>
/// 绑定类 leaf：匹配 argument 读取，命中时绑定到具体 ParameterDefinition。
/// 同一对象重复出现表示同一个 argument（合一）；可挂 index 约束。
/// </summary>
public class CilArg : ValuePattern
{
    internal CilArg(ArgumentPatternNode node, string? displayName)
        : base(node, options: null, displayName)
    {
        node.Capture = this;
        LeafNode = node;
    }

    internal ArgumentPatternNode LeafNode { get; }
    public int? Index => LeafNode.Index;

    /// <summary>按目标方法参数名约束时的名字；否则为 null。</summary>
    public string? ParameterName => LeafNode.ParameterName;
}

/// <summary>CLR 类型便捷形态的 Arg leaf。</summary>
public sealed class CilArg<T> : CilArg
{
    internal CilArg(ArgumentPatternNode node, string? displayName)
        : base(node, displayName) { }

    /// <summary>
    /// 在 pattern lambda 中引用此 pattern 的显式写法。当 T 是 object 或 interface 时，
    /// C# 会用内建引用转换而绕过隐式转换算子，此时必须用 .Value；直接调用是错误用法。
    /// </summary>
    public T Value
        => throw new InvalidOperationException(
            "A pattern object can only be embedded inside a lambda passed to Cil.Value/Cil.Effect/Cil.Condition.");

    /// <summary>仅供在 pattern lambda 中引用；直接调用是错误用法。</summary>
    public static implicit operator T(CilArg<T> pattern)
        => throw new InvalidOperationException(
            "A pattern object can only be embedded inside a lambda passed to Cil.Value/Cil.Effect/Cil.Condition.");
}

/// <summary>绑定类 leaf：匹配 instance (this)。同一对象重复出现表示同一个 this。</summary>
public class CilThis : ValuePattern
{
    internal CilThis(ArgumentPatternNode node, string? displayName)
        : base(node, options: null, displayName)
    {
        node.Capture = this;
    }
}

/// <summary>CLR 类型便捷形态的 This leaf。</summary>
public sealed class CilThis<T> : CilThis
{
    internal CilThis(ArgumentPatternNode node, string? displayName)
        : base(node, displayName) { }

    /// <summary>
    /// 在 pattern lambda 中引用此 pattern 的显式写法。当 T 是 object 或 interface 时，
    /// C# 会用内建引用转换而绕过隐式转换算子，此时必须用 .Value；直接调用是错误用法。
    /// </summary>
    public T Value
        => throw new InvalidOperationException(
            "A pattern object can only be embedded inside a lambda passed to Cil.Value/Cil.Effect/Cil.Condition.");

    /// <summary>仅供在 pattern lambda 中引用；直接调用是错误用法。</summary>
    public static implicit operator T(CilThis<T> pattern)
        => throw new InvalidOperationException(
            "A pattern object can only be embedded inside a lambda passed to Cil.Value/Cil.Effect/Cil.Condition.");
}

/// <summary>expression-pattern DSL 的入口。</summary>
public static class Cil
{
    public static ValuePattern<T> Value<T>(Expression<Func<T>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ValuePattern<TResult> Value<T1, TResult>(Expression<Func<T1, TResult>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ValuePattern<TResult> Value<T1, T2, TResult>(Expression<Func<T1, T2, TResult>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ValuePattern<TResult> Value<T1, T2, T3, TResult>(Expression<Func<T1, T2, T3, TResult>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ValuePattern<TResult> Value<T1, T2, T3, T4, TResult>(Expression<Func<T1, T2, T3, T4, TResult>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ValuePattern<TResult> Value<T1, T2, T3, T4, T5, TResult>(Expression<Func<T1, T2, T3, T4, T5, TResult>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ValuePattern<TResult> Value<T1, T2, T3, T4, T5, T6, TResult>(Expression<Func<T1, T2, T3, T4, T5, T6, TResult>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ValuePattern<TResult> Value<T1, T2, T3, T4, T5, T6, T7, TResult>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, TResult>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ValuePattern<TResult> Value<T1, T2, T3, T4, T5, T6, T7, T8, TResult>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, T8, TResult>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ValuePattern<TResult> Value<T1, T2, T3, T4, T5, T6, T7, T8, T9, TResult>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, T8, T9, TResult>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ValuePattern<TResult> Value<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, TResult>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, TResult>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ValuePattern<TResult> Value<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, TResult>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, TResult>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ValuePattern<TResult> Value<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, TResult>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, TResult>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ValuePattern<TResult> Value<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, TResult>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, TResult>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ValuePattern<TResult> Value<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, TResult>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, TResult>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ValuePattern<TResult> Value<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, TResult>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, TResult>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ValuePattern<TResult> Value<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, TResult>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, TResult>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static EffectPattern Effect(Expression<Action> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static EffectPattern Effect<T1>(Expression<Action<T1>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static EffectPattern Effect<T1, T2>(Expression<Action<T1, T2>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static EffectPattern Effect<T1, T2, T3>(Expression<Action<T1, T2, T3>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static EffectPattern Effect<T1, T2, T3, T4>(Expression<Action<T1, T2, T3, T4>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static EffectPattern Effect<T1, T2, T3, T4, T5>(Expression<Action<T1, T2, T3, T4, T5>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static EffectPattern Effect<T1, T2, T3, T4, T5, T6>(Expression<Action<T1, T2, T3, T4, T5, T6>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static EffectPattern Effect<T1, T2, T3, T4, T5, T6, T7>(Expression<Action<T1, T2, T3, T4, T5, T6, T7>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static EffectPattern Effect<T1, T2, T3, T4, T5, T6, T7, T8>(Expression<Action<T1, T2, T3, T4, T5, T6, T7, T8>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static EffectPattern Effect<T1, T2, T3, T4, T5, T6, T7, T8, T9>(Expression<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static EffectPattern Effect<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(Expression<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static EffectPattern Effect<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(Expression<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static EffectPattern Effect<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(Expression<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static EffectPattern Effect<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(Expression<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static EffectPattern Effect<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(Expression<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static EffectPattern Effect<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(Expression<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static EffectPattern Effect<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(Expression<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ConditionPattern Condition(Expression<Func<bool>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ConditionPattern Condition<T1>(Expression<Func<T1, bool>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ConditionPattern Condition<T1, T2>(Expression<Func<T1, T2, bool>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ConditionPattern Condition<T1, T2, T3>(Expression<Func<T1, T2, T3, bool>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ConditionPattern Condition<T1, T2, T3, T4>(Expression<Func<T1, T2, T3, T4, bool>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ConditionPattern Condition<T1, T2, T3, T4, T5>(Expression<Func<T1, T2, T3, T4, T5, bool>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ConditionPattern Condition<T1, T2, T3, T4, T5, T6>(Expression<Func<T1, T2, T3, T4, T5, T6, bool>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ConditionPattern Condition<T1, T2, T3, T4, T5, T6, T7>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, bool>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ConditionPattern Condition<T1, T2, T3, T4, T5, T6, T7, T8>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, T8, bool>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ConditionPattern Condition<T1, T2, T3, T4, T5, T6, T7, T8, T9>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, T8, T9, bool>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ConditionPattern Condition<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, bool>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ConditionPattern Condition<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, bool>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ConditionPattern Condition<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, bool>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ConditionPattern Condition<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, bool>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ConditionPattern Condition<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, bool>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ConditionPattern Condition<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, bool>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ConditionPattern Condition<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>(Expression<Func<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, bool>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ValuePattern Value(CilExpr expression, PatternOptions? options = null)
    {
        if (expression is null)
            throw new ArgumentNullException(nameof(expression));
        if (expression.ResultType.IsVoid)
            throw new ArgumentException("A value pattern must have a non-Void result type.", nameof(expression));
        return new ValuePattern(expression.Node, options);
    }

    public static EffectPattern Effect(CilExpr expression, PatternOptions? options = null)
    {
        if (expression is null)
            throw new ArgumentNullException(nameof(expression));
        if (!expression.ResultType.IsVoid)
        {
            throw new ArgumentException(
                "An effect pattern must have Void result type. Use Cil.Discard for a value whose result is popped.",
                nameof(expression));
        }
        return new EffectPattern(expression.Node, options);
    }

    public static EffectPattern Discard(CilExpr expression, PatternOptions? options = null)
    {
        if (expression is null)
            throw new ArgumentNullException(nameof(expression));
        if (expression.ResultType.IsVoid)
        {
            throw new ArgumentException(
                "Cil.Discard requires a non-Void expression. Use Cil.Effect for a Void operation.",
                nameof(expression));
        }
        return new EffectPattern(expression.Node, options);
    }

    public static ConditionPattern Condition(CilExpr expression, PatternOptions? options = null)
    {
        if (expression is null)
            throw new ArgumentNullException(nameof(expression));
        if (!expression.ResultType.IsBoolean)
            throw new ArgumentException("A condition pattern must have Boolean result type.", nameof(expression));
        return new ConditionPattern(expression.Node, options);
    }

    // ---- 绑定类 / 形状类 leaf pattern 工厂 ----
    // name 仅用于诊断输出，不参与匹配。

    /// <summary>任意符合类型约束的表达式（零结构通配）。不绑定身份，每个 pattern 只能出现一次。</summary>
    public static CilAny<T> Any<T>(string? name = null)
        => new(new AnyPatternNode(CilTypeSpec.From(typeof(T)).Assignable()), name);

    /// <summary>任意符合 metadata 类型约束的表达式（零结构通配）。</summary>
    public static ValuePattern Any(CilTypeSpec type, string? name = null)
    {
        var node = new AnyPatternNode(RequireLeafType(type));
        var pattern = new ValuePattern(node, options: null, name);
        node.Capture = pattern;
        return pattern;
    }

    /// <summary>任意符合类型的 local。同一对象重复出现表示同一个 local。</summary>
    public static CilLocal<T> Local<T>(string? name = null)
        => new(new LocalPatternNode(index: null, CilTypeSpec.From(typeof(T)).Assignable()), name);

    /// <summary>指定序号的 local。</summary>
    public static CilLocal<T> Local<T>(int index, string? name = null)
        => new(new LocalPatternNode(RequireIndex(index), CilTypeSpec.From(typeof(T)).Assignable()), name);

    /// <summary>由指定 value expression 唯一定义的 local。</summary>
    public static CilLocal<T> Local<T>(ValuePattern<T> definedBy, string? name = null)
        => new(new LocalPatternNode(index: null, CilTypeSpec.From(typeof(T)).Assignable(),
            definedBy ?? throw new ArgumentNullException(nameof(definedBy))), name);

    /// <summary>任意符合 metadata 类型的 local。</summary>
    public static CilLocal Local(CilTypeSpec type, string? name = null)
        => new(new LocalPatternNode(index: null, RequireLeafType(type)), name);

    /// <summary>指定序号、指定 metadata 类型的 local。</summary>
    public static CilLocal Local(CilTypeSpec type, int index, string? name = null)
        => new(new LocalPatternNode(RequireIndex(index), RequireLeafType(type)), name);

    /// <summary>由指定 value expression 唯一定义、指定 metadata 类型的 local。</summary>
    public static CilLocal Local(CilTypeSpec type, ValuePattern definedBy, string? name = null)
        => new(new LocalPatternNode(index: null, RequireLeafType(type),
            definedBy ?? throw new ArgumentNullException(nameof(definedBy))), name);

    /// <summary>任意符合类型的 argument。同一对象重复出现表示同一个 argument。</summary>
    public static CilArg<T> Arg<T>()
        => new(new ArgumentPatternNode(isThis: false, index: null, CilTypeSpec.From(typeof(T)).Assignable()), displayName: null);

    /// <summary>指定序号的 argument。index 不包含 this。</summary>
    public static CilArg<T> Arg<T>(int index)
        => new(new ArgumentPatternNode(isThis: false, RequireIndex(index), CilTypeSpec.From(typeof(T)).Assignable()), displayName: null);

    /// <summary>
    /// 按目标方法的参数名匹配的 argument（与 lambda 参数按名绑定同一规则）。
    /// 参数名是目标程序集的 metadata 名字，与方法名、类型名同类。
    /// </summary>
    public static CilArg<T> Arg<T>(string parameterName)
        => new(new ArgumentPatternNode(isThis: false, index: null, CilTypeSpec.From(typeof(T)).Assignable(),
            RequireParameterName(parameterName)), parameterName);

    /// <summary>任意符合 metadata 类型的 argument。</summary>
    public static CilArg Arg(CilTypeSpec type)
        => new(new ArgumentPatternNode(isThis: false, index: null, RequireLeafType(type)), displayName: null);

    /// <summary>指定序号、指定 metadata 类型的 argument。index 不包含 this。</summary>
    public static CilArg Arg(CilTypeSpec type, int index)
        => new(new ArgumentPatternNode(isThis: false, RequireIndex(index), RequireLeafType(type)), displayName: null);

    /// <summary>按目标方法参数名匹配、指定 metadata 类型的 argument。</summary>
    public static CilArg Arg(CilTypeSpec type, string parameterName)
        => new(new ArgumentPatternNode(isThis: false, index: null, RequireLeafType(type),
            RequireParameterName(parameterName)), parameterName);

    /// <summary>参数位声明用：不按名约束、只带显示名的 argument leaf。</summary>
    internal static CilArg ArgForLambdaParameter(CilTypeSpec type, string? displayName)
        => new(new ArgumentPatternNode(isThis: false, index: null, RequireLeafType(type)), displayName);

    private static string RequireParameterName(string parameterName)
        => string.IsNullOrWhiteSpace(parameterName)
            ? throw new ArgumentException("A parameter name is required.", nameof(parameterName))
            : parameterName;

    /// <summary>instance (this)。同一对象重复出现表示同一个 this。</summary>
    public static CilThis<T> This<T>(string? name = null)
        => new(new ArgumentPatternNode(isThis: true, index: null, CilTypeSpec.From(typeof(T)).Assignable()), name);

    /// <summary>指定 metadata 类型的 instance (this)。</summary>
    public static CilThis This(CilTypeSpec type, string? name = null)
        => new(new ArgumentPatternNode(isThis: true, index: null, RequireLeafType(type)), name);

    private static CilTypeSpec RequireLeafType(CilTypeSpec? type)
    {
        if (type is null)
            throw new ArgumentNullException(nameof(type));
        if (type.IsVoid)
            throw new ArgumentException("A leaf pattern cannot have Void type.", nameof(type));
        return type;
    }

    private static int RequireIndex(int index)
        => index >= 0 ? index : throw new ArgumentOutOfRangeException(nameof(index));

    private static ParsedLambda Parse(LambdaExpression expression)
        => PatternExpressionParser.ParseLambda(expression);
}

/// <summary>
/// parser 可识别的占位符。
/// 这些占位符只能出现在传给 <see cref="Cil"/> 的 lambda 内；
/// 直接执行它们无实际意义，是错误用法。
/// </summary>
public static class P
{
    /// <summary>
    /// 匹配 instance (<c>this</c>)，不捕获。要捕获用 Cil.This&lt;T&gt;()。
    /// </summary>
    public static T This<T>() => Throw<T>();

    /// <summary>
    /// 按参数序号匹配函数参数，不捕获。要捕获用 Cil.Arg&lt;T&gt;(index)。
    /// 注：index 不包含 this，请使用 This&lt;T&gt; 匹配。
    /// </summary>
    public static T Arg<T>(int index) => Throw<T>();

    /// <summary>
    /// 按 index 匹配特定序号的 local，不捕获。要捕获用 Cil.Local&lt;T&gt;(index)。
    /// </summary>
    public static T Local<T>(int index) => Throw<T>();

    /// <summary>
    /// 匹配数组元素写入。
    /// </summary>
    public static void StoreElement<T>(T[] array, int index, T value)
        => ThrowVoid();

    /// <summary>
    /// 匹配字段写入。第一个参数写出字段访问表达式（如 <c>P.Arg&lt;Player&gt;(0).Hp</c> 或静态字段），
    /// 第二个参数描述写入的值。
    /// </summary>
    public static void StoreField<T>(T field, T value)
        => ThrowVoid();

    /// <summary>
    /// 匹配 metadata type 指定的 instance，不捕获，不要求该 type 被 CLR 加载。要捕获用 Cil.This(type)。
    /// </summary>
    public static CilExpr This(CilTypeSpec type)
        => new(new ArgumentPatternNode(true, null, RequireType(type)));

    /// <summary>
    /// 按显式参数 index 匹配 metadata type 指定的 argument，不捕获。要捕获用 Cil.Arg(type, index)。
    /// </summary>
    public static CilExpr Arg(int index, CilTypeSpec type)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        return new CilExpr(new ArgumentPatternNode(false, index, RequireType(type)));
    }

    /// <summary>
    /// 按 index 匹配特定序号的 local，不捕获。要捕获用 Cil.Local(type, index)。
    /// </summary>
    public static CilExpr Local(int index, CilTypeSpec type)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        return new CilExpr(new LocalPatternNode(index, RequireType(type)));
    }

    /// <summary>
    /// 匹配 static method call。
    /// </summary>
    public static CilExpr Call(CilMethodSpec method, params CilExpr[] arguments)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (method.HasThis || method.IsConstructor)
            throw new ArgumentException("P.Call requires a static non-constructor method. Use instance.Call or P.New.", nameof(method));
        return new CilExpr(new CallPatternNode(method, null, CilExpr.RequireArguments(method, arguments)));
    }

    /// <summary>
    /// 匹配 newobj。
    /// </summary>
    public static CilExpr New(CilMethodSpec constructor, params CilExpr[] arguments)
    {
        if (constructor is null)
            throw new ArgumentNullException(nameof(constructor));
        if (!constructor.IsConstructor)
            throw new ArgumentException("P.New requires a constructor specification.", nameof(constructor));
        return new CilExpr(new CallPatternNode(constructor, null,
            CilExpr.RequireArguments(constructor, arguments), constructor.DeclaringType));
    }

    /// <summary>
    /// 匹配 static field read。
    /// </summary>
    public static CilExpr Field(CilFieldSpec field)
    {
        if (field is null)
            throw new ArgumentNullException(nameof(field));
        if (field.IsStatic == false)
            throw new ArgumentException("P.Field requires a static field. Use instance.Field for an instance field.", nameof(field));
        return new CilExpr(new FieldPatternNode(field, null));
    }

    public static CilExpr NewArray(CilTypeSpec elementType, CilExpr length)
    {
        elementType = RequireType(elementType);
        if (length is null)
            throw new ArgumentNullException(nameof(length));
        return new CilExpr(new NewArrayPatternNode(elementType, new[] { length.Node }, elementType.MakeArrayType()));
    }

    public static CilExpr StoreElement(CilExpr array, CilExpr index, CilExpr value)
        => new(new ArrayStorePatternNode(
            array?.Node ?? throw new ArgumentNullException(nameof(array)),
            index?.Node ?? throw new ArgumentNullException(nameof(index)),
            value?.Node ?? throw new ArgumentNullException(nameof(value))));

    /// <summary>
    /// 匹配 instance field 写入（stfld）。
    /// </summary>
    public static CilExpr StoreField(CilExpr instance, CilFieldSpec field, CilExpr value)
    {
        if (instance is null)
            throw new ArgumentNullException(nameof(instance));
        if (field is null)
            throw new ArgumentNullException(nameof(field));
        if (field.IsStatic == true)
            throw new ArgumentException("The field is static. Use the P.StoreField(field, value) overload.", nameof(field));
        return new CilExpr(new FieldStorePatternNode(field, instance.Node,
            value?.Node ?? throw new ArgumentNullException(nameof(value))));
    }

    /// <summary>
    /// 匹配 static field 写入（stsfld）。
    /// </summary>
    public static CilExpr StoreField(CilFieldSpec field, CilExpr value)
    {
        if (field is null)
            throw new ArgumentNullException(nameof(field));
        if (field.IsStatic == false)
            throw new ArgumentException("The field is not static. Use the P.StoreField(instance, field, value) overload.", nameof(field));
        return new CilExpr(new FieldStorePatternNode(field, null,
            value?.Node ?? throw new ArgumentNullException(nameof(value))));
    }

    public static CilExpr Constant(object? value, CilTypeSpec type)
        => new(new ConstantPatternNode(value, RequireType(type)));

    public static CilExpr Constant(bool value) => Constant(value, CilTypeSpec.Boolean);
    public static CilExpr Constant(byte value) => Constant(value, CilTypeSpec.Byte);
    public static CilExpr Constant(sbyte value) => Constant(value, CilTypeSpec.SByte);
    public static CilExpr Constant(short value) => Constant(value, CilTypeSpec.Int16);
    public static CilExpr Constant(ushort value) => Constant(value, CilTypeSpec.UInt16);
    public static CilExpr Constant(int value) => Constant(value, CilTypeSpec.Int32);
    public static CilExpr Constant(uint value) => Constant(value, CilTypeSpec.UInt32);
    public static CilExpr Constant(long value) => Constant(value, CilTypeSpec.Int64);
    public static CilExpr Constant(ulong value) => Constant(value, CilTypeSpec.UInt64);
    public static CilExpr Constant(float value) => Constant(value, CilTypeSpec.Single);
    public static CilExpr Constant(double value) => Constant(value, CilTypeSpec.Double);
    public static CilExpr Constant(char value) => Constant(value, CilTypeSpec.Char);
    public static CilExpr Constant(string value)
        => Constant(value ?? throw new ArgumentNullException(nameof(value)), CilTypeSpec.String);
    public static CilExpr Null(CilTypeSpec? nominalType = null)
        => Constant(null, nominalType ?? CilTypeSpec.Object);

    private static CilTypeSpec RequireType(CilTypeSpec? type)
        => type ?? throw new ArgumentNullException(nameof(type));

    private static T Throw<T>()
        => throw new InvalidOperationException("MonoWeaver pattern placeholders may only be used inside a lambda passed to Cil.Value/Cil.Effect/Cil.Condition.");

    private static void ThrowVoid()
        => throw new InvalidOperationException("MonoWeaver pattern placeholders may only be used inside a lambda passed to Cil.Value/Cil.Effect/Cil.Condition.");
}
