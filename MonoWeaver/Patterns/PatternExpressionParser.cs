using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace MonoWeaver.Patterns;

internal sealed class PatternExpressionParser
{
    private static readonly Type PlaceholderType = typeof(P);

    //每个 lambda 参数对应一个不对外暴露的 leaf pattern：同一参数的每次使用共享同一个节点（同一身份支撑合一）。
    private readonly Dictionary<ParameterExpression, ExpressionPattern> _parameterLeaves = new();
    private readonly Dictionary<string, ExpressionPattern> _parametersByName = new(StringComparer.Ordinal);

    private PatternExpressionParser() { }

    public static PatternNode Parse(Expression expression)
        => new PatternExpressionParser().ParseNode(expression);

    /// <summary>解析 lambda：返回根节点和"lambda 参数名 → 其 leaf pattern"表。</summary>
    public static ParsedLambda ParseLambda(LambdaExpression lambda)
    {
        if (lambda is null)
            throw new ArgumentNullException(nameof(lambda));
        var parser = new PatternExpressionParser();
        //未在 body 里出现的参数也登记，结果侧按名取时能给出明确错误而不是"未捕获"。
        foreach (var parameter in lambda.Parameters)
            parser.GetParameterLeaf(parameter);
        var root = parser.ParseNode(lambda.Body);
        return new ParsedLambda(root, parser._parametersByName);
    }

    private PatternNode ParseNode(Expression expression)
    {
        if (expression is null)
            throw new ArgumentNullException(nameof(expression));

        return expression.NodeType switch
        {
            ExpressionType.Call => ParseCall((MethodCallExpression)expression),
            ExpressionType.MemberAccess => ParseMember((MemberExpression)expression),
            ExpressionType.Constant => ParseConstant((ConstantExpression)expression),
            ExpressionType.ArrayLength => ParseArrayLength((UnaryExpression)expression),
            ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.Not or ExpressionType.Negate or
                ExpressionType.NegateChecked or ExpressionType.UnaryPlus or ExpressionType.TypeAs
                => ParseUnary((UnaryExpression)expression),
            ExpressionType.ArrayIndex => ParseArrayIndex((BinaryExpression)expression),
            ExpressionType.Assign => ParseAssign((BinaryExpression)expression),
            ExpressionType.Add or ExpressionType.AddChecked or ExpressionType.Subtract or ExpressionType.SubtractChecked or
                ExpressionType.Multiply or ExpressionType.MultiplyChecked or ExpressionType.Divide or ExpressionType.Modulo or
                ExpressionType.And or ExpressionType.Or or ExpressionType.ExclusiveOr or ExpressionType.LeftShift or
                ExpressionType.RightShift or ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan or
                ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual or
                ExpressionType.AndAlso or ExpressionType.OrElse or ExpressionType.Coalesce
                => ParseBinary((BinaryExpression)expression),
            ExpressionType.NewArrayBounds => ParseNewArrayBounds((NewArrayExpression)expression),
            ExpressionType.New => ParseNew((NewExpression)expression),
            ExpressionType.Parameter => ParseParameter((ParameterExpression)expression),
            ExpressionType.Default => new ConstantPatternNode(null, CilTypeSpec.From(expression.Type)),
            _ => throw Unsupported(expression, $"Expression node '{expression.NodeType}' is not supported by the single-expression matcher.")
        };
    }

    private PatternNode ParseCall(MethodCallExpression call)
    {
        if (call.Method.DeclaringType == PlaceholderType)
            return ParsePlaceholder(call);

        if (call.Method.DeclaringType == typeof(Cil))
        {
            throw Unsupported(call,
                "Cil factory calls cannot appear inside a pattern lambda. Declare the pattern object outside the lambda and reference it.");
        }

        var instance = call.Object is null ? null : ParseNode(call.Object);
        var arguments = call.Arguments.Select(ParseNode).ToArray();
        return new CallPatternNode(CilMethodSpec.From(call.Method), instance, arguments, CilTypeSpec.From(call.Type));
    }

    private PatternNode ParsePlaceholder(MethodCallExpression call)
    {
        var name = call.Method.Name;
        return name switch
        {
            nameof(P.This) => new ArgumentPatternNode(
                isThis: true,
                index: null,
                resultType: CilTypeSpec.From(call.Type).Assignable()),

            nameof(P.Arg) => new ArgumentPatternNode(false, GetRequiredInt(call.Arguments[0]),
                CilTypeSpec.From(call.Type).Assignable()),

            nameof(P.Local) => new LocalPatternNode(GetRequiredInt(call.Arguments[0]),
                CilTypeSpec.From(call.Type).Assignable()),

            nameof(P.StoreElement) => ParseStoreElementPlaceholder(call),
            nameof(P.StoreField) => ParseStoreFieldPlaceholder(call),
            _ => throw Unsupported(call, $"Unknown pattern placeholder P.{name}.")
        };
    }

    private PatternNode ParseMember(MemberExpression member)
    {
        if (member.Member is FieldInfo field)
        {
            if (member.Expression is ConstantExpression closure && closure.Value is not null)
            {
                throw Unsupported(member,
                    "Captured runtime values are not stable IL patterns. Reference a pattern object (Cil.Local/Cil.Arg/Cil.Any/...), use a lambda parameter, a literal constant, or a static field instead.");
            }

            var instance = member.Expression is null ? null : ParseNode(member.Expression);
            return new FieldPatternNode(CilFieldSpec.From(field), instance);
        }

        if (member.Member is PropertyInfo property)
        {
            //leaf/片段的 .Value：显式引用写法（T 为 object/interface 时隐式转换算子会被内建转换绕过）。
            if (property.Name == nameof(ValuePattern<int>.Value)
                && typeof(ExpressionPattern).IsAssignableFrom(property.DeclaringType)
                && member.Expression is not null)
            {
                return ParsePatternReference(member.Expression);
            }

            var getter = property.GetMethod ?? throw Unsupported(member, $"Property '{property.Name}' has no getter.");
            var instance = member.Expression is null ? null : ParseNode(member.Expression);
            return new CallPatternNode(CilMethodSpec.From(getter), instance, Array.Empty<PatternNode>(), CilTypeSpec.From(property.PropertyType));
        }

        throw Unsupported(member, $"Member kind '{member.Member.MemberType}' is not supported.");
    }

    private static PatternNode ParseConstant(ConstantExpression constant)
        => new ConstantPatternNode(constant.Value, CilTypeSpec.From(constant.Type));

    private PatternNode ParseArrayLength(UnaryExpression unary)
        => new ArrayLengthPatternNode(ParseNode(unary.Operand), CilTypeSpec.From(unary.Type));

    private PatternNode ParseUnary(UnaryExpression unary)
    {
        //pattern 对象的隐式转换（CilLocal<T>→T、ValuePattern<T>→T、ConditionPattern→bool 等）
        //是嵌入点，不是 IL 转换指令。
        if (unary.Method is { Name: "op_Implicit" } implicitOperator
            && typeof(ExpressionPattern).IsAssignableFrom(implicitOperator.DeclaringType))
        {
            return ParsePatternReference(unary.Operand);
        }

        return new UnaryPatternNode(unary.NodeType, ParseNode(unary.Operand),
            unary.Method is null ? null : CilMethodSpec.From(unary.Method), CilTypeSpec.From(unary.Type));
    }

    private PatternNode ParsePatternReference(Expression operand)
    {
        if (operand is ParameterExpression parameter)
            return GetParameterLeaf(parameter).Root;

        var value = EvaluateReference(operand);
        if (value is not ExpressionPattern pattern)
        {
            throw Unsupported(operand,
                "A pattern reference must evaluate to a pattern object declared outside the lambda.");
        }

        return pattern.CreateEmbedNode();
    }

    /// <summary>
    /// lambda 参数就是一个不对外暴露的 leaf pattern：普通类型的参数 = 按目标参数名匹配的 Cil.Arg
    /// （<c>__this</c> = Cil.This），leaf 类型的参数 = 对应的 pattern 局部 leaf。
    /// 与外部声明的 Cil.Local/Arg/This/Any 走同一条路；结果侧按 lambda 参数名取回。
    /// </summary>
    private ExpressionPattern GetParameterLeaf(ParameterExpression parameter)
    {
        if (_parameterLeaves.TryGetValue(parameter, out var existing))
            return existing;

        if (string.IsNullOrWhiteSpace(parameter.Name))
            throw Unsupported(parameter, "Pattern lambda parameters must have non-empty names.");
        var name = parameter.Name!;

        var type = parameter.Type;
        ExpressionPattern leaf;
        if (typeof(ExpressionPattern).IsAssignableFrom(type))
        {
            var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : null;
            var resultType = type.IsGenericType
                ? CilTypeSpec.From(type.GetGenericArguments()[0]).Assignable()
                : null;
            if (definition == typeof(CilAny<>) && resultType is not null)
                leaf = Cil.Any(resultType, name);
            else if (definition == typeof(CilLocal<>) && resultType is not null)
                leaf = Cil.Local(resultType, name);
            else if (definition == typeof(CilArg<>) && resultType is not null)
                leaf = Cil.ArgForLambdaParameter(resultType, name);
            else if (definition == typeof(CilThis<>) && resultType is not null)
                leaf = Cil.This(resultType, name);
            else
            {
                throw Unsupported(parameter,
                    $"Lambda parameters may be plain target-argument types or the leaf pattern types CilAny<T>/CilLocal<T>/CilArg<T>/CilThis<T>; '{type.Name}' is not supported here.");
            }
        }
        else
        {
            var resultType = CilTypeSpec.From(type).Assignable();
            leaf = name == ThisParameterName
                ? Cil.This(resultType, name)
                : Cil.Arg(resultType, name);
        }

        _parameterLeaves.Add(parameter, leaf);
        _parametersByName[name] = leaf;
        return leaf;
    }

    /// <summary>lambda 参数名为此值时表示当前实例。</summary>
    internal const string ThisParameterName = "__this";

    //对闭包引用求值取出 pattern 对象。只在解析期消费，不进入生成的 pattern 树。
    private static object? EvaluateReference(Expression expression) => expression switch
    {
        ConstantExpression constant => constant.Value,
        MemberExpression { Member: FieldInfo field } member
            => field.GetValue(member.Expression is null ? null : EvaluateReference(member.Expression)),
        MemberExpression { Member: PropertyInfo property } member
            => property.GetValue(member.Expression is null ? null : EvaluateReference(member.Expression)),
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.TypeAs } unary
            => EvaluateReference(unary.Operand),
        _ => throw Unsupported(expression,
            "A pattern reference must be a variable, field, or property access. Declare the pattern object outside the lambda and reference it.")
    };

    private PatternNode ParseArrayIndex(BinaryExpression binary)
        => new ArrayElementPatternNode(ParseNode(binary.Left), ParseNode(binary.Right), CilTypeSpec.From(binary.Type));

    private PatternNode ParseAssign(BinaryExpression binary)
    {
        if (binary.Left is BinaryExpression { NodeType: ExpressionType.ArrayIndex } arrayIndex)
        {
            return new ArrayStorePatternNode(ParseNode(arrayIndex.Left), ParseNode(arrayIndex.Right), ParseNode(binary.Right));
        }

        if (binary.Left is MemberExpression { Member: FieldInfo } fieldAccess
            && ParseNode(fieldAccess) is FieldPatternNode fieldRead)
        {
            return new FieldStorePatternNode(fieldRead.Field, fieldRead.Instance, ParseNode(binary.Right));
        }

        throw Unsupported(binary, "Only array element and field assignment are supported by the pattern matcher.");
    }

    private PatternNode ParseBinary(BinaryExpression binary)
        => new BinaryPatternNode(binary.NodeType, ParseNode(binary.Left), ParseNode(binary.Right), binary.Method is null ? null : CilMethodSpec.From(binary.Method), CilTypeSpec.From(binary.Type));

    private PatternNode ParseNewArrayBounds(NewArrayExpression expression)
    {
        if (expression.Expressions.Count != 1)
            throw Unsupported(expression, "Only single-dimensional zero-based array creation is supported.");

        var elementType = expression.Type.GetElementType()
                          ?? throw Unsupported(expression, "Array element type could not be resolved.");
        return new NewArrayPatternNode(CilTypeSpec.From(elementType), expression.Expressions.Select(ParseNode).ToArray(), CilTypeSpec.From(expression.Type));
    }

    private PatternNode ParseNew(NewExpression expression)
    {
        if (expression.Constructor is null)
            throw Unsupported(expression, "A value-type default constructor without a ConstructorInfo is not currently supported.");

        return new CallPatternNode(CilMethodSpec.From(expression.Constructor), null, expression.Arguments.Select(ParseNode).ToArray(), CilTypeSpec.From(expression.Type));
    }

    private PatternNode ParseStoreElementPlaceholder(MethodCallExpression call)
    {
        if (call.Arguments.Count != 3)
            throw Unsupported(call, "P.StoreElement expects array, index, and value arguments.");

        return new ArrayStorePatternNode(ParseNode(call.Arguments[0]), ParseNode(call.Arguments[1]), ParseNode(call.Arguments[2]));
    }

    private PatternNode ParseStoreFieldPlaceholder(MethodCallExpression call)
    {
        if (call.Arguments.Count != 2)
            throw Unsupported(call, "P.StoreField expects a field access and a value argument.");

        if (ParseNode(call.Arguments[0]) is not FieldPatternNode fieldRead)
        {
            throw Unsupported(call.Arguments[0],
                "P.StoreField requires a field access expression as its first argument, " +
                "for example P.Arg<Player>(0).Hp or a static field.");
        }

        return new FieldStorePatternNode(fieldRead.Field, fieldRead.Instance, ParseNode(call.Arguments[1]));
    }

    private PatternNode ParseParameter(ParameterExpression parameter)
        => GetParameterLeaf(parameter).Root;

    private static int GetRequiredInt(Expression expression)
    {
        if (expression is ConstantExpression { Value: int value } && value >= 0)
            return value;
        throw Unsupported(expression, "Argument and local indices must be non-negative integer literals.");
    }

    private static NotSupportedException Unsupported(Expression expression, string message)
        => new($"{message} Expression: {expression}");
}

/// <summary>lambda 解析结果：根节点 + lambda 参数名到 leaf pattern 的表。</summary>
internal readonly struct ParsedLambda
{
    public ParsedLambda(PatternNode root, IReadOnlyDictionary<string, ExpressionPattern> parameters)
    {
        Root = root;
        Parameters = parameters;
    }

    public PatternNode Root { get; }
    public IReadOnlyDictionary<string, ExpressionPattern> Parameters { get; }
}
