using System;
using Mono.Cecil;
// --8<-- [start:monomod-usings]
using MonoMod.Cil;
using MonoWeaver.Cecil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;
// --8<-- [end:monomod-usings]
using MonoWeaver.Utils;

namespace MonoWeaver.DocSamples;

public static class MonoModSamples
{
    // --8<-- [start:monomod-patch]
    public static void Patch(ILContext il)
    {
        var pattern = Cil.Value((int baseDamage, int bonus) =>
            baseDamage + bonus);

        il.Method.Match(pattern)
          .Single()
          .Transform((Func<int, int>)Hooks.ClampDamage)
          .Apply(VerifyOptions.Full);
    }
    // --8<-- [end:monomod-patch]

    public static void TakeMethodFromContext(ILContext il)
    {
        // --8<-- [start:monomod-method]
        MethodDefinition method = il.Method;
        // Matching and rewriting from here on target `method`.
        // --8<-- [end:monomod-method]
        _ = method;
    }

    public static void ManualCecilInterop(ILContext il)
    {
        // --8<-- [start:monomod-labels]
        CecilHelper.BranchLabelsToTarget(il);
        try
        {
            // Your own Cecil analysis or edits.
        }
        finally
        {
            CecilHelper.BranchTargetsToLabels(il);
        }
        // --8<-- [end:monomod-labels]
    }
}
