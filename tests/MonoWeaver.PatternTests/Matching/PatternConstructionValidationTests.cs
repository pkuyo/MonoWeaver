using System;
using MonoWeaver.Patterns;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class PatternConstructionValidationTests
{
    [Fact]
    public void ExpressionDslRejectsCapturedRuntimeValue()
    {
        var captured = 42;

        var exception = Assert.Throws<NotSupportedException>(() => Cil.Value(() => captured));

        Assert.Contains("Captured runtime values", exception.Message);
    }

    [Fact]
    public void ExpressionDslRejectsMultidimensionalArrayCreation()
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            Cil.Value(() => new int[P.Arg<int>(0), P.Arg<int>(1)]));

        Assert.Contains("single-dimensional", exception.Message);
    }

    [Fact]
    public void PlaceholderCannotBeExecutedDirectly()
        => Assert.Throws<InvalidOperationException>(() => P.Arg<int>(0));

    [Fact]
    public void MetadataArgumentAndLocalIndicesMustBeNonNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => P.Arg(-1, CilType.Int32));
        Assert.Throws<ArgumentOutOfRangeException>(() => P.Local(-1, CilType.Int32));
    }

    [Fact]
    public void LeafFactoriesRejectVoidType()
    {
        Assert.Throws<ArgumentException>(() => Cil.Any(CilType.Void));
        Assert.Throws<ArgumentException>(() => Cil.Local(CilType.Void));
        Assert.Throws<ArgumentException>(() => Cil.Arg(CilType.Void));
        Assert.Throws<ArgumentException>(() => Cil.This(CilType.Void));
    }

    [Fact]
    public void LeafFactoriesRejectNegativeIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Cil.Local<int>(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Cil.Arg<int>(-1));
    }

    [Fact]
    public void PatternObjectCannotBeUsedOutsideLambda()
    {
        var local = Cil.Local<int>();

        Assert.Throws<InvalidOperationException>(() =>
        {
            int value = local;
            return value;
        });
    }

    [Fact]
    public void CilFactoryCallInsideLambdaIsRejected()
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            Cil.Value(() => Cil.Local<int>(0, null) + 1));

        Assert.Contains("outside the lambda", exception.Message);
    }

    [Fact]
    public void StaticCallFactoryRejectsInstanceMethod()
    {
        var method = RuntimeSymbols.Method<MemberHost>(nameof(MemberHost.Add), typeof(int));

        Assert.Throws<ArgumentException>(() => P.Call(method, P.Constant(1)));
    }

    [Fact]
    public void InstanceCallFactoryRejectsStaticMethod()
    {
        var method = RuntimeSymbols.Method<MemberHost>(nameof(MemberHost.StaticAdd), typeof(int), typeof(int));

        Assert.Throws<ArgumentException>(() =>
            P.Arg(0, RuntimeSymbols.Type<MemberHost>()).Call(method, P.Constant(1), P.Constant(2)));
    }

    [Fact]
    public void NewFactoryRequiresConstructor()
    {
        var method = RuntimeSymbols.Method<MemberHost>(nameof(MemberHost.Add), typeof(int));

        Assert.Throws<ArgumentException>(() => P.New(method, P.Constant(1)));
    }

    [Fact]
    public void FieldFactoriesEnforceStaticness()
    {
        var instance = RuntimeSymbols.Field<MemberHost>(nameof(MemberHost.InstanceField));
        var @static = RuntimeSymbols.Field<MemberHost>(nameof(MemberHost.StaticField));
        var host = P.Arg(0, RuntimeSymbols.Type<MemberHost>());

        Assert.Throws<ArgumentException>(() => P.Field(instance));
        Assert.Throws<ArgumentException>(() => host.Field(@static));
    }

    [Fact]
    public void CallArgumentCountIsValidatedBeforeMatching()
    {
        var method = RuntimeSymbols.Method<Ops>(nameof(Ops.Add), typeof(int), typeof(int));

        Assert.Throws<ArgumentException>(() => P.Call(method, P.Constant(1)));
    }

    [Fact]
    public void MetadataConditionRequiresBooleanResult()
        => Assert.Throws<ArgumentException>(() => Cil.Condition(P.Constant(1)));

    [Fact]
    public void MetadataEffectRequiresVoidResult()
        => Assert.Throws<ArgumentException>(() => Cil.Effect(P.Constant(1)));

    [Fact]
    public void MetadataDiscardRequiresNonVoidResult()
    {
        var consume = RuntimeSymbols.Method<Ops>(nameof(Ops.ConsumeInt), typeof(int));
        var call = P.Call(consume, P.Constant(1));

        Assert.Throws<ArgumentException>(() => Cil.Discard(call));
    }

    [Fact]
    public void ShortCircuitOperatorsRequireBooleanOperands()
    {
        var left = P.Constant(1);
        var right = P.Constant(2);

        Assert.Throws<InvalidOperationException>(() => left.AndAlso(right));
        Assert.Throws<InvalidOperationException>(() => left.OrElse(right));
    }

    [Fact]
    public void ArrayRankMustBePositive()
        => Assert.Throws<ArgumentOutOfRangeException>(() => CilType.Int32.MakeArrayType(0));
}
