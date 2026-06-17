using Mono.Cecil;

namespace MonoWeaver.ILTests;

public enum ExpectedKind
{
    Valid,
    Invalid,
    Warning,
}

public enum MethodTestScope
{
    Both,
    Full,
}

public sealed class MethodTestCase
{
    public required string AssemblyPath { get; init; }
    public required string AssemblyName { get; init; }
    public required string FriendlyName { get; init; }
    public required string MethodName { get; init; }
    public required MethodDefinition Method { get; init; }
    public required MethodTestScope Scope { get; init; }
    public required ExpectedKind ExpectedKind { get; init; }
    public IReadOnlyList<string> ExpectedVerifierErrors { get; init; } = Array.Empty<string>();

    public override string ToString()
    {
        var expected = ExpectedKind switch
        {
            ExpectedKind.Valid => "Valid",
            ExpectedKind.Invalid => "Invalid(" + string.Join('.', ExpectedVerifierErrors) + ")",
            ExpectedKind.Warning => "Warning(" + string.Join('.', ExpectedVerifierErrors) + ")",
            _ => ExpectedKind.ToString(),
        };
        var scope = Scope == MethodTestScope.Both ? string.Empty : $" [{Scope}]";
        return $"[{AssemblyName}] {FriendlyName}{scope} :: {MethodName} => {expected}";
    }
}

internal sealed class VerificationRunResult
{
    public required MethodTestCase TestCase { get; init; }
    public required bool HasVerifierWarning { get; init; }
    public required bool HasVerifierError { get; init; }
    public required bool Crashed { get; init; }
    public required IReadOnlyList<string> Diagnostics { get; init; }
    public string? ExceptionText { get; init; }

    // This deliberately uses only valid-vs-invalid. .NET runtime VerifierError names do not map 1:1 to MonoWeaver.CFGExceptionType.
    public bool LooseMismatch => TestCase.ExpectedKind switch
    {
        ExpectedKind.Valid => HasVerifierError || Crashed,
        ExpectedKind.Invalid => !HasVerifierError && !Crashed,
        ExpectedKind.Warning => !HasVerifierWarning || HasVerifierError || Crashed,
        _ => true,
    };
}
