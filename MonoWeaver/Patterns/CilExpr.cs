using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace MonoWeaver.Patterns;

/// <summary>
/// 不依赖 CLR 泛型参数的 CIL 表达式构造器。
/// 它只构造模式树，不执行目标代码，因此可以直接使用未加载程序集中的 Cecil 引用。
/// </summary>
public sealed class CilExpr
{
    internal CilExpr(PatternNode node)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
    }

    internal PatternNode Node { get; }

    public CilTypeSpec ResultType => Node.ResultType;

    /// <summary>
    /// 以当前表达式作为 instance 调用方法。
    /// </summary>
    public CilExpr Call(CilMethodSpec method, params CilExpr[] arguments)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (!method.HasThis || method.IsConstructor)
            throw new ArgumentException("Instance Call requires a non-constructor method with HasThis=true.", nameof(method));

        var children = RequireArguments(method, arguments);
        return new CilExpr(new CallPatternNode(method, Node, children));
    }

    /// <summary>
    /// 读取当前表达式 instance 上的字段。
    /// </summary>
    public CilExpr Field(CilFieldSpec field)
    {
        if (field is null)
            throw new ArgumentNullException(nameof(field));
        if (field.IsStatic == true)
            throw new ArgumentException("Use P.Field for a static field.", nameof(field));
        return new CilExpr(new FieldPatternNode(field, Node));
    }

    public CilExpr ConvertTo(CilTypeSpec resultType, bool @checked = false)
        => new(new UnaryPatternNode(@checked ? ExpressionType.ConvertChecked : ExpressionType.Convert,
            Node, method: null, resultType ?? throw new ArgumentNullException(nameof(resultType))));

    public CilExpr As(CilTypeSpec resultType)
        => new(new UnaryPatternNode(ExpressionType.TypeAs, Node, method: null,
            resultType ?? throw new ArgumentNullException(nameof(resultType))));

    public CilExpr ElementAt(CilExpr index, CilTypeSpec elementType)
        => new(new ArrayElementPatternNode(Node, Require(index, nameof(index)).Node,
            elementType ?? throw new ArgumentNullException(nameof(elementType))));

    public CilExpr Length()
        => new(new ArrayLengthPatternNode(Node, CilTypeSpec.Int32));

    public CilExpr EqualTo(CilExpr right) => Compare(ExpressionType.Equal, right);
    public CilExpr NotEqualTo(CilExpr right) => Compare(ExpressionType.NotEqual, right);
    public CilExpr GreaterThan(CilExpr right) => Compare(ExpressionType.GreaterThan, right);
    public CilExpr GreaterThanOrEqual(CilExpr right) => Compare(ExpressionType.GreaterThanOrEqual, right);
    public CilExpr LessThan(CilExpr right) => Compare(ExpressionType.LessThan, right);
    public CilExpr LessThanOrEqual(CilExpr right) => Compare(ExpressionType.LessThanOrEqual, right);

    public CilExpr AndAlso(CilExpr right)
    {
        RequireBoolean(this, "Left operand");
        RequireBoolean(right, "Right operand");
        return Binary(ExpressionType.AndAlso, this, right, CilTypeSpec.Boolean);
    }

    public CilExpr OrElse(CilExpr right)
    {
        RequireBoolean(this, "Left operand");
        RequireBoolean(right, "Right operand");
        return Binary(ExpressionType.OrElse, this, right, CilTypeSpec.Boolean);
    }

    /// <summary>
    /// 匹配位与运算。BitAnd/BitOr 没有对应的 C# 重载运算符入口。
    /// </summary>
    public CilExpr BitAnd(CilExpr right) => Binary(ExpressionType.And, this, right, ResultType);

    /// <summary>
    /// 匹配位或运算。BitAnd/BitOr 没有对应的 C# 重载运算符入口。
    /// </summary>
    public CilExpr BitOr(CilExpr right) => Binary(ExpressionType.Or, this, right, ResultType);
    public CilExpr BitNot()
        => new(new UnaryPatternNode(ExpressionType.Not, Node, method: null, ResultType));

    public CilExpr AddChecked(CilExpr right)
        => Binary(ExpressionType.AddChecked, this, right, ResultType);

    public CilExpr SubtractChecked(CilExpr right)
        => Binary(ExpressionType.SubtractChecked, this, right, ResultType);

    public CilExpr MultiplyChecked(CilExpr right)
        => Binary(ExpressionType.MultiplyChecked, this, right, ResultType);

    public CilExpr Xor(CilExpr right) => Binary(ExpressionType.ExclusiveOr, this, right, ResultType);
    public CilExpr ShiftLeft(CilExpr count) => Binary(ExpressionType.LeftShift, this, count, ResultType);
    public CilExpr ShiftRight(CilExpr count) => Binary(ExpressionType.RightShift, this, count, ResultType);

    public static CilExpr operator +(CilExpr left, CilExpr right)
        => Binary(ExpressionType.Add, left, right, Require(left, nameof(left)).ResultType);

    public static CilExpr operator -(CilExpr left, CilExpr right)
        => Binary(ExpressionType.Subtract, left, right, Require(left, nameof(left)).ResultType);

    public static CilExpr operator *(CilExpr left, CilExpr right)
        => Binary(ExpressionType.Multiply, left, right, Require(left, nameof(left)).ResultType);

    public static CilExpr operator /(CilExpr left, CilExpr right)
        => Binary(ExpressionType.Divide, left, right, Require(left, nameof(left)).ResultType);

    public static CilExpr operator %(CilExpr left, CilExpr right)
        => Binary(ExpressionType.Modulo, left, right, Require(left, nameof(left)).ResultType);

    public static CilExpr operator -(CilExpr operand)
        => new(new UnaryPatternNode(ExpressionType.Negate, Require(operand, nameof(operand)).Node,
            method: null, operand.ResultType));

    public static CilExpr operator +(CilExpr operand)
        => new(new UnaryPatternNode(ExpressionType.UnaryPlus, Require(operand, nameof(operand)).Node,
            method: null, operand.ResultType));

    public static CilExpr operator !(CilExpr operand)
    {
        RequireBoolean(operand, "Operand");
        return new CilExpr(new UnaryPatternNode(ExpressionType.Not, operand.Node,
            method: null, CilTypeSpec.Boolean));
    }

    public static CilExpr operator ~(CilExpr operand)
        => Require(operand, nameof(operand)).BitNot();

    public static CilExpr operator>>(CilExpr left, CilExpr right)
        => left.ShiftRight(right);

    public static CilExpr operator<<(CilExpr left, CilExpr right)
        => left.ShiftLeft(right);

    public static CilExpr operator==(CilExpr left, CilExpr right)
        => left.EqualTo(right);

    public static CilExpr operator!=(CilExpr left, CilExpr right)
        => left.NotEqualTo(right);

    public static CilExpr operator >(CilExpr left, CilExpr right)
        => left.GreaterThan(right);

    public static CilExpr operator <(CilExpr left, CilExpr right)
        => left.LessThan(right);
    public static CilExpr operator >=(CilExpr left, CilExpr right)
        => left.GreaterThanOrEqual(right);

    public static CilExpr operator <=(CilExpr left, CilExpr right)
        => left.LessThanOrEqual(right);

    public static CilExpr operator ^(CilExpr left, CilExpr right)
        => left.Xor(right);

    public CilExpr this[CilExpr index, CilTypeSpec type]
    {
       get => this.ElementAt(index, type);
    }

    /// <summary>
    /// 为了让 && 和 || 可以自然构造短路匹配，
    /// & 和 | 在此 DSL 中也表示 AndAlso/OrElse；位运算请显式使用 BitAnd/BitOr。
    /// </summary>
    public static CilExpr operator &(CilExpr left, CilExpr right)
        => Require(left, nameof(left)).AndAlso(right);

    public static CilExpr operator |(CilExpr left, CilExpr right)
        => Require(left, nameof(left)).OrElse(right);

    public static bool operator true(CilExpr expression)
    {
        RequireBoolean(expression, "Expression");
        return false;
    }

    public static bool operator false(CilExpr expression)
    {
        RequireBoolean(expression, "Expression");
        return false;
    }

    public static implicit operator CilExpr(bool value) => P.Constant(value);
    public static implicit operator CilExpr(byte value) => P.Constant(value);
    public static implicit operator CilExpr(sbyte value) => P.Constant(value);
    public static implicit operator CilExpr(short value) => P.Constant(value);
    public static implicit operator CilExpr(ushort value) => P.Constant(value);
    public static implicit operator CilExpr(int value) => P.Constant(value);
    public static implicit operator CilExpr(uint value) => P.Constant(value);
    public static implicit operator CilExpr(long value) => P.Constant(value);
    public static implicit operator CilExpr(ulong value) => P.Constant(value);
    public static implicit operator CilExpr(float value) => P.Constant(value);
    public static implicit operator CilExpr(double value) => P.Constant(value);
    public static implicit operator CilExpr(char value) => P.Constant(value);
    public static implicit operator CilExpr(string value) => P.Constant(value);

    public override string ToString() => $"CilExpr<{ResultType}>";

    private CilExpr Compare(ExpressionType operation, CilExpr right)
        => Binary(operation, this, right, CilTypeSpec.Boolean);

    private static CilExpr Binary(ExpressionType operation, CilExpr left, CilExpr right, CilTypeSpec resultType)
    {
        left = Require(left, nameof(left));
        right = Require(right, nameof(right));
        return new CilExpr(new BinaryPatternNode(operation, left.Node, right.Node,
            method: null, resultType));
    }

    internal static IReadOnlyList<PatternNode> RequireArguments(CilMethodSpec method, CilExpr[]? arguments)
    {
        if (arguments is null)
            throw new ArgumentNullException(nameof(arguments));
        if (arguments.Length != method.ParameterTypes.Count)
        {
            throw new ArgumentException(
                $"Method '{method}' expects {method.ParameterTypes.Count} explicit arguments, but {arguments.Length} were supplied.",
                nameof(arguments));
        }
        if (arguments.Any(static argument => argument is null))
            throw new ArgumentException("CIL call arguments cannot contain null. Use P.Null(type) for ldnull.", nameof(arguments));
        return arguments.Select(static argument => argument.Node).ToArray();
    }

    private static CilExpr Require(CilExpr? expression, string parameterName)
        => expression ?? throw new ArgumentNullException(parameterName);

    private static void RequireBoolean(CilExpr? expression, string name)
    {
        if (expression is null)
            throw new ArgumentNullException(nameof(expression));
        if (!expression.ResultType.IsBoolean)
            throw new InvalidOperationException($"{name} must have Boolean result type, but is '{expression.ResultType}'.");
    }
}

/// <summary>
/// 常用 metadata type 的短名入口。
/// </summary>
public static class CilType
{
    public static CilTypeSpec Named(string fullName, string? assemblyName = null, bool isValueType = false)
        => CilTypeSpec.Named(fullName, assemblyName, isValueType);

    public static CilTypeSpec Void => CilTypeSpec.Void;
    public static CilTypeSpec Boolean => CilTypeSpec.Boolean;
    public static CilTypeSpec Bool => CilTypeSpec.Boolean;
    public static CilTypeSpec Char => CilTypeSpec.Char;
    public static CilTypeSpec SByte => CilTypeSpec.SByte;
    public static CilTypeSpec Byte => CilTypeSpec.Byte;
    public static CilTypeSpec Int16 => CilTypeSpec.Int16;
    public static CilTypeSpec UInt16 => CilTypeSpec.UInt16;
    public static CilTypeSpec Int32 => CilTypeSpec.Int32;
    public static CilTypeSpec UInt32 => CilTypeSpec.UInt32;
    public static CilTypeSpec Int64 => CilTypeSpec.Int64;
    public static CilTypeSpec UInt64 => CilTypeSpec.UInt64;
    public static CilTypeSpec Single => CilTypeSpec.Single;
    public static CilTypeSpec Double => CilTypeSpec.Double;
    public static CilTypeSpec I1 => CilTypeSpec.SByte;
    public static CilTypeSpec U1 => CilTypeSpec.Byte;
    public static CilTypeSpec I2 => CilTypeSpec.Int16;
    public static CilTypeSpec U2 => CilTypeSpec.UInt16;
    public static CilTypeSpec I4 => CilTypeSpec.Int32;
    public static CilTypeSpec U4 => CilTypeSpec.UInt32;
    public static CilTypeSpec I8 => CilTypeSpec.Int64;
    public static CilTypeSpec U8 => CilTypeSpec.UInt64;
    public static CilTypeSpec R4 => CilTypeSpec.Single;
    public static CilTypeSpec R8 => CilTypeSpec.Double;
    public static CilTypeSpec String => CilTypeSpec.String;
    public static CilTypeSpec IntPtr => CilTypeSpec.IntPtr;
    public static CilTypeSpec UIntPtr => CilTypeSpec.UIntPtr;
    public static CilTypeSpec NativeInt => CilTypeSpec.IntPtr;
    public static CilTypeSpec NativeUInt => CilTypeSpec.UIntPtr;
    public static CilTypeSpec Object => CilTypeSpec.Object;
}
