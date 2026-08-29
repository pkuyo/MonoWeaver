using System;
using Mono.Cecil.Cil;
using MonoWeaver.Cecil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class AddressReceiverPatternTests
{
    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void StructInstanceCallOnArgumentMatches(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "StructArgCall");
        var sum = RuntimeSymbols.Method<GamePoint>(nameof(GamePoint.Sum));

        var pattern = DualPattern.Value(dsl,
            () => P.Arg<GamePoint>(0).Sum(),
            () => P.Arg(0, RuntimeSymbols.Type<GamePoint>()).Call(sum));

        var match = method.Match(pattern).Single();

        Assert.Equal(Code.Call, match.DefinitionInstruction.OpCode.Code);
        //匹配范围从 ldarga 接收者开始
        Assert.True(match.FirstInstruction.OpCode.Code is Code.Ldarga or Code.Ldarga_S,
            $"Expected the match to start at the ldarga receiver, got {match.FirstInstruction.OpCode.Code}.");
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void StructInstanceCallArgumentSubpatternMatches(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "StructArgCallWithArgument");
        var scaled = RuntimeSymbols.Method<GamePoint>(nameof(GamePoint.Scaled), typeof(int));

        var factor = Cil.Arg<int>(1);
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<GamePoint>(0).Scaled(factor),
            () => P.Arg(0, RuntimeSymbols.Type<GamePoint>())
                .Call(scaled, factor.Expr));

        var match = method.Match(pattern).Single();

        Assert.Equal(1, match[factor].ParameterIndex);
    }

    [Fact]
    public void StructPropertyReadMatches()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "StructPropertyRead");

        var match = method.Match(Cil.Value(() => P.Arg<GamePoint>(0).First)).Single();

        Assert.Equal(Code.Call, match.DefinitionInstruction.OpCode.Code);
    }

    [Fact]
    public void NullableHasValueMatches()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "NullableHasValue");

        var match = method.Match(Cil.Value(() => P.Arg<int?>(0).HasValue)).Single();

        Assert.Equal(Code.Call, match.DefinitionInstruction.OpCode.Code);
    }

    [Fact]
    public void NullableValueMatches()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "NullableValue");

        var match = method.Match(Cil.Value(() => P.Arg<int?>(0).Value)).Single();

        Assert.Equal(Code.Call, match.DefinitionInstruction.OpCode.Code);
    }

    [Fact]
    public void StructArrayElementReceiverMatches()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "StructArrayElementCall");

        var match = method.Match(Cil.Value(() => P.Arg<GamePoint[]>(0)[0].Sum())).Single();

        Assert.Equal(Code.Call, match.DefinitionInstruction.OpCode.Code);
    }

    [Fact]
    public void RefArgumentMatchesUnderlyingArgument()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "RefArgumentCall");
        var mutate = RuntimeSymbols.Method<Ops>(nameof(Ops.Mutate), typeof(int).MakeByRefType());

        //ref 实参在 IL 中是 ldarga；pattern 直接按底层参数描述
        var target = Cil.Arg(RuntimeSymbols.Type<int>(assignable: true), 0);
        var pattern = Cil.Effect(P.Call(mutate, target.Expr));

        var match = method.Match(pattern).Single();

        Assert.Equal(0, match[target].ParameterIndex);
    }

    [Fact]
    public void AddressItselfIsNotAValueCandidate()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "StructArgCall");

        //方法体里只有 ldarga（无 ldarg），裸参数 pattern 不应把地址当成可改写的值
        Assert.Empty(method.Match(Cil.Value(() => P.Arg<GamePoint>(0))));
    }

    [Fact]
    public void ObserveOnAddressBackedCaptureIsRejected()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "RefArgumentCall");
        var mutate = RuntimeSymbols.Method<Ops>(nameof(Ops.Mutate), typeof(int).MakeByRefType());

        var target = Cil.Arg(RuntimeSymbols.Type<int>(assignable: true), 0);
        var match = method.Match(Cil.Effect(P.Call(mutate, target.Expr))).Single();
        var capture = match[target];

        //该 capture 的占位是 ldarga：栈上是指针而非 int，占位改写必须被拒绝
        Assert.True(capture.IsAddressBacked);
        Assert.Throws<NotSupportedException>(() => capture.Observe((Action<int>)Ops.ObserveInt));
        Assert.Throws<NotSupportedException>(() => capture.Transform((Func<int, int>)Ops.IdentityInt));
        Assert.Throws<NotSupportedException>(() => capture.Replace((Func<int>)Ops.FortyTwo));
        Assert.Throws<NotSupportedException>(() => capture.After((Action)Ops.ConsumeNothing));
    }

    [Fact]
    public void StructReceiverCaptureRejectsValueRewrites()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "StructArgCall");

        var p = Cil.Arg<GamePoint>(0);
        var match = method.Match(Cil.Value(() => p.Value.Sum())).Single();
        var capture = match[p];

        Assert.True(capture.IsAddressBacked);
        Assert.Throws<NotSupportedException>(() => capture.Observe((Action<GamePoint>)Ops.ObservePoint));
    }

    [Fact]
    public void ArgsCaptureOfAddressBackedValueIsRejected()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "RefArgumentCall");
        var mutate = RuntimeSymbols.Method<Ops>(nameof(Ops.Mutate), typeof(int).MakeByRefType());

        var target = Cil.Arg(RuntimeSymbols.Type<int>(assignable: true), 0);
        var match = method.Match(Cil.Effect(P.Call(mutate, target.Expr))).Single();
        var capture = match[target];

        Assert.Throws<NotSupportedException>(() =>
            match.Before((Action<int>)Ops.ObserveInt, args => args.Capture(capture)));
    }

    [Fact]
    public void AddressBackedCaptureStillSupportsBeforeAndArgLoading()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "RefArgumentCall");
        var mutate = RuntimeSymbols.Method<Ops>(nameof(Ops.Mutate), typeof(int).MakeByRefType());

        var target = Cil.Arg(RuntimeSymbols.Type<int>(assignable: true), 0);
        var match = method.Match(Cil.Effect(P.Call(mutate, target.Expr))).Single();
        var capture = match[target];

        //推荐替代路径：args.Arg 重新按变量装载值，安全可用
        match.Before((Action<int>)Ops.ObserveInt, args => args.Arg(capture))
            .Apply(VerifyOptions.Full);

        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Fact]
    public void TransformStructCallResultProducesValidIL()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "StructArgCall");

        var match = method.Match(Cil.Value(() => P.Arg<GamePoint>(0).Sum())).Single();
        match.Transform((Func<int, int>)Ops.IdentityInt).Apply(VerifyOptions.Full);

        var callback = Assert.Single(method.Body.Instructions,
            instruction => instruction.OpCode.Code == Code.Call
                           && instruction.Operand is Mono.Cecil.MethodReference { Name: nameof(Ops.IdentityInt) });
        Assert.NotNull(callback);
        PatternTestSupport.AssertNoVerificationErrors(method);
    }
}
