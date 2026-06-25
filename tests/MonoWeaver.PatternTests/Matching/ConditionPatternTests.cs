using MonoWeaver.Patterns;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class ConditionPatternTests
{
    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesBooleanLeafCondition(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "BoolCondition");
        var pattern = DualPattern.Condition(dsl,
            () => P.Arg<bool>(0),
            () => P.Arg(0, CilType.Boolean));

        var condition = method.Match(pattern).Single();

        Assert.Single(condition.Fragment.TrueExits);
        Assert.Single(condition.Fragment.FalseExits);
        Assert.NotSame(condition.Fragment.TrueContinuation, condition.Fragment.FalseContinuation);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesNegatedCondition(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "NotCondition");
        var pattern = DualPattern.Condition(dsl,
            () => !P.Arg<bool>(0),
            () => !P.Arg(0, CilType.Boolean));

        var condition = method.Match(pattern).Single();

        Assert.Single(condition.Fragment.TrueExits);
        Assert.Single(condition.Fragment.FalseExits);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesAndAlsoShortCircuit(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ShortCircuitAndCondition");
        var callA = RuntimeSymbols.Method<Ops>(nameof(Ops.CallA));
        var callC = RuntimeSymbols.Method<Ops>(nameof(Ops.CallC));
        var pattern = DualPattern.Condition(dsl,
            () => Ops.CallA() && Ops.CallC(),
            () => P.Call(callA).AndAlso(P.Call(callC)));

        var condition = method.Match(pattern).Single();

        Assert.Single(condition.Fragment.TrueExits);
        Assert.Equal(2, condition.Fragment.FalseExits.Count);
        Assert.True(condition.CanRewrite, condition.RewriteFailureReason);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesOrElseShortCircuit(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ShortCircuitOrCondition");
        var callA = RuntimeSymbols.Method<Ops>(nameof(Ops.CallA));
        var callC = RuntimeSymbols.Method<Ops>(nameof(Ops.CallC));
        var pattern = DualPattern.Condition(dsl,
            () => Ops.CallA() || Ops.CallC(),
            () => P.Call(callA).OrElse(P.Call(callC)));

        var condition = method.Match(pattern).Single();

        Assert.Equal(2, condition.Fragment.TrueExits.Count);
        Assert.Single(condition.Fragment.FalseExits);
        Assert.True(condition.CanRewrite, condition.RewriteFailureReason);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesMaterializedAndLoweringForPureBooleanOperands(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "AndCondition");
        var pattern = DualPattern.Condition(dsl,
            () => P.Arg<bool>(0) && P.Arg<bool>(1),
            () => P.Arg(0, CilType.Boolean).AndAlso(P.Arg(1, CilType.Boolean)));

        var condition = method.Match(pattern).Single();

        Assert.Single(condition.Fragment.TrueExits);
        Assert.Single(condition.Fragment.FalseExits);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesMaterializedOrLoweringForPureBooleanOperands(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "OrCondition");
        var pattern = DualPattern.Condition(dsl,
            () => P.Arg<bool>(0) || P.Arg<bool>(1),
            () => P.Arg(0, CilType.Boolean).OrElse(P.Arg(1, CilType.Boolean)));

        var condition = method.Match(pattern).Single();

        Assert.Single(condition.Fragment.TrueExits);
        Assert.Single(condition.Fragment.FalseExits);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesNestedShortCircuitAndCapturesSubcondition(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Condition");
        var bType = RuntimeSymbols.Type<B>(assignable: true);
        var callA = RuntimeSymbols.Method<Ops>(nameof(Ops.CallA));
        var callB = RuntimeSymbols.Method<B>(nameof(B.CallB));
        var callC = RuntimeSymbols.Method<Ops>(nameof(Ops.CallC));
        var callD = RuntimeSymbols.Method<Ops>(nameof(Ops.CallD));
        var pattern = DualPattern.Condition(dsl,
            () => P.Mark("ab", Ops.CallA() && P.Arg<B>(0).CallB())
                  && (Ops.CallC() || Ops.CallD()),
            () => P.Mark("ab",
                    P.Call(callA).AndAlso(P.Arg(0, bType).Call(callB)))
                .AndAlso(P.Call(callC).OrElse(P.Call(callD))));

        var match = method.Match(pattern).Single();
        var ab = match.Captures.Condition("ab");

        Assert.Single(ab.Fragment.TrueExits);
        Assert.Equal(2, ab.Fragment.FalseExits.Count);
        Assert.True(ab.CanRewrite, ab.RewriteFailureReason);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesEqualityBranchEvenWhenCompilerUsesInverseOpcode(PatternDsl dsl)
        => AssertComparisonMatch("EqualCondition", dsl,
            () => P.Arg<int>(0) == P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) == P.Arg(1, CilType.Int32));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesInequalityBranchEvenWhenCompilerUsesInverseOpcode(PatternDsl dsl)
        => AssertComparisonMatch("NotEqualCondition", dsl,
            () => P.Arg<int>(0) != P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) != P.Arg(1, CilType.Int32));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesGreaterThanBranch(PatternDsl dsl)
        => AssertComparisonMatch("GreaterCondition", dsl,
            () => P.Arg<int>(0) > P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) > P.Arg(1, CilType.Int32));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesGreaterThanOrEqualBranch(PatternDsl dsl)
        => AssertComparisonMatch("GreaterOrEqualCondition", dsl,
            () => P.Arg<int>(0) >= P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) >= P.Arg(1, CilType.Int32));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesLessThanBranch(PatternDsl dsl)
        => AssertComparisonMatch("LessCondition", dsl,
            () => P.Arg<int>(0) < P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) < P.Arg(1, CilType.Int32));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesLessThanOrEqualBranch(PatternDsl dsl)
        => AssertComparisonMatch("LessOrEqualCondition", dsl,
            () => P.Arg<int>(0) <= P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) <= P.Arg(1, CilType.Int32));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void WrongShortCircuitShapeDoesNotMatch(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ShortCircuitAndCondition");
        var callA = RuntimeSymbols.Method<Ops>(nameof(Ops.CallA));
        var callC = RuntimeSymbols.Method<Ops>(nameof(Ops.CallC));
        var pattern = DualPattern.Condition(dsl,
            () => Ops.CallA() || Ops.CallC(),
            () => P.Call(callA).OrElse(P.Call(callC)));

        Assert.Empty(method.Match(pattern));
    }

    private static void AssertComparisonMatch(string methodName, PatternDsl dsl,
        System.Linq.Expressions.Expression<System.Func<bool>> expression,
        System.Func<CilExpr> cilExpr)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, methodName);
        var condition = method.Match(DualPattern.Condition(dsl, expression, cilExpr))
            .Single();

        Assert.Single(condition.Fragment.TrueExits);
        Assert.Single(condition.Fragment.FalseExits);
    }
}
