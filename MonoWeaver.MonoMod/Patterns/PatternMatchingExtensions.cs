using System;
using MonoMod.Cil;
using MonoWeaver.Patterns;

namespace MonoWeaver.MonoMod.Patterns;

/// <summary>Cecil expression matcher 的 MonoMod 入口。</summary>
public static class PatternMatchingExtensions
{
    /// <summary>
    /// 匹配当前由此 <see cref="ILContext"/> 持有的 method body。
    /// 修改 body 后请创建新的 match；match object 是 snapshot。
    /// </summary>
    public static CilMatchSet Match(this ILContext context, CilExpressionPattern pattern)
    {
        if (context is null)
            throw new ArgumentNullException(nameof(context));
        return CilMatcher.For(context.Method).Find(pattern);
    }

    /// <summary>在此具体 value occurrence 之后立即创建 insertion site。</summary>
    public static MatchedValueSite AfterUse(this MatchedValue value, ILContext context)
        => new(context, value, value.AfterUseInstruction, MoveType.After);

    /// <summary>
    /// 在原始 producer 之后创建 insertion site。当 value 被存入 temporary 时，
    /// 这可能影响后续所有 use；通常 <see cref="AfterUse"/> 更安全。
    /// </summary>
    public static MatchedValueSite AfterProducer(this MatchedValue value, ILContext context)
        => new(context, value, value.ProducerInstruction, MoveType.After);

    /// <summary>在 matched expression 开始 evaluation 前创建普通 insertion site。</summary>
    public static CilInsertionSite BeforeEvaluation(this MatchedValue value, ILContext context)
        => new(context, value.FirstInstruction, MoveType.Before);

    /// <summary>在 matched condition 开始 evaluation 前创建普通 insertion site。</summary>
    public static CilInsertionSite BeforeEvaluation(this MatchedCondition condition, ILContext context)
        => new(context, condition.EntryInstruction, MoveType.Before);

    /// <summary>在完整 matched expression/statement 之前创建普通 insertion site。</summary>
    public static CilInsertionSite Before(this CilMatch match, ILContext context)
        => new(context, match.FirstInstruction, MoveType.Before);

    /// <summary>在完整 matched expression/statement 之后创建普通 insertion site。</summary>
    public static CilInsertionSite After(this CilMatch match, ILContext context)
    {
        if (match is null)
            throw new ArgumentNullException(nameof(match));
        if (match.Pattern.Kind == CilPatternKind.Condition)
        {
            throw new InvalidOperationException(
                "A branch-based condition has no single after-site. Use the captured condition's Transform method or insert on an explicit continuation.");
        }

        return new CilInsertionSite(context, match.LastInstruction, MoveType.After);
    }
}
