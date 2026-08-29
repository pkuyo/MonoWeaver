using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Cecil;
using MonoWeaver.Patterns;
using MonoWeaver.Utils;
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

        var hookFragment = Cil.Value(() => P.Arg<A>(0).B());
        var pattern = Cil.Value(() => hookFragment.Value.C());
        var match = PatternMatcher.For(method).Find(pattern).Single();
        var hook = match[hookFragment];

        AssertCallTo(hook.DefinitionInstruction, nameof(A.B));
        Assert.Same(hook.DefinitionInstruction, hook.ResultInstruction);
    }
    [Fact]
    public void MatchesComplexShortCircuitAndCapturesSubcondition()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Condition");

        var abFragment = Cil.Condition(() => Ops.CallA() && P.Arg<B>(0).CallB());
        var pattern = Cil.Condition(() => abFragment && (Ops.CallC() || Ops.CallD()));

        var match = PatternMatcher.For(method).Find(pattern).Single();
        var ab = match[abFragment];
        Assert.True(ab.Fragment.TrueExits.Count == 1, "A && B has one true exit into the remaining condition.");
        Assert.True(ab.Fragment.FalseExits.Count == 2, "A && B has two short-circuit false exits.");
        Assert.True(ab.CanRewrite, ab.RewriteFailureReason ?? "The captured condition should be rewritable.");
    }


    [Fact]
    public void AmbiguousInnerPatternIsRejected()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Ambiguous");

        var matches = PatternMatcher.For(method).Find(Cil.Value(() => P.Arg<A>(0).B()));
        Assert.True(matches.Count == 2, "Both B() occurrences must remain visible to the matcher.");
        Assert.Throws<CilPatternMatchException>(() => matches.Single());
    }

    [Fact]
    public void OuterCallDisambiguatesRepeatedInnerCall()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Context");

        var hookFragment = Cil.Value(() => P.Arg<A>(0).B());
        var pattern = Cil.Value(() => hookFragment.Value.D());
        var hook = PatternMatcher.For(method).Find(pattern).Single()[hookFragment];

        AssertCallTo(hook.DefinitionInstruction, nameof(A.B));
        AssertCallTo(hook.ResultInstruction.Next, nameof(B.D));
    }

    [Fact]
    public void ExactOverloadIsRequired()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Overloads");

        var match = PatternMatcher.For(method)
            .Find(Cil.Value(() => P.Arg<B>(0).Select("selected")))
            .Single();

        var operand = Assert.IsType<MethodReference>(match.DefinitionInstruction.Operand);
        Assert.Equal(nameof(B.Select), operand.Name);
        Assert.True(operand.Parameters.Single().ParameterType.MetadataType == MetadataType.String,
            "Method matching must include the exact overload signature and literal argument.");
    }

    [Fact]
    public void StackTypeAllowsAssignableArgumentPattern()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "AssignableArgument");

        var valueLeaf = Cil.Arg<object>(0);
        var match = PatternMatcher.For(method)
            .Find(Cil.Value(() => valueLeaf.Value))
            .Single();
        var value = match[valueLeaf];

        Assert.True(value.ParameterIndex == 0, "ParameterDefinition operands must keep explicit parameter indexes.");
        Assert.True(value.DefinitionInstruction.OpCode.Code is Code.Ldarg or Code.Ldarg_S or Code.Ldarg_0,
            "A reference-assignable argument should match through StackType compatibility.");
    }

    [Fact]
    public void ConstantTypesAreNotInterchanged()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Constants");

        var match = PatternMatcher.For(method).Find(Cil.Value(() => 1.0)).Single();
        Assert.Equal(Code.Ldc_R8, match.DefinitionInstruction.OpCode.Code);
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

        var relaxed = PatternMatcher.For(method).Find(pattern);
        Assert.True(relaxed.Count == 1,
            "The default mode should tolerate call/callvirt lowering differences for the same method.");

        var strictOptions = new PatternOptions { IgnoreCallOpcodeDifference = false };
        var strict = PatternMatcher.For(method).Find(Cil.Value(P.This(directCaller).Call(baseCall), strictOptions));
        Assert.True(strict.Count == 0,
            "Strict call matching must reject an instance call opcode different from the Lambda lowering contract.");
    }

    [Fact]
    public void MultipleReachingDefinitionsRejectLocalConstraint()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "MultipleDefinitions");

        var ret = Cil.Local(Cil.Value(() => Ops.XXX()));
        var pattern = Cil.Condition(() => ret.Value);
        var matches = PatternMatcher.For(method).Find(pattern);

        Assert.True(matches.Count == 0,
            "A local-definition constraint must reject a load reached by more than one store.");
    }

    [Fact]
    public void EffectPatternRequiresDiscardedResult()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Discarded");

        var match = PatternMatcher.For(method)
            .Find(Cil.Effect(() => P.Arg<A>(0).B()))
            .Single();

        Assert.Equal(Code.Pop, match.LastInstruction.OpCode.Code);
    }

    [Fact]
    public void MatchesNewArrayCreation()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "NewIntArray");

        var match = PatternMatcher.For(method)
            .Find(Cil.Value(() => new int[P.Arg<int>(0)]))
            .Single();

        Assert.Equal(Code.Newarr, match.DefinitionInstruction.OpCode.Code);
    }

    [Fact]
    public void MatchesArrayElementLoad()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "LoadIntElement");

        var match = PatternMatcher.For(method)
            .Find(Cil.Value(() => P.Arg<int[]>(0)[1]))
            .Single();

        Assert.True(match.DefinitionInstruction.OpCode.Code is Code.Ldelem_I4 or Code.Ldelem_Any,
            "ldelem should be modeled as a matchable array element read.");
    }

    [Fact]
    public void MatchesArrayLength()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Length");

        var match = PatternMatcher.For(method)
            .Find(Cil.Value(() => P.Arg<int[]>(0).Length))
            .Single();

        Assert.Equal(Code.Ldlen, match.DefinitionInstruction.OpCode.Code);
    }

    [Fact]
    public void MatchesArrayElementStoreEffect()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "StoreIntElement");

        var match = PatternMatcher.For(method)
            .Find(Cil.Effect(() => P.StoreElement(P.Arg<int[]>(0), 1, P.Arg<int>(1))))
            .Single();

        Assert.True(match.LastInstruction.OpCode.Code is Code.Stelem_I4 or Code.Stelem_Any,
            "stelem should be modeled as a matchable array element store effect.");
    }

    [Fact]
    public void TransformLeavesReplacementOnStack()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "ChainTransform");

        var hookFragment = Cil.Value(() => P.Arg<A>(0).B());
        var pattern = Cil.Value(() => hookFragment.Value.C());
        var hook = method.Match(pattern).Single()[hookFragment];
        hook.Transform((Func<B, B>)Ops.IdentityB).Apply();

        var callbackCall = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.IdentityB) });
        AssertCallTo(callbackCall.Previous, nameof(A.B));
        AssertCallTo(callbackCall.Next, nameof(B.C));
        Assert.True(!HasVerificationErrors(method), "The transformed call chain must remain valid IL.");
    }

    [Fact]
    public void ObservePreservesOriginalValue()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Observe");

        var hookFragment = Cil.Value(() => P.Arg<A>(0).B());
        var hook = method.Match(Cil.Value(() => hookFragment.Value.C()))
            .Single()[hookFragment];
        hook.Observe((Action<B>)Ops.ObserveB).Apply();

        var duplicate = method.Body.Instructions.Single(instruction => instruction.OpCode.Code == Code.Dup);
        AssertCallTo(duplicate.Previous, nameof(A.B));
        AssertCallTo(duplicate.Next, nameof(Ops.ObserveB));
        Assert.True(!HasVerificationErrors(method), "Observe must leave the original value for C().");
    }


    [Fact]
    public void ConditionTransformProducesValidIL()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "ConditionTransform");

        var abFragment = Cil.Condition(() => Ops.CallA() && P.Arg<B>(0).CallB());
        var pattern = Cil.Condition(() => abFragment && (Ops.CallC() || Ops.CallD()));
        var condition = method.Match(pattern).Single()[abFragment];
        condition.Transform((Func<bool, bool>)Ops.IdentityBool).Apply();

        var callbackCalls = method.Body.Instructions.Count(static instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.IdentityBool) });
        Assert.Equal(condition.Fragment.TrueExits.Count + condition.Fragment.FalseExits.Count, callbackCalls);
        Assert.True(!HasVerificationErrors(method), "The rewritten short-circuit condition must remain valid IL.");
    }

    [Fact]
    public void BeforeInsertionBeforeFallThroughValuePreservesBranchTarget()
    {
        using var module = OpenFixtureModule();
        var method = FixtureMethod(module, "Select");
        var touch = FixtureMethod(module, "Touch");
        var match = method.Match(Cil.Value(() => P.Arg<int>(1))).Single();
        var anchor = match.FirstInstruction;
        var branch = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code is Code.Brfalse or Code.Brfalse_S);
        var originalBranchTarget = Assert.IsType<Instruction>(branch.Operand);

        match.Before(touch).Apply();

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
