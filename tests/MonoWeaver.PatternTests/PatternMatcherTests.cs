using System;
using System.Reflection;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.Patterns;
using MonoWeaver.MonoMod.Patterns;
using MonoMod.Cil;
using MonoWeaver.CFG;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class PatternMatcherTests
{
    [Fact]
    public void MatchesNestedCallAndMarkedSubexpression()
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

        Assert.True(ReferenceEquals(hook.ProducerInstruction, bCall), "The marked producer must be A.B().");
        Assert.True(ReferenceEquals(hook.AfterUseInstruction, bCall), "A direct chain must insert immediately after A.B().");
    }

    [Fact]
    public void UsesLoadSiteWhenCompilerTemporaryIsTransparent()
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

        Assert.True(ReferenceEquals(hook.ProducerInstruction, bCall), "Producer identity must survive a transparent local.");
        Assert.True(ReferenceEquals(hook.AfterUseInstruction, load), "AfterUse must target the concrete ldloc consumed by C().");
    }

    [Fact]
    public void MatchesComplexShortCircuitAndCapturesSubcondition()
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
        Assert.True(ab.TrueExits.Count == 1, "A && B has one true exit into the remaining condition.");
        Assert.True(ab.FalseExits.Count == 2, "A && B has two short-circuit false exits.");
        Assert.True(ab.CanRewrite, ab.RewriteFailureReason ?? "The captured condition should be rewritable.");
    }

    [Fact]
    public void LocalDefinitionConstraintDisambiguatesBooleanLocal()
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
        Assert.True(match.Local("ret").Variable.Index == 0, "The unique XXX() definition should identify V_0.");
    }



    [Fact]
    public void AmbiguousInnerPatternIsRejected()
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
        Assert.True(matches.Count == 2, "Both B() occurrences must remain visible to the matcher.");
        Assert.Throws<CilPatternMatchException>(() => matches.Single());
    }

    [Fact]
    public void OuterCallDisambiguatesRepeatedInnerCall()
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
        Assert.True(ReferenceEquals(hook.ProducerInstruction, secondB),
            "The enclosing D() call must select only the B() occurrence consumed by D().");
    }

    [Fact]
    public void ExactOverloadIsRequired()
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
        Assert.True(ReferenceEquals(match.Value().ProducerInstruction, stringCall),
            "Method matching must include the exact overload signature and literal argument.");
    }

    [Fact]
    public void StackTypeAllowsAssignableArgumentPattern()
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

        Assert.True(value.ParameterIndex == 0, "ParameterDefinition operands must keep explicit parameter indexes.");
        Assert.True(ReferenceEquals(value.ProducerInstruction, load),
            "A reference-assignable argument should match through StackType compatibility.");
    }

    [Fact]
    public void ConstantTypesAreNotInterchanged()
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
        Assert.True(ReferenceEquals(match.Value().ProducerInstruction, doubleConstant),
            "A Double literal must not match an equal-valued Int32 constant.");
    }

    [Fact]
    public void CallOpcodeDifferenceCanBeMadeStrict()
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
        Assert.True(relaxed.Count == 1,
            "The default mode should tolerate call/callvirt lowering differences for the same method.");

        var strictOptions = new CilPatternOptions { IgnoreCallOpcodeDifference = false };
        var strict = CilMatcher.For(method)
            .Find(Cil.Value(() => P.Arg<B>(0).C(), strictOptions));
        Assert.True(strict.Count == 0,
            "Strict call matching must reject an instance call opcode different from the Lambda lowering contract.");
    }

    [Fact]
    public void InvertedShortCircuitBranchLayoutMatches()
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
        Assert.True(condition.TrueExits.Count == 1 && condition.FalseExits.Count == 2,
            "The condition graph must ignore brtrue/brfalse polarity and transparent branch trampolines.");
    }

    [Fact]
    public void MultipleReachingDefinitionsRejectLocalConstraint()
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
        Assert.True(matches.Count == 0,
            "A local-definition constraint must reject a load reached by more than one store.");
    }

    [Fact]
    public void EffectPatternRequiresDiscardedResult()
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
        Assert.True(ReferenceEquals(match.LastInstruction, pop),
            "A non-void Effect pattern must include the concrete pop that discards its result.");
    }

    [Fact]
    public void MatchesNewArrayCreation()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "NewIntArray", module.ImportReference(typeof(int[])), typeof(int));
        var il = method.Body.GetILProcessor();
        var newarr = Instruction.Create(OpCodes.Newarr, module.TypeSystem.Int32);

        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(newarr);
        il.Append(Instruction.Create(OpCodes.Ret));

        var match = CilMatcher.For(method)
            .Find(Cil.Value(() => new int[P.Arg<int>(0)]))
            .Single();

        Assert.True(ReferenceEquals(match.Value().ProducerInstruction, newarr),
            "newarr should be modeled as a matchable array creation expression.");
    }

    [Fact]
    public void MatchesArrayElementLoad()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "LoadIntElement", module.TypeSystem.Int32, typeof(int[]));
        var il = method.Body.GetILProcessor();
        var ldelem = Instruction.Create(OpCodes.Ldelem_I4);

        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(ldelem);
        il.Append(Instruction.Create(OpCodes.Ret));

        var match = CilMatcher.For(method)
            .Find(Cil.Value(() => P.Arg<int[]>(0)[1]))
            .Single();

        Assert.True(ReferenceEquals(match.Value().ProducerInstruction, ldelem),
            "ldelem should be modeled as a matchable array element read.");
    }

    [Fact]
    public void MatchesArrayLength()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "Length", module.TypeSystem.Int32, typeof(int[]));
        var il = method.Body.GetILProcessor();
        var ldlen = Instruction.Create(OpCodes.Ldlen);

        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(ldlen);
        il.Append(Instruction.Create(OpCodes.Conv_I4));
        il.Append(Instruction.Create(OpCodes.Ret));

        var match = CilMatcher.For(method)
            .Find(Cil.Value(() => P.Arg<int[]>(0).Length))
            .Single();

        Assert.True(ReferenceEquals(match.Value().ProducerInstruction, ldlen),
            "ldlen should be modeled as a matchable array length expression.");
    }

    [Fact]
    public void MatchesArrayElementStoreEffect()
    {
        using var module = CreateTestModule();
        var method = CreateStaticMethod(module, "StoreIntElement", module.TypeSystem.Void, typeof(int[]), typeof(int));
        var il = method.Body.GetILProcessor();
        var stelem = Instruction.Create(OpCodes.Stelem_I4);

        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Ldarg_1));
        il.Append(stelem);
        il.Append(Instruction.Create(OpCodes.Ret));

        var match = CilMatcher.For(method)
            .Find(Cil.Effect(() => P.StoreElement(P.Arg<int[]>(0), 1, P.Arg<int>(1))))
            .Single();

        Assert.True(ReferenceEquals(match.LastInstruction, stelem),
            "stelem should be modeled as a matchable array element store effect.");
    }

    [Fact]
    public void MonoModTransformLeavesReplacementOnStack()
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
            var pattern = Cil.Value(() => P.Mark("hook", P.Arg<A>(0).B()).C()); //匹配 P_0.B().C() 并取 P_0.B()为"hook"
            var hook = il.Match(pattern).Single().Value("hook"); // 匹配实际函数
            hook.AfterUse(il).Transform((Func<B, B>)Ops.IdentityB).LeaveOnStack(); // 调用函数并标记为返回值回到栈内 等价于 (IdentityB(P_0.B()).C())
        });

        var bIndex = method.Body.Instructions.IndexOf(bCall);
        var cIndex = method.Body.Instructions.IndexOf(cCall);
        Assert.True(cIndex == bIndex + 2, "Transform must insert exactly one delegate call between B() and C().");
        Assert.True(method.Body.Instructions[bIndex + 1].OpCode.Code == Code.Call,
            "A static Transform callback should be emitted as a call.");
        Assert.True(!HasVerificationErrors(method), "The transformed call chain must remain valid IL.");
    }


    [Fact]
    public void MonoModObservePreservesOriginalValue()
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
        Assert.True(method.Body.Instructions[bIndex + 1].OpCode.Code == Code.Dup,
            "Observe must duplicate the matched value before passing it to a void callback.");
        Assert.True(!HasVerificationErrors(method), "Observe must leave the original value for C().");
    }

    [Fact]
    public void PlainInsertionCallCanStoreResult()
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

        Assert.True(method.Body.Instructions[0].OpCode.Code == Code.Call,
            "A plain before-site call should be emitted before expression evaluation.");
        Assert.True(method.Body.Instructions[1].OpCode.Code is Code.Stloc or Code.Stloc_S,
            "An explicitly selected local destination must consume the callback result.");
        Assert.True(!HasVerificationErrors(method), "A stored plain-call result must not disturb the original stack contract.");
    }

    [Fact]
    public void MonoModConditionTransformProducesValidIL()
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
                P.Mark("ab", Ops.CallA() && P.Arg<B>(0).CallB()) && (Ops.CallC() || Ops.CallD()));
            //匹配 ( CallA() && P_0.CallB() && (CallC() || CallD()) 这个条件
            //并将其中的 CallA() && P_0.CallB() 标记为 "ab"
            var condition = il.Match(pattern).Single().Condition("ab");
            //再method内匹配对应代码段，并取 "ab"匹配结果。
            condition.Transform(il, (Func<bool, bool>)Ops.IdentityBool);
            //返回值传入Ops.IdentityBool，等价于修改成  ( IdentityBool(CallA() && P_0.CallB()) && (CallC() || CallD()) 
        });
        Assert.True(method.Body.Instructions.Count > 12, "Condition transform should insert bridge instructions.");
        var callbackCalls = method.Body.Instructions.Count(static instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.IdentityBool) });
        Assert.Equal(1, callbackCalls);
        Assert.True(!HasVerificationErrors(method), "The rewritten short-circuit condition must remain valid IL.");
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
