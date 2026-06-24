using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;
using MonoWeaver.Utils;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class CecilMetadataPatternTests
{
    [Fact]
    public void MetadataOnlyPatternAndExternalMethodReferenceTransformRoundTrip()
    {
        using var game = PatternTestModules.Open("GameAssembly");
        using var hooks = PatternTestModules.Open("Game.Hooks");
        var target = GameReadState(game);
        var transform = hooks.RequireType("Hooks.Callbacks").RequireMethod("Transform", hooks.TypeSystem.Int32);

        var symbols = CilSymbols.In("GameAssembly");
        var player = symbols.Type("Game.Player");
        var getState = player.InstanceMethod("GetState", CilType.Int32);
        var match = target.Match(Cil.Value(
            P.Arg(0, player).Call(getState).Mark("state"))).Single();

        match.Value("state").AfterUse().Transform(transform).Apply();

        var imported = (MethodReference)target.Body.Instructions
            .Where(i => i.OpCode.Code == Code.Call)
            .Single().Operand;
        var scope = Assert.IsType<AssemblyNameReference>(imported.DeclaringType.Scope);
        Assert.Equal("Game.Hooks", scope.Name);
        Assert.Equal(new Version(2, 3, 4, 5), scope.Version);

        using var stream = new MemoryStream();
        game.Write(stream);
        stream.Position = 0;
        using var reread = ModuleDefinition.ReadModule(stream);
        Assert.Equal(new Version(2, 3, 4, 5),
            reread.AssemblyReferences.Single(r => r.Name == "Game.Hooks").Version);
    }

    [Fact]
    public void InvalidCallbackDoesNotChangeIlOrAssemblyReferences()
    {
        using var game = PatternTestModules.Open("GameAssembly");
        using var hooks = PatternTestModules.Open("Bad.Hooks");
        var target = GameReadState(game);
        var invalid = hooks.RequireType("Hooks.Callbacks").RequireMethod("Transform", hooks.TypeSystem.String);

        var player = CilTypeSpec.From(target.Parameters[0].ParameterType);
        var getState = CilMethodSpec.From((MethodReference)target.Body.Instructions
            .Single(instruction => instruction.Operand is MethodReference { Name: "GetState" }).Operand);
        var match = target.Match(Cil.Value(P.Arg(0, player).Call(getState))).Single();
        var instructionCount = target.Body.Instructions.Count;
        var referenceCount = game.AssemblyReferences.Count;

        Assert.Throws<ArgumentException>(() => match.Value().AfterUse().Transform(invalid));
        Assert.Equal(instructionCount, target.Body.Instructions.Count);
        Assert.Equal(referenceCount, game.AssemblyReferences.Count);
    }

    [Fact]
    public void VerificationStackCompatibilityMustBeExplicit()
    {
        using var module = PatternTestModules.Open("PatternFixtures");
        var method = module.RequireType("MonoWeaver.PatternTestFixtures.Target")
            .RequireMethod("IdentityInt", module.TypeSystem.Int32);

        Assert.Empty(method.Match(Cil.Value(P.Arg(0, CilType.Boolean))));
        Assert.Single(method.Match(Cil.Value(
            P.Arg(0, CilType.Boolean.StackCompatible()))));
    }

    [Fact]
    public void ConcreteCecilTypeKeepsAssemblyIdentity()
    {
        using var first = PatternTestModules.Open("First");
        using var second = PatternTestModules.Open("Second");
        var firstPlayer = first.RequireType("Game.Player");
        var secondPlayer = second.RequireType("Game.Player");
        var method = first.RequireType("Game.Host").RequireMethod("Identity", firstPlayer);

        Assert.Single(method.Match(Cil.Value(P.Arg(0, firstPlayer))));
        Assert.Empty(method.Match(Cil.Value(P.Arg(0, secondPlayer))));
        Assert.Empty(method.Match(Cil.Value(
            P.Arg(0, CilSymbols.In("Second").Type("Game.Player")))));
    }

    [Fact]
    public void TypeResolutionCacheDoesNotReuseCecilObjectsAcrossModuleInstances()
    {
        TypeReference firstBaseType;
        ModuleDefinition firstResolvedModule;
        using (var first = PatternTestModules.Open("PatternFixtures"))
        {
            var parameterType = first.RequireType("MonoWeaver.PatternTestFixtures.Target")
                .Methods.Single(method => method.Name == "Chain")
                .Parameters[0].ParameterType;
            firstBaseType = parameterType.BaseType()
                ?? throw new InvalidOperationException("The fixture parameter must have a base type.");
            firstResolvedModule = firstBaseType.Module;
        }

        using var second = PatternTestModules.Open("PatternFixtures");
        var secondParameterType = second.RequireType("MonoWeaver.PatternTestFixtures.Target")
            .Methods.Single(method => method.Name == "Chain")
            .Parameters[0].ParameterType;
        var secondBaseType = secondParameterType.BaseType()
            ?? throw new InvalidOperationException("The fixture parameter must have a base type.");

        Assert.Equal(firstBaseType.FullName, secondBaseType.FullName);
        Assert.NotSame(firstBaseType, secondBaseType);
        Assert.NotSame(firstResolvedModule, secondBaseType.Module);
    }

    [Fact]
    public void SameModuleTransformPassesVerifier()
    {
        using var module = PatternTestModules.Open("GameAssembly");
        var target = GameReadState(module);
        var transform = module.RequireType("Game.LocalCallbacks")
            .RequireMethod("LocalTransform", module.TypeSystem.Int32);

        var player = CilTypeSpec.From(target.Parameters[0].ParameterType);
        var getState = CilMethodSpec.From((MethodReference)target.Body.Instructions
            .Single(instruction => instruction.Operand is MethodReference { Name: "GetState" }).Operand);
        target.Match(Cil.Value(P.Arg(0, player).Call(getState))).Single()
            .Value().AfterUse().Transform(transform).Apply();

        var verifier = new ILMethodVerifier(target,
            VerifyOptions.Instructions | VerifyOptions.StackTypes);
        verifier.Verify();
        Assert.DoesNotContain(verifier.Diagnostics,
            d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Fatal);
    }

    private static MethodDefinition GameReadState(ModuleDefinition game)
    {
        var player = game.RequireType("Game.Player");
        return game.RequireType("Game.Host").RequireMethod("ReadState", player);
    }
}
