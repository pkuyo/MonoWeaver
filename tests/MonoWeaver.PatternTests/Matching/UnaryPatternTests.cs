using Mono.Cecil.Cil;
using MonoWeaver.Patterns;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class UnaryPatternTests
{
    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesArithmeticNegation(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Negate");
        var pattern = DualPattern.Value(dsl,
            () => -P.Arg<int>(0),
            () => -P.Arg(0, CilType.Int32));

        Assert.Equal(Code.Neg, method.Match(pattern).Single().DefinitionInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesIntegralBitwiseNot(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "BitNot");
        var pattern = DualPattern.Value(dsl,
            () => ~P.Arg<int>(0),
            () => ~P.Arg(0, CilType.Int32));

        Assert.Equal(Code.Not, method.Match(pattern).Single().DefinitionInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesLogicalNotValueLowering(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "NotValue");
        var pattern = DualPattern.Value(dsl,
            () => !P.Arg<bool>(0),
            () => !P.Arg(0, CilType.Boolean));

        Assert.Equal(Code.Ceq, method.Match(pattern).Single().DefinitionInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesNumericConversion(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ConvertToInt64");
        var pattern = DualPattern.Value(dsl,
            () => (long)P.Arg<int>(0),
            () => P.Arg(0, CilType.Int32).ConvertTo(CilType.Int64));

        Assert.Equal(Code.Conv_I8, method.Match(pattern).Single().DefinitionInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesCheckedNumericConversion(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ConvertCheckedToByte");
        var pattern = DualPattern.Value(dsl,
            () => checked((byte)P.Arg<int>(0)),
            () => P.Arg(0, CilType.Int32).ConvertTo(CilType.Byte, @checked: true));

        var code = method.Match(pattern).Single().DefinitionInstruction.OpCode.Code;
        Assert.True(code is Code.Conv_Ovf_U1 or Code.Conv_Ovf_U1_Un);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void CheckedConversionPatternDoesNotMatchUncheckedOpcode(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ConvertUncheckedToByte");
        var pattern = DualPattern.Value(dsl,
            () => checked((byte)P.Arg<int>(0)),
            () => P.Arg(0, CilType.Int32).ConvertTo(CilType.Byte, @checked: true));

        Assert.Empty(method.Match(pattern));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void UncheckedConversionPatternDoesNotMatchCheckedOpcode(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ConvertCheckedToByte");
        var pattern = DualPattern.Value(dsl,
            () => unchecked((byte)P.Arg<int>(0)),
            () => P.Arg(0, CilType.Int32).ConvertTo(CilType.Byte));

        Assert.Empty(method.Match(pattern));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesCastClass(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "CastToString");
        var pattern = DualPattern.Value(dsl,
            () => (string)P.Arg<object>(0),
            () => P.Arg(0, CilType.Object.Assignable()).ConvertTo(CilType.String));

        Assert.Equal(Code.Castclass, method.Match(pattern).Single().DefinitionInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesTypeAs(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "AsString");
        var pattern = DualPattern.Value<string?>(dsl,
            () => P.Arg<object>(0) as string,
            () => P.Arg(0, CilType.Object.Assignable()).As(CilType.String));

        Assert.Equal(Code.Isinst, method.Match(pattern).Single().DefinitionInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesBoxingConversion(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "BoxInt");
        var pattern = DualPattern.Value<object>(dsl,
            () => (object)P.Arg<int>(0),
            () => P.Arg(0, CilType.Int32).ConvertTo(CilType.Object));

        Assert.Equal(Code.Box, method.Match(pattern).Single().DefinitionInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesUnboxAnyConversion(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "UnboxInt");
        var pattern = DualPattern.Value(dsl,
            () => (int)P.Arg<object>(0),
            () => P.Arg(0, CilType.Object.Assignable()).ConvertTo(CilType.Int32));

        Assert.Equal(Code.Unbox_Any, method.Match(pattern).Single().DefinitionInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ConversionResultTypeMustMatch(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ConvertToInt64");
        var pattern = DualPattern.Value(dsl,
            () => (double)P.Arg<int>(0),
            () => P.Arg(0, CilType.Int32).ConvertTo(CilType.Double));

        Assert.Empty(method.Match(pattern));
    }
}
