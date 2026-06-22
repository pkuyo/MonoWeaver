using MonoWeaver.CFG;

namespace MonoWeaver.ILTests;

internal static class MonoWeaverVerifierAdapter
{
    public static VerificationRunResult Verify(MethodTestCase testCase, VerifyOptions options)
    {
        var diagnostics = new List<string>();
        var errorTypes = new List<string>();
        var warningTypes = new List<string>();
        var crashed = false;
        var hasVerifierWarning = false;
        var hasVerifierError = false;
        string? exceptionText = null;

        try
        {
            var analyzer = new ILMethodVerifier(testCase.Method, options);
            AddDiagnostics(analyzer.Diagnostics);
        }
        catch (ILMethodVerifier.CfgVerifyException ex)
        {
            // ILMethodAnalyzer uses this as a verification-abort exception after collecting diagnostics.
            AddDiagnostics(ex.Diagnostics);
            exceptionText = ex.Message;
        }
        catch (Exception ex)
        {
            crashed = true;
            hasVerifierError = true;
            exceptionText = ex.ToString();
            diagnostics.Add("[Fatal] HarnessException: " + ex.GetType().FullName + ": " + ex.Message);
        }

        return new VerificationRunResult
        {
            TestCase = testCase,
            HasVerifierWarning = hasVerifierWarning,
            HasVerifierError = hasVerifierError,
            Crashed = crashed,
            Diagnostics = diagnostics,
            ErrorTypes = errorTypes.Distinct(StringComparer.Ordinal).ToArray(),
            WarningTypes = warningTypes.Distinct(StringComparer.Ordinal).ToArray(),
            ExceptionText = exceptionText,
        };

        void AddDiagnostics(IEnumerable<CFGDiagnostic> source)
        {
            foreach (var diagnostic in source)
            {
                hasVerifierWarning |= diagnostic.Severity == DiagnosticSeverity.Warning;
                hasVerifierError |= diagnostic.Severity >= DiagnosticSeverity.Error;
                if (diagnostic.Severity == DiagnosticSeverity.Warning)
                    warningTypes.Add(diagnostic.Type.ToString());
                if (diagnostic.Severity >= DiagnosticSeverity.Error)
                    errorTypes.Add(diagnostic.Type.ToString());
                diagnostics.Add(FormatDiagnostic(diagnostic));
            }
        }
    }

    private static string FormatDiagnostic(CFGDiagnostic diagnostic)
        => $"[{diagnostic.Severity}] {diagnostic.Type}: {diagnostic.Message}";
}
