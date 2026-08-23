using System;
using Mono.Cecil;
using MonoWeaver.Cecil;
// --8<-- [start:verify-usings]
using MonoWeaver.CFG;
using MonoWeaver.Utils;
// --8<-- [end:verify-usings]

namespace MonoWeaver.DocSamples;

public static class VerificationSamples
{
    public static void VerifyManually(MethodDefinition method)
    {
        // --8<-- [start:verify-manual]
        var report = method.Verify(
            VerifyOptions.Full,
            maxErrCount: 20);

        foreach (var item in report.Diagnostics)
            Console.WriteLine(item);

        report.ThrowIfHasErrors();
        // --8<-- [end:verify-manual]
    }

    public static void CatchDiagnostics(RewritePlan plan)
    {
        // --8<-- [start:catch-diagnostics]
        try
        {
            plan.Apply(VerifyOptions.Full);
        }
        catch (ILMethodVerifier.CfgVerifyException error)
        {
            foreach (var item in error.Diagnostics)
                Console.Error.WriteLine(item);

            throw;
        }
        // --8<-- [end:catch-diagnostics]
    }

    public static void CombineOptions(MethodDefinition method)
    {
        // --8<-- [start:verify-options-combo]
        var options =
            VerifyOptions.Instructions |
            VerifyOptions.StackBalance |
            VerifyOptions.LocalInit;

        method.Verify(options).ThrowIfHasErrors();
        // --8<-- [end:verify-options-combo]
    }
}
