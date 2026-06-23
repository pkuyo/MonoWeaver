using System;
using System.Linq.Expressions;
using Mono.Cecil.Cil;
using MonoWeaver.Patterns;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class BinaryPatternTests
{
    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesAdd(PatternDsl dsl)
        => Assert.Equal(Code.Add, MatchProducer("Add", dsl,
            () => P.Arg<int>(0) + P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) + P.Arg(1, CilType.Int32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesCheckedAdd(PatternDsl dsl)
        => Assert.Equal(Code.Add_Ovf, MatchProducer("AddChecked", dsl,
            () => checked(P.Arg<int>(0) + P.Arg<int>(1)),
            () => P.Arg(0, CilType.Int32).AddChecked(P.Arg(1, CilType.Int32))));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesSubtract(PatternDsl dsl)
        => Assert.Equal(Code.Sub, MatchProducer("Subtract", dsl,
            () => P.Arg<int>(0) - P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) - P.Arg(1, CilType.Int32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesCheckedSubtract(PatternDsl dsl)
        => Assert.Equal(Code.Sub_Ovf, MatchProducer("SubtractChecked", dsl,
            () => checked(P.Arg<int>(0) - P.Arg<int>(1)),
            () => P.Arg(0, CilType.Int32).SubtractChecked(P.Arg(1, CilType.Int32))));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesMultiply(PatternDsl dsl)
        => Assert.Equal(Code.Mul, MatchProducer("Multiply", dsl,
            () => P.Arg<int>(0) * P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) * P.Arg(1, CilType.Int32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesCheckedMultiply(PatternDsl dsl)
        => Assert.Equal(Code.Mul_Ovf, MatchProducer("MultiplyChecked", dsl,
            () => checked(P.Arg<int>(0) * P.Arg<int>(1)),
            () => P.Arg(0, CilType.Int32).MultiplyChecked(P.Arg(1, CilType.Int32))));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesDivide(PatternDsl dsl)
        => Assert.Equal(Code.Div, MatchProducer("Divide", dsl,
            () => P.Arg<int>(0) / P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) / P.Arg(1, CilType.Int32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesUnsignedDivide(PatternDsl dsl)
        => Assert.Equal(Code.Div_Un, MatchProducer("DivideUnsigned", dsl,
            () => P.Arg<uint>(0) / P.Arg<uint>(1),
            () => P.Arg(0, CilType.UInt32) / P.Arg(1, CilType.UInt32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesModulo(PatternDsl dsl)
        => Assert.Equal(Code.Rem, MatchProducer("Modulo", dsl,
            () => P.Arg<int>(0) % P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) % P.Arg(1, CilType.Int32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesUnsignedModulo(PatternDsl dsl)
        => Assert.Equal(Code.Rem_Un, MatchProducer("ModuloUnsigned", dsl,
            () => P.Arg<uint>(0) % P.Arg<uint>(1),
            () => P.Arg(0, CilType.UInt32) % P.Arg(1, CilType.UInt32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesBitwiseAnd(PatternDsl dsl)
        => Assert.Equal(Code.And, MatchProducer("BitAnd", dsl,
            () => P.Arg<int>(0) & P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32).BitAnd(P.Arg(1, CilType.Int32))));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesBitwiseOr(PatternDsl dsl)
        => Assert.Equal(Code.Or, MatchProducer("BitOr", dsl,
            () => P.Arg<int>(0) | P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32).BitOr(P.Arg(1, CilType.Int32))));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesExclusiveOr(PatternDsl dsl)
        => Assert.Equal(Code.Xor, MatchProducer("Xor", dsl,
            () => P.Arg<int>(0) ^ P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) ^ P.Arg(1, CilType.Int32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesShiftLeft(PatternDsl dsl)
        => Assert.Equal(Code.Shl, MatchProducer("ShiftLeft", dsl,
            () => P.Arg<int>(0) << P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) << P.Arg(1, CilType.Int32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesShiftRight(PatternDsl dsl)
        => Assert.Equal(Code.Shr, MatchProducer("ShiftRight", dsl,
            () => P.Arg<int>(0) >> P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) >> P.Arg(1, CilType.Int32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesUnsignedShiftRight(PatternDsl dsl)
        => Assert.Equal(Code.Shr_Un, MatchProducer("ShiftRightUnsigned", dsl,
            () => P.Arg<uint>(0) >> P.Arg<int>(1),
            () => P.Arg(0, CilType.UInt32) >> P.Arg(1, CilType.Int32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesUnsignedCheckedAdd(PatternDsl dsl)
        => Assert.Equal(Code.Add_Ovf_Un, MatchProducer("AddCheckedUnsigned", dsl,
            () => checked(P.Arg<uint>(0) + P.Arg<uint>(1)),
            () => P.Arg(0, CilType.UInt32).AddChecked(P.Arg(1, CilType.UInt32))));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesEqualityValue(PatternDsl dsl)
        => Assert.Equal(Code.Ceq, MatchProducer("Equal", dsl,
            () => P.Arg<int>(0) == P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) == P.Arg(1, CilType.Int32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesInequalityValueLowering(PatternDsl dsl)
        => Assert.Equal(Code.Ceq, MatchProducer("NotEqual", dsl,
            () => P.Arg<int>(0) != P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) != P.Arg(1, CilType.Int32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void CapturedOperandInNegatedComparisonKeepsItsImmediateConsumer(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "NotEqual");
        var pattern = DualPattern.Value(dsl,
            () => P.Mark("left", P.Arg<int>(0)) != P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32).Mark("left") != P.Arg(1, CilType.Int32));

        var match = method.Match(pattern).Single();
        var left = match.Value("left");

        Assert.NotNull(left.ConsumerInstruction);
        var consumer = left.ConsumerInstruction!;
        Assert.Equal(Code.Ceq, consumer.OpCode.Code);
        Assert.NotSame(match.Value().ProducerInstruction, consumer);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesGreaterThanValue(PatternDsl dsl)
        => Assert.Equal(Code.Cgt, MatchProducer("GreaterThan", dsl,
            () => P.Arg<int>(0) > P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) > P.Arg(1, CilType.Int32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesGreaterThanOrEqualValueLowering(PatternDsl dsl)
        => Assert.Equal(Code.Ceq, MatchProducer("GreaterThanOrEqual", dsl,
            () => P.Arg<int>(0) >= P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) >= P.Arg(1, CilType.Int32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesUnsignedGreaterThanValue(PatternDsl dsl)
        => Assert.Equal(Code.Cgt_Un, MatchProducer("GreaterThanUnsigned", dsl,
            () => P.Arg<uint>(0) > P.Arg<uint>(1),
            () => P.Arg(0, CilType.UInt32) > P.Arg(1, CilType.UInt32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesLessThanValue(PatternDsl dsl)
        => Assert.Equal(Code.Clt, MatchProducer("LessThan", dsl,
            () => P.Arg<int>(0) < P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) < P.Arg(1, CilType.Int32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesLessThanOrEqualValueLowering(PatternDsl dsl)
        => Assert.Equal(Code.Ceq, MatchProducer("LessThanOrEqual", dsl,
            () => P.Arg<int>(0) <= P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) <= P.Arg(1, CilType.Int32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesUnsignedLessThanValue(PatternDsl dsl)
        => Assert.Equal(Code.Clt_Un, MatchProducer("LessThanUnsigned", dsl,
            () => P.Arg<uint>(0) < P.Arg<uint>(1),
            () => P.Arg(0, CilType.UInt32) < P.Arg(1, CilType.UInt32)));

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void CheckedPatternDoesNotMatchUncheckedOpcode(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Add");
        var pattern = DualPattern.Value(dsl,
            () => checked(P.Arg<int>(0) + P.Arg<int>(1)),
            () => P.Arg(0, CilType.Int32).AddChecked(P.Arg(1, CilType.Int32)));

        Assert.Empty(method.Match(pattern));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void UncheckedPatternDoesNotMatchCheckedOpcode(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "AddChecked");
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<int>(0) + P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) + P.Arg(1, CilType.Int32));

        Assert.Empty(method.Match(pattern));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void OperandOrderIsSignificant(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Subtract");
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<int>(1) - P.Arg<int>(0),
            () => P.Arg(1, CilType.Int32) - P.Arg(0, CilType.Int32));

        Assert.Empty(method.Match(pattern));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void OperationKindIsSignificant(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Add");
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<int>(0) - P.Arg<int>(1),
            () => P.Arg(0, CilType.Int32) - P.Arg(1, CilType.Int32));

        Assert.Empty(method.Match(pattern));
    }

    private static Code MatchProducer<T>(string methodName, PatternDsl dsl,
        Expression<Func<T>> expression, Func<CilExpr> cilExpr)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, methodName);
        return method.Match(DualPattern.Value(dsl, expression, cilExpr))
            .Single().Value().ProducerInstruction.OpCode.Code;
    }
}
