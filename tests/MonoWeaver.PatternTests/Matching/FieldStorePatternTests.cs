using System;
using Mono.Cecil.Cil;
using MonoWeaver.Cecil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class FieldStorePatternTests
{
    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesInstanceFieldStore(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "WriteInstanceField");
        var field = RuntimeSymbols.Field<MemberHost>(nameof(MemberHost.InstanceField));

        var amount = Cil.Arg<int>(1);
        var pattern = DualPattern.Effect(dsl,
            () => P.StoreField(P.Arg<MemberHost>(0).InstanceField, amount),
            () => P.StoreField(
                P.Arg(0, RuntimeSymbols.Type<MemberHost>(assignable: true)),
                field,
                amount.Expr));

        var match = method.Match(pattern).Single();

        Assert.Equal(Code.Stfld, match.LastInstruction.OpCode.Code);
        Assert.Equal(1, match[amount].ParameterIndex);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesStaticFieldStore(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "WriteStaticField");
        var field = RuntimeSymbols.Field<MemberHost>(nameof(MemberHost.StaticField));

        var pattern = DualPattern.Effect(dsl,
            () => P.StoreField(MemberHost.StaticField, P.Arg<int>(0)),
            () => P.StoreField(field, P.Arg(0, RuntimeSymbols.Type<int>(assignable: true))));

        var match = method.Match(pattern).Single();

        Assert.Equal(Code.Stsfld, match.LastInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesComputedValueSubpattern(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "WriteComputedInstanceField");
        var field = RuntimeSymbols.Field<MemberHost>(nameof(MemberHost.InstanceField));

        var pattern = DualPattern.Effect(dsl,
            () => P.StoreField(P.Arg<MemberHost>(0).InstanceField, P.Arg<int>(1) * 2),
            () => P.StoreField(
                P.Arg(0, RuntimeSymbols.Type<MemberHost>(assignable: true)),
                field,
                P.Arg(1, RuntimeSymbols.Type<int>(assignable: true)) * P.Constant(2)));

        Assert.Single(method.Match(pattern));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ValueSubpatternMismatchRejects(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "WriteComputedInstanceField");
        var field = RuntimeSymbols.Field<MemberHost>(nameof(MemberHost.InstanceField));

        //目标写入的是 amount * 2，纯 amount 不应命中
        var pattern = DualPattern.Effect(dsl,
            () => P.StoreField(P.Arg<MemberHost>(0).InstanceField, P.Arg<int>(1)),
            () => P.StoreField(
                P.Arg(0, RuntimeSymbols.Type<MemberHost>(assignable: true)),
                field,
                P.Arg(1, RuntimeSymbols.Type<int>(assignable: true))));

        Assert.Empty(method.Match(pattern));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void DifferentFieldRejects(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "WriteStaticField");
        var otherField = RuntimeSymbols.Field<MemberHost>(nameof(MemberHost.InstanceField));

        var value = Cil.Any<int>();
        var pattern = DualPattern.Effect(dsl,
            () => P.StoreField(P.Arg<MemberHost>(0).InstanceField, value),
            () => P.StoreField(
                P.Arg(0, RuntimeSymbols.Type<MemberHost>(assignable: true)),
                otherField,
                value.Expr));

        Assert.Empty(method.Match(pattern));
    }

    [Fact]
    public void FieldStoreDoesNotShadowFieldRead()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "WriteFieldThenRead");

        //同一方法里写和读并存：读取 pattern 只命中 ldfld，不受 stfld 支持影响
        var read = method.Match(Cil.Value(() => P.Arg<MemberHost>(0).InstanceField)).Single();
        Assert.Equal(Code.Ldfld, read.DefinitionInstruction.OpCode.Code);

        var store = method.Match(Cil.Effect(() =>
            P.StoreField(P.Arg<MemberHost>(0).InstanceField, P.Arg<int>(1)))).Single();
        Assert.Equal(Code.Stfld, store.LastInstruction.OpCode.Code);
    }

    [Fact]
    public void StaticStoreWhoseResultIsUsedIsNotAnEffect()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "WriteAndReturnStaticField");

        //return Type.F = x; 的赋值结果被 dup 复用，删除会导致 ret 栈下溢——不作为 effect 提供
        var value = Cil.Any<int>();
        Assert.Empty(method.Match(Cil.Effect(() =>
            P.StoreField(MemberHost.StaticField, value))));
    }

    [Fact]
    public void InstanceStoreWhoseResultIsUsedIsNotAnEffect()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "WriteAndReturnInstanceField");

        var value = Cil.Any<int>();
        Assert.Empty(method.Match(Cil.Effect(() =>
            P.StoreField(P.Arg<MemberHost>(0).InstanceField, value))));
    }

    [Fact]
    public void RemoveFieldStoreProducesValidIL()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "WriteInstanceField");

        var match = method.Match(Cil.Effect(() =>
            P.StoreField(P.Arg<MemberHost>(0).InstanceField, P.Arg<int>(1)))).Single();
        match.Remove().Apply(VerifyOptions.Full);

        Assert.DoesNotContain(method.Body.Instructions,
            instruction => instruction.OpCode.Code == Code.Stfld);
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Fact]
    public void BeforeFieldStoreInsertsCallback()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "WriteInstanceField");

        var match = method.Match(Cil.Effect(() =>
            P.StoreField(P.Arg<MemberHost>(0).InstanceField, P.Arg<int>(1)))).Single();
        match.Before((Action)Ops.ConsumeNothing).Apply(VerifyOptions.Full);

        var callback = Assert.Single(method.Body.Instructions,
            instruction => instruction.OpCode.Code == Code.Call
                           && instruction.Operand is Mono.Cecil.MethodReference { Name: nameof(Ops.ConsumeNothing) });
        Assert.NotNull(callback);
        PatternTestSupport.AssertNoVerificationErrors(method);
    }
}
