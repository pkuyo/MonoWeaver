using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace MonoWeaver.Patterns;

internal static class PatternExpressionParser
{
    private static readonly Type PlaceholderType = typeof(P);

    public static PatternNode Parse(Expression expression)
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

    private static PatternNode ParseCall(MethodCallExpression call)
    {
        if (call.Method.DeclaringType == PlaceholderType)
            return ParsePlaceholder(call);

        var instance = call.Object is null ? null : Parse(call.Object);
        var arguments = call.Arguments.Select(Parse).ToArray();
        return new CallPatternNode(CilMethodSpec.From(call.Method), instance, arguments, CilTypeSpec.From(call.Type));
    }

    private static PatternNode ParsePlaceholder(MethodCallExpression call)
    {
        var name = call.Method.Name;
        return name switch
        {
            nameof(P.This) => new ArgumentPatternNode(
                isThis: true,
                index: null,
                captureName: call.Arguments.Count == 0 ? null : GetRequiredString(call.Arguments[0]),
                resultType: CilTypeSpec.From(call.Type).Assignable()),

            nameof(P.Arg) => ParseArgumentPlaceholder(call),
            nameof(P.Local) => ParseLocalPlaceholder(call),
            nameof(P.Any) => new AnyPatternNode(GetRequiredString(call.Arguments[0]), CilTypeSpec.From(call.Type).Assignable()),
            nameof(P.Mark) => new MarkPatternNode(GetRequiredString(call.Arguments[0]), Parse(call.Arguments[1])),
            nameof(P.StoreElement) => ParseStoreElementPlaceholder(call),
            nameof(P.StoreField) => ParseStoreFieldPlaceholder(call),
            _ => throw Unsupported(call, $"Unknown pattern placeholder P.{name}.")
        };
    }

    private static PatternNode ParseArgumentPlaceholder(MethodCallExpression call)
    {
        int? index = null;
        string? capture = null;

        if (call.Arguments.Count == 1)
        {
            if (call.Arguments[0].Type == typeof(string))
                capture = GetRequiredString(call.Arguments[0]);
            else
                index = GetRequiredInt(call.Arguments[0]);
        }
        else if (call.Arguments.Count == 2)
        {
            index = GetRequiredInt(call.Arguments[0]);
            capture = GetRequiredString(call.Arguments[1]);
        }

        return new ArgumentPatternNode(false, index, capture, CilTypeSpec.From(call.Type).Assignable());
    }

    private static PatternNode ParseLocalPlaceholder(MethodCallExpression call)
    {
        int? index = null;
        string? capture = null;

        if (call.Arguments.Count == 1)
        {
            if (call.Arguments[0].Type == typeof(string))
                capture = GetRequiredString(call.Arguments[0]);
            else
                index = GetRequiredInt(call.Arguments[0]);
        }
        else if (call.Arguments.Count == 2)
        {
            index = GetRequiredInt(call.Arguments[0]);
            capture = GetRequiredString(call.Arguments[1]);
        }

        return new LocalPatternNode(index, capture, CilTypeSpec.From(call.Type).Assignable());
    }

    private static PatternNode ParseMember(MemberExpression member)
    {
        if (member.Member is FieldInfo field)
        {
            if (member.Expression is ConstantExpression closure && closure.Value is not null)
            {
                throw Unsupported(member,
                    "Captured runtime values are not stable IL patterns. Use P.Arg/P.Local/P.Any, a literal constant, or a static field instead.");
            }

            var instance = member.Expression is null ? null : Parse(member.Expression);
            return new FieldPatternNode(CilFieldSpec.From(field), instance);
        }

        if (member.Member is PropertyInfo property)
        {
            var getter = property.GetMethod ?? throw Unsupported(member, $"Property '{property.Name}' has no getter.");
            var instance = member.Expression is null ? null : Parse(member.Expression);
            return new CallPatternNode(CilMethodSpec.From(getter), instance, Array.Empty<PatternNode>(), CilTypeSpec.From(property.PropertyType));
        }

        throw Unsupported(member, $"Member kind '{member.Member.MemberType}' is not supported.");
    }

    private static PatternNode ParseConstant(ConstantExpression constant)
        => new ConstantPatternNode(constant.Value, CilTypeSpec.From(constant.Type));

    private static PatternNode ParseArrayLength(UnaryExpression unary)
        => new ArrayLengthPatternNode(Parse(unary.Operand), CilTypeSpec.From(unary.Type));

    private static PatternNode ParseUnary(UnaryExpression unary)
        => new UnaryPatternNode(unary.NodeType, Parse(unary.Operand), unary.Method is null ? null : CilMethodSpec.From(unary.Method), CilTypeSpec.From(unary.Type));

    private static PatternNode ParseArrayIndex(BinaryExpression binary)
        => new ArrayElementPatternNode(Parse(binary.Left), Parse(binary.Right), CilTypeSpec.From(binary.Type));

    private static PatternNode ParseAssign(BinaryExpression binary)
    {
        if (binary.Left is BinaryExpression { NodeType: ExpressionType.ArrayIndex } arrayIndex)
        {
            return new ArrayStorePatternNode(Parse(arrayIndex.Left), Parse(arrayIndex.Right), Parse(binary.Right));
        }

        if (binary.Left is MemberExpression { Member: FieldInfo } fieldAccess
            && Parse(fieldAccess) is FieldPatternNode fieldRead)
        {
            return new FieldStorePatternNode(fieldRead.Field, fieldRead.Instance, Parse(binary.Right));
        }

        throw Unsupported(binary, "Only array element and field assignment are supported by the pattern matcher.");
    }

    private static PatternNode ParseBinary(BinaryExpression binary)
        => new BinaryPatternNode(binary.NodeType, Parse(binary.Left), Parse(binary.Right), binary.Method is null ? null : CilMethodSpec.From(binary.Method), CilTypeSpec.From(binary.Type));

    private static PatternNode ParseNewArrayBounds(NewArrayExpression expression)
    {
        if (expression.Expressions.Count != 1)
            throw Unsupported(expression, "Only single-dimensional zero-based array creation is supported.");

        var elementType = expression.Type.GetElementType()
                          ?? throw Unsupported(expression, "Array element type could not be resolved.");
        return new NewArrayPatternNode(CilTypeSpec.From(elementType), expression.Expressions.Select(Parse).ToArray(), CilTypeSpec.From(expression.Type));
    }

    private static PatternNode ParseNew(NewExpression expression)
    {
        if (expression.Constructor is null)
            throw Unsupported(expression, "A value-type default constructor without a ConstructorInfo is not currently supported.");

        return new CallPatternNode(CilMethodSpec.From(expression.Constructor), null, expression.Arguments.Select(Parse).ToArray(), CilTypeSpec.From(expression.Type));
    }

    private static PatternNode ParseStoreElementPlaceholder(MethodCallExpression call)
    {
        if (call.Arguments.Count != 3)
            throw Unsupported(call, "P.StoreElement expects array, index, and value arguments.");

        return new ArrayStorePatternNode(Parse(call.Arguments[0]), Parse(call.Arguments[1]), Parse(call.Arguments[2]));
    }

    private static PatternNode ParseStoreFieldPlaceholder(MethodCallExpression call)
    {
        if (call.Arguments.Count != 2)
            throw Unsupported(call, "P.StoreField expects a field access and a value argument.");

        if (Parse(call.Arguments[0]) is not FieldPatternNode fieldRead)
        {
            throw Unsupported(call.Arguments[0],
                "P.StoreField requires a field access expression as its first argument, " +
                "for example P.Arg<Player>(0).Hp or a static field.");
        }

        return new FieldStorePatternNode(fieldRead.Field, fieldRead.Instance, Parse(call.Arguments[1]));
    }

    private static PatternNode ParseParameter(ParameterExpression parameter)
    {
        // 参数名用于绑定目标方法参数；__this 由 matcher 解释为当前实例。
        if (string.IsNullOrWhiteSpace(parameter.Name))
            throw Unsupported(parameter, "Pattern lambda parameters must have non-empty names.");

        return new LambdaParameterPatternNode(
            parameter.Name,
            CilTypeSpec.From(parameter.Type).Assignable());
    }

    private static string GetRequiredString(Expression expression)
    {
        if (expression is ConstantExpression { Value: string value } && !string.IsNullOrWhiteSpace(value))
            return value;
        throw Unsupported(expression, "Capture names must be non-empty string literals.");
    }

    private static int GetRequiredInt(Expression expression)
    {
        if (expression is ConstantExpression { Value: int value } && value >= 0)
            return value;
        throw Unsupported(expression, "Argument and local indices must be non-negative integer literals.");
    }

    private static NotSupportedException Unsupported(Expression expression, string message)
        => new($"{message} Expression: {expression}");
}
