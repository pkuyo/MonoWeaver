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
    public static ValuePattern<T> Value<T>(PatternDsl dsl,
        Expression<Func<T>> expression,
        Func<CilExpr> cilExpr,
        PatternOptions? options = null)
        => dsl switch
        {
            PatternDsl.Expression => Cil.Value(expression, options),
            PatternDsl.CilExpr => ValuePatternFromCilExpr<T>(cilExpr(), options),
            _ => throw new ArgumentOutOfRangeException(nameof(dsl)),
        };

    public static EffectPattern Effect(PatternDsl dsl,
        Expression<Action> expression,
        Func<CilExpr> cilExpr,
        PatternOptions? options = null)
        => dsl switch
        {
            PatternDsl.Expression => Cil.Effect(expression, options),
            PatternDsl.CilExpr => Cil.Effect(cilExpr(), options),
            _ => throw new ArgumentOutOfRangeException(nameof(dsl)),
        };

    public static EffectPattern Discard(PatternDsl dsl,
        Expression<Action> expression,
        Func<CilExpr> cilExpr,
        PatternOptions? options = null)
        => dsl switch
        {
            PatternDsl.Expression => Cil.Effect(expression, options),
            PatternDsl.CilExpr => Cil.Discard(cilExpr(), options),
            _ => throw new ArgumentOutOfRangeException(nameof(dsl)),
        };

    public static ConditionPattern Condition(PatternDsl dsl,
        Expression<Func<bool>> expression,
        Func<CilExpr> cilExpr,
        PatternOptions? options = null)
        => dsl switch
        {
            PatternDsl.Expression => Cil.Condition(expression, options),
            PatternDsl.CilExpr => Cil.Condition(cilExpr(), options),
            _ => throw new ArgumentOutOfRangeException(nameof(dsl)),
        };

    private static ValuePattern<T> ValuePatternFromCilExpr<T>(CilExpr expression, PatternOptions? options)
    {
        if (expression is null)
            throw new ArgumentNullException(nameof(expression));
        if (expression.ResultType.IsVoid)
            throw new ArgumentException("A value pattern must have a non-Void result type.", nameof(expression));
        return new ValuePattern<T>(expression.Node, options);
    }
}
