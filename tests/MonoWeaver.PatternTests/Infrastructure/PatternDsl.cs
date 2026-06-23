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
    public static CilExpressionPattern Value<T>(PatternDsl dsl,
        Expression<Func<T>> expression,
        Func<CilExpr> cilExpr,
        CilPatternOptions? options = null)
        => dsl switch
        {
            PatternDsl.Expression => Cil.Value(expression, options),
            PatternDsl.CilExpr => Cil.Value(cilExpr(), options),
            _ => throw new ArgumentOutOfRangeException(nameof(dsl)),
        };

    public static CilExpressionPattern Effect(PatternDsl dsl,
        Expression<Action> expression,
        Func<CilExpr> cilExpr,
        CilPatternOptions? options = null)
        => dsl switch
        {
            PatternDsl.Expression => Cil.Effect(expression, options),
            PatternDsl.CilExpr => Cil.Effect(cilExpr(), options),
            _ => throw new ArgumentOutOfRangeException(nameof(dsl)),
        };

    public static CilExpressionPattern Discard(PatternDsl dsl,
        Expression<Action> expression,
        Func<CilExpr> cilExpr,
        CilPatternOptions? options = null)
        => dsl switch
        {
            PatternDsl.Expression => Cil.Effect(expression, options),
            PatternDsl.CilExpr => Cil.Discard(cilExpr(), options),
            _ => throw new ArgumentOutOfRangeException(nameof(dsl)),
        };

    public static CilExpressionPattern Condition(PatternDsl dsl,
        Expression<Func<bool>> expression,
        Func<CilExpr> cilExpr,
        CilPatternOptions? options = null)
        => dsl switch
        {
            PatternDsl.Expression => Cil.Condition(expression, options),
            PatternDsl.CilExpr => Cil.Condition(cilExpr(), options),
            _ => throw new ArgumentOutOfRangeException(nameof(dsl)),
        };
}
