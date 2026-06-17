using MonoWeaver.CFG;
using Xunit;

namespace MonoWeaver.ILTests;

public sealed class ILVerificationTests
{
    public static IEnumerable<object[]> LightMethodCases()
    {
        foreach (var testCase in FilteredMethodCases().Where(static testCase => testCase.Scope == MethodTestScope.Both))
            yield return new object[] { testCase };
    }

    public static IEnumerable<object[]> FullMethodCases()
    {
        foreach (var testCase in FilteredMethodCases())
            yield return new object[] { testCase };
    }

    private static IReadOnlyList<MethodTestCase> FilteredMethodCases()
    {
        var filter = Environment.GetEnvironmentVariable("ILTEST_FILTER");
        var cases = string.IsNullOrWhiteSpace(filter)
            ? ILCaseData.Value.MethodCases
            : ILCaseData.Value.MethodCases.Where(testCase => MatchesFilter(testCase, filter)).ToArray();

        if (cases.Count == 0)
            throw new InvalidOperationException($"No IL method test cases matched ILTEST_FILTER='{filter}'.");

        return cases;
    }

    [Fact]
    public void ILMethodCasesAreDiscoverable()
    {
        var data = ILCaseData.Value;

        Assert.NotEmpty(data.CompiledAssemblyPaths);
        Assert.NotEmpty(data.MethodCases);
    }

    [Theory]
    [MemberData(nameof(LightMethodCases), DisableDiscoveryEnumeration = true)]
    public void LightMethodValidityMatchesExpectedResult(MethodTestCase testCase)
    {
        VerifyMethodValidityMatchesExpectedResult(testCase, VerifyOptions.Light);
    }

    [Theory]
    [MemberData(nameof(FullMethodCases), DisableDiscoveryEnumeration = true)]
    public void FullMethodValidityMatchesExpectedResult(MethodTestCase testCase)
    {
        VerifyMethodValidityMatchesExpectedResult(testCase, VerifyOptions.Full);
    }

    private static void VerifyMethodValidityMatchesExpectedResult(MethodTestCase testCase, VerifyOptions options)
    {
        var result = MonoWeaverVerifierAdapter.Verify(testCase, options);

        Assert.False(result.LooseMismatch, FormatFailure(result, options));
    }

    private static string FormatFailure(VerificationRunResult result, VerifyOptions options)
    {
        var expected = result.TestCase.ExpectedKind switch
        {
            ExpectedKind.Valid => "Valid",
            ExpectedKind.Invalid => "Invalid(" + string.Join('.', result.TestCase.ExpectedVerifierErrors) + ")",
            ExpectedKind.Warning => "Warning(" + string.Join('.', result.TestCase.ExpectedVerifierErrors) + ")",
            _ => result.TestCase.ExpectedKind.ToString(),
        };
        var actual = result.Crashed
            ? "HarnessCrash"
            : result.HasVerifierError
                ? "Invalid"
                : result.HasVerifierWarning
                    ? "Warning"
                : "Valid";
        var diagnostics = result.Diagnostics.Count == 0
            ? "  <none>"
            : string.Join(Environment.NewLine, result.Diagnostics.Take(10).Select(static d => "  " + d));

        return string.Join(Environment.NewLine,
            result.TestCase.ToString(),
            $"Options:  {options}",
            $"Scope:    {result.TestCase.Scope}",
            $"Expected: {expected}",
            $"Actual:   {actual}",
            "Diagnostics:",
            diagnostics,
            result.ExceptionText is null ? string.Empty : "Exception: " + result.ExceptionText);
    }

    private static bool MatchesFilter(MethodTestCase testCase, string filter)
    {
        return Contains(testCase.AssemblyName, filter) ||
            Contains(testCase.FriendlyName, filter) ||
            Contains(testCase.MethodName, filter) ||
            Contains(testCase.ToString(), filter);

        static bool Contains(string value, string filter)
            => value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }
}
