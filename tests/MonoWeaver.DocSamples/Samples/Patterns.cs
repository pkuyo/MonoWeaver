using System;
using Mono.Cecil;
using MonoWeaver.Cecil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;

namespace MonoWeaver.DocSamples;

public static class PatternSamples
{
    public static void ThreeKinds()
    {
        // --8<-- [start:value-pattern]
        var damagePattern = Cil.Value((int baseDamage, int bonus) =>
            baseDamage + bonus);
        // --8<-- [end:value-pattern]

        // --8<-- [start:effect-pattern]
        var soundPattern = Cil.Effect((int soundId) =>
            GameAudio.Play(soundId));
        // --8<-- [end:effect-pattern]

        // --8<-- [start:condition-pattern]
        var gatePattern = Cil.Condition((bool left, bool right) =>
            left && right);
        // --8<-- [end:condition-pattern]

        _ = (damagePattern, soundPattern, gatePattern);
    }

    public static void SingleMatch(MethodDefinition method)
    {
        var damagePattern = Cil.Value((int baseDamage, int bonus) => baseDamage + bonus);

        // --8<-- [start:single]
        var damage = method.Match(damagePattern).Single();

        damage.Transform((Func<int, int>)Hooks.ClampDamage)
              .Apply(VerifyOptions.Full);
        // --8<-- [end:single]
    }

    public static void ListCandidates(MethodDefinition method)
    {
        var damagePattern = Cil.Value((int baseDamage, int bonus) => baseDamage + bonus);

        // --8<-- [start:list-candidates]
        var candidates = method.Match(damagePattern);

        foreach (var candidate in candidates)
            Console.WriteLine($"IL_{candidate.FirstInstruction.Offset:X4}");
        // --8<-- [end:list-candidates]
    }

    public static void ExplainFailure(MethodDefinition method)
    {
        var damagePattern = Cil.Value((int baseDamage, int bonus) => baseDamage + bonus);

        // --8<-- [start:explain-failure]
        var candidates = method.Match(damagePattern);

        if (candidates.Count != 1)
            Console.WriteLine(candidates.ExplainFailure());
        // --8<-- [end:explain-failure]
    }

    public static void MarkAndCapture(MethodDefinition method)
    {
        // --8<-- [start:mark-capture]
        var pattern = Cil.Value((int baseDamage, int bonus) =>
            P.Mark("baseDamage", baseDamage) + bonus);

        var match = method.Match(pattern).Single();
        var baseDamage = match.Captures.Value("baseDamage");

        baseDamage.Transform((Func<int, int>)Hooks.ClampBaseDamage)
                  .Apply(VerifyOptions.Full);
        // --8<-- [end:mark-capture]
    }

    public static void LocalDefinedBy()
    {
        // --8<-- [start:local-defined-by]
        var pattern = Cil.Value(() =>
                P.Local<int>("tmp") * 2)
            .LocalDefinedBy(
                "tmp",
                Cil.Value((int damage) => damage + 1));
        // --8<-- [end:local-defined-by]
        _ = pattern;
    }

    public static void DisableTemporaryFollowing()
    {
        // --8<-- [start:temporary-off]
        var options = new PatternOptions
        {
            TemporaryNormalization = TemporaryNormalization.None
        };

        var pattern = Cil.Value(
            () => P.Local<int>(0),
            options);
        // --8<-- [end:temporary-off]
        _ = pattern;
    }

    public static void LambdaForm()
    {
        // --8<-- [start:lambda-form]
        var pattern = Cil.Value((Player player) =>
            player.GetScore());
        // --8<-- [end:lambda-form]
        _ = pattern;
    }

    public static void SymbolForm()
    {
        // --8<-- [start:symbol-form]
        var game = CilSymbols.In("GameAssembly");
        var player = game.Type("Game.Player");
        var enemy = game.Type("Game.Enemy");
        var getDamage = player.InstanceMethod(
            "GetDamage",
            CilType.Int32,
            enemy);

        var pattern = Cil.Value(
            P.Arg(0, player.Assignable(), "player")
             .Call(getDamage, P.Arg(1, enemy.Assignable()))
             .Mark("damage"));
        // --8<-- [end:symbol-form]
        _ = pattern;
    }

    public static void SymbolFormShort()
    {
        // --8<-- [start:symbol-form-short]
        var game = CilSymbols.In("GameAssembly");
        var player = game.Type("Game.Player");
        var getScore = player.InstanceMethod("GetScore", CilType.Int32);

        var scorePattern = Cil.Value(
            P.Arg(0, player.Assignable())
             .Call(getScore));
        // --8<-- [end:symbol-form-short]
        _ = scorePattern;
    }

    public static void ExactVersusAssignable()
    {
        // --8<-- [start:exact-vs-assignable]
        var game = CilSymbols.In("GameAssembly");
        var enemy = game.Type("Game.Enemy");

        var exact = P.Arg(0, enemy);
        var allowDerived = P.Arg(0, enemy.Assignable());
        // --8<-- [end:exact-vs-assignable]
        _ = (exact, allowDerived);
    }
}
