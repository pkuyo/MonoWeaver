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
/// local capture 可以被约束为：在匹配到的 load 处由某个 value expression 唯一定义。
/// </summary>
public sealed class LocalDefinitionConstraint
{
    internal LocalDefinitionConstraint(string captureName, ValuePattern definition)
    {
        CaptureName = captureName;
        Definition = definition;
    }

    public string CaptureName { get; }
    public ValuePattern Definition { get; }
}

/// <summary>expression-pattern 的公共基类。具体 pattern kind 由派生类型在编译期表达。</summary>
public abstract class ExpressionPattern
{
    private readonly List<LocalDefinitionConstraint> _localDefinitionConstraints = new();

    private protected ExpressionPattern(PatternNode root, PatternOptions? options)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        root.IsRoot = true;
        Options = options ?? new PatternOptions();
    }

    public PatternNode Root { get; }
    public PatternOptions Options { get; }
    public IReadOnlyList<LocalDefinitionConstraint> LocalDefinitionConstraints => _localDefinitionConstraints;

    private protected void AddLocalDefinitionConstraint(string captureName, ValuePattern definition)
    {
        if (string.IsNullOrWhiteSpace(captureName))
            throw new ArgumentException("A capture name is required.", nameof(captureName));
        if (definition is null)
            throw new ArgumentNullException(nameof(definition));
        _localDefinitionConstraints.Add(new LocalDefinitionConstraint(captureName, definition));
    }
}

/// <summary>匹配一个会在当前 occurrence 产生值的表达式。</summary>
public class ValuePattern : ExpressionPattern
{
    internal ValuePattern(PatternNode root, PatternOptions? options)
        : base(root, options) { }

    public ValuePattern LocalDefinedBy(string captureName, ValuePattern definition)
    {
        AddLocalDefinitionConstraint(captureName, definition);
        return this;
    }
}

/// <summary>由强类型 lambda 构造的 value pattern。</summary>
public sealed class ValuePattern<T> : ValuePattern
{
    internal ValuePattern(PatternNode root, PatternOptions? options)
        : base(root, options) { }

    public new ValuePattern<T> LocalDefinedBy(string captureName, ValuePattern definition)
    {
        AddLocalDefinitionConstraint(captureName, definition);
        return this;
    }
}

/// <summary>匹配一个完整的无结果 effect，包括 void expression 或被 pop 的 value expression。</summary>
public sealed class EffectPattern : ExpressionPattern
{
    internal EffectPattern(PatternNode root, PatternOptions? options)
        : base(root, options) { }

    public EffectPattern LocalDefinedBy(string captureName, ValuePattern definition)
    {
        AddLocalDefinitionConstraint(captureName, definition);
        return this;
    }
}

/// <summary>匹配一个会决定 true/false 控制流的 Boolean condition。</summary>
public sealed class ConditionPattern : ExpressionPattern
{
    internal ConditionPattern(PatternNode root, PatternOptions? options)
        : base(root, options) { }

    public ConditionPattern LocalDefinedBy(string captureName, ValuePattern definition)
    {
        AddLocalDefinitionConstraint(captureName, definition);
        return this;
    }
}

/// <summary>expression-pattern DSL 的入口。</summary>
public static class Cil
{
    public static ValuePattern<T> Value<T>(Expression<Func<T>> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static EffectPattern Effect(Expression<Action> expression, PatternOptions? options = null)
        => new(Parse(expression), options);

    public static ConditionPattern Condition(Expression<Func<bool>> expression, PatternOptions? options = null)
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

    private static PatternNode Parse(LambdaExpression expression)
    {
        if (expression is null)
            throw new ArgumentNullException(nameof(expression));
        if (expression.Parameters.Count != 0)
        {
            throw new ArgumentException(
                "CIL patterns require a parameterless lambda. Use P.Arg/P.Local/P.Any inside the lambda.",
                nameof(expression));
        }
        return PatternExpressionParser.Parse(expression.Body);
    }
}

/// <summary>
/// parser 可识别的占位符。
/// 这些占位符只能出现在传给 <see cref="Cil"/> 的 lambda 内；
/// 直接执行它们无实际意义，是错误用法。
/// </summary>
public static class P
{
    /// <summary>
    /// 匹配 instance (<c>this</c>)。
    /// </summary>
    public static T This<T>() => Throw<T>();

    /// <summary>
    /// 匹配并捕获 instance (<c>this</c>)。
    /// </summary>
    public static T This<T>(string captureName) => Throw<T>();

    /// <summary>
    /// 按参数序号匹配函数参数。
    /// 注：index 不包含 this，请使用 This&lt;T&gt; 匹配。
    /// </summary>
    public static T Arg<T>(int index) => Throw<T>();

    /// <summary>
    /// 按参数序号匹配并捕获函数参数。
    /// 注：index 不包含 this，请使用 This&lt;T&gt; 匹配。
    /// </summary>
    public static T Arg<T>(int index, string captureName) => Throw<T>();

    /// <summary>
    /// 匹配并捕获任意符合指定 type 的参数。
    /// </summary>
    public static T Arg<T>(string captureName) => Throw<T>();

    /// <summary>
    /// 按 index 匹配特定序号的 local。
    /// </summary>
    public static T Local<T>(int index) => Throw<T>();

    /// <summary>
    /// 按 index 匹配并捕获特定序号的 local。
    /// </summary>
    public static T Local<T>(int index, string captureName) => Throw<T>();

    /// <summary>
    /// 匹配并捕获任意符合指定 type 的 local。
    /// </summary>
    public static T Local<T>(string captureName) => Throw<T>();

    /// <summary>
    /// 匹配并捕获任意符合指定 type 的表达式。
    /// </summary>
    public static T Any<T>(string captureName) => Throw<T>();

    /// <summary>
    /// 标记具体的匹配段，方便精确匹配。
    /// 对于能直接匹配的内容不要使用，可能会影响和其他 hook 的兼容。
    /// </summary>
    public static T Mark<T>(string captureName, T value) => Throw<T>();

    /// <summary>
    /// 匹配数组元素写入。
    /// </summary>
    public static void StoreElement<T>(T[] array, int index, T value)
        => ThrowVoid();

    /// <summary>
    /// 匹配 metadata type 指定的 instance，不要求该 type 被 CLR 加载。
    /// </summary>
    public static CilExpr This(CilTypeSpec type, string? captureName = null)
        => new(new ArgumentPatternNode(true, null, NormalizeCapture(captureName), RequireType(type)));

    /// <summary>
    /// 按显式参数 index 匹配 metadata type 指定的 argument。
    /// </summary>
    public static CilExpr Arg(int index, CilTypeSpec type, string? captureName = null)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        return new CilExpr(new ArgumentPatternNode(false, index, NormalizeCapture(captureName), RequireType(type)));
    }

    /// <summary>
    /// 匹配任意序号、指定 metadata type 的 argument，并捕获。
    /// </summary>
    public static CilExpr Arg(CilTypeSpec type, string captureName)
        => new(new ArgumentPatternNode(false, null, RequireCapture(captureName), RequireType(type)));

    public static CilExpr Local(int index, CilTypeSpec type, string? captureName = null)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        return new CilExpr(new LocalPatternNode(index, NormalizeCapture(captureName), RequireType(type)));
    }

    public static CilExpr Local(CilTypeSpec type, string captureName)
        => new(new LocalPatternNode(null, RequireCapture(captureName), RequireType(type)));

    public static CilExpr Any(CilTypeSpec type, string captureName)
        => new(new AnyPatternNode(RequireCapture(captureName), RequireType(type)));

    public static CilExpr Mark(string captureName, CilExpr value)
        => new(new MarkPatternNode(RequireCapture(captureName),
            value?.Node ?? throw new ArgumentNullException(nameof(value))));

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

    private static string RequireCapture(string captureName)
    {
        if (string.IsNullOrWhiteSpace(captureName))
            throw new ArgumentException("A non-empty capture name is required.", nameof(captureName));
        return captureName;
    }

    private static string? NormalizeCapture(string? captureName)
        => captureName is null ? null : RequireCapture(captureName);

    private static T Throw<T>()
        => throw new InvalidOperationException("MonoWeaver pattern placeholders may only be used inside a lambda passed to Cil.Value/Cil.Effect/Cil.Condition.");

    private static void ThrowVoid()
        => throw new InvalidOperationException("MonoWeaver pattern placeholders may only be used inside a lambda passed to Cil.Value/Cil.Effect/Cil.Condition.");
}
