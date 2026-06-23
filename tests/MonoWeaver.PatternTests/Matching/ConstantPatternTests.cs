using Mono.Cecil.Cil;
using MonoWeaver.Patterns;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class ConstantPatternTests
{
    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesInt32Constant(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "IntConstant");
        var pattern = DualPattern.Value(dsl, () => 123, () => P.Constant(123));

        var value = method.Match(pattern).Single().Value();

        Assert.True(value.ProducerInstruction.OpCode.Code is Code.Ldc_I4 or Code.Ldc_I4_S);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesInt64Constant(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "LongConstant");
        var pattern = DualPattern.Value(dsl,
            () => 1234567890123L,
            () => P.Constant(1234567890123L));

        Assert.Equal(Code.Ldc_I8, method.Match(pattern).Single().Value().ProducerInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesSingleConstant(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "FloatConstant");
        var pattern = DualPattern.Value(dsl, () => 1.25f, () => P.Constant(1.25f));

        Assert.Equal(Code.Ldc_R4, method.Match(pattern).Single().Value().ProducerInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesDoubleConstant(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "DoubleConstant");
        var pattern = DualPattern.Value(dsl, () => 2.5d, () => P.Constant(2.5d));

        Assert.Equal(Code.Ldc_R8, method.Match(pattern).Single().Value().ProducerInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesStringConstant(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "StringConstant");
        var pattern = DualPattern.Value(dsl,
            () => "mono-weaver",
            () => P.Constant("mono-weaver"));

        Assert.Equal(Code.Ldstr, method.Match(pattern).Single().Value().ProducerInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesNullConstantWithoutLoadingTargetType(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "NullConstant");
        var pattern = DualPattern.Value<object?>(dsl,
            () => default(object),
            () => P.Null(CilType.Object));

        Assert.Equal(Code.Ldnull, method.Match(pattern).Single().Value().ProducerInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ConstantValueMustMatchExactly(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "IntConstant");
        var pattern = DualPattern.Value(dsl, () => 124, () => P.Constant(124));

        Assert.Empty(method.Match(pattern));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ConstantMetadataTypesAreNotInterchanged(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Constants");
        var pattern = DualPattern.Value(dsl, () => 1.0d, () => P.Constant(1.0d));

        var match = method.Match(pattern).Single();

        Assert.Equal(Code.Ldc_R8, match.Value().ProducerInstruction.OpCode.Code);
    }
}
