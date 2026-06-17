using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoWeaver.CFG;
using MonoWeaver.Utils;
using System.Reflection;
using System.Runtime.CompilerServices;
using static MonoWeaver.CFG.ILMethodAnalyzer;

namespace MonoWeaver.ILHookEarlyReturnSample;

public static class GameCombat
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int GetFinalDamage(int baseDamage)
    {
        return baseDamage + 10;
    }
}

public static class BadDamageHook
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

    private static bool ShouldBypassBonus()
    {
        return true;
    }

    private static void BadPatch(ILContext il)
    {
        var c = new ILCursor(il);
        il.Body.MaxStackSize = 10;
        var finalRet = il.Instrs.Last(i => i.OpCode == OpCodes.Ret);
        var finalRetLabel = il.DefineLabel(finalRet);

        c.EmitCall(typeof(BadDamageHook).GetMethod(
            nameof(ShouldBypassBonus),
            BindingFlags.NonPublic | BindingFlags.Static
        )!);

        c.EmitBrtrue(finalRetLabel);

        Console.WriteLine("---------------------MonoWeaver Verify------------------");
        try
        {
            il.Body.Analyze(VerifyOptions.Light).ThrowIfHasErrors();
        }
        catch (CfgVerifyException e)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            foreach(var diag in  e.Diagnostics)
            {
                Console.WriteLine(diag);
            }
            Console.ForegroundColor = ConsoleColor.White;
        }

        Console.WriteLine("---------------------MonoMod Apply------------------");
       
    }
}

internal static class Program
{
    private static void Main()
    {
        Console.WriteLine("Applying the intentionally broken ILHook early-return patch...");
        try
        {
            BadDamageHook.Apply();
        }
        finally
        {
            BadDamageHook.Dispose();
        }
    }
}
