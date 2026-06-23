using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.Cecil;
using MonoWeaver.Patterns;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class CecilPatternTransformTests
{
    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void TransformAfterUseLeavesReplacementForConsumer(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ChainTransform");
        var pattern = ChainPattern(dsl);
        var callback = CilMethodSpec.From(typeof(Ops).GetMethod(nameof(Ops.IdentityB))!);

        method.Match(pattern).Single().Value("hook").AfterUse().Transform(callback);

        var callbackCall = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.IdentityB) });
        PatternTestSupport.AssertCallTo(callbackCall.Previous, nameof(A.B));
        PatternTestSupport.AssertCallTo(callbackCall.Next, nameof(B.C));
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ObserveDuplicatesAndPreservesOriginalValue(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Observe");
        var callback = CilMethodSpec.From(typeof(Ops).GetMethod(nameof(Ops.ObserveB))!);

        method.Match(ChainPattern(dsl)).Single().Value("hook").AfterUse().Observe(callback);

        var duplicate = method.Body.Instructions.Single(instruction => instruction.OpCode.Code == Code.Dup);
        PatternTestSupport.AssertCallTo(duplicate.Previous, nameof(A.B));
        PatternTestSupport.AssertCallTo(duplicate.Next, nameof(Ops.ObserveB));
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void InsertionCallCanStoreResultInExistingLocal(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "BeforeExpression");
        var temp = method.Body.Variables[0];
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<int>(0),
            () => P.Arg(0, CilType.Int32));
        var callback = CilMethodSpec.From(typeof(Ops).GetMethod(nameof(Ops.FortyTwo))!);

        var match = Assert.Single(method.Match(pattern).Where(candidate =>
            ReferenceEquals(candidate.Value().ProducerInstruction, candidate.Value().AfterUseInstruction)));
        match.BeforeEvaluation().CallValue(callback).StoreLocal(temp);

        var callbackCall = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.FortyTwo) });
        Assert.True(callbackCall.Next?.OpCode.Code is
            Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3);
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ConditionTransformProducesValidIl(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ConditionTransform");
        var pattern = ComplexConditionPattern(dsl);
        var callback = CilMethodSpec.From(typeof(Ops).GetMethod(nameof(Ops.IdentityBool))!);

        var condition = method.Match(pattern).Single().Condition("ab");
        // The pure Cecil backend creates one bridge per outgoing condition edge.
        // A source block can therefore contribute both a taken and a fall-through call site.
        var expectedCallSites = condition.TrueExits.Count + condition.FalseExits.Count;

        condition.Transform(callback);

        Assert.Equal(expectedCallSites, method.Body.Instructions.Count(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.IdentityBool) }));
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void InsertionBeforeFallThroughValueDoesNotRetargetExistingBranch(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Select");
        var touch = PatternTestSupport.FixtureMethod(module, "Touch");
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<int>(1),
            () => P.Arg(1, CilType.Int32));
        var match = method.Match(pattern).Single();
        var anchor = match.Value().FirstInstruction;
        var branch = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code is Code.Brfalse or Code.Brfalse_S);
        var originalBranchTarget = Assert.IsType<Instruction>(branch.Operand);

        match.BeforeEvaluation().CallVoid(touch);

        Assert.Same(originalBranchTarget, branch.Operand);
        Assert.Equal(Code.Call, anchor.Previous?.OpCode.Code);
        Assert.Same(anchor, anchor.Previous?.Next);
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    private static ExpressionPattern ChainPattern(PatternDsl dsl)
    {
        var aType = RuntimeSymbols.Type<A>(assignable: true);
        var callB = RuntimeSymbols.Method<A>(nameof(A.B));
        var callC = RuntimeSymbols.Method<B>(nameof(B.C));
        return DualPattern.Value(dsl,
            () => P.Mark("hook", P.Arg<A>(0).B()).C(),
            () => P.Arg(0, aType).Call(callB).Mark("hook").Call(callC));
    }

    private static ExpressionPattern ComplexConditionPattern(PatternDsl dsl)
    {
        var bType = RuntimeSymbols.Type<B>(assignable: true);
        var callA = RuntimeSymbols.Method<Ops>(nameof(Ops.CallA));
        var callB = RuntimeSymbols.Method<B>(nameof(B.CallB));
        var callC = RuntimeSymbols.Method<Ops>(nameof(Ops.CallC));
        var callD = RuntimeSymbols.Method<Ops>(nameof(Ops.CallD));
        return DualPattern.Condition(dsl,
            () => P.Mark("ab", Ops.CallA() && P.Arg<B>(0).CallB())
                  && (Ops.CallC() || Ops.CallD()),
            () => P.Mark("ab", P.Call(callA).AndAlso(P.Arg(0, bType).Call(callB)))
                .AndAlso(P.Call(callC).OrElse(P.Call(callD))));
    }
}
