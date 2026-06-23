using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoWeaver.MonoMod.Patterns;
using MonoWeaver.Patterns;
using Xunit;

namespace MonoWeaver.PatternTests;

/// <summary>
/// Exercises the optional MonoMod adapter with the same semantic patterns used by
/// the pure Mono.Cecil transform tests. Pattern construction is run through both DSLs.
/// </summary>
public sealed class MonoModPatternTransformTests
{
    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void TransformAfterUseLeavesReplacementForConsumer(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ChainTransform");

        using var context = new ILContext(method);
        context.Invoke(cursor =>
        {
            var hook = cursor.Match(ChainPattern(dsl)).Single().Value("hook");
            hook.AfterUse(cursor)
                .Transform((Func<B, B>)Ops.IdentityB)
                .LeaveOnStack();
        });

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

        using var context = new ILContext(method);
        context.Invoke(cursor =>
        {
            var hook = cursor.Match(ChainPattern(dsl)).Single().Value("hook");
            hook.AfterUse(cursor).Observe((Action<B>)Ops.ObserveB);
        });

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

        using var context = new ILContext(method);
        context.Invoke(cursor =>
        {
            var match = Assert.Single(cursor.Match(pattern).Where(candidate =>
                ReferenceEquals(candidate.Value().ProducerInstruction, candidate.Value().AfterUseInstruction)));
            match.Before(cursor)
                .Call((Func<int>)Ops.FortyTwo)
                .StoreLocal(temp);
        });

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

        using var context = new ILContext(method);
        context.Invoke(cursor =>
        {
            var condition = cursor.Match(ComplexConditionPattern(dsl)).Single().Condition("ab");
            condition.Transform(cursor, (Func<bool, bool>)Ops.IdentityBool);
        });

        Assert.Single(method.Body.Instructions.Where(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.IdentityBool) }));
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void BeforeInsertionPreservesExistingBranchTarget(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Select");
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<int>(1),
            () => P.Arg(1, CilType.Int32));
        var branch = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code is Code.Brfalse or Code.Brfalse_S);
        var originalTarget = Assert.IsType<Instruction>(branch.Operand);

        using var context = new ILContext(method);
        context.Invoke(cursor =>
        {
            var match = cursor.Match(pattern).Single();
            match.Before(cursor).Call((Action)Ops.ConsumeNothing);
        });

        Assert.Same(originalTarget, branch.Operand);
        Assert.Contains(method.Body.Instructions, instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.ConsumeNothing) });
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    private static CilExpressionPattern ChainPattern(PatternDsl dsl)
    {
        var aType = RuntimeSymbols.Type<A>(assignable: true);
        var callB = RuntimeSymbols.Method<A>(nameof(A.B));
        var callC = RuntimeSymbols.Method<B>(nameof(B.C));
        return DualPattern.Value(dsl,
            () => P.Mark("hook", P.Arg<A>(0).B()).C(),
            () => P.Arg(0, aType).Call(callB).Mark("hook").Call(callC));
    }

    private static CilExpressionPattern ComplexConditionPattern(PatternDsl dsl)
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
