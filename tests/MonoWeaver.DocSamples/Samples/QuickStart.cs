// --8<-- [start:usings]
using System;
using System.Linq;
using Mono.Cecil;
using MonoWeaver.Cecil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;
// --8<-- [end:usings]

namespace MonoWeaver.DocSamples;

public static class QuickStart
{
    public static void PatchGameAssembly()
    {
        // --8<-- [start:quickstart]
        using var module = ModuleDefinition.ReadModule("Game.dll");

        var method = module.Types
            .Single(type => type.FullName == "Game.Player")
            .Methods.Single(candidate => candidate.Name == "ComputeDamage");

        var damagePattern = Cil.Value((int baseDamage, int bonus) =>
            baseDamage + bonus);

        var damage = method.Match(damagePattern).Single();

        damage.Transform((Func<int, int>)Hooks.ClampDamage)
              .Apply(VerifyOptions.Full);

        module.Write("Game.Patched.dll");
        // --8<-- [end:quickstart]
    }

    public static void ReadModuleOnly()
    {
        // --8<-- [start:read-module]
        using var module = ModuleDefinition.ReadModule("Game.dll");

        var method = module.Types
            .Single(type => type.FullName == "Game.Player")
            .Methods.Single(candidate => candidate.Name == "ComputeDamage");
        // --8<-- [end:read-module]
        _ = method;
    }
}
