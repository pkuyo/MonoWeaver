using Mono.Cecil.Cil;
using MonoWeaver.Patterns;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class ArrayPatternTests
{
    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesPrimitiveArrayCreation(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "NewIntArray");
        var pattern = DualPattern.Value(dsl,
            () => new int[P.Arg<int>(0)],
            () => P.NewArray(CilType.Int32, P.Arg(0, CilType.Int32)));

        Assert.Equal(Code.Newarr, method.Match(pattern).Single().DefinitionInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesReferenceArrayCreation(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "NewStringArray");
        var pattern = DualPattern.Value(dsl,
            () => new string[P.Arg<int>(0)],
            () => P.NewArray(CilType.String, P.Arg(0, CilType.Int32)));

        Assert.Equal(Code.Newarr, method.Match(pattern).Single().DefinitionInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesPrimitiveArrayElementLoad(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "LoadIntElement");
        var arrayType = CilType.Int32.MakeArrayType();
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<int[]>(0)[1],
            () => P.Arg(0, arrayType).ElementAt(1, CilType.Int32));

        var code = method.Match(pattern).Single().DefinitionInstruction.OpCode.Code;
        Assert.True(code is Code.Ldelem_I4 or Code.Ldelem_Any);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesReferenceArrayElementLoad(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "LoadStringElement");
        var arrayType = CilType.String.MakeArrayType();
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<string[]>(0)[1],
            () => P.Arg(0, arrayType).ElementAt(1, CilType.String));

        var code = method.Match(pattern).Single().DefinitionInstruction.OpCode.Code;
        Assert.True(code is Code.Ldelem_Ref or Code.Ldelem_Any);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesArrayLength(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Length");
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<int[]>(0).Length,
            () => P.Arg(0, CilType.Int32.MakeArrayType()).Length());

        Assert.Equal(Code.Ldlen, method.Match(pattern).Single().DefinitionInstruction.OpCode.Code);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesPrimitiveArrayStoreEffect(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "StoreIntElement");
        var pattern = DualPattern.Effect(dsl,
            () => P.StoreElement(P.Arg<int[]>(0), 1, P.Arg<int>(1)),
            () => P.StoreElement(
                P.Arg(0, CilType.Int32.MakeArrayType()),
                P.Constant(1),
                P.Arg(1, CilType.Int32)));

        var code = method.Match(pattern).Single().LastInstruction.OpCode.Code;
        Assert.True(code is Code.Stelem_I4 or Code.Stelem_Any);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void MatchesReferenceArrayStoreEffect(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "StoreStringElement");
        var pattern = DualPattern.Effect(dsl,
            () => P.StoreElement(P.Arg<string[]>(0), 1, P.Arg<string>(1)),
            () => P.StoreElement(
                P.Arg(0, CilType.String.MakeArrayType()),
                P.Constant(1),
                P.Arg(1, CilType.String.Assignable())));

        var code = method.Match(pattern).Single().LastInstruction.OpCode.Code;
        Assert.True(code is Code.Stelem_Ref or Code.Stelem_Any);
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ArrayIndexConstantMustMatch(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "LoadIntElement");
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<int[]>(0)[2],
            () => P.Arg(0, CilType.Int32.MakeArrayType()).ElementAt(2, CilType.Int32));

        Assert.Empty(method.Match(pattern));
    }

    [Theory]
    [MemberData(nameof(PatternDslData.Both), MemberType = typeof(PatternDslData))]
    public void ArrayElementTypeMustMatch(PatternDsl dsl)
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "LoadIntElement");
        var pattern = DualPattern.Value(dsl,
            () => P.Arg<long[]>(0)[1],
            () => P.Arg(0, CilType.Int64.MakeArrayType()).ElementAt(1, CilType.Int64));

        Assert.Empty(method.Match(pattern));
    }
}
