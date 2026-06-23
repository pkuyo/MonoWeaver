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
        var pattern = DualPattern.Value(dsl,
            () => P.Mark("hook", P.Arg<A>(0).B()).D(),
            () => P.Arg(0, aType).Call(callB).Mark("hook").Call(callD));

        var hook = method.Match(pattern).Single().Value("hook");

        PatternTestSupport.AssertCallTo(hook.ProducerInstruction, nameof(A.B));
        PatternTestSupport.AssertCallTo(hook.ConsumerInstruction, nameof(B.D));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void DuplicateCaptureNameRejectsCandidateInsteadOfOverwriting(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Argument0");
        var pattern = DualPattern.Value(dsl,
            () => P.Mark("same", P.Mark("same", P.Arg<int>(0))),
            () => P.Arg(0, CilType.Int32).Mark("same").Mark("same"));

        Assert.Empty(method.Match(pattern));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MissingLocalConstraintCaptureRejectsCandidate(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Argument0");
        var pattern = DualPattern.Value(dsl,
                () => P.Arg<int>(0),
                () => P.Arg(0, CilType.Int32))
            .LocalDefinedBy("missing", DualPattern.Value(dsl,
                () => 1,
                () => P.Constant(1)));

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
    public void CaptureAccessorsRejectWrongCaptureKind(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Argument0");
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<int>(0, "argument"),
            () => P.Arg(0, CilType.Int32, "argument"));
        var match = method.Match(pattern).Single();

        Assert.IsType<MatchedArgument>(match.Argument("argument"));
        Assert.Throws<System.InvalidOperationException>(() => match.Local("argument"));
        Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => match.Value("missing"));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void RootAccessorRejectsWrongPatternKind(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "BoolCondition");
        var pattern = DualPattern.Condition(dsl,
            () => P.Arg<bool>(0),
            () => P.Arg(0, CilType.Boolean));
        var match = method.Match(pattern).Single();

        Assert.Throws<System.InvalidOperationException>(() => match.Value());
        Assert.NotNull(match.Condition());
    }
}
