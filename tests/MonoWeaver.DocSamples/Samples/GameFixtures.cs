using System;

namespace MonoWeaver.DocSamples;

// Stand-ins for the game assembly a mod would reference.
// The docs use the same names, so a reader can map an example onto their own game types.

public sealed class Player
{
    public int Level;
    public int HitSound;
    public bool HasKey;
    public bool IsDead;

    public int GetScore() => 0;
    public int GetDamage(Enemy enemy) => 0;
}

public class Enemy
{
    public int GetHealth() => 0;
}

public sealed class EliteEnemy : Enemy;

public sealed class Reward(string rarity, int amount)
{
    public string Rarity = rarity;
    public int Amount = amount;
}

public static class GameAudio
{
    public static void Play(int soundId) { }
}

// The game methods the examples target. The bodies are what a decompiler would show.
public static class GameCode
{
    // --8<-- [start:target-calculate-damage]
    public static int CalculateDamage(int baseDamage, int bonus)
        => baseDamage + bonus;
    // --8<-- [end:target-calculate-damage]

    // --8<-- [start:target-read-selected-score]
    public static int ReadSelectedScore(Player player)
    {
        _ = player.GetScore().ToString();
        return player.GetScore() + 10;
    }
    // --8<-- [end:target-read-selected-score]

    // --8<-- [start:target-play-hit-sound]
    public static void PlayHitSound(Player player)
        => GameAudio.Play(player.HitSound);
    // --8<-- [end:target-play-hit-sound]

    // --8<-- [start:target-try-open]
    public static bool TryOpen(Player player)
    {
        if (player.HasKey && !player.IsDead)
            return true;

        return false;
    }
    // --8<-- [end:target-try-open]

    // --8<-- [start:target-create-reward]
    public static Reward CreateReward(Player player)
        => new Reward("rare", player.Level * 100);
    // --8<-- [end:target-create-reward]

    // --8<-- [start:target-double-damage]
    public static int DoubleDamage(int damage)
    {
        var adjusted = damage + 1;
        return adjusted * 2;
    }
    // --8<-- [end:target-double-damage]

    // --8<-- [start:target-read-next]
    public static int ReadNext(int[] values, int index)
        => values[index + 1];
    // --8<-- [end:target-read-next]

    // --8<-- [start:target-read-score]
    public static int ReadScore(Player player)
        => player.GetScore();
    // --8<-- [end:target-read-score]
}

// --8<-- [start:hooks]
public static class Hooks
{
    public static int ClampDamage(int original)
        => Math.Min(Math.Max(original, 0), 999);

    public static int ClampBaseDamage(int original)
        => Math.Max(original, 0);

    public static void LogDamage(int original)
        => Console.WriteLine($"damage = {original}");

    public static int FixedDamage() => 42;

    public static void OnDamageCalculationStarted() { }

    public static void OnDamageCalculated() { }

    public static int AdjustDamage(int original, int firstMethodArg, int limit)
        => Math.Min(original + firstMethodArg, limit);

    public static int LogAndNormalize(int original)
        => original;

    public static int RecordAndReturnId(int original) => 0;

    public static int CreateTraceId() => 0;

    public static void BeforeSound() { }

    public static void AfterSound() { }

    public static void PlayCustomSound() { }

    public static bool ChangeGate(bool original) => original;

    public static void LogGate(bool original) { }

    public static bool CustomGate() => true;
}
// --8<-- [end:hooks]
