using System;
using Mono.Cecil;
// --8<-- [start:cookbook-usings]
using MonoWeaver.Cecil;
using MonoWeaver.Patterns;
// --8<-- [end:cookbook-usings]

namespace MonoWeaver.DocSamples;

// One method per cookbook entry. The matching game method lives in GameCode,
// so a reader can see the target and the pattern side by side.
public static class CookbookSamples
{
    public static void CalculationPattern()
    {
        // --8<-- [start:pattern-calculation]
        var pattern = Cil.Value(() =>
            P.Arg<int>(0) + P.Arg<int>(1));
        // --8<-- [end:pattern-calculation]
        _ = pattern;
    }

    public static void CallInsideLargerCalculation(MethodDefinition method)
    {
        // --8<-- [start:pattern-inner-call]
        var pattern = Cil.Value(() =>
            P.Mark(
                "score",
                P.Arg<Player>(0).GetScore())
            + 10);
        // --8<-- [end:pattern-inner-call]

        // --8<-- [start:pattern-inner-call-capture]
        var score = method.Match(pattern)
            .Single()
            .Captures.Value("score");
        // --8<-- [end:pattern-inner-call-capture]
        _ = score;
    }

    public static void EffectCall()
    {
        // --8<-- [start:pattern-effect-call]
        var pattern = Cil.Effect(() =>
            GameAudio.Play(
                P.Arg<Player>(0).HitSound));
        // --8<-- [end:pattern-effect-call]
        _ = pattern;
    }

    public static void IfCondition()
    {
        // --8<-- [start:pattern-if-condition]
        var pattern = Cil.Condition(() =>
            P.Arg<Player>(0).HasKey
            && !P.Arg<Player>(0).IsDead);
        // --8<-- [end:pattern-if-condition]
        _ = pattern;
    }

    public static void ConstructorConstantProperty()
    {
        // --8<-- [start:pattern-constructor]
        var pattern = Cil.Value(() =>
            new Reward(
                "rare",
                P.Mark(
                    "amount",
                    P.Arg<Player>(0).Level * 100)));
        // --8<-- [end:pattern-constructor]
        _ = pattern;
    }

    public static void AcrossLocal()
    {
        // --8<-- [start:pattern-across-local]
        var pattern = Cil.Value(() =>
            P.Mark(
                "adjusted",
                P.Arg<int>(0) + 1)
            * 2);
        // --8<-- [end:pattern-across-local]
        _ = pattern;
    }

    public static void ArrayRead()
    {
        // --8<-- [start:pattern-array-read]
        var pattern = Cil.Value(() =>
            P.Arg<int[]>(0)[P.Arg<int>(1) + 1]);
        // --8<-- [end:pattern-array-read]
        _ = pattern;
    }

    public static void WithoutGameReference()
    {
        // --8<-- [start:pattern-symbols-decl]
        var game = CilSymbols.In("GameAssembly");
        var player = game.Type("Game.Player");
        var getScore = player.InstanceMethod(
            "GetScore",
            CilType.Int32);
        // --8<-- [end:pattern-symbols-decl]

        // --8<-- [start:pattern-symbols]
        var pattern = Cil.Value(
            P.Arg(0, player.Assignable(), "player")
             .Call(getScore)
             .Mark("score"));
        // --8<-- [end:pattern-symbols]
        _ = pattern;
    }

    public static void WithGameReference()
    {
        // --8<-- [start:pattern-lambda-equivalent]
        var pattern = Cil.Value(() =>
            P.Mark(
                "score",
                P.Arg<Player>(0).GetScore()));
        // --8<-- [end:pattern-lambda-equivalent]
        _ = pattern;
    }
}
