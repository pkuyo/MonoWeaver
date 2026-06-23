using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoWeaver.Cecil;
using MonoWeaver.Patterns;
using CecilCode = Mono.Cecil.Cil.Code;
using CecilOpCodes = Mono.Cecil.Cil.OpCodes;
using EmitOpCode = System.Reflection.Emit.OpCode;
using EmitOpCodes = System.Reflection.Emit.OpCodes;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace MonoWeaver.HookBenchmarks;

internal static class Program
{
    private static readonly MethodInfo ProbeReflection =
        typeof(Program).GetMethod(nameof(Probe), BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(Program).FullName, nameof(Probe));

    private static readonly ExpressionPattern Pattern =
        Cil.Value(P.Arg(0, CilType.Int32) + 1);

    private static int _sink;

    private static int Main(string[] args)
    {
        var options = BenchmarkOptions.Parse(args);

        Console.WriteLine("MonoWeaver hook/matcher microbenchmark");
        Console.WriteLine($"Iterations: {options.Iterations:N0}, warmup: {options.WarmupIterations:N0}");
        Console.WriteLine("Workload: insert a void Probe() call before the expression `arg0 + 1`.");
        Console.WriteLine();

        Warmup(options.WarmupIterations);

        var results = new[]
        {
            RunCecilCase(
                "MonoMod ILCursor",
                options.Iterations,
                static (method, callback) => RunMonoModILCursor(method, callback)),
            RunHarmonyCase(
                "Harmony CodeInstruction",
                options.Iterations,
                RunHarmonyTranspiler),
            RunCecilCase(
                "MonoWeaver Pattern",
                options.Iterations,
                static (method, callback) => RunMonoWeaverPattern(method, callback)),
        };

        Print(results);
        GC.KeepAlive(_sink);
        return 0;
    }

    private static void Warmup(int iterations)
    {
        if (iterations <= 0)
            return;

        var cecil = CecilWorkload.Create(iterations, "WarmupCecil");
        var harmony = HarmonyWorkload.Create(iterations);
        for (var i = 0; i < iterations; i++)
        {
            _sink ^= RunMonoModILCursor(cecil.Methods[i], cecil.Callback);
            _sink ^= RunHarmonyTranspiler(harmony.Instructions[i]);
        }

        cecil = CecilWorkload.Create(iterations, "WarmupPattern");
        for (var i = 0; i < iterations; i++)
            _sink ^= RunMonoWeaverPattern(cecil.Methods[i], cecil.Callback);
    }

    private static BenchmarkResult RunCecilCase(
        string name,
        int iterations,
        Func<MethodDefinition, MethodReference, int> action)
    {
        var workload = CecilWorkload.Create(iterations, name.Replace(' ', '_'));
        return Measure(name, iterations, i => action(workload.Methods[i], workload.Callback));
    }

    private static BenchmarkResult RunHarmonyCase(
        string name,
        int iterations,
        Func<List<CodeInstruction>, int> action)
    {
        var workload = HarmonyWorkload.Create(iterations);
        return Measure(name, iterations, i => action(workload.Instructions[i]));
    }

    private static BenchmarkResult Measure(string name, int iterations, Func<int, int> action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var checksum = 0;

        for (var i = 0; i < iterations; i++)
            checksum += action(i);

        sw.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _sink ^= checksum;

        return new BenchmarkResult(name, iterations, sw.Elapsed, allocated, checksum);
    }

    private static int RunMonoModILCursor(MethodDefinition method, MethodReference callback)
    {
        using var context = new ILContext(method);
        context.Invoke(il =>
        {
            var cursor = new ILCursor(il);
            if (!cursor.TryGotoNext(
                    MoveType.Before,
                    static instruction => IsLdarg0(instruction),
                    static instruction => IsLdcI4(instruction, 1),
                    static instruction => instruction.OpCode.Code == CecilCode.Add))
            {
                throw new InvalidOperationException("MonoMod cursor could not find `arg0 + 1`.");
            }

            cursor.Emit(CecilOpCodes.Call, callback);
        });

        return method.Body.Instructions.Count;
    }

    private static int RunHarmonyTranspiler(List<CodeInstruction> instructions)
    {
        for (var i = 0; i <= instructions.Count - 3; i++)
        {
            if (!IsHarmonyLdarg0(instructions[i])
                || !IsHarmonyLdcI4(instructions[i + 1], 1)
                || instructions[i + 2].opcode != EmitOpCodes.Add)
            {
                continue;
            }

            instructions.Insert(i, new CodeInstruction(EmitOpCodes.Call, ProbeReflection));
            return instructions.Count;
        }

        throw new InvalidOperationException("Harmony transpiler could not find `arg0 + 1`.");
    }

    private static int RunMonoWeaverPattern(MethodDefinition method, MethodReference callback)
    {
        method.Match(Pattern)
            .Single()
            .BeforeEvaluation()
            .CallVoid(callback);

        return method.Body.Instructions.Count;
    }

    private static bool IsLdarg0(Instruction instruction)
        => instruction.OpCode.Code switch
        {
            CecilCode.Ldarg_0 => true,
            CecilCode.Ldarg or CecilCode.Ldarg_S
                => instruction.Operand is ParameterDefinition { Index: 0 },
            _ => false,
        };

    private static bool IsLdcI4(Instruction instruction, int value)
        => instruction.OpCode.Code switch
        {
            CecilCode.Ldc_I4_M1 => value == -1,
            CecilCode.Ldc_I4_0 => value == 0,
            CecilCode.Ldc_I4_1 => value == 1,
            CecilCode.Ldc_I4_2 => value == 2,
            CecilCode.Ldc_I4_3 => value == 3,
            CecilCode.Ldc_I4_4 => value == 4,
            CecilCode.Ldc_I4_5 => value == 5,
            CecilCode.Ldc_I4_6 => value == 6,
            CecilCode.Ldc_I4_7 => value == 7,
            CecilCode.Ldc_I4_8 => value == 8,
            CecilCode.Ldc_I4_S => instruction.Operand is sbyte shortValue && shortValue == value,
            CecilCode.Ldc_I4 => instruction.Operand is int intValue && intValue == value,
            _ => false,
        };

    private static bool IsHarmonyLdarg0(CodeInstruction instruction)
        => instruction.opcode == EmitOpCodes.Ldarg_0
           || IsHarmonyLdarg(instruction, 0);

    private static bool IsHarmonyLdarg(CodeInstruction instruction, int index)
    {
        if (instruction.opcode != EmitOpCodes.Ldarg
            && instruction.opcode != EmitOpCodes.Ldarg_S)
        {
            return false;
        }

        return instruction.operand switch
        {
            int intValue => intValue == index,
            short shortValue => shortValue == index,
            byte byteValue => byteValue == index,
            _ => false,
        };
    }

    private static bool IsHarmonyLdcI4(CodeInstruction instruction, int value)
        => instruction.opcode == LdcI4Opcode(value)
           || instruction.opcode == EmitOpCodes.Ldc_I4 && instruction.operand is int intValue && intValue == value
           || instruction.opcode == EmitOpCodes.Ldc_I4_S && instruction.operand is sbyte shortValue && shortValue == value;

    private static EmitOpCode LdcI4Opcode(int value)
        => value switch
        {
            -1 => EmitOpCodes.Ldc_I4_M1,
            0 => EmitOpCodes.Ldc_I4_0,
            1 => EmitOpCodes.Ldc_I4_1,
            2 => EmitOpCodes.Ldc_I4_2,
            3 => EmitOpCodes.Ldc_I4_3,
            4 => EmitOpCodes.Ldc_I4_4,
            5 => EmitOpCodes.Ldc_I4_5,
            6 => EmitOpCodes.Ldc_I4_6,
            7 => EmitOpCodes.Ldc_I4_7,
            8 => EmitOpCodes.Ldc_I4_8,
            _ => default,
        };

    private static void Print(IReadOnlyList<BenchmarkResult> results)
    {
        Console.WriteLine("| Case | Total | ns/op | ops/sec | B/op | Checksum |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: |");

        foreach (var result in results)
        {
            var nsPerOp = result.Elapsed.TotalMilliseconds * 1_000_000.0 / result.Iterations;
            var opsPerSecond = result.Iterations / result.Elapsed.TotalSeconds;
            var bytesPerOp = result.AllocatedBytes / (double)result.Iterations;
            Console.WriteLine(
                $"| {result.Name} | {result.Elapsed.TotalMilliseconds:F2} ms | {nsPerOp:F1} | {opsPerSecond:N0} | {bytesPerOp:F1} | {result.Checksum} |");
        }

        Console.WriteLine();
        Console.WriteLine("Run with: dotnet run -c Release --project benchmarks/MonoWeaver.HookBenchmarks -- --iterations 20000 --warmup 2000");
    }

    private static void Probe()
    {
    }

    private sealed record BenchmarkResult(
        string Name,
        int Iterations,
        TimeSpan Elapsed,
        long AllocatedBytes,
        int Checksum);

    private sealed record BenchmarkOptions(int Iterations, int WarmupIterations)
    {
        public static BenchmarkOptions Parse(string[] args)
        {
            var iterations = GetInt(args, "--iterations", 5_000);
            var warmup = GetInt(args, "--warmup", Math.Min(500, Math.Max(0, iterations / 10)));
            return new BenchmarkOptions(Math.Max(1, iterations), Math.Max(0, warmup));
        }

        private static int GetInt(string[] args, string name, int defaultValue)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(args[i + 1], out var value))
                {
                    return value;
                }
            }

            return defaultValue;
        }
    }

    private sealed record CecilWorkload(IReadOnlyList<MethodDefinition> Methods, MethodReference Callback)
    {
        public static CecilWorkload Create(int count, string suffix)
        {
            var module = ModuleDefinition.CreateModule("MonoWeaver.HookBenchmarks." + suffix, ModuleKind.Dll);
            var type = new TypeDefinition(
                "MonoWeaver.HookBenchmarks.Generated",
                "Target",
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
                module.TypeSystem.Object);
            module.Types.Add(type);

            var methods = new MethodDefinition[count];
            for (var i = 0; i < count; i++)
            {
                var method = new MethodDefinition(
                    "Target" + i,
                    MethodAttributes.Public | MethodAttributes.Static,
                    module.TypeSystem.Int32);
                method.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.Int32));
                type.Methods.Add(method);

                var il = method.Body.GetILProcessor();
                il.Emit(CecilOpCodes.Ldarg_0);
                il.Emit(CecilOpCodes.Ldc_I4_1);
                il.Emit(CecilOpCodes.Add);
                il.Emit(CecilOpCodes.Ldc_I4_2);
                il.Emit(CecilOpCodes.Mul);
                il.Emit(CecilOpCodes.Ret);

                methods[i] = method;
            }

            return new CecilWorkload(methods, module.ImportReference(ProbeReflection));
        }
    }

    private sealed record HarmonyWorkload(IReadOnlyList<List<CodeInstruction>> Instructions)
    {
        public static HarmonyWorkload Create(int count)
        {
            var methods = new List<CodeInstruction>[count];
            for (var i = 0; i < count; i++)
            {
                methods[i] =
                [
                    new CodeInstruction(EmitOpCodes.Ldarg_0),
                    new CodeInstruction(EmitOpCodes.Ldc_I4_1),
                    new CodeInstruction(EmitOpCodes.Add),
                    new CodeInstruction(EmitOpCodes.Ldc_I4_2),
                    new CodeInstruction(EmitOpCodes.Mul),
                    new CodeInstruction(EmitOpCodes.Ret),
                ];
            }

            return new HarmonyWorkload(methods);
        }
    }
}
