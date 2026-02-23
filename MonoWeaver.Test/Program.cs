using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Utils;

namespace MonoWeaver.Test;

public static class Program
{
    public static void Main()
    {
        var path = Assembly.GetExecutingAssembly().Location;
        Console.WriteLine($"Self assembly: {path}, {OpCodes.Endfilter.FlowControl}");

        var asm = AssemblyDefinition.ReadAssembly(path);
        var module = asm.MainModule;
        
        var playground = module.Types.First(t => t.FullName.EndsWith("CilPlayground"));
        foreach (var md in playground.Methods.Where(m => m.HasBody).Take(8))
        {
            Console.WriteLine($"- {md.FullName}");
            try
            {
                var analyzer = md.Analyze(VerifyOptions.Light);
                if (analyzer.Diagnostics.Count == 0)
                {
                    Console.WriteLine("  Analyze: OK (no errors)");
                }
                else
                {
                    Console.WriteLine("  Analyze errors: " + string.Join("\n", analyzer.Diagnostics.Select(i => $"[{i.Type}] {i.Message}").Distinct()));
                }
            }
            catch (ILMethodAnalyzer.CfgVerifyException e)
            {
                Console.WriteLine("  Analyze errors: " + string.Join("\n", e.Diagnostics.Select(i => $"[{i.Type}] {i.Message}").Distinct()));
            }
        }

        // 演示：对某个 stub 做一次 mutation 后 analyze
        var stubs = module.Types.First(t => t.FullName.EndsWith("MutationStubs"));
        var target = stubs.Methods.First(m => m.Name == nameof(MutationStubs.M_BrTargetCrossEhRegion));
        CecilMutator.Apply(target, CFGExceptionType.BrTargetCrossEhRegion);

        Console.WriteLine($"Mutated: {target.FullName} => {CFGExceptionType.BrTargetCrossEhRegion}");
        try
        {
            var analyzer = target.Analyze(VerifyOptions.Light);
            if (analyzer.Diagnostics.Count == 0)
            {
                Console.WriteLine("  Analyze: OK (no errors)");
            }
            else
            {
                Console.WriteLine("  Analyze errors: " + string.Join("\n", analyzer.Diagnostics.Select(i => $"[{i.Type}] {i.Message}").Distinct()));
            }
        }
        catch (ILMethodAnalyzer.CfgVerifyException e)
        {
            Console.WriteLine("  Analyze errors: " + string.Join("\n", e.Diagnostics.Select(i => $"[{i.Type}] {i.Message}").Distinct()));
        }
    }
}