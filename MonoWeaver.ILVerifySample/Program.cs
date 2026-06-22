using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoWeaver.CFG;
using MonoWeaver.Utils;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MonoWeaver.ILVerifySample;

public sealed class DamageContext
{
    public bool Critical { get; init; }
    public bool UseTextDamageHook { get; init; }
    public bool SkipVanillaHook { get; init; }
    public int CriticalBonus { get; init; } = 5;
    public int FlatBonus { get; init; } = 10;
}

public static class GameCombat
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int GetFinalDamage(DamageContext context, int baseDamage)
    {
        var scaledDamage = baseDamage * 2;
        if (context.Critical)
        {
            scaledDamage += context.CriticalBonus;
        }

        var finalDamage = scaledDamage + context.FlatBonus;
        return Math.Max(0, finalDamage);
    }
}

public static class InvalidCombatHook
{
    private static ILHook? hook;

    public static void Apply()
    {
        var method = typeof(GameCombat).GetMethod(
            nameof(GameCombat.GetFinalDamage),
            BindingFlags.Public | BindingFlags.Static
        )!;

        hook = new ILHook(method, BadPatch, applyByDefault: true);
    }

    public static void Dispose()
    {
        hook?.Dispose();
        hook = null;
    }

    public static bool ShouldUseTextDamage(DamageContext context)
    {
        return context.UseTextDamageHook;
    }

    public static bool ShouldSkipVanillaWithExtraStack(DamageContext context)
    {
        return context.SkipVanillaHook;
    }

    private static void BadPatch(ILContext il)
    {
        var c = new ILCursor(il);
        il.Body.MaxStackSize = Math.Max(il.Body.MaxStackSize, 8);

        var vanillaEntry = il.DefineLabel(il.Instrs[0]);
        var scaledDamageStore = il.DefineLabel(FindFirstLocalStore(il));
        var textDamagePath = c.DefineLabel();
        var extraStackJumpPath = c.DefineLabel();

        c.Emit(OpCodes.Ldarg_0);
        c.EmitCall(typeof(InvalidCombatHook).GetMethod(
            nameof(ShouldUseTextDamage),
            BindingFlags.Public | BindingFlags.Static
        )!);
        c.EmitBrtrue(textDamagePath);

        c.Emit(OpCodes.Ldarg_0);
        c.EmitCall(typeof(InvalidCombatHook).GetMethod(
            nameof(ShouldSkipVanillaWithExtraStack),
            BindingFlags.Public | BindingFlags.Static
        )!);
        c.EmitBrtrue(extraStackJumpPath);
        c.EmitBr(vanillaEntry);

        c.MarkLabel(textDamagePath);
        c.Emit(OpCodes.Ldstr, "poison-tick");
        c.EmitBr(scaledDamageStore);

        c.MarkLabel(extraStackJumpPath);
        c.Emit(OpCodes.Ldc_I4, 999);
        c.EmitBr(vanillaEntry);

        PrintFullVerification(il);
        Console.WriteLine("--------------------- MonoMod Apply ---------------------");
    }

    private static Instruction FindFirstLocalStore(ILContext il)
    {
        return il.Instrs.First(i => i.OpCode.Code is Code.Stloc_0 or Code.Stloc_S or Code.Stloc);
    }

    private static void PrintFullVerification(ILContext il)
    {
        Console.WriteLine("---------------- MonoWeaver Full Verify -----------------");
        try
        {
            var analyzer = il.Body.Analyze(VerifyOptions.Full);
            analyzer.ThrowIfHasErrors();
            Console.WriteLine("No diagnostics.");
        }
        catch (ILMethodVerifier.CfgVerifyException ex)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            foreach (var diagnostic in ex.Diagnostics)
            {
                Console.WriteLine(diagnostic);
            }

            Console.ResetColor();
        }
    }
}

internal static class Program
{
    private static void Main()
    {
        Console.WriteLine("Applying the intentionally broken ILVerify hook sample...");
        try
        {
            InvalidCombatHook.Apply();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("MonoMod rejected the patched body after MonoWeaver reported diagnostics.");
            Console.WriteLine($"{ex.GetType().FullName}: {ex.Message}");
            Console.ResetColor();
        }
        finally
        {
            InvalidCombatHook.Dispose();
        }
    }
}
