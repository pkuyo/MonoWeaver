using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace MonoWeaver.Patterns;

internal static class PatternExpressionParser
{
    private static readonly Type PlaceholderType = typeof(P);

    public static CilPatternNode Parse(Expression expression)
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
            ExpressionType.Default => new ConstantPatternNode(null, expression.Type),
            _ => throw Unsupported(expression, $"Expression node '{expression.NodeType}' is not supported by the single-expression matcher.")
        };
    }

    private static CilPatternNode ParseCall(MethodCallExpression call)
    {
        if (call.Method.DeclaringType == PlaceholderType)
            return ParsePlaceholder(call);

        var instance = call.Object is null ? null : Parse(call.Object);
        var arguments = call.Arguments.Select(Parse).ToArray();
        return new CallPatternNode(call.Method, instance, arguments, call.Type);
    }

    private static CilPatternNode ParsePlaceholder(MethodCallExpression call)
    {
        var name = call.Method.Name;
        return name switch
        {
            nameof(P.This) => new ArgumentPatternNode(
                isThis: true,
                index: null,
                captureName: call.Arguments.Count == 0 ? null : GetRequiredString(call.Arguments[0]),
                resultType: call.Type),

            nameof(P.Arg) => ParseArgumentPlaceholder(call),
            nameof(P.Local) => ParseLocalPlaceholder(call),
            nameof(P.Any) => new AnyPatternNode(GetRequiredString(call.Arguments[0]), call.Type),
            nameof(P.Mark) => new MarkPatternNode(GetRequiredString(call.Arguments[0]), Parse(call.Arguments[1])),
            nameof(P.StoreElement) => ParseStoreElementPlaceholder(call),
            _ => throw Unsupported(call, $"Unknown pattern placeholder P.{name}.")
        };
    }

    private static CilPatternNode ParseArgumentPlaceholder(MethodCallExpression call)
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

        return new ArgumentPatternNode(false, index, capture, call.Type);
    }

    private static CilPatternNode ParseLocalPlaceholder(MethodCallExpression call)
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

        return new LocalPatternNode(index, capture, call.Type);
    }

    private static CilPatternNode ParseMember(MemberExpression member)
    {
        if (member.Member is FieldInfo field)
        {
            if (member.Expression is ConstantExpression closure && closure.Value is not null)
            {
                throw Unsupported(member,
                    "Captured runtime values are not stable IL patterns. Use P.Arg/P.Local/P.Any, a literal constant, or a static field instead.");
            }

            var instance = member.Expression is null ? null : Parse(member.Expression);
            return new FieldPatternNode(field, instance);
        }

        if (member.Member is PropertyInfo property)
        {
            var getter = property.GetMethod ?? throw Unsupported(member, $"Property '{property.Name}' has no getter.");
            var instance = member.Expression is null ? null : Parse(member.Expression);
            return new CallPatternNode(getter, instance, Array.Empty<CilPatternNode>(), property.PropertyType);
        }

        throw Unsupported(member, $"Member kind '{member.Member.MemberType}' is not supported.");
    }

    private static CilPatternNode ParseConstant(ConstantExpression constant)
        => new ConstantPatternNode(constant.Value, constant.Type);

    private static CilPatternNode ParseArrayLength(UnaryExpression unary)
        => new ArrayLengthPatternNode(Parse(unary.Operand), unary.Type);

    private static CilPatternNode ParseUnary(UnaryExpression unary)
        => new UnaryPatternNode(unary.NodeType, Parse(unary.Operand), unary.Method, unary.Type);

    private static CilPatternNode ParseArrayIndex(BinaryExpression binary)
        => new ArrayElementPatternNode(Parse(binary.Left), Parse(binary.Right), binary.Type);

    private static CilPatternNode ParseAssign(BinaryExpression binary)
    {
        if (binary.Left is BinaryExpression { NodeType: ExpressionType.ArrayIndex } arrayIndex)
        {
            return new ArrayStorePatternNode(Parse(arrayIndex.Left), Parse(arrayIndex.Right), Parse(binary.Right));
        }

        throw Unsupported(binary, "Only array element assignment is supported by the pattern matcher.");
    }

    private static CilPatternNode ParseBinary(BinaryExpression binary)
        => new BinaryPatternNode(binary.NodeType, Parse(binary.Left), Parse(binary.Right), binary.Method, binary.Type);

    private static CilPatternNode ParseNewArrayBounds(NewArrayExpression expression)
    {
        if (expression.Expressions.Count != 1)
            throw Unsupported(expression, "Only single-dimensional zero-based array creation is supported.");

        var elementType = expression.Type.GetElementType()
                          ?? throw Unsupported(expression, "Array element type could not be resolved.");
        return new NewArrayPatternNode(elementType, expression.Expressions.Select(Parse).ToArray(), expression.Type);
    }

    private static CilPatternNode ParseNew(NewExpression expression)
    {
        if (expression.Constructor is null)
            throw Unsupported(expression, "A value-type default constructor without a ConstructorInfo is not currently supported.");

        return new CallPatternNode(expression.Constructor, null, expression.Arguments.Select(Parse).ToArray(), expression.Type);
    }

    private static CilPatternNode ParseStoreElementPlaceholder(MethodCallExpression call)
    {
        if (call.Arguments.Count != 3)
            throw Unsupported(call, "P.StoreElement expects array, index, and value arguments.");

        return new ArrayStorePatternNode(Parse(call.Arguments[0]), Parse(call.Arguments[1]), Parse(call.Arguments[2]));
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
