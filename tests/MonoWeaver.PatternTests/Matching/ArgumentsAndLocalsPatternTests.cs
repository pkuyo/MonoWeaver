using System.Linq;
using Mono.Cecil.Cil;
using MonoWeaver.Patterns;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class ArgumentsAndLocalsPatternTests
{
    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesThisAndPreservesThisCapture(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenCurrentTestModule();
        var method = PatternTestSupport.CurrentMethod(module, typeof(InstancePatternTarget),
            nameof(InstancePatternTarget.IdentityThis));
        var type = RuntimeSymbols.Type<InstancePatternTarget>(assignable: true);
        var pattern = DualPattern.Value(dsl,
            () => P.This<InstancePatternTarget>("self"),
            () => P.This(type, "self"));

        var self = method.Match(pattern).Single().Argument("self");

        Assert.True(self.IsThis);
        Assert.Null(self.Parameter);
        Assert.Equal(-1, self.ParameterIndex);
        Assert.Equal(Code.Ldarg_0, self.ProducerInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesArgumentByExplicitIndex(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Argument1");
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<string>(1, "text"),
            () => P.Arg(1, CilType.String.Assignable(), "text"));

        var argument = method.Match(pattern).Single().Argument("text");

        Assert.False(argument.IsThis);
        Assert.Equal(1, argument.ParameterIndex);
        Assert.Equal("text", argument.Parameter?.Name);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesArgumentByTypeAndCapture(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Argument1");
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<string>("text"),
            () => P.Arg(CilType.String.Assignable(), "text"));

        var argument = method.Match(pattern).Single().Argument("text");

        Assert.Equal(1, argument.ParameterIndex);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesLocalByExplicitIndex(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "LocalRead");
        var pattern = DualPattern.Value(dsl,
            () => P.Local<int>(0, "local"),
            () => P.Local(0, CilType.Int32, "local"));

        var local = method.Match(pattern).Single().Local("local");

        Assert.Equal(0, local.Variable.Index);
        Assert.True(local.ProducerInstruction.OpCode.Code is Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesLocalByTypeInsideSurroundingExpression(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "TransparentLocal");
        var pattern = DualPattern.Value(dsl,
            () => P.Local<int>("temporary") * 2,
            () => P.Local(CilType.Int32, "temporary") * 2);

        var matches = method.Match(pattern);
        var directOccurrence = Assert.Single(matches.Where(match =>
            ReferenceEquals(match.Value().ProducerInstruction, match.Value().AfterUseInstruction)));
        var local = directOccurrence.Local("temporary");

        Assert.Equal(0, local.Variable.Index);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void AnyCapturesAWholeTypedSubexpression(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Add");
        var pattern = DualPattern.Value(dsl,
            () => P.Any<int>("left") + P.Arg<int>(1),
            () => P.Any(CilType.Int32, "left") + P.Arg(1, CilType.Int32));

        var left = method.Match(pattern).Single().Value("left");

        Assert.True(left.ProducerInstruction.OpCode.Code is Code.Ldarg or Code.Ldarg_S or Code.Ldarg_0);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void UniqueTemporaryDefinitionIsTransparentAndKeepsConcreteUseSite(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "TransparentLocal");
        var pattern = DualPattern.Value(dsl,
            () => P.Mark("sum", P.Arg<int>(0) + 1) * 2,
            () => (P.Arg(0, CilType.Int32) + 1).Mark("sum") * 2);

        var matches = method.Match(pattern);
        var directOccurrence = Assert.Single(matches.Where(match =>
            ReferenceEquals(match.Value().ProducerInstruction, match.Value().AfterUseInstruction)));
        var sum = directOccurrence.Value("sum");

        Assert.Equal(Code.Add, sum.ProducerInstruction.OpCode.Code);
        Assert.True(sum.AfterUseInstruction.OpCode.Code is Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0,
            "The capture must retain the concrete ldloc occurrence consumed by multiplication.");
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void TemporaryNormalizationCanBeDisabled(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "TransparentLocal");
        var options = new CilPatternOptions { TemporaryNormalization = TemporaryNormalization.None };
        var pattern = DualPattern.Value(dsl,
            () => (P.Arg<int>(0) + 1) * 2,
            () => (P.Arg(0, CilType.Int32) + 1) * 2,
            options);

        Assert.Empty(method.Match(pattern));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void LocalDefinitionConstraintDisambiguatesBooleanLocal(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "LocalCondition");
        var callXxx = RuntimeSymbols.Method<Ops>(nameof(Ops.XXX));
        var pattern = DualPattern.Condition(dsl,
                () => P.Local<bool>("ret"),
                () => P.Local(CilType.Boolean, "ret"))
            .LocalDefinedBy("ret", DualPattern.Value(dsl,
                () => Ops.XXX(),
                () => P.Call(callXxx)));

        var local = method.Match(pattern).Single().Local("ret");

        Assert.True(local.Variable.Index >= 0);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MultipleReachingDefinitionsRejectLocalConstraint(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "MultipleDefinitions");
        var callXxx = RuntimeSymbols.Method<Ops>(nameof(Ops.XXX));
        var pattern = DualPattern.Condition(dsl,
                () => P.Local<bool>("ret"),
                () => P.Local(CilType.Boolean, "ret"))
            .LocalDefinedBy("ret", DualPattern.Value(dsl,
                () => Ops.XXX(),
                () => P.Call(callXxx)));

        Assert.Empty(method.Match(pattern));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void AddressTakenLocalIsNeverExpandedAsTransparent(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "AddressTakenLocal");
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<int>(0) + 1,
            () => P.Arg(0, CilType.Int32) + 1);

        Assert.Empty(method.Match(pattern));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ReferenceArgumentUsesNormalAssignability(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "AssignableArgument");
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<object>(0, "value"),
            () => P.Arg(0, CilType.Object.Assignable(), "value"));

        var value = method.Match(pattern).Single().Argument("value");

        Assert.Equal(0, value.ParameterIndex);
    }
}
