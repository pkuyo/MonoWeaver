using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.Patterns;
using MonoWeaver.Utils;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class CallsAndMembersPatternTests
{
    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesNestedInstanceCallsAndMarkedSubexpression(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Chain");
        var aType = RuntimeSymbols.Type<A>(assignable: true);
        var callB = RuntimeSymbols.Method<A>(nameof(A.B));
        var callC = RuntimeSymbols.Method<B>(nameof(B.C));
        var pattern = DualPattern.Value(dsl,
            () => P.Mark("hook", P.Arg<A>(0).B()).C(),
            () => P.Arg(0, aType).Call(callB).Mark("hook").Call(callC));

        var hook = method.Match(pattern).Single().Value("hook");

        PatternTestSupport.AssertCallTo(hook.ProducerInstruction, nameof(A.B));
        PatternTestSupport.AssertCallTo(hook.ConsumerInstruction, nameof(B.C));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void CallThroughCompilerTemporaryUsesLoadOccurrence(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenUnoptimizedFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Temporary");
        var aType = RuntimeSymbols.Type<A>(assignable: true);
        var callB = RuntimeSymbols.Method<A>(nameof(A.B));
        var callC = RuntimeSymbols.Method<B>(nameof(B.C));
        var pattern = DualPattern.Value(dsl,
            () => P.Mark("hook", P.Arg<A>(0).B()).C(),
            () => P.Arg(0, aType).Call(callB).Mark("hook").Call(callC));

        var match = Assert.Single(method.Match(pattern).Where(candidate =>
            ReferenceEquals(candidate.Value().ProducerInstruction, candidate.Value().AfterUseInstruction)));
        var hook = match.Value("hook");

        PatternTestSupport.AssertCallTo(hook.ProducerInstruction, nameof(A.B));
        Assert.True(hook.AfterUseInstruction.OpCode.Code is Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0,
            "AfterUse must target the concrete ldloc consumed by C().");
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesStaticCallWithArguments(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "StaticCall");
        var add = RuntimeSymbols.Method<Ops>(nameof(Ops.Add), typeof(int), typeof(int));
        var pattern = DualPattern.Value(dsl,
            () => Ops.Add(P.Arg<int>(0), P.Arg<int>(1)),
            () => P.Call(add, P.Arg(0, CilType.Int32), P.Arg(1, CilType.Int32)));

        var match = method.Match(pattern).Single();

        Assert.Equal(Code.Call, match.Value().ProducerInstruction.OpCode.Code);
        PatternTestSupport.AssertCallTo(match.Value().ProducerInstruction, nameof(Ops.Add));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesVoidCallAsEffect(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "VoidCall");
        var consume = RuntimeSymbols.Method<Ops>(nameof(Ops.ConsumeInt), typeof(int));
        var pattern = DualPattern.Effect(dsl,
            () => Ops.ConsumeInt(P.Arg<int>(0)),
            () => P.Call(consume, P.Arg(0, CilType.Int32)));

        var match = method.Match(pattern).Single();

        Assert.Equal(Code.Call, match.LastInstruction.OpCode.Code);
        PatternTestSupport.AssertCallTo(match.LastInstruction, nameof(Ops.ConsumeInt));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesDiscardedNonVoidCallAsEffect(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Discarded");
        var aType = RuntimeSymbols.Type<A>(assignable: true);
        var callB = RuntimeSymbols.Method<A>(nameof(A.B));
        var pattern = DualPattern.Discard(dsl,
            () => P.Arg<A>(0).B(),
            () => P.Arg(0, aType).Call(callB));

        var match = method.Match(pattern).Single();

        Assert.Equal(Code.Pop, match.LastInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesConstructorCall(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "NewMemberHost");
        var constructor = RuntimeSymbols.Constructor<MemberHost>(typeof(int));
        var pattern = DualPattern.Value(dsl,
            () => new MemberHost(P.Arg<int>(0)),
            () => P.New(constructor, P.Arg(0, CilType.Int32)));

        var value = method.Match(pattern).Single().Value();

        Assert.Equal(Code.Newobj, value.ProducerInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesInstanceMethodWithExplicitArgument(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "InstanceCall");
        var hostType = RuntimeSymbols.Type<MemberHost>(assignable: true);
        var add = RuntimeSymbols.Method<MemberHost>(nameof(MemberHost.Add), typeof(int));
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<MemberHost>(0).Add(P.Arg<int>(1)),
            () => P.Arg(0, hostType).Call(add, P.Arg(1, CilType.Int32)));

        var value = method.Match(pattern).Single().Value();

        PatternTestSupport.AssertCallTo(value.ProducerInstruction, nameof(MemberHost.Add));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ExactOverloadSignatureIsRequired(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Overloads");
        var bType = RuntimeSymbols.Type<B>(assignable: true);
        var selectString = RuntimeSymbols.Method<B>(nameof(B.Select), typeof(string));
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<B>(0).Select("selected"),
            () => P.Arg(0, bType).Call(selectString, P.Constant("selected")));

        var operand = Assert.IsType<MethodReference>(method.Match(pattern).Single().Value().ProducerInstruction.Operand);

        Assert.Equal(MetadataType.String, operand.Parameters.Single().ParameterType.MetadataType);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesInstanceFieldRead(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ReadInstanceField");
        var hostType = RuntimeSymbols.Type<MemberHost>(assignable: true);
        var field = RuntimeSymbols.Field<MemberHost>(nameof(MemberHost.InstanceField));
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<MemberHost>(0).InstanceField,
            () => P.Arg(0, hostType).Field(field));

        Assert.Equal(Code.Ldfld, method.Match(pattern).Single().Value().ProducerInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesStaticFieldRead(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ReadStaticField");
        var field = RuntimeSymbols.Field<MemberHost>(nameof(MemberHost.StaticField));
        var pattern = DualPattern.Value(dsl,
            () => MemberHost.StaticField,
            () => P.Field(field));

        Assert.Equal(Code.Ldsfld, method.Match(pattern).Single().Value().ProducerInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void PropertyGetterAndExplicitGetterCallAreEquivalent(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ReadProperty");
        var hostType = RuntimeSymbols.Type<MemberHost>(assignable: true);
        var getter = CilMethodSpec.From(typeof(MemberHost).GetProperty(nameof(MemberHost.Property))!.GetMethod!);
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<MemberHost>(0).Property,
            () => P.Arg(0, hostType).Call(getter));

        PatternTestSupport.AssertCallTo(method.Match(pattern).Single().Value().ProducerInstruction,
            "get_" + nameof(MemberHost.Property));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesClosedGenericMethodCall(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "GenericCall");
        var identity = RuntimeSymbols.ClosedGenericMethod<Ops>(nameof(Ops.Identity),
            new[] { typeof(int) }, typeof(int));
        var pattern = DualPattern.Value(dsl,
            () => Ops.Identity(P.Arg<int>(0)),
            () => P.Call(identity, P.Arg(0, CilType.Int32)));

        var operand = Assert.IsAssignableFrom<GenericInstanceMethod>(
            method.Match(pattern).Single().Value().ProducerInstruction.Operand);

        Assert.Equal(MetadataType.Int32, operand.GenericArguments.Single().MetadataType);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesInterfaceDispatch(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "InterfaceCall");
        var interfaceType = RuntimeSymbols.Type<ICompute>(assignable: true);
        var compute = RuntimeSymbols.Method<ICompute>(nameof(ICompute.Compute), typeof(int));
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<ICompute>(0).Compute(7),
            () => P.Arg(0, interfaceType).Call(compute, P.Constant(7)));

        var value = method.Match(pattern).Single().Value();

        Assert.Equal(Code.Callvirt, value.ProducerInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesNullCallArgument(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "NullArgument");
        var accept = RuntimeSymbols.Method<Ops>(nameof(Ops.AcceptObject), typeof(object));
        var pattern = DualPattern.Value<object?>(dsl,
            () => Ops.AcceptObject(default(object)),
            () => P.Call(accept, P.Null(CilType.Object)));

        var value = method.Match(pattern).Single().Value();

        PatternTestSupport.AssertCallTo(value.ProducerInstruction, nameof(Ops.AcceptObject));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void DirectCallCanBeRejectedWhenCallOpcodeMatchingIsStrict(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = module.RequireType("MonoWeaver.PatternTestFixtures.DirectCaller")
            .RequireMethod("CallBase");
        var bType = RuntimeSymbols.Type<B>(assignable: true);
        var baseCall = RuntimeSymbols.Method<B>(nameof(B.C));
        var relaxed = DualPattern.Value(dsl,
            () => P.Any<B>("self").C(),
            () => P.Any(bType, "self").Call(baseCall));

        Assert.Single(method.Match(relaxed));

        var strictOptions = new PatternOptions { IgnoreCallOpcodeDifference = false };
        var strict = DualPattern.Value(dsl,
            () => P.Any<B>("self").C(),
            () => P.Any(bType, "self").Call(baseCall),
            strictOptions);

        Assert.Empty(method.Match(strict));
    }
}
