using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Cecil;
using MonoWeaver.Patterns;
using MonoWeaver.Utils;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class PatternTransformTests
{
    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void TransformCaptureLeavesReplacementForConsumer(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ChainTransform");
        var callback = CilMethodSpec.From(typeof(Ops).GetMethod(nameof(Ops.IdentityB))!);

        MatchChainHook(method, dsl).Transform(callback).Apply();

        var callbackCall = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.IdentityB) });
        PatternTestSupport.AssertCallTo(callbackCall.Previous, nameof(A.B));
        PatternTestSupport.AssertCallTo(callbackCall.Next, nameof(B.C));
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void DelegateTransformCaptureLeavesReplacementForConsumer(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ChainTransform");

        MatchChainHook(method, dsl).Transform((Func<B, B>)Ops.IdentityB).Apply();

        var callbackCall = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.IdentityB) });
        PatternTestSupport.AssertCallTo(callbackCall.Previous, nameof(A.B));
        PatternTestSupport.AssertCallTo(callbackCall.Next, nameof(B.C));
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ReplaceCaptureProducerReplacesCapturedIlRange(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ChainTransform");

        MatchChainHook(method, dsl)
            .Replace(m => new[] { CreateNewB(m) })
            .Apply(VerifyOptions.Full);

        Assert.DoesNotContain(method.Body.Instructions, instruction =>
            (instruction.OpCode.Code is Code.Call or Code.Callvirt)
            && instruction.Operand is MethodReference { Name: nameof(A.B) });
        var newB = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Newobj
            && instruction.Operand is MethodReference { DeclaringType.Name: nameof(B) });
        PatternTestSupport.AssertCallTo(newB.Next, nameof(B.C));
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ReplaceReplacesConcreteTemporaryLoadOnly(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Temporary");

        MatchChainHook(method, dsl)
            .Replace(m => new[] { CreateNewB(m) })
            .Apply(VerifyOptions.Full);

        Assert.Contains(method.Body.Instructions, instruction =>
            (instruction.OpCode.Code is Code.Call or Code.Callvirt)
            && instruction.Operand is MethodReference { Name: nameof(A.B) });
        var newB = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Newobj
            && instruction.Operand is MethodReference { DeclaringType.Name: nameof(B) });
        PatternTestSupport.AssertCallTo(newB.Next, nameof(B.C));
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ClosureDelegateTransformUsesRuntimeReferenceInvoker(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ChainTransform");
        var captured = new B();
        Func<B, B> callback = value => ReferenceEquals(captured, value) ? captured : value;

        MatchChainHook(method, dsl).Transform(callback).Apply();

        Assert.Equal(1, RuntimeInvokeCallCount(method));
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ObserveDuplicatesAndPreservesOriginalValue(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Observe");
        var callback = CilMethodSpec.From(typeof(Ops).GetMethod(nameof(Ops.ObserveB))!);

        MatchChainHook(method, dsl).Observe(callback).Apply();

        var duplicate = method.Body.Instructions.Single(instruction => instruction.OpCode.Code == Code.Dup);
        PatternTestSupport.AssertCallTo(duplicate.Previous, nameof(A.B));
        PatternTestSupport.AssertCallTo(duplicate.Next, nameof(Ops.ObserveB));
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void DelegateObserveDuplicatesAndPreservesOriginalValue(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Observe");

        MatchChainHook(method, dsl).Observe((Action<B>)Ops.ObserveB).Apply();

        var duplicate = method.Body.Instructions.Single(instruction => instruction.OpCode.Code == Code.Dup);
        PatternTestSupport.AssertCallTo(duplicate.Previous, nameof(A.B));
        PatternTestSupport.AssertCallTo(duplicate.Next, nameof(Ops.ObserveB));
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void InstanceDelegateObserveUsesRuntimeReferenceInvoker(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Observe");
        var receiver = new RuntimeDelegateReceiver();

        MatchChainHook(method, dsl).Observe((Action<B>)receiver.ObserveB).Apply();

        Assert.Equal(1, RuntimeInvokeCallCount(method));
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MulticastDelegateObserveUsesRuntimeReferenceInvoker(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Observe");
        Action<B> callback = Ops.ObserveB;
        callback += Ops.ObserveB;

        MatchChainHook(method, dsl).Observe(callback).Apply();

        Assert.Equal(1, RuntimeInvokeCallCount(method));
        Assert.DoesNotContain(method.Body.Instructions, instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.ObserveB) });
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Fact]
    public void RuntimeDelegatesForInvalidModuleNameShareGeneratedAssemblyUntilApply()
    {
        var invalidPathChar = Path.GetInvalidPathChars().FirstOrDefault(ch => ch != '\0');
        if (invalidPathChar == default)
            return;

        using var module = PatternTestSupport.OpenFixtureModule();
        module.Name = "Pattern" + invalidPathChar + "Fixtures.dll";
        module.Assembly.Name.Name = "Pattern" + invalidPathChar + "Fixtures";

        var transformMethod = PatternTestSupport.FixtureMethod(module, "ChainTransform");
        var observeMethod = PatternTestSupport.FixtureMethod(module, "Observe");
        var captured = new B();
        var receiver = new RuntimeDelegateReceiver();
        var loadedAssemblies = new List<string>();
        AssemblyLoadEventHandler handler = (_, args) =>
        {
            var name = args.LoadedAssembly.GetName().Name;
            if (name is not null &&
                name.StartsWith("MonoWeaver.Generated.PatternFixtures.", StringComparison.Ordinal))
            {
                loadedAssemblies.Add(name);
            }
        };

        AppDomain.CurrentDomain.AssemblyLoad += handler;
        try
        {
            var transform = MatchChainHook(transformMethod, PatternDsl.CilExpr)
                .Transform((Func<B, B>)(value => ReferenceEquals(value, captured) ? captured : value));
            var observe = MatchChainHook(observeMethod, PatternDsl.CilExpr)
                .Observe((Action<B>)receiver.ObserveB);

            Assert.Empty(loadedAssemblies);

            transform.Apply();
            Assert.Single(loadedAssemblies);

            observe.Apply();
            Assert.Single(loadedAssemblies);
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyLoad -= handler;
        }

        var generatedAssemblyName = loadedAssemblies.Single();
        Assert.Equal(generatedAssemblyName, AssemblyScopeName(SingleRuntimeInvokeReference(transformMethod)));
        Assert.Equal(generatedAssemblyName, AssemblyScopeName(SingleRuntimeInvokeReference(observeMethod)));
        Assert.DoesNotContain(module.Types, type =>
            type.Namespace == "MonoWeaver.Generated" &&
            type.Name.StartsWith("__CecilDelegateInvokers", StringComparison.Ordinal));

        var loadedAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .Single(assembly => assembly.GetName().Name == generatedAssemblyName);
        var invokerType = loadedAssembly.GetType("MonoWeaver.Generated.__CecilDelegateInvokers");
        Assert.NotNull(invokerType);
        var invokers = invokerType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name.StartsWith("Invoke_", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, invokers.Length);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ReplaceValueCanFeedExistingLocalStore(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "BeforeExpression");
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<int>(0),
            () => P.Arg(0, CilType.Int32));
        var callback = CilMethodSpec.From(typeof(Ops).GetMethod(nameof(Ops.FortyTwo))!);

        var match = Assert.Single(method.Match(pattern).Where(candidate =>
            ReferenceEquals(candidate.DefinitionInstruction, candidate.ResultInstruction)));
        match.Replace(callback).Apply();

        var callbackCall = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.FortyTwo) });
        Assert.True(callbackCall.Next?.OpCode.Code is
            Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3);
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void DelegateReplaceValueCanFeedExistingLocalStore(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "BeforeExpression");
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<int>(0),
            () => P.Arg(0, CilType.Int32));

        var match = Assert.Single(method.Match(pattern).Where(candidate =>
            ReferenceEquals(candidate.DefinitionInstruction, candidate.ResultInstruction)));
        match.Replace((Func<int>)Ops.FortyTwo).Apply();

        var callbackCall = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.FortyTwo) });
        Assert.True(callbackCall.Next?.OpCode.Code is
            Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3);
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ReplaceCapturedLocalLoadWithCallback(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "BeforeExpression");
        var slot = Cil.Local<int>(0);
        var pattern = DualPattern.Value(dsl,
            () => slot.Value,
            () => slot.Expr);
        var callback = CilMethodSpec.From(typeof(Ops).GetMethod(nameof(Ops.FortyTwo))!);

        var match = method.Match(pattern).Single();
        ValueCapture target = match[slot];
        target.Replace(callback).Apply();

        var callbackCall = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.FortyTwo) });
        Assert.True(callbackCall.Next?.OpCode.Code is
            Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3);
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ReplaceCapturedArgumentLoadWithCallback(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Select");
        var arg = Cil.Arg<int>(1);
        var pattern = DualPattern.Value(dsl,
            () => arg.Value,
            () => arg.Expr);
        var callback = CilMethodSpec.From(typeof(Ops).GetMethod(nameof(Ops.FortyTwo))!);

        var match = method.Match(pattern).Single();
        ValueCapture target = match[arg];
        target.Replace(callback).Apply();

        var callbackCall = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.FortyTwo) });
        Assert.Equal(Code.Ret, callbackCall.Next?.OpCode.Code);
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ConditionTransformProducesValidIl(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ConditionTransform");
        var callback = CilMethodSpec.From(typeof(Ops).GetMethod(nameof(Ops.IdentityBool))!);

        var condition = MatchConditionAb(method, dsl);
        // The pure Cecil backend creates one bridge per outgoing condition edge.
        // A source block can therefore contribute both a taken and a fall-through call site.
        var expectedCallSites = condition.Fragment.TrueExits.Count + condition.Fragment.FalseExits.Count;

        condition.Transform(callback).Apply();

        Assert.Equal(expectedCallSites, method.Body.Instructions.Count(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.IdentityBool) }));
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void DelegateConditionTransformProducesValidIl(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ConditionTransform");
        var condition = MatchConditionAb(method, dsl);
        var expectedCallSites = condition.Fragment.TrueExits.Count + condition.Fragment.FalseExits.Count;

        condition.Transform((Func<bool, bool>)Ops.IdentityBool).Apply();

        Assert.Equal(expectedCallSites, method.Body.Instructions.Count(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.IdentityBool) }));
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ClosureDelegateConditionTransformUsesRuntimeReferenceInvoker(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ConditionTransform");
        var condition = MatchConditionAb(method, dsl);
        var expectedCallSites = condition.Fragment.TrueExits.Count + condition.Fragment.FalseExits.Count;
        var invert = false;

        condition.Transform((Func<bool, bool>)(value => invert ? !value : value)).Apply();

        Assert.Equal(expectedCallSites, RuntimeInvokeCallCount(method));
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ConditionObserveVoidCallbackPreservesOriginalBranch(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ConditionTransform");
        var condition = MatchConditionAb(method, dsl);
        var expectedCallSites = condition.Fragment.TrueExits.Count + condition.Fragment.FalseExits.Count;
        var callback = CilMethodSpec.From(typeof(Ops).GetMethod(nameof(Ops.ObserveBool))!);

        condition.Observe(callback).Apply();

        Assert.Equal(expectedCallSites, method.Body.Instructions.Count(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.ObserveBool) }));
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ConditionObserveCanReadExplicitArgument(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ConditionObserve");
        var condition = MatchObserveGate(method, dsl);
        var expectedCallSites = condition.Fragment.TrueExits.Count + condition.Fragment.FalseExits.Count;
        var callback = CilMethodSpec.From(typeof(Ops).GetMethod(nameof(Ops.ObserveConditionTarget))!);

        condition.Observe(callback, args => args.Arg(0)).Apply();

        var callbackCalls = method.Body.Instructions.Where(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.ObserveConditionTarget) }).ToArray();
        Assert.Equal(expectedCallSites, callbackCalls.Length);
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
        var anchor = match.FirstInstruction;
        var branch = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code is Code.Brfalse or Code.Brfalse_S);
        var originalBranchTarget = Assert.IsType<Instruction>(branch.Operand);

        match.Before(touch).Apply();

        Assert.Same(originalBranchTarget, branch.Operand);
        Assert.Equal(Code.Call, anchor.Previous?.OpCode.Code);
        Assert.Same(anchor, anchor.Previous?.Next);
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void DelegateInsertionBeforeFallThroughValueDoesNotRetargetExistingBranch(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Select");
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<int>(1),
            () => P.Arg(1, CilType.Int32));
        var match = method.Match(pattern).Single();
        var anchor = match.FirstInstruction;
        var branch = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code is Code.Brfalse or Code.Brfalse_S);
        var originalBranchTarget = Assert.IsType<Instruction>(branch.Operand);

        match.Before((Action)Ops.ConsumeNothing).Apply();

        Assert.Same(originalBranchTarget, branch.Operand);
        Assert.Equal(Code.Call, anchor.Previous?.OpCode.Code);
        Assert.Same(anchor, anchor.Previous?.Next);
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    //构造 "hook片段.C()" pattern，匹配后返回片段捕获（原 Mark("hook") 的等价物）。
    private static ValueCapture MatchChainHook(MethodDefinition method, PatternDsl dsl)
    {
        var aType = RuntimeSymbols.Type<A>(assignable: true);
        var callB = RuntimeSymbols.Method<A>(nameof(A.B));
        var callC = RuntimeSymbols.Method<B>(nameof(B.C));
        var hook = DualPattern.Value(dsl,
            () => P.Arg<A>(0).B(),
            () => P.Arg(0, aType).Call(callB));
        var pattern = DualPattern.Value(dsl,
            () => hook.Value.C(),
            () => hook.Expr.Call(callC));
        return method.Match(pattern).Single()[hook];
    }

    private static ConditionCapture MatchConditionAb(MethodDefinition method, PatternDsl dsl)
    {
        var bType = RuntimeSymbols.Type<B>(assignable: true);
        var callA = RuntimeSymbols.Method<Ops>(nameof(Ops.CallA));
        var callB = RuntimeSymbols.Method<B>(nameof(B.CallB));
        var callC = RuntimeSymbols.Method<Ops>(nameof(Ops.CallC));
        var callD = RuntimeSymbols.Method<Ops>(nameof(Ops.CallD));
        var ab = DualPattern.Condition(dsl,
            () => Ops.CallA() && P.Arg<B>(0).CallB(),
            () => P.Call(callA).AndAlso(P.Arg(0, bType).Call(callB)));
        var pattern = DualPattern.Condition(dsl,
            () => ab && (Ops.CallC() || Ops.CallD()),
            () => ab.Expr.AndAlso(P.Call(callC).OrElse(P.Call(callD))));
        return method.Match(pattern).Single()[ab];
    }

    private static ConditionCapture MatchObserveGate(MethodDefinition method, PatternDsl dsl)
    {
        var bType = RuntimeSymbols.Type<B>(assignable: true);
        var callA = RuntimeSymbols.Method<Ops>(nameof(Ops.CallA));
        var callB = RuntimeSymbols.Method<B>(nameof(B.CallB));
        var gate = DualPattern.Condition(dsl,
            () => P.Arg<B>(0).CallB() && Ops.CallA(),
            () => P.Arg(0, bType).Call(callB).AndAlso(P.Call(callA)));
        var pattern = DualPattern.Condition(dsl,
            () => gate.Value,
            () => gate.Expr);
        return method.Match(pattern).Single()[gate];
    }

    private static int RuntimeInvokeCallCount(MethodDefinition method)
        => method.Body.Instructions.Count(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference mf
            && mf.FullName.Contains("__CecilDelegateInvokers"));

    private static MethodReference SingleRuntimeInvokeReference(MethodDefinition method)
        => method.Body.Instructions
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>()
            .Single(reference =>
                reference.Name.StartsWith("Invoke_", StringComparison.Ordinal) &&
                reference.DeclaringType.FullName.Contains("__CecilDelegateInvokers"));

    private static string AssemblyScopeName(MethodReference reference)
        => Assert.IsType<AssemblyNameReference>(reference.DeclaringType.Scope).Name;

    private static Instruction CreateNewB(ModuleDefinition module)
    {
        var constructor = typeof(B).GetConstructor(Type.EmptyTypes)
                          ?? throw new MissingMethodException(typeof(B).FullName, ".ctor");
        return Instruction.Create(OpCodes.Newobj, module.ImportReference(constructor));
    }

    private sealed class RuntimeDelegateReceiver
    {
        public void ObserveB(B value)
        {
        }
    }
}
