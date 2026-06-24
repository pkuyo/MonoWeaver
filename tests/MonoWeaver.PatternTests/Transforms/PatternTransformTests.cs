using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.Cecil;
using MonoWeaver.Patterns;
using MonoWeaver.Utils;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class PatternTransformTests
{
    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void TransformAfterUseLeavesReplacementForConsumer(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ChainTransform");
        var pattern = ChainPattern(dsl);
        var callback = CilMethodSpec.From(typeof(Ops).GetMethod(nameof(Ops.IdentityB))!);

        method.Match(pattern).Single().Value("hook").AfterUse().Transform(callback).Apply();

        var callbackCall = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.IdentityB) });
        PatternTestSupport.AssertCallTo(callbackCall.Previous, nameof(A.B));
        PatternTestSupport.AssertCallTo(callbackCall.Next, nameof(B.C));
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void DelegateTransformAfterUseLeavesReplacementForConsumer(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ChainTransform");

        method.Match(ChainPattern(dsl)).Single().Value("hook").AfterUse()
            .Transform((Func<B, B>)Ops.IdentityB).Apply();

        var callbackCall = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.IdentityB) });
        PatternTestSupport.AssertCallTo(callbackCall.Previous, nameof(A.B));
        PatternTestSupport.AssertCallTo(callbackCall.Next, nameof(B.C));
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

        method.Match(ChainPattern(dsl)).Single().Value("hook").AfterUse()
            .Transform(callback).Apply();

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

        method.Match(ChainPattern(dsl)).Single().Value("hook").AfterUse().Observe(callback).Apply();

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

        method.Match(ChainPattern(dsl)).Single().Value("hook").AfterUse()
            .Observe((Action<B>)Ops.ObserveB).Apply();

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

        method.Match(ChainPattern(dsl)).Single().Value("hook").AfterUse()
            .Observe((Action<B>)receiver.ObserveB).Apply();

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

        method.Match(ChainPattern(dsl)).Single().Value("hook").AfterUse()
            .Observe(callback).Apply();

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
            var transform = transformMethod.Match(ChainPattern(PatternDsl.CilExpr)).Single()
                .Value("hook")
                .AfterUse()
                .Transform((Func<B, B>)(value => ReferenceEquals(value, captured) ? captured : value));
            var observe = observeMethod.Match(ChainPattern(PatternDsl.CilExpr)).Single()
                .Value("hook")
                .AfterUse()
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
        match.BeforeEvaluation().CallValue(callback).StoreLocal(temp).Apply();

        var callbackCall = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.FortyTwo) });
        Assert.True(callbackCall.Next?.OpCode.Code is
            Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3);
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void DelegateInsertionCallCanStoreResultInExistingLocal(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "BeforeExpression");
        var temp = method.Body.Variables[0];
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<int>(0),
            () => P.Arg(0, CilType.Int32));

        var match = Assert.Single(method.Match(pattern).Where(candidate =>
            ReferenceEquals(candidate.Value().ProducerInstruction, candidate.Value().AfterUseInstruction)));
        match.BeforeEvaluation().CallValue((Func<int>)Ops.FortyTwo).StoreLocal(temp).Apply();

        var callbackCall = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.FortyTwo) });
        Assert.True(callbackCall.Next?.OpCode.Code is
            Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3);
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void InsertionCallCanStoreResultInCapturedLocal(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "BeforeExpression");
        var pattern = DualPattern.Value(dsl,
            () => P.Local<int>(0, "target"),
            () => P.Local(0, CilType.Int32, "target"));
        var callback = CilMethodSpec.From(typeof(Ops).GetMethod(nameof(Ops.FortyTwo))!);

        var match = method.Match(pattern).Single();
        MatchedValue target = match.Value("target");
        match.BeforeEvaluation("target").CallValue(callback).Store(target).Apply();

        var callbackCall = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.FortyTwo) });
        Assert.True(callbackCall.Next?.OpCode.Code is
            Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3);
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void InsertionCallCanStoreResultInCapturedArgument(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Select");
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<int>(1, "target"),
            () => P.Arg(1, CilType.Int32, "target"));
        var callback = CilMethodSpec.From(typeof(Ops).GetMethod(nameof(Ops.FortyTwo))!);

        var match = method.Match(pattern).Single();
        MatchedValue target = match.Value("target");
        match.BeforeEvaluation("target").CallValue(callback).Store(target).Apply();

        var callbackCall = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.FortyTwo) });
        Assert.True(callbackCall.Next?.OpCode.Code is Code.Starg or Code.Starg_S);
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
        var condition = method.Match(ComplexConditionPattern(dsl)).Single().Condition("ab");
        var expectedCallSites = condition.TrueExits.Count + condition.FalseExits.Count;

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
        var condition = method.Match(ComplexConditionPattern(dsl)).Single().Condition("ab");
        var expectedCallSites = condition.TrueExits.Count + condition.FalseExits.Count;
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
        var condition = method.Match(ComplexConditionPattern(dsl)).Single().Condition("ab");
        var expectedCallSites = condition.TrueExits.Count + condition.FalseExits.Count;
        var callback = CilMethodSpec.From(typeof(Ops).GetMethod(nameof(Ops.ObserveBool))!);

        condition.Observe(callback).Apply();

        Assert.Equal(expectedCallSites, method.Body.Instructions.Count(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.ObserveBool) }));
        PatternTestSupport.AssertNoVerificationErrors(method);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ConditionObserveCanStoreCallbackResultInCapturedTarget(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ConditionObserve");
        var match = method.Match(ObserveConditionPattern(dsl)).Single();
        var condition = match.Condition("gate");
        var target = match.Value("target");
        var expectedCallSites = condition.TrueExits.Count + condition.FalseExits.Count;
        var callback = CilMethodSpec.From(typeof(Ops).GetMethod(nameof(Ops.ObserveConditionB))!);

        condition.Observe(callback, args => args.Capture(target))
            .Store(target)
            .Apply();

        var callbackCalls = method.Body.Instructions.Where(instruction =>
            instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference { Name: nameof(Ops.ObserveConditionB) }).ToArray();
        Assert.Equal(expectedCallSites, callbackCalls.Length);
        Assert.All(callbackCalls, instruction =>
            Assert.True(instruction.Next?.OpCode.Code is Code.Starg or Code.Starg_S));
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

        match.BeforeEvaluation().CallVoid(touch).Apply();

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
        var anchor = match.Value().FirstInstruction;
        var branch = method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code is Code.Brfalse or Code.Brfalse_S);
        var originalBranchTarget = Assert.IsType<Instruction>(branch.Operand);

        match.BeforeEvaluation().CallVoid((Action)Ops.ConsumeNothing).Apply();

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

    private static ExpressionPattern ObserveConditionPattern(PatternDsl dsl)
    {
        var bType = RuntimeSymbols.Type<B>(assignable: true);
        var callA = RuntimeSymbols.Method<Ops>(nameof(Ops.CallA));
        var callB = RuntimeSymbols.Method<B>(nameof(B.CallB));
        return DualPattern.Condition(dsl,
            () => P.Mark("gate", P.Arg<B>(0, "target").CallB() && Ops.CallA()),
            () => P.Mark("gate", P.Arg(0, bType, "target").Call(callB).AndAlso(P.Call(callA))));
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

    private sealed class RuntimeDelegateReceiver
    {
        public void ObserveB(B value)
        {
        }
    }
}
