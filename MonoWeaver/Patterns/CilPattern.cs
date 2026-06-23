using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace MonoWeaver.Patterns;

/// <summary>
/// 描述匹配表达式在目标方法中的使用方式。
/// </summary>
public enum CilPatternKind
{
    Value, //匹配表达式返回一个值
    Effect, //匹配表达式不返回东西
    Condition, //匹配表达式返回 bool 并且影响分支
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
/// 将 pattern 匹配到方法时使用的选项。
/// </summary>
public sealed class CilPatternOptions
{
    // 匹配临时变量(local)的策略
    public TemporaryNormalization TemporaryNormalization { get; set; } = TemporaryNormalization.UniqueDefinitions;

    // 忽略 call/callvirt 差异 （c#编译生成 对于non-virtual一样会有callvirt）
    public bool IgnoreCallOpcodeDifference { get; set; } = true;

    //透明跳转展开 
    //eg:
    // brtrue.s IL_0010
    // IL_0010:
    // nop
    // br.s IL_0020
    // IL_0020:
    // ....
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
/// 从 lambda 表达式构建的可复用 pattern。lambda 只会被检查；
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

    /// <summary>此 pattern 的使用方式。</summary>
    public CilPatternKind Kind { get; }

    /// <summary>解析后的 pattern tree。</summary>
    public CilPatternNode Root { get; }

    /// <summary>matcher 应用的选项。</summary>
    public CilPatternOptions Options { get; }

    /// <summary>捕获的 local 的附加约束。</summary>
    public IReadOnlyList<LocalDefinitionConstraint> LocalDefinitionConstraints => _localDefinitionConstraints;

    /// <summary>
    /// 要求由 <paramref name="captureName"/> 捕获的 local 恰好有一个 stloc 来源，
    /// 并要求其 stloc的来源匹配 <paramref name="definition"/>。
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

    public static CilExpressionPattern operator==(CilExpressionPattern a, CilExpressionPattern b)
    {

    }
    public static CilExpressionPattern operator!=(CilExpressionPattern a, CilExpressionPattern b)
    {

    }
}

/// <summary>
/// expression-pattern DSL 的入口。
/// </summary>
public static class Cil
{
    /// <summary>为会返回一个值的表达式创建 pattern。
    /// （不需要实际保存到local或者arg，只要表达式返回值即可）
    /// </summary>
    public static CilExpressionPattern Value<T>(Expression<Func<T>> expression, CilPatternOptions? options = null)
        => Build(CilPatternKind.Value, expression, options);

    /// <summary>为会返回一个值的表达式创建 pattern。
    /// （不需要实际保存到local或者arg，只要表达式返回值即可）并在hook内忽略该返回值
    /// </summary>
    /*
    public static CilExpressionPattern Effect<T>(Expression<Func<T>> expression, CilPatternOptions? options = null)
        => Build(CilPatternKind.Effect, expression, options);
    */

    /// <summary>为无返回值的表达式创建 pattern。
    /// </summary>
    public static CilExpressionPattern Effect(Expression<Action> expression, CilPatternOptions? options = null)
        => Build(CilPatternKind.Effect, expression, options);

    /// <summary>为会返回bool并可能进行分支的表达式创建 pattern。
    /// （会按照短路结构匹配）
    /// </summary>
    public static CilExpressionPattern Condition(Expression<Func<bool>> expression, CilPatternOptions? options = null)
        => Build(CilPatternKind.Condition, expression, options);

    /// <summary>从 metadata-native expression 创建 value pattern；不会加载目标程序集。</summary>
    public static CilExpressionPattern Value(CilExpr expression, CilPatternOptions? options = null)
        => Build(CilPatternKind.Value, expression, options);

    /// <summary>从 metadata-native expression 创建 effect pattern；result type 必须为 void。</summary>
    public static CilExpressionPattern Effect(CilExpr expression, CilPatternOptions? options = null)
    {
        if (expression is null)
            throw new ArgumentNullException(nameof(expression));
        if (!expression.ResultType.IsVoid)
            throw new ArgumentException("An effect pattern must have Void result type.", nameof(expression));
        return Build(CilPatternKind.Effect, expression, options);
    }

    /// <summary>从 metadata-native expression 创建短路条件 pattern。</summary>
    public static CilExpressionPattern Condition(CilExpr expression, CilPatternOptions? options = null)
    {
        if (expression is null)
            throw new ArgumentNullException(nameof(expression));
        if (!expression.ResultType.IsBoolean)
            throw new ArgumentException("A condition pattern must have Boolean result type.", nameof(expression));
        return Build(CilPatternKind.Condition, expression, options);
    }

    private static CilExpressionPattern Build(CilPatternKind kind, CilExpr expression, CilPatternOptions? options)
    {
        if (expression is null)
            throw new ArgumentNullException(nameof(expression));
        return new CilExpressionPattern(kind, expression.Node, options);
    }

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
/// parser可识别的占位符。
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
    /// （注：index不包含this， 请使用This<T>匹配） 
    /// </summary>
    public static T Arg<T>(int index) => Throw<T>();

    /// <summary>
    /// 按参数序号匹配并捕获函数参数。
    /// （注：index不包含this， 请使用This<T>匹配） 
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
    /// 匹配捕获任意符合指定 type 的 表达式。
    /// </summary>
    public static T Any<T>(string captureName) => Throw<T>();

    /// <summary>
    /// 标记具体的匹配段，方便精确匹配；对于能直接匹配的进来不要使用。可能会影响和其他Hook的兼容。
    /// </summary>
    public static T Mark<T>(string captureName, T value) => Throw<T>();

    /// <summary>
    /// 匹配数组元素写入。
    /// </summary>
    public static void StoreElement<T>(T[] array, int index, T value)
        => ThrowVoid();

    /// <summary>匹配 metadata type 指定的 instance，不要求该 type 被 CLR 加载。</summary>
    public static CilExpr This(CilTypeSpec type, string? captureName = null)
        => new(new ArgumentPatternNode(true, null, NormalizeCapture(captureName), RequireType(type)));

    /// <summary>按显式参数 index 匹配 metadata type 指定的 argument。</summary>
    public static CilExpr Arg(int index, CilTypeSpec type, string? captureName = null)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        return new CilExpr(new ArgumentPatternNode(false, index, NormalizeCapture(captureName), RequireType(type)));
    }

    /// <summary>匹配任意序号、指定 metadata type 的 argument，并捕获。</summary>
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

    /// <summary>匹配 static method call。</summary>
    public static CilExpr Call(CilMethodSpec method, params CilExpr[] arguments)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (method.HasThis || method.IsConstructor)
            throw new ArgumentException("P.Call requires a static non-constructor method. Use instance.Call or P.New.", nameof(method));
        return new CilExpr(new CallPatternNode(method, null, CilExpr.RequireArguments(method, arguments)));
    }

    /// <summary>匹配 newobj。</summary>
    public static CilExpr New(CilMethodSpec constructor, params CilExpr[] arguments)
    {
        if (constructor is null)
            throw new ArgumentNullException(nameof(constructor));
        if (!constructor.IsConstructor)
            throw new ArgumentException("P.New requires a constructor specification.", nameof(constructor));
        return new CilExpr(new CallPatternNode(constructor, null,
            CilExpr.RequireArguments(constructor, arguments), constructor.DeclaringType));
    }

    /// <summary>匹配 static field read。</summary>
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
