using System;
using System.Reflection;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.Patterns;
using MonoWeaver.MonoMod.Patterns;
using MonoMod.Cil;
using MonoWeaver.CFG;

namespace MonoWeaver.PatternTests;

internal static class Program
{
    private static int _passed;

    public static void Main()
    {
        Run(nameof(MatchesNestedCallAndMarkedSubexpression), MatchesNestedCallAndMarkedSubexpression);
        Run(nameof(UsesLoadSiteWhenCompilerTemporaryIsTransparent), UsesLoadSiteWhenCompilerTemporaryIsTransparent);
        Run(nameof(MatchesComplexShortCircuitAndCapturesSubcondition), MatchesComplexShortCircuitAndCapturesSubcondition);
        Run(nameof(LocalDefinitionConstraintDisambiguatesBooleanLocal), LocalDefinitionConstraintDisambiguatesBooleanLocal);
        Run(nameof(AmbiguousInnerPatternIsRejected), AmbiguousInnerPatternIsRejected);
        Run(nameof(OuterCallDisambiguatesRepeatedInnerCall), OuterCallDisambiguatesRepeatedInnerCall);
        Run(nameof(ExactOverloadIsRequired), ExactOverloadIsRequired);
        Run(nameof(StackTypeAllowsAssignableArgumentPattern), StackTypeAllowsAssignableArgumentPattern);
        Run(nameof(ConstantTypesAreNotInterchanged), ConstantTypesAreNotInterchanged);
        Run(nameof(CallOpcodeDifferenceCanBeMadeStrict), CallOpcodeDifferenceCanBeMadeStrict);
        Run(nameof(InvertedShortCircuitBranchLayoutMatches), InvertedShortCircuitBranchLayoutMatches);
        Run(nameof(MultipleReachingDefinitionsRejectLocalConstraint), MultipleReachingDefinitionsRejectLocalConstraint);
        Run(nameof(EffectPatternRequiresDiscardedResult), EffectPatternRequiresDiscardedResult);
        Run(nameof(MonoModTransformLeavesReplacementOnStack), MonoModTransformLeavesReplacementOnStack);
        Run(nameof(MonoModObservePreservesOriginalValue), MonoModObservePreservesOriginalValue);
        Run(nameof(PlainInsertionCallCanStoreResult), PlainInsertionCallCanStoreResult);
        Run(nameof(MonoModConditionTransformProducesValidIL), MonoModConditionTransformProducesValidIL);
        Console.WriteLine($"All {_passed} MonoWeaver pattern tests passed.");
    }

    private static void Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }

    private static void MatchesNestedCallAndMarkedSubexpression()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "Chain", module.ImportReference(typeof(C)), typeof(A));
        var il = method.Body.GetILProcessor();
        var bCall = Instruction.Create(OpCodes.Callvirt, module.ImportReference(Method(nameof(A.B), typeof(A))));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(bCall);
        il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(Method(nameof(B.C), typeof(B)))));
        il.Append(Instruction.Create(OpCodes.Ret));

        var pattern = Cil.Value(() => P.Mark("hook", P.Arg<A>(0).B()).C());
        var match = CilMatcher.For(method).Find(pattern).Single();
        var hook = match.Value("hook");

        Assert(ReferenceEquals(hook.ProducerInstruction, bCall), "The marked producer must be A.B().");
        Assert(ReferenceEquals(hook.AfterUseInstruction, bCall), "A direct chain must insert immediately after A.B().");
    }

    private static void UsesLoadSiteWhenCompilerTemporaryIsTransparent()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "Temporary", module.ImportReference(typeof(C)), typeof(A));
        var local = new VariableDefinition(module.ImportReference(typeof(B)));
        method.Body.Variables.Add(local);
        method.Body.InitLocals = true;
        var il = method.Body.GetILProcessor();
        var bCall = Instruction.Create(OpCodes.Callvirt, module.ImportReference(Method(nameof(A.B), typeof(A))));
        var load = Instruction.Create(OpCodes.Ldloc, local);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(bCall);
        il.Append(Instruction.Create(OpCodes.Stloc, local));
        il.Append(load);
        il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(Method(nameof(B.C), typeof(B)))));
        il.Append(Instruction.Create(OpCodes.Ret));

        var pattern = Cil.Value(() => P.Mark("hook", P.Arg<A>(0).B()).C());
        var hook = CilMatcher.For(method).Find(pattern).Single().Value("hook");

        Assert(ReferenceEquals(hook.ProducerInstruction, bCall), "Producer identity must survive a transparent local.");
        Assert(ReferenceEquals(hook.AfterUseInstruction, load), "AfterUse must target the concrete ldloc consumed by C().");
    }

    private static void MatchesComplexShortCircuitAndCapturesSubcondition()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "Condition", module.TypeSystem.Boolean, typeof(B));
        var il = method.Body.GetILProcessor();

        var falseLabel = Instruction.Create(OpCodes.Ldc_I4_0);
        var trueLabel = Instruction.Create(OpCodes.Ldc_I4_1);

        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(Method(nameof(Ops.CallA), typeof(Ops)))));
        il.Append(Instruction.Create(OpCodes.Brfalse, falseLabel));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(Method(nameof(B.CallB), typeof(B)))));
        il.Append(Instruction.Create(OpCodes.Brfalse, falseLabel));
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(Method(nameof(Ops.CallC), typeof(Ops)))));
        il.Append(Instruction.Create(OpCodes.Brtrue, trueLabel));
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(Method(nameof(Ops.CallD), typeof(Ops)))));
        il.Append(Instruction.Create(OpCodes.Brfalse, falseLabel));
        il.Append(trueLabel);
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(falseLabel);
        il.Append(Instruction.Create(OpCodes.Ret));

        var pattern = Cil.Condition(() =>
            P.Mark("ab", Ops.CallA() && P.Arg<B>(0).CallB())
            && (Ops.CallC() || Ops.CallD()));

        var match = CilMatcher.For(method).Find(pattern).Single();
        var ab = match.Condition("ab");
        Assert(ab.TrueExits.Count == 1, "A && B has one true exit into the remaining condition.");
        Assert(ab.FalseExits.Count == 2, "A && B has two short-circuit false exits.");
        Assert(ab.CanRewrite, ab.RewriteFailureReason ?? "The captured condition should be rewritable.");
    }

    private static void LocalDefinitionConstraintDisambiguatesBooleanLocal()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "LocalCondition", module.TypeSystem.Boolean);
        var local = new VariableDefinition(module.TypeSystem.Boolean);
        method.Body.Variables.Add(local);
        method.Body.InitLocals = true;
        var il = method.Body.GetILProcessor();
        var falseLabel = Instruction.Create(OpCodes.Ldc_I4_0);

        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(Method(nameof(Ops.XXX), typeof(Ops)))));
        il.Append(Instruction.Create(OpCodes.Stloc, local));
        il.Append(Instruction.Create(OpCodes.Ldloc, local));
        il.Append(Instruction.Create(OpCodes.Brfalse, falseLabel));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(falseLabel);
        il.Append(Instruction.Create(OpCodes.Ret));

        var pattern = Cil.Condition(() => P.Local<bool>("ret"))
            .LocalDefinedBy("ret", Cil.Value(() => Ops.XXX()));

        var match = CilMatcher.For(method).Find(pattern).Single();
        Assert(match.Local("ret").Variable.Index == 0, "The unique XXX() definition should identify V_0.");
    }



    private static void AmbiguousInnerPatternIsRejected()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "Ambiguous", module.ImportReference(typeof(B)), typeof(A));
        var il = method.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(Method(nameof(A.B), typeof(A)))));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(Method(nameof(A.B), typeof(A)))));
        il.Append(Instruction.Create(OpCodes.Ret));

        var matches = CilMatcher.For(method).Find(Cil.Value(() => P.Arg<A>(0).B()));
        Assert(matches.Count == 2, "Both B() occurrences must remain visible to the matcher.");
        AssertThrows<CilPatternMatchException>(() => matches.Single(),
            "Single() must reject an ambiguous insertion point instead of choosing the first call.");
    }

    private static void OuterCallDisambiguatesRepeatedInnerCall()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "Context", module.ImportReference(typeof(C)), typeof(A));
        var il = method.Body.GetILProcessor();
        var firstB = Instruction.Create(OpCodes.Callvirt, module.ImportReference(Method(nameof(A.B), typeof(A))));
        var secondB = Instruction.Create(OpCodes.Callvirt, module.ImportReference(Method(nameof(A.B), typeof(A))));

        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(firstB);
        il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(Method(nameof(B.C), typeof(B)))));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(secondB);
        il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(Method(nameof(B.D), typeof(B)))));
        il.Append(Instruction.Create(OpCodes.Ret));

        var pattern = Cil.Value(() => P.Mark("hook", P.Arg<A>(0).B()).D());
        var hook = CilMatcher.For(method).Find(pattern).Single().Value("hook");
        Assert(ReferenceEquals(hook.ProducerInstruction, secondB),
            "The enclosing D() call must select only the B() occurrence consumed by D().");
    }

    private static void ExactOverloadIsRequired()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "Overloads", module.TypeSystem.Int32, typeof(B));
        var il = method.Body.GetILProcessor();
        var intCall = Instruction.Create(OpCodes.Callvirt,
            module.ImportReference(Method(nameof(B.Select), typeof(B), typeof(int))));
        var stringCall = Instruction.Create(OpCodes.Callvirt,
            module.ImportReference(Method(nameof(B.Select), typeof(B), typeof(string))));

        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(intCall);
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldstr, "selected"));
        il.Append(stringCall);
        il.Append(Instruction.Create(OpCodes.Ret));

        var match = CilMatcher.For(method)
            .Find(Cil.Value(() => P.Arg<B>(0).Select("selected")))
            .Single();
        Assert(ReferenceEquals(match.Value().ProducerInstruction, stringCall),
            "Method matching must include the exact overload signature and literal argument.");
    }

    private static void StackTypeAllowsAssignableArgumentPattern()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "AssignableArgument",
            module.ImportReference(typeof(string)), typeof(string));
        var il = method.Body.GetILProcessor();
        var load = Instruction.Create(OpCodes.Ldarg, method.Parameters[0]);

        il.Append(load);
        il.Append(Instruction.Create(OpCodes.Ret));

        var match = CilMatcher.For(method)
            .Find(Cil.Value(() => P.Arg<object>(0, "value")))
            .Single();
        var value = match.Argument("value");

        Assert(value.ParameterIndex == 0, "ParameterDefinition operands must keep explicit parameter indexes.");
        Assert(ReferenceEquals(value.ProducerInstruction, load),
            "A reference-assignable argument should match through StackType compatibility.");
    }

    private static void ConstantTypesAreNotInterchanged()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "Constants", module.TypeSystem.Double);
        var il = method.Body.GetILProcessor();
        var intConstant = Instruction.Create(OpCodes.Ldc_I4_1);
        var doubleConstant = Instruction.Create(OpCodes.Ldc_R8, 1.0);

        il.Append(intConstant);
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(doubleConstant);
        il.Append(Instruction.Create(OpCodes.Ret));

        var match = CilMatcher.For(method).Find(Cil.Value(() => 1.0)).Single();
        Assert(ReferenceEquals(match.Value().ProducerInstruction, doubleConstant),
            "A Double literal must not match an equal-valued Int32 constant.");
    }

    private static void CallOpcodeDifferenceCanBeMadeStrict()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "CallOpcode", module.ImportReference(typeof(C)), typeof(B));
        var il = method.Body.GetILProcessor();
        var directCall = Instruction.Create(OpCodes.Call,
            module.ImportReference(Method(nameof(B.C), typeof(B))));

        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(directCall);
        il.Append(Instruction.Create(OpCodes.Ret));

        var relaxed = CilMatcher.For(method)
            .Find(Cil.Value(() => P.Arg<B>(0).C()));
        Assert(relaxed.Count == 1,
            "The default mode should tolerate call/callvirt lowering differences for the same method.");

        var strictOptions = new CilPatternOptions { IgnoreCallOpcodeDifference = false };
        var strict = CilMatcher.For(method)
            .Find(Cil.Value(() => P.Arg<B>(0).C(), strictOptions));
        Assert(strict.Count == 0,
            "Strict call matching must reject an instance call opcode different from the Lambda lowering contract.");
    }

    private static void InvertedShortCircuitBranchLayoutMatches()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "InvertedCondition", module.TypeSystem.Boolean, typeof(B));
        var il = method.Body.GetILProcessor();
        var checkB = Instruction.Create(OpCodes.Ldarg_0);
        var trueLabel = Instruction.Create(OpCodes.Ldc_I4_1);
        var falseLabel = Instruction.Create(OpCodes.Ldc_I4_0);
        var falseBridge1 = Instruction.Create(OpCodes.Br, falseLabel);
        var falseBridge2 = Instruction.Create(OpCodes.Br, falseLabel);

        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(Method(nameof(Ops.CallA), typeof(Ops)))));
        il.Append(Instruction.Create(OpCodes.Brtrue, checkB));
        il.Append(falseBridge1);
        il.Append(checkB);
        il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(Method(nameof(B.CallB), typeof(B)))));
        il.Append(Instruction.Create(OpCodes.Brtrue, trueLabel));
        il.Append(falseBridge2);
        il.Append(trueLabel);
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(falseLabel);
        il.Append(Instruction.Create(OpCodes.Ret));

        var match = CilMatcher.For(method)
            .Find(Cil.Condition(() => Ops.CallA() && P.Arg<B>(0).CallB()))
            .Single();
        var condition = match.Condition();
        Assert(condition.TrueExits.Count == 1 && condition.FalseExits.Count == 2,
            "The condition graph must ignore brtrue/brfalse polarity and transparent branch trampolines.");
    }

    private static void MultipleReachingDefinitionsRejectLocalConstraint()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "MultipleDefinitions", module.TypeSystem.Boolean, typeof(bool));
        var ret = new VariableDefinition(module.TypeSystem.Boolean);
        method.Body.Variables.Add(ret);
        method.Body.InitLocals = true;
        var il = method.Body.GetILProcessor();
        var elseLabel = Instruction.Create(OpCodes.Call, module.ImportReference(Method(nameof(Ops.CallA), typeof(Ops))));
        var join = Instruction.Create(OpCodes.Ldloc, ret);
        var falseLabel = Instruction.Create(OpCodes.Ldc_I4_0);

        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Brfalse, elseLabel));
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(Method(nameof(Ops.XXX), typeof(Ops)))));
        il.Append(Instruction.Create(OpCodes.Stloc, ret));
        il.Append(Instruction.Create(OpCodes.Br, join));
        il.Append(elseLabel);
        il.Append(Instruction.Create(OpCodes.Stloc, ret));
        il.Append(join);
        il.Append(Instruction.Create(OpCodes.Brfalse, falseLabel));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(falseLabel);
        il.Append(Instruction.Create(OpCodes.Ret));

        var pattern = Cil.Condition(() => P.Local<bool>("ret"))
            .LocalDefinedBy("ret", Cil.Value(() => Ops.XXX()));
        var matches = CilMatcher.For(method).Find(pattern);
        Assert(matches.Count == 0,
            "A local-definition constraint must reject a load reached by more than one store.");
    }

    private static void EffectPatternRequiresDiscardedResult()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "Discarded", module.TypeSystem.Void, typeof(A));
        var il = method.Body.GetILProcessor();
        var pop = Instruction.Create(OpCodes.Pop);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(Method(nameof(A.B), typeof(A)))));
        il.Append(pop);
        il.Append(Instruction.Create(OpCodes.Ret));

        var match = CilMatcher.For(method)
            .Find(Cil.Effect(() => P.Arg<A>(0).B()))
            .Single();
        Assert(ReferenceEquals(match.LastInstruction, pop),
            "A non-void Effect pattern must include the concrete pop that discards its result.");
    }

    private static void MonoModTransformLeavesReplacementOnStack()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "ChainTransform", module.ImportReference(typeof(C)), typeof(A));
        var ilp = method.Body.GetILProcessor();
        var bCall = Instruction.Create(OpCodes.Callvirt, module.ImportReference(Method(nameof(A.B), typeof(A))));
        var cCall = Instruction.Create(OpCodes.Callvirt, module.ImportReference(Method(nameof(B.C), typeof(B))));
        ilp.Append(Instruction.Create(OpCodes.Ldarg_0));
        ilp.Append(bCall);
        ilp.Append(cCall);
        ilp.Append(Instruction.Create(OpCodes.Ret));

        using var context = new ILContext(method);
        context.Invoke(il =>
        {
            var pattern = Cil.Value(() => P.Mark("hook", P.Arg<A>(0).B()).C());
            var hook = il.Match(pattern).Single().Value("hook");
            hook.AfterUse(il).Transform((Func<B, B>)Ops.IdentityB).LeaveOnStack();
        });

        var bIndex = method.Body.Instructions.IndexOf(bCall);
        var cIndex = method.Body.Instructions.IndexOf(cCall);
        Assert(cIndex == bIndex + 2, "Transform must insert exactly one delegate call between B() and C().");
        Assert(method.Body.Instructions[bIndex + 1].OpCode.Code == Code.Call,
            "A static Transform callback should be emitted as a call.");
        Assert(!HasVerificationErrors(method), "The transformed call chain must remain valid IL.");
    }


    private static void MonoModObservePreservesOriginalValue()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "Observe", module.ImportReference(typeof(C)), typeof(A));
        var ilp = method.Body.GetILProcessor();
        var bCall = Instruction.Create(OpCodes.Callvirt, module.ImportReference(Method(nameof(A.B), typeof(A))));
        var cCall = Instruction.Create(OpCodes.Callvirt, module.ImportReference(Method(nameof(B.C), typeof(B))));
        ilp.Append(Instruction.Create(OpCodes.Ldarg_0));
        ilp.Append(bCall);
        ilp.Append(cCall);
        ilp.Append(Instruction.Create(OpCodes.Ret));

        using var context = new ILContext(method);
        context.Invoke(il =>
        {
            var hook = il.Match(Cil.Value(() => P.Mark("hook", P.Arg<A>(0).B()).C()))
                .Single().Value("hook");
            hook.AfterUse(il).Observe((Action<B>)Ops.ObserveB);
        });

        var bIndex = method.Body.Instructions.IndexOf(bCall);
        Assert(method.Body.Instructions[bIndex + 1].OpCode.Code == Code.Dup,
            "Observe must duplicate the matched value before passing it to a void callback.");
        Assert(!HasVerificationErrors(method), "Observe must leave the original value for C().");
    }

    private static void PlainInsertionCallCanStoreResult()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "BeforeExpression", module.TypeSystem.Int32, typeof(int));
        var temp = new VariableDefinition(module.TypeSystem.Int32);
        method.Body.Variables.Add(temp);
        method.Body.InitLocals = true;
        var ilp = method.Body.GetILProcessor();
        var loadArg = Instruction.Create(OpCodes.Ldarg_0);
        ilp.Append(loadArg);
        ilp.Append(Instruction.Create(OpCodes.Ret));

        using var context = new ILContext(method);
        context.Invoke(il =>
        {
            var match = il.Match(Cil.Value(() => P.Arg<int>(0))).Single();
            match.Before(il).Call((Func<int>)Ops.FortyTwo).StoreLocal(temp);
        });

        Assert(method.Body.Instructions[0].OpCode.Code == Code.Call,
            "A plain before-site call should be emitted before expression evaluation.");
        Assert(method.Body.Instructions[1].OpCode.Code is Code.Stloc or Code.Stloc_S,
            "An explicitly selected local destination must consume the callback result.");
        Assert(!HasVerificationErrors(method), "A stored plain-call result must not disturb the original stack contract.");
    }

    private static void MonoModConditionTransformProducesValidIL()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "ConditionTransform", module.TypeSystem.Boolean, typeof(B));
        var ilp = method.Body.GetILProcessor();
        var falseLabel = Instruction.Create(OpCodes.Ldc_I4_0);
        var trueLabel = Instruction.Create(OpCodes.Ldc_I4_1);

        ilp.Append(Instruction.Create(OpCodes.Call, module.ImportReference(Method(nameof(Ops.CallA), typeof(Ops)))));
        ilp.Append(Instruction.Create(OpCodes.Brfalse, falseLabel));
        ilp.Append(Instruction.Create(OpCodes.Ldarg_0));
        ilp.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(Method(nameof(B.CallB), typeof(B)))));
        ilp.Append(Instruction.Create(OpCodes.Brfalse, falseLabel));
        ilp.Append(Instruction.Create(OpCodes.Call, module.ImportReference(Method(nameof(Ops.CallC), typeof(Ops)))));
        ilp.Append(Instruction.Create(OpCodes.Brtrue, trueLabel));
        ilp.Append(Instruction.Create(OpCodes.Call, module.ImportReference(Method(nameof(Ops.CallD), typeof(Ops)))));
        ilp.Append(Instruction.Create(OpCodes.Brfalse, falseLabel));
        ilp.Append(trueLabel);
        ilp.Append(Instruction.Create(OpCodes.Ret));
        ilp.Append(falseLabel);
        ilp.Append(Instruction.Create(OpCodes.Ret));

        using var context = new ILContext(method);
        context.Invoke(il =>
        {
            var pattern = Cil.Condition(() =>
                P.Mark("ab", Ops.CallA() && P.Arg<B>(0).CallB())
                && (Ops.CallC() || Ops.CallD()));
            var condition = il.Match(pattern).Single().Condition("ab");
            condition.Transform(il, (Func<bool, bool>)Ops.IdentityBool);
        });

        Assert(method.Body.Instructions.Count > 12, "Condition transform should insert bridge instructions.");
        Assert(!HasVerificationErrors(method), "The rewritten short-circuit condition must remain valid IL.");
    }

    private static bool HasVerificationErrors(MethodDefinition method)
    {
        var analyzer = new ILMethodVerifier(method, VerifyOptions.Full);
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


    private static ModuleDefinition CreateTestModule()
    {
        // verifier 会解析 imported callback 和 model method。让 Cecil 指向 test output directory，
        // 而不是依赖 process working directory。
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(AppContext.BaseDirectory);

        var runtimeDirectory = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (!string.IsNullOrEmpty(runtimeDirectory))
            resolver.AddSearchDirectory(runtimeDirectory);

        return ModuleDefinition.CreateModule("PatternTests", new ModuleParameters
        {
            Kind = ModuleKind.Dll,
            AssemblyResolver = resolver,
        });
    }

    private static MethodDefinition CreateStaticMethod(ModuleDefinition module, string name,
        TypeReference returnType, params Type[] parameterTypes)
    {
        var type = new TypeDefinition("Tests", "Target", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract | Mono.Cecil.TypeAttributes.Sealed,
            module.TypeSystem.Object);
        module.Types.Add(type);
        var method = new MethodDefinition(name, Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, returnType);
        method.Body.MaxStackSize = 8;
        foreach (var parameterType in parameterTypes)
            method.Parameters.Add(new ParameterDefinition(module.ImportReference(parameterType)));
        type.Methods.Add(method);
        return method;
    }

    private static MethodInfo Method(string name, Type declaringType, params Type[] parameterTypes)
        => declaringType.GetMethod(name,
               BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance,
               binder: null, parameterTypes, modifiers: null)
           ?? throw new MissingMethodException(declaringType.FullName, name);

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

public sealed class A
{
    public B B() => throw new NotSupportedException();
}

public sealed class B
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
}
