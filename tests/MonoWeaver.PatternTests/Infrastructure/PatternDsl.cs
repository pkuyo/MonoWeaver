using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using MonoWeaver.Patterns;

namespace MonoWeaver.PatternTests;

public enum PatternDsl
{
    Expression,
    CilExpr,
}

public static class PatternDslData
{
    public static IEnumerable<object[]> Both
    {
        get
        {
            yield return new object[] { PatternDsl.Expression };
            yield return new object[] { PatternDsl.CilExpr };
        }
    }
}

internal static class DualPattern
{
    public static ExpressionPattern Value<T>(PatternDsl dsl,
        Expression<Func<T>> expression,
        Func<CilExpr> cilExpr,
        PatternOptions? options = null)
        => dsl switch
        {
            PatternDsl.Expression => Cil.Value(expression, options),
            PatternDsl.CilExpr => Cil.Value(cilExpr(), options),
            _ => throw new ArgumentOutOfRangeException(nameof(dsl)),
        };

    public static ExpressionPattern Effect(PatternDsl dsl,
        Expression<Action> expression,
        Func<CilExpr> cilExpr,
        PatternOptions? options = null)
        => dsl switch
        {
            PatternDsl.Expression => Cil.Effect(expression, options),
            PatternDsl.CilExpr => Cil.Effect(cilExpr(), options),
            _ => throw new ArgumentOutOfRangeException(nameof(dsl)),
        };

    public static ExpressionPattern Discard(PatternDsl dsl,
        Expression<Action> expression,
        Func<CilExpr> cilExpr,
        PatternOptions? options = null)
        => dsl switch
        {
            PatternDsl.Expression => Cil.Effect(expression, options),
            PatternDsl.CilExpr => Cil.Discard(cilExpr(), options),
            _ => throw new ArgumentOutOfRangeException(nameof(dsl)),
        };

    public static ExpressionPattern Condition(PatternDsl dsl,
        Expression<Func<bool>> expression,
        Func<CilExpr> cilExpr,
        PatternOptions? options = null)
        => dsl switch
        {
            PatternDsl.Expression => Cil.Condition(expression, options),
            PatternDsl.CilExpr => Cil.Condition(cilExpr(), options),
            _ => throw new ArgumentOutOfRangeException(nameof(dsl)),
        };
}
