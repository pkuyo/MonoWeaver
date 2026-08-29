using Mono.Cecil.Cil;
using MonoWeaver.Patterns;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class CaptureAndAmbiguityPatternTests
{
    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void AmbiguousInnerPatternReturnsEveryCandidateAndSingleRejectsIt(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Ambiguous");
        var aType = RuntimeSymbols.Type<A>(assignable: true);
        var callB = RuntimeSymbols.Method<A>(nameof(A.B));
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<A>(0).B(),
            () => P.Arg(0, aType).Call(callB));

        var matches = method.Match(pattern);

        Assert.Equal(2, matches.Count);
        Assert.Throws<CilPatternMatchException>(() => matches.Single());
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void SurroundingCallContextDisambiguatesRepeatedInnerCall(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Context");
        var aType = RuntimeSymbols.Type<A>(assignable: true);
        var callB = RuntimeSymbols.Method<A>(nameof(A.B));
        var callD = RuntimeSymbols.Method<B>(nameof(B.D));
        //内嵌片段替代原 Mark：片段对象即捕获身份。
        var hookFragment = DualPattern.Value(dsl,
            () => P.Arg<A>(0).B(),
            () => P.Arg(0, aType).Call(callB));
        var pattern = DualPattern.Value(dsl,
            () => hookFragment.Value.D(),
            () => hookFragment.Expr.Call(callD));

        var hook = method.Match(pattern).Single()[hookFragment];

        PatternTestSupport.AssertCallTo(hook.DefinitionInstruction, nameof(A.B));
        PatternTestSupport.AssertCallTo(hook.ConsumerInstruction, nameof(B.D));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void DuplicateFragmentEmbeddingThrowsAtConstruction(PatternDsl dsl)
    {
        var fragment = DualPattern.Value(dsl,
            () => P.Arg<int>(0),
            () => P.Arg(0, CilType.Int32));

        //形状类 pattern 不绑定身份，同一对象嵌两次在构造期拒绝。
        Assert.Throws<System.InvalidOperationException>(() => DualPattern.Value(dsl,
            () => fragment + fragment,
            () => fragment.Expr + fragment.Expr));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void DuplicateWildcardThrowsAtConstruction(PatternDsl dsl)
    {
        var any = Cil.Any<int>();

        Assert.Throws<System.InvalidOperationException>(() => DualPattern.Value(dsl,
            () => any + any,
            () => any.Expr + any.Expr));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void UnsatisfiableLocalDefinitionRejectsAllCandidates(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Argument0");

        var local = Cil.Local(DualPattern.Value(dsl,
            () => 1,
            () => P.Constant(1)));
        var pattern = DualPattern.Value(dsl,
            () => local.Value,
            () => local.Expr);

        Assert.Empty(method.Match(pattern));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void SingleReportsNoMatch(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Argument0");
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<int>(1) + 100,
            () => P.Arg(1, CilType.Int32) + 100);

        var exception = Assert.Throws<CilPatternMatchException>(() => method.Match(pattern).Single());

        Assert.Contains("No matching expression", exception.Message);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void CaptureIndexerRejectsUnrelatedPattern(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Argument0");
        var arg = Cil.Arg<int>(0);
        var pattern = DualPattern.Value(dsl,
            () => arg.Value,
            () => arg.Expr);
        var match = method.Match(pattern).Single();

        Assert.IsType<ArgumentCapture>(match[arg]);
        var unrelated = Cil.Arg<int>(0);
        Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => match[unrelated]);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void RootMatchIsStronglyTypedByPatternKind(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "BoolCondition");
        var pattern = DualPattern.Condition(dsl,
            () => P.Arg<bool>(0),
            () => P.Arg(0, CilType.Boolean));
        var match = method.Match(pattern).Single();

        Assert.IsType<ConditionMatch>(match);
    }
}
