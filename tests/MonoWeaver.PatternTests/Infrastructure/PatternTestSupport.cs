using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;
using Xunit;

namespace MonoWeaver.PatternTests;

internal static class PatternTestSupport
{
    public const string TargetType = "MonoWeaver.PatternTestFixtures.Target";

    public static ModuleDefinition OpenFixtureModule()
        => PatternTestModules.Open("PatternFixtures");

    public static ModuleDefinition OpenUnoptimizedFixtureModule()
        => PatternTestModules.Open("PatternFixtures.Debug");

    public static ModuleDefinition OpenCurrentTestModule()
    {
        var resolver = new DefaultAssemblyResolver();
        AddSearchDirectoryIfExists(resolver, AppContext.BaseDirectory);
        AddSearchDirectoryIfExists(resolver, Path.GetDirectoryName(typeof(object).Assembly.Location));
        return ModuleDefinition.ReadModule(typeof(PatternTestSupport).Assembly.Location,
            new ReaderParameters
            {
                AssemblyResolver = resolver,
                ReadSymbols = false,
                InMemory = true,
            });
    }

    public static MethodDefinition FixtureMethod(ModuleDefinition module, string name)
    {
        var matches = module.RequireType(TargetType).Methods
            .Where(method => method.Name == name)
            .ToArray();
        return Assert.Single(matches);
    }

    public static MethodDefinition CurrentMethod(ModuleDefinition module, Type declaringType, string name)
    {
        var type = module.RequireType(declaringType.FullName
            ?? throw new InvalidOperationException($"Type '{declaringType}' has no metadata full name."));
        return Assert.Single(type.Methods.Where(method => method.Name == name));
    }

    public static void AssertCallTo(Instruction? instruction, string methodName)
    {
        Assert.NotNull(instruction);
        Assert.True(instruction!.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj,
            $"Expected call/newobj, got {instruction.OpCode.Code}.");
        var method = Assert.IsType<MethodReference>(instruction.Operand);
        Assert.Equal(methodName, method.Name);
    }

    public static void AssertNoVerificationErrors(MethodDefinition method)
    {
        var verifier = new ILMethodVerifier(method, VerifyOptions.Full);
        verifier.Verify();
        var errors = verifier.Diagnostics
            .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Fatal)
            .ToArray();

        if (errors.Length == 0)
            return;

        var instructions = string.Join(Environment.NewLine,
            method.Body.Instructions.Select(instruction => "  " + instruction));
        var diagnostics = string.Join(Environment.NewLine, errors.Select(error => "  " + error));
        Assert.Fail($"Verification failed for {method.FullName}:{Environment.NewLine}{instructions}{Environment.NewLine}{diagnostics}");
    }

    private static void AddSearchDirectoryIfExists(DefaultAssemblyResolver resolver, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            resolver.AddSearchDirectory(path);
    }
}
