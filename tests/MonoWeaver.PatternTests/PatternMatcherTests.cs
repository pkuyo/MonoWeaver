using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Cecil;
using MonoWeaver.MonoMod.Patterns;
using MonoWeaver.Patterns;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class PatternMatcherTests
{
    private const string TargetType = "MonoWeaver.PatternTestFixtures.Target";

    [Fact]
    public void MatchesNestedCallAndMarkedSubexpression()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Chain");

        var pattern = Cil.Value(() => P.Mark("hook", P.Arg<A>(0).B()).C());
        var match = CilMatcher.For(method).Find(pattern).Single();
        var hook = match.Value("hook");

        AssertCallTo(hook.ProducerInstruction, nameof(A.B));
        Assert.Same(hook.ProducerInstruction, hook.AfterUseInstruction);
    }

    [Fact]
    public void UsesLoadSiteWhenCompilerTemporaryIsTransparent()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Temporary");

        var pattern = Cil.Value(() => P.Mark("hook", P.Arg<A>(0).B()).C());
        var hook = CilMatcher.For(method).Find(pattern).Single().Value("hook");

        AssertCallTo(hook.ProducerInstruction, nameof(A.B));
        Assert.True(hook.AfterUseInstruction.OpCode.Code is Code.Ldloc or Code.Ldloc_S,
            "AfterUse must target the concrete ldloc consumed by C().");
    }

    [Fact]
    public void MatchesComplexShortCircuitAndCapturesSubcondition()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Condition");

        var pattern = Cil.Condition(() =>
            P.Mark("ab", Ops.CallA() && P.Arg<B>(0).CallB())
            && (Ops.CallC() || Ops.CallD()));

        var match = CilMatcher.For(method).Find(pattern).Single();
        var ab = match.Condition("ab");
        Assert.True(ab.TrueExits.Count == 1, "A && B has one true exit into the remaining condition.");
        Assert.True(ab.FalseExits.Count == 2, "A && B has two short-circuit false exits.");
        Assert.True(ab.CanRewrite, ab.RewriteFailureReason ?? "The captured condition should be rewritable.");
    }

    [Fact]
    public void LocalDefinitionConstraintDisambiguatesBooleanLocal()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "LocalCondition");

        var pattern = Cil.Condition(() => P.Local<bool>("ret"))
            .LocalDefinedBy("ret", Cil.Value(() => Ops.XXX()));

        var match = CilMatcher.For(method).Find(pattern).Single();
        Assert.True(match.Local("ret").Variable.Index >= 0,
            "The unique XXX() definition should identify a concrete local.");
    }

    [Fact]
    public void AmbiguousInnerPatternIsRejected()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Ambiguous");

        var matches = CilMatcher.For(method).Find(Cil.Value(() => P.Arg<A>(0).B()));
        Assert.True(matches.Count == 2, "Both B() occurrences must remain visible to the matcher.");
        Assert.Throws<CilPatternMatchException>(() => matches.Single());
    }

    [Fact]
    public void OuterCallDisambiguatesRepeatedInnerCall()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Context");

        var pattern = Cil.Value(() => P.Mark("hook", P.Arg<A>(0).B()).D());
        var hook = CilMatcher.For(method).Find(pattern).Single().Value("hook");

        AssertCallTo(hook.ProducerInstruction, nameof(A.B));
        AssertCallTo(hook.AfterUseInstruction.Next, nameof(B.D));
    }

    [Fact]
    public void ExactOverloadIsRequired()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Overloads");

        var match = CilMatcher.For(method)
            .Find(Cil.Value(() => P.Arg<B>(0).Select("selected")))
            .Single();

        var operand = Assert.IsType<MethodReference>(match.Value().ProducerInstruction.Operand);
        Assert.Equal(nameof(B.Select), operand.Name);
        Assert.True(operand.Parameters.Single().ParameterType.MetadataType == MetadataType.String,
            "Method matching must include the exact overload signature and literal argument.");
    }

    [Fact]
    public void StackTypeAllowsAssignableArgumentPattern()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "AssignableArgument");

        var match = CilMatcher.For(method)
            .Find(Cil.Value(() => P.Arg<object>(0, "value")))
            .Single();
        var value = match.Argument("value");

        Assert.True(value.ParameterIndex == 0, "ParameterDefinition operands must keep explicit parameter indexes.");
        Assert.True(value.ProducerInstruction.OpCode.Code is Code.Ldarg or Code.Ldarg_S or Code.Ldarg_0,
            "A reference-assignable argument should match through StackType compatibility.");
    }

    [Fact]
    public void ConstantTypesAreNotInterchanged()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Constants");

        var match = CilMatcher.For(method).Find(Cil.Value(() => 1.0)).Single();
        Assert.Equal(Code.Ldc_R8, match.Value().ProducerInstruction.OpCode.Code);
    }

    [Fact]
    public void CallOpcodeDifferenceCanBeMadeStrict()
    {
        using var module = OpenFixtureModule();
        var method = module.RequireType("MonoWeaver.PatternTestFixtures.DirectCaller")
            .RequireMethod("CallBase");
        var directCaller = CilSymbols.In("PatternFixtures")
            .Type("MonoWeaver.PatternTestFixtures.DirectCaller");
        var baseCall = CilMethodSpec.From(typeof(B).GetMethod(nameof(B.C))
            ?? throw new MissingMethodException(typeof(B).FullName, nameof(B.C)));
        var pattern = Cil.Value(P.This(directCaller).Call(baseCall));

        var relaxed = CilMatcher.For(method).Find(pattern);
        Assert.True(relaxed.Count == 1,
            "The default mode should tolerate call/callvirt lowering differences for the same method.");

        var strictOptions = new CilPatternOptions { IgnoreCallOpcodeDifference = false };
        var strict = CilMatcher.For(method).Find(Cil.Value(P.This(directCaller).Call(baseCall), strictOptions));
        Assert.True(strict.Count == 0,
            "Strict call matching must reject an instance call opcode different from the Lambda lowering contract.");
    }

    [Fact]
    public void MultipleReachingDefinitionsRejectLocalConstraint()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "MultipleDefinitions");

        var pattern = Cil.Condition(() => P.Local<bool>("ret"))
            .LocalDefinedBy("ret", Cil.Value(() => Ops.XXX()));
        var matches = CilMatcher.For(method).Find(pattern);

        Assert.True(matches.Count == 0,
            "A local-definition constraint must reject a load reached by more than one store.");
    }

    [Fact]
    public void EffectPatternRequiresDiscardedResult()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Discarded");

        var match = CilMatcher.For(method)
            .Find(Cil.Effect(() => P.Arg<A>(0).B()))
            .Single();

        Assert.Equal(Code.Pop, match.LastInstruction.OpCode.Code);
    }

    [Fact]
    public void MatchesNewArrayCreation()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "NewIntArray");

        var match = CilMatcher.For(method)
            .Find(Cil.Value(() => new int[P.Arg<int>(0)]))
            .Single();

        Assert.Equal(Code.Newarr, match.Value().ProducerInstruction.OpCode.Code);
    }

    [Fact]
    public void MatchesArrayElementLoad()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "LoadIntElement");

        var match = CilMatcher.For(method)
            .Find(Cil.Value(() => P.Arg<int[]>(0)[1]))
            .Single();

        Assert.True(match.Value().ProducerInstruction.OpCode.Code is Code.Ldelem_I4 or Code.Ldelem_Any,
            "ldelem should be modeled as a matchable array element read.");
    }

    [Fact]
    public void MatchesArrayLength()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Length");

        var match = CilMatcher.For(method)
            .Find(Cil.Value(() => P.Arg<int[]>(0).Length))
            .Single();

        Assert.Equal(Code.Ldlen, match.Value().ProducerInstruction.OpCode.Code);
    }

    [Fact]
    public void MatchesArrayElementStoreEffect()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "StoreIntElement");

        var match = CilMatcher.For(method)
            .Find(Cil.Effect(() => P.StoreElement(P.Arg<int[]>(0), 1, P.Arg<int>(1))))
            .Single();

        Assert.True(match.LastInstruction.OpCode.Code is Code.Stelem_I4 or Code.Stelem_Any,
            "stelem should be modeled as a matchable array element store effect.");
    }

    [Fact]
    public void MonoModTransformLeavesReplacementOnStack()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "ChainTransform");

        using var context = new ILContext(method);
        context.Invoke(il =>
        {
            var pattern = Cil.Value(() => P.Mark("hook", P.Arg<A>(0).B()).C());
            var hook = il.Match(pattern).Single().Value("hook");
            hook.AfterUse(il).Transform((Func<B, B>)Ops.IdentityB).LeaveOnStack();
        });

        var callbackCall = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.IdentityB) });
        AssertCallTo(callbackCall.Previous, nameof(A.B));
        AssertCallTo(callbackCall.Next, nameof(B.C));
        Assert.True(!HasVerificationErrors(method), "The transformed call chain must remain valid IL.");
    }

    [Fact]
    public void MonoModObservePreservesOriginalValue()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Observe");

        using var context = new ILContext(method);
        context.Invoke(il =>
        {
            var hook = il.Match(Cil.Value(() => P.Mark("hook", P.Arg<A>(0).B()).C()))
                .Single().Value("hook");
            hook.AfterUse(il).Observe((Action<B>)Ops.ObserveB);
        });

        var duplicate = method.Body.Instructions.Single(instruction => instruction.OpCode.Code == Code.Dup);
        AssertCallTo(duplicate.Previous, nameof(A.B));
        AssertCallTo(duplicate.Next, nameof(Ops.ObserveB));
        Assert.True(!HasVerificationErrors(method), "Observe must leave the original value for C().");
    }

    [Fact]
    public void PlainInsertionCallCanStoreResult()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "BeforeExpression");
        var temp = method.Body.Variables.FirstOrDefault()
            ?? throw new InvalidOperationException("The fixture method should contain a compiler-generated local.");

        using var context = new ILContext(method);
        context.Invoke(il =>
        {
            var match = il.Match(Cil.Value(() => P.Arg<int>(0))).Single();
            match.Before(il).Call((Func<int>)Ops.FortyTwo).StoreLocal(temp);
        });

        Assert.Equal(Code.Call, method.Body.Instructions[0].OpCode.Code);
        Assert.True(method.Body.Instructions[1].OpCode.Code is Code.Stloc or Code.Stloc_S,
            "An explicitly selected local destination must consume the callback result.");
        Assert.True(!HasVerificationErrors(method), "A stored plain-call result must not disturb the original stack contract.");
    }

    [Fact]
    public void MonoModConditionTransformProducesValidIL()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "ConditionTransform");

        using var context = new ILContext(method);
        context.Invoke(il =>
        {
            var pattern = Cil.Condition(() =>
                P.Mark("ab", Ops.CallA() && P.Arg<B>(0).CallB()) && (Ops.CallC() || Ops.CallD()));
            var condition = il.Match(pattern).Single().Condition("ab");
            condition.Transform(il, (Func<bool, bool>)Ops.IdentityBool);
        });

        var callbackCalls = method.Body.Instructions.Count(static instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.IdentityBool) });
        Assert.Equal(1, callbackCalls);
        Assert.True(!HasVerificationErrors(method), "The rewritten short-circuit condition must remain valid IL.");
    }

    [Fact]
    public void BeforeInsertionBeforeFallThroughValuePreservesBranchTarget()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Select");
        var touch = FixtureMethod(module, "Touch");
        var match = method.Match(Cil.Value(() => P.Arg<int>(1))).Single();
        var anchor = match.Value().FirstInstruction;
        var branch = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code is Code.Brfalse or Code.Brfalse_S);
        var originalBranchTarget = Assert.IsType<Instruction>(branch.Operand);

        match.BeforeEvaluation().CallVoid(touch);

        Assert.Equal(Code.Brfalse, branch.OpCode.Code);
        Assert.Same(originalBranchTarget, branch.Operand);
        var insertedCall = anchor.Previous;
        Assert.NotNull(insertedCall);
        Assert.Equal(Code.Call, insertedCall.OpCode.Code);
        Assert.Same(anchor, insertedCall!.Next);
    }

    private static bool HasVerificationErrors(MethodDefinition method)
    {
        var analyzer = new ILMethodVerifier(method, VerifyOptions.Full);
        analyzer.Verify();
        var errors = analyzer.Diagnostics
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Fatal)
            .ToArray();
        if (errors.Length != 0)
        {
            Console.WriteLine($"Verification failed for {method.FullName}:");
            foreach (var instruction in method.Body.Instructions)
                Console.WriteLine($"  {instruction}");
            foreach (var error in errors)
                Console.WriteLine($"  {error}");
        }
        return errors.Length != 0;
    }

    private static ModuleDefinition OpenFixtureModule()
        => PatternTestModules.Open("PatternFixtures");

    private static MethodDefinition FixtureMethod(ModuleDefinition module, string name)
    {
        var matches = module.RequireType(TargetType).Methods
            .Where(method => method.Name == name)
            .ToArray();
        return Assert.Single(matches);
    }

    private static void AssertCallTo(Instruction? instruction, string methodName)
    {
        Assert.NotNull(instruction);
        Assert.True(instruction!.OpCode.Code is Code.Call or Code.Callvirt,
            $"Expected a call instruction, got {instruction.OpCode.Code}.");
        var method = Assert.IsType<MethodReference>(instruction.Operand);
        Assert.Equal(methodName, method.Name);
    }
}

public sealed class A
{
    public B B() => throw new NotSupportedException();
}

public class B
{
    public C C() => throw new NotSupportedException();
    public C D() => throw new NotSupportedException();
    public int Select(int value) => throw new NotSupportedException();
    public int Select(string value) => throw new NotSupportedException();
    public bool CallB() => throw new NotSupportedException();
}

public sealed class C { }

public static class Ops
{
    public static B IdentityB(B value) => value;
    public static bool IdentityBool(bool value) => value;
    public static void ObserveB(B value) { }
    public static int FortyTwo() => 42;
    public static bool CallA() => throw new NotSupportedException();
    public static bool CallC() => throw new NotSupportedException();
    public static bool CallD() => throw new NotSupportedException();
    public static bool XXX() => throw new NotSupportedException();
    public static void ConsumeInt(int value) { }
}
