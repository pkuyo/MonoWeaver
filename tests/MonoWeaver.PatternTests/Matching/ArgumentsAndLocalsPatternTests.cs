using System.Linq;
using Mono.Cecil.Cil;
using MonoWeaver.Patterns;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class ArgumentsAndLocalsPatternTests
{
    [Fact]
    public void LambdaParametersMatchByNameRegardlessOfOrderAndAreRetrievable()
    {
        using var module = PatternTestSupport.OpenCurrentTestModule();
        var method = PatternTestSupport.CurrentMethod(module, typeof(MemberHost), nameof(MemberHost.StaticAdd));
        var pattern = Cil.Value((int right, int left) => left + right);

        var match = method.Match(pattern).Single();

        //lambda 参数就是隐式的 Cil.Arg<T>(参数名)，结果按参数名取回。
        Assert.Equal(0, match.Arg("left").ParameterIndex);
        Assert.Equal(1, match.Arg("right").ParameterIndex);
    }

    [Fact]
    public void ThisLambdaParameterBindsToInstance()
    {
        using var module = PatternTestSupport.OpenCurrentTestModule();
        var method = PatternTestSupport.CurrentMethod(module, typeof(MemberHost), nameof(MemberHost.Add));
        var pattern = Cil.Value((MemberHost __this, int value) => __this.InstanceField + value);

        var match = method.Match(pattern).Single();

        Assert.True(match.This().IsThis);
        Assert.Equal(0, match.Arg("value").ParameterIndex);
    }

    [Fact]
    public void LambdaParameterCaptureIsRetrievableByName()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Argument1");
        var pattern = Cil.Value((string text) => text);

        var match = method.Match(pattern).Single();

        Assert.Equal(1, match.Arg("text").ParameterIndex);
        Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => match.Arg("missing"));
    }

    [Fact]
    public void RepeatedLambdaParameterUnifiesToTheSameArgument()
    {
        using var module = PatternTestSupport.OpenCurrentTestModule();
        var method = PatternTestSupport.CurrentMethod(module, typeof(ArgumentsAndLocalsPatternTests),
            nameof(AddArgumentToItself));
        var pattern = Cil.Value((int value) => value + value);

        var match = method.Match(pattern).Single();

        Assert.Equal(0, match.Arg("value").ParameterIndex);
    }

    public static int AddArgumentToItself(int value) => value + value;

    [Fact]
    public void RepeatedArgLeafUnifiesToTheSameArgument()
    {
        using var module = PatternTestSupport.OpenCurrentTestModule();
        var method = PatternTestSupport.CurrentMethod(module, typeof(ArgumentsAndLocalsPatternTests),
            nameof(AddArgumentToItself));
        var value = Cil.Arg<int>();
        var pattern = Cil.Value(() => value + value);

        var match = method.Match(pattern).Single();

        Assert.Equal(0, match[value].ParameterIndex);
    }

    [Fact]
    public void RepeatedArgLeafRejectsTwoDifferentArguments()
    {
        using var module = PatternTestSupport.OpenCurrentTestModule();
        var method = PatternTestSupport.CurrentMethod(module, typeof(MemberHost), nameof(MemberHost.StaticAdd));
        var value = Cil.Arg<int>();
        var pattern = Cil.Value(() => value + value);

        //StaticAdd 是 left + right：两个不同的实参不满足合一。
        Assert.Empty(method.Match(pattern));
    }

    [Fact]
    public void LeafParameterDeclaresPatternLocalArgument()
    {
        using var module = PatternTestSupport.OpenCurrentTestModule();
        var method = PatternTestSupport.CurrentMethod(module, typeof(ArgumentsAndLocalsPatternTests),
            nameof(AddArgumentToItself));
        //leaf 类型的参数：不按名约束的 pattern 局部声明，同一参数两次出现按身份合一，同样按参数名取回。
        var pattern = Cil.Value((CilArg<int> value) => value + value);

        var match = method.Match(pattern).Single();

        Assert.Equal(0, match.Arg("value").ParameterIndex);
    }

    [Fact]
    public void LeafParameterUnificationRejectsTwoDifferentArguments()
    {
        using var module = PatternTestSupport.OpenCurrentTestModule();
        var method = PatternTestSupport.CurrentMethod(module, typeof(MemberHost), nameof(MemberHost.StaticAdd));
        var pattern = Cil.Value((CilArg<int> value) => value + value);

        Assert.Empty(method.Match(pattern));
    }

    [Fact]
    public void ParameterizedEffectLambdaMatchesArgumentByName()
    {
        using var module = PatternTestSupport.OpenCurrentTestModule();
        var method = PatternTestSupport.CurrentMethod(module, typeof(MemberHost),
            nameof(MemberHost.InvokeNamedEffect));
        var pattern = Cil.Effect((int value) => MemberHost.Consume(value));

        var match = method.Match(pattern).Single();

        Assert.Equal(0, match.Arg("value").ParameterIndex);
    }

    [Fact]
    public void ParameterizedConditionLambdaMatchesArgumentsByName()
    {
        using var module = PatternTestSupport.OpenCurrentTestModule();
        var method = PatternTestSupport.CurrentMethod(module, typeof(MemberHost),
            nameof(MemberHost.NamedCondition));
        var pattern = Cil.Condition((bool right, bool left) => left && right);

        var match = method.Match(pattern).Single();

        Assert.Equal(0, match.Arg("left").ParameterIndex);
        Assert.Equal(1, match.Arg("right").ParameterIndex);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesThisAndPreservesThisCapture(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenCurrentTestModule();
        var method = PatternTestSupport.CurrentMethod(module, typeof(InstancePatternTarget),
            nameof(InstancePatternTarget.IdentityThis));
        var thisLeaf = Cil.This<InstancePatternTarget>();
        var pattern = DualPattern.Value(dsl,
            () => thisLeaf.Value,
            () => thisLeaf.Expr);

        var self = method.Match(pattern).Single()[thisLeaf];

        Assert.True(self.IsThis);
        Assert.Null(self.Parameter);
        Assert.Equal(-1, self.ParameterIndex);
        Assert.Equal(Code.Ldarg_0, self.DefinitionInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesArgumentByExplicitIndex(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Argument1");
        var text = Cil.Arg<string>(1);
        var pattern = DualPattern.Value(dsl,
            () => text.Value,
            () => text.Expr);

        var argument = method.Match(pattern).Single()[text];

        Assert.False(argument.IsThis);
        Assert.Equal(1, argument.ParameterIndex);
        Assert.Equal("text", argument.Parameter?.Name);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesArgumentByParameterName(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Argument1");
        //按名匹配与 lambda 参数按名绑定是同一规则，只是这个对象还能从结果取回。
        var text = Cil.Arg<string>("text");
        var pattern = DualPattern.Value(dsl,
            () => text.Value,
            () => text.Expr);

        var argument = method.Match(pattern).Single()[text];

        Assert.Equal(1, argument.ParameterIndex);
        Assert.Equal("text", text.ParameterName);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ArgumentByParameterNameRejectsOtherNames(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Argument1");
        var missing = Cil.Arg<string>("noSuchParameter");
        var pattern = DualPattern.Value(dsl,
            () => missing.Value,
            () => missing.Expr);

        Assert.Empty(method.Match(pattern));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesArgumentByTypeAndCapture(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Argument1");
        var text = Cil.Arg<string>();
        var pattern = DualPattern.Value(dsl,
            () => text.Value,
            () => text.Expr);

        var argument = method.Match(pattern).Single()[text];

        Assert.Equal(1, argument.ParameterIndex);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesLocalByExplicitIndex(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "LocalRead");
        var slot = Cil.Local<int>(0);
        var pattern = DualPattern.Value(dsl,
            () => slot.Value,
            () => slot.Expr);

        var local = method.Match(pattern).Single()[slot];

        Assert.Equal(0, local.Variable.Index);
        Assert.True(local.DefinitionInstruction.OpCode.Code is Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesLocalByTypeInsideSurroundingExpression(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "TransparentLocal");
        var temporary = Cil.Local<int>();
        var pattern = DualPattern.Value(dsl,
            () => temporary * 2,
            () => temporary.Expr * 2);

        var matches = method.Match(pattern);
        var directOccurrence = Assert.Single(matches.Where(match =>
            ReferenceEquals(match.DefinitionInstruction, match.ResultInstruction)));
        var local = directOccurrence[temporary];

        Assert.Equal(0, local.Variable.Index);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void AnyCapturesAWholeTypedSubexpression(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Add");
        var anyLeft = Cil.Any<int>();
        var pattern = DualPattern.Value(dsl,
            () => anyLeft + P.Arg<int>(1),
            () => anyLeft.Expr + P.Arg(1, CilType.Int32));

        var left = method.Match(pattern).Single()[anyLeft];

        Assert.True(left.DefinitionInstruction.OpCode.Code is Code.Ldarg or Code.Ldarg_S or Code.Ldarg_0);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void UniqueTemporaryDefinitionIsTransparentAndKeepsConcreteUseSite(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "TransparentLocal");
        var sum = DualPattern.Value(dsl,
            () => P.Arg<int>(0) + 1,
            () => P.Arg(0, CilType.Int32) + 1);
        var pattern = DualPattern.Value(dsl,
            () => sum * 2,
            () => sum.Expr * 2);

        var matches = method.Match(pattern);
        var directOccurrence = Assert.Single(matches.Where(match =>
            ReferenceEquals(match.DefinitionInstruction, match.ResultInstruction)));
        var captured = directOccurrence[sum];

        Assert.Equal(Code.Add, captured.DefinitionInstruction.OpCode.Code);
        Assert.True(captured.ResultInstruction.OpCode.Code is Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0,
            "The capture must retain the concrete ldloc occurrence consumed by multiplication.");
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void TemporaryNormalizationCanBeDisabled(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "TransparentLocal");
        var options = new PatternOptions { TemporaryNormalization = TemporaryNormalization.None };
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

        var ret = Cil.Local(DualPattern.Value(dsl,
            () => Ops.XXX(),
            () => P.Call(callXxx)));
        var pattern = DualPattern.Condition(dsl,
            () => ret.Value,
            () => ret.Expr);

        var local = method.Match(pattern).Single()[ret];

        Assert.True(local.Variable.Index >= 0);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MultipleReachingDefinitionsRejectLocalConstraint(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "MultipleDefinitions");
        var callXxx = RuntimeSymbols.Method<Ops>(nameof(Ops.XXX));

        var ret = Cil.Local(DualPattern.Value(dsl,
            () => Ops.XXX(),
            () => P.Call(callXxx)));
        var pattern = DualPattern.Condition(dsl,
            () => ret.Value,
            () => ret.Expr);

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
        var boxed = Cil.Arg<object>(0);
        //T 为 object 时 C# 用内建引用转换绕过隐式算子，须用 .Value 显式引用
        var pattern = DualPattern.Value(dsl,
            () => boxed.Value,
            () => boxed.Expr);

        var value = method.Match(pattern).Single()[boxed];

        Assert.Equal(0, value.ParameterIndex);
    }
}
