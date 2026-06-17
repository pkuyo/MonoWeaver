using MonoWeaver.CFG;

namespace MonoWeaver.ILTests;

internal static class MonoWeaverVerifierAdapter
{
    public static VerificationRunResult Verify(MethodTestCase testCase, VerifyOptions options)
    {
        var diagnostics = new List<string>();
        var crashed = false;
        string? exceptionText = null;

        try
        {
            var analyzer = new ILMethodAnalyzer(testCase.Method, options);
            diagnostics.AddRange(analyzer.Diagnostics.Select(FormatDiagnostic));
        }
        catch (ILMethodAnalyzer.CfgVerifyException ex)
        {
            // ILMethodAnalyzer uses this as a verification-abort exception after collecting diagnostics.
            diagnostics.AddRange(ex.Diagnostics.Select(FormatDiagnostic));
            exceptionText = ex.Message;
        }
        catch (Exception ex)
        {
            crashed = true;
            exceptionText = ex.ToString();
            diagnostics.Add("[Fatal] HarnessException: " + ex.GetType().FullName + ": " + ex.Message);
        }

        var hasVerifierError = diagnostics.Any(static d =>
            d.Contains("[Error]", StringComparison.Ordinal) ||
            d.Contains("[Fatal]", StringComparison.Ordinal));

        return new VerificationRunResult
        {
            TestCase = testCase,
            HasVerifierError = hasVerifierError,
            Crashed = crashed,
            Diagnostics = diagnostics,
            ExceptionText = exceptionText,
        };
    }

    private static string FormatDiagnostic(CFGDiagnostic diagnostic)
        => $"[{diagnostic.Severity}] {diagnostic.Type}: {diagnostic.Message}";
}
