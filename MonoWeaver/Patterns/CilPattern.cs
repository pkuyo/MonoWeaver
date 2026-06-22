using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace MonoWeaver.Patterns;

/// <summary>
/// 描述 root expression 在目标 method 中的使用方式。
/// </summary>
public enum CilPatternKind
{
    /// <summary>expression 必须产生一个值。</summary>
    Value,

    /// <summary>expression 只用于 side effect。</summary>
    Effect,

    /// <summary>expression 控制 true/false 决策，并且可能被 lowering 成 branch。</summary>
    Condition
}

/// <summary>
/// 控制 matcher 在匹配时多积极地把 compiler-generated local 视为 transparent。
/// matcher 有意保持保守：有歧义的 local 永远不会成为 match。
/// </summary>
public enum TemporaryNormalization
{
    /// <summary>永不从 local read 追踪到它的 definition。</summary>
    None,

    /// <summary>
    /// 只有在恰好一个 store 能到达该 load，且 local 没有被取地址时，才追踪该 local。
    /// </summary>
    UniqueDefinitions
}

/// <summary>
/// 将 pattern 匹配到 Cecil method body 时使用的选项。
/// </summary>
public sealed class CilPatternOptions
{
    /// <summary>
    /// 获取或设置 local temporary 的 normalization 方式。默认值较保守，但适用于常见的
    /// compiler-generated <c>stloc; ...; ldloc</c> 序列。
    /// </summary>
    public TemporaryNormalization TemporaryNormalization { get; set; } = TemporaryNormalization.UniqueDefinitions;

    /// <summary>
    /// 获取或设置在 referenced method 其他部分相同时，是否忽略 <c>call</c>/<c>callvirt</c>
    /// opcode 差异。默认启用，因为 C# 常为 non-virtual instance call 生成 <c>callvirt</c>
    /// 以保留 null check。
    /// </summary>
    public bool IgnoreCallOpcodeDifference { get; set; } = true;

    /// <summary>
    /// 获取或设置是否忽略 no-op instruction，以及只 forward 到另一个 block 的 basic block。
    /// </summary>
    public bool IgnoreTransparentControlFlow { get; set; } = true;
}

/// <summary>
/// local capture 可以被约束为：在 matched load 处由某个 value expression 唯一定义。
/// </summary>
public sealed class LocalDefinitionConstraint
{
    internal LocalDefinitionConstraint(string captureName, CilExpressionPattern definition)
    {
        CaptureName = captureName;
        Definition = definition;
    }

    /// <summary>传给 <see cref="P.Local{T}(string)"/> 的名称。</summary>
    public string CaptureName { get; }

    /// <summary>unique reaching definition 必须存储的 expression。</summary>
    public CilExpressionPattern Definition { get; }
}

/// <summary>
/// 从 lambda expression 构建的可复用 pattern。lambda 只会被检查；
/// 永远不会被编译或执行。
/// </summary>
public sealed class CilExpressionPattern
{
    private readonly List<LocalDefinitionConstraint> _localDefinitionConstraints = new();

    internal CilExpressionPattern(CilPatternKind kind, CilPatternNode root, CilPatternOptions? options)
    {
        Kind = kind;
        Root = root;
        Options = options ?? new CilPatternOptions();
    }

    /// <summary>此 pattern 的 root 使用方式。</summary>
    public CilPatternKind Kind { get; }

    /// <summary>解析后的 pattern tree。</summary>
    public CilPatternNode Root { get; }

    /// <summary>matcher 应用的选项。</summary>
    public CilPatternOptions Options { get; }

    /// <summary>captured local 的附加约束。</summary>
    public IReadOnlyList<LocalDefinitionConstraint> LocalDefinitionConstraints => _localDefinitionConstraints;

    /// <summary>
    /// 要求由 <paramref name="captureName"/> 捕获的 local 恰好有一个 reaching definition，
    /// 并要求其 stored value 匹配 <paramref name="definition"/>。
    /// </summary>
    public CilExpressionPattern LocalDefinedBy(string captureName, CilExpressionPattern definition)
    {
        if (string.IsNullOrWhiteSpace(captureName))
            throw new ArgumentException("A capture name is required.", nameof(captureName));
        if (definition is null)
            throw new ArgumentNullException(nameof(definition));
        if (definition.Kind != CilPatternKind.Value)
            throw new ArgumentException("A local definition constraint must use a value pattern.", nameof(definition));

        _localDefinitionConstraints.Add(new LocalDefinitionConstraint(captureName, definition));
        return this;
    }
}

/// <summary>
/// expression-pattern DSL 的入口。
/// </summary>
public static class Cil
{
    /// <summary>为会在 evaluation stack 上留下一个值的 expression 创建 pattern。</summary>
    public static CilExpressionPattern Value<T>(Expression<Func<T>> expression, CilPatternOptions? options = null)
        => Build(CilPatternKind.Value, expression, options);

    /// <summary>
    /// 从一个返回值会被目标 method 丢弃的 expression 创建 side-effect pattern。
    /// </summary>
    public static CilExpressionPattern Effect<T>(Expression<Func<T>> expression, CilPatternOptions? options = null)
        => Build(CilPatternKind.Effect, expression, options);

    /// <summary>为 void expression 创建 side-effect pattern。</summary>
    public static CilExpressionPattern Effect(Expression<Action> expression, CilPatternOptions? options = null)
        => Build(CilPatternKind.Effect, expression, options);

    /// <summary>
    /// 创建 boolean decision pattern。目标不需要 materialize 一个 Boolean 值；
    /// short-circuit branch 会按结构匹配。
    /// </summary>
    public static CilExpressionPattern Condition(Expression<Func<bool>> expression, CilPatternOptions? options = null)
        => Build(CilPatternKind.Condition, expression, options);

    private static CilExpressionPattern Build(CilPatternKind kind, LambdaExpression expression, CilPatternOptions? options)
    {
        if (expression is null)
            throw new ArgumentNullException(nameof(expression));
        if (expression.Parameters.Count != 0)
            throw new ArgumentException("CIL patterns currently require a parameterless lambda. Use P.Arg/P.Local/P.Any inside the lambda.", nameof(expression));

        var root = PatternExpressionParser.Parse(expression.Body);
        return new CilExpressionPattern(kind, root, options);
    }
}

/// <summary>
/// expression parser 可识别的 placeholder method。这些 method 只能出现在传给
/// <see cref="Cil"/> 的 lambda 内；直接执行它们是错误用法。
/// </summary>
public static class P
{
    /// <summary>匹配 instance (<c>this</c>) argument。</summary>
    public static T This<T>() => Throw<T>();

    /// <summary>匹配并捕获 instance (<c>this</c>) argument。</summary>
    public static T This<T>(string captureName) => Throw<T>();

    /// <summary>按 Cecil parameter index 匹配显式 method parameter，该 index 不包含 <c>this</c>。</summary>
    public static T Arg<T>(int index) => Throw<T>();

    /// <summary>按 Cecil parameter index 匹配并捕获显式 method parameter。</summary>
    public static T Arg<T>(int index, string captureName) => Throw<T>();

    /// <summary>匹配并捕获任意符合指定 type 的显式 parameter。</summary>
    public static T Arg<T>(string captureName) => Throw<T>();

    /// <summary>按 index 匹配 local variable。</summary>
    public static T Local<T>(int index) => Throw<T>();

    /// <summary>按 index 匹配并捕获 local variable。</summary>
    public static T Local<T>(int index, string captureName) => Throw<T>();

    /// <summary>匹配并捕获任意符合指定 type 的 local。</summary>
    public static T Local<T>(string captureName) => Throw<T>();

    /// <summary>匹配任意符合指定 nominal type 的 expression，并捕获该 occurrence。</summary>
    public static T Any<T>(string captureName) => Throw<T>();

    /// <summary>
    /// 标记精确的 subexpression 或 subcondition，且不弱化其内部 pattern。
    /// 因此 enclosing expression 可以消除重复 call 的歧义，而返回的 match 会指向被标记部分。
    /// </summary>
    public static T Mark<T>(string captureName, T value) => Throw<T>();

    /// <summary>匹配数组元素写入。</summary>
    public static void StoreElement<T>(T[] array, int index, T value)
        => ThrowVoid();

    private static T Throw<T>()
        => throw new InvalidOperationException("MonoWeaver pattern placeholders may only be used inside a lambda passed to Cil.Value/Cil.Effect/Cil.Condition.");

    private static void ThrowVoid()
        => throw new InvalidOperationException("MonoWeaver pattern placeholders may only be used inside a lambda passed to Cil.Value/Cil.Effect/Cil.Condition.");
}
