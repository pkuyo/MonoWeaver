using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Utils;

namespace MonoWeaver.Test;

public static class Program
{
    public static void Main()
    {
        var path = Assembly.GetExecutingAssembly().Location;
        Console.WriteLine($"Self assembly: {path}");
        //TestHook(null);
        var asm = AssemblyDefinition.ReadAssembly(path);
        var module = asm.MainModule;

        var playground = module.Types.First(t => t.FullName.EndsWith("CilPlayground"));
        foreach (var md in playground.Methods.Where(m => m.HasBody).Take(8))
        {
            Console.BackgroundColor = ConsoleColor.DarkBlue;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"Origin - {md.FullName}");
            Console.ResetColor();
            try
            {
                var analyzer = md.Analyze(VerifyOptions.Light).ThrowIfHasErrors();
                Console.WriteLine("  Analyze: OK (no errors)");
            }
            catch (ILMethodAnalyzer.CfgVerifyException e)
            {
                Console.Write("  Analyze errors: ");
                Console.WriteLine(string.Join("\n  ", e.Diagnostics));
            }
        }

        CFGExceptionType[] tests =
        [
            CFGExceptionType.InvalidOpCode,
            CFGExceptionType.EhRegionOverlap,
            CFGExceptionType.EhRegionNonTryDuplication,
            CFGExceptionType.EhNestedInFilter,
            CFGExceptionType.TryAndHandlerNotInSameEnclosingRegion,
            CFGExceptionType.InvalidEhTableOrdering,

            CFGExceptionType.InvalidInstruction,
            //CFGExceptionType.TypeMismatch,
            CFGExceptionType.InconsistentFieldAccess,
            CFGExceptionType.StackUnderflow,
            CFGExceptionType.StackOverflow,
            CFGExceptionType.InvalidFallThrough,
            CFGExceptionType.UninitializedLocal,
            //CFGExceptionType.IncompatibleMergeTypes,
            CFGExceptionType.IncompatibleMergeDepth,
            CFGExceptionType.InvalidBrTarget,
            CFGExceptionType.BrTargetCrossEhRegion,
            CFGExceptionType.OutOfRange
        ];
        // 演示：对某个 stub 做一次 mutation 后 analyze
        var stubs = module.Types.First(t => t.FullName.EndsWith("MutationStubs"));
        foreach (var test in tests)
        {
            var target = stubs.Methods.First(m => m.Name == "M_" + test);
            CecilMutator.Apply(target, test);
            Console.BackgroundColor = ConsoleColor.DarkBlue;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"Mutated: {target.FullName} => {test}");
            Console.ResetColor();
            try
            {
                var analyzer = target.Analyze(VerifyOptions.Light | VerifyOptions.LocalInit).ThrowIfHasErrors();
                Console.WriteLine("  Analyze: OK (no errors)");
            }
            catch (ILMethodAnalyzer.CfgVerifyException e)
            {
                Console.Write("  Analyze errors: ");
                Console.WriteLine(string.Join("\n  ", e.Diagnostics));
            }
        }
    }

    private static void TestHook(ILContext il)
    {
        var c = new ILCursor(il);
        c.GotoNext(i => i.MatchAdd());
        c.Emit(OpCodes.Ldloc_0, 0);

        try
        {
            il.Body.Method.Analyze(VerifyOptions.Light).ThrowIfHasErrors();
        }
        catch (ILMethodAnalyzer.CfgVerifyException e)
        {
            Console.WriteLine(e.ToString());
        }
    }
}