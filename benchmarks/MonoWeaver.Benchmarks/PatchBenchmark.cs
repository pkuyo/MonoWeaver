using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoWeaver.Cecil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;
using CecilCode = Mono.Cecil.Cil.Code;
using CecilOpCodes = Mono.Cecil.Cil.OpCodes;

namespace MonoWeaver.Benchmarks;

/// <summary>被插入的回调。必须公开：合成模块在另一个程序集里调用它，Full 验证会检查访问性。</summary>
public static class BenchmarkProbe
{
    public static void Probe()
    {
    }
}

internal sealed record PatchRow(string Name, int Iterations, double BatchMs, long AllocatedBytes, int Checksum);

/// <summary>
/// 横向对比：在同一份方法体上找到 `arg0 + 1` 并在其前插入一次调用。
/// MonoMod 不做任何验证，所以 MonoWeaver 也要用不验证的 Apply() 才是对等比较；
/// Apply(Light)/Apply(Full) 单列，用来看验证本身加了多少。
/// </summary>
internal static class PatchBenchmark
{
    private static readonly MethodInfo ProbeReflection =
        typeof(BenchmarkProbe).GetMethod(nameof(BenchmarkProbe.Probe), BindingFlags.Static | BindingFlags.Public)
        ?? throw new MissingMethodException(typeof(BenchmarkProbe).FullName, nameof(BenchmarkProbe.Probe));

    private static readonly ValuePattern Pattern = Cil.Value(P.Arg(0, CilType.Int32) + 1);

    public static List<PatchRow> Run(BenchmarkOptions options)
    {
        var cases = new (string Name, Action<MethodDefinition, MethodReference> Body)[]
        {
            ("MonoMod ILCursor (无验证)", MonoModILCursor),
            ("MonoWeaver Apply() (无验证)", (m, cb) => MonoWeaverApply(m, cb, null)),
            ("MonoWeaver Apply(Light)", (m, cb) => MonoWeaverApply(m, cb, VerifyOptions.Light)),
            ("MonoWeaver Apply(Full)", (m, cb) => MonoWeaverApply(m, cb, VerifyOptions.Full)),
        };

        var rows = new List<PatchRow>();
        foreach (var (name, body) in cases)
        {
            // 每次迭代都要一份没被改写过的方法体，所以工作负载按迭代次数预生成
            if (options.WarmupIterations > 0)
            {
                using var warm = Workload.Create(options.WarmupIterations, "Warmup");
                for (var i = 0; i < options.WarmupIterations; i++)
                    body(warm.Methods[i], warm.Callback);
            }

            using var workload = Workload.Create(options.Iterations, name);
            Stats.Collect();
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var ms = Stats.BestOf(1, () =>
            {
                for (var i = 0; i < options.Iterations; i++)
                    body(workload.Methods[i], workload.Callback);
            });
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            var checksum = workload.Methods.Sum(m => m.Body.Instructions.Count);
            rows.Add(new PatchRow(name, options.Iterations, ms, allocated, checksum));
        }

        return rows;
    }

    private static void MonoModILCursor(MethodDefinition method, MethodReference callback)
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
    }

    private static void MonoWeaverApply(MethodDefinition method, MethodReference callback, VerifyOptions? verify)
    {
        var plan = method.Match(Pattern).Single().Before(callback);
        if (verify is { } options)
            plan.Apply(options);
        else
            plan.Apply();
    }

    private static bool IsLdarg0(Instruction instruction)
        => instruction.OpCode.Code switch
        {
            CecilCode.Ldarg_0 => true,
            CecilCode.Ldarg or CecilCode.Ldarg_S => instruction.Operand is ParameterDefinition { Index: 0 },
            _ => false,
        };

    private static bool IsLdcI4(Instruction instruction, int value)
        => instruction.OpCode.Code switch
        {
            CecilCode.Ldc_I4_0 => value == 0,
            CecilCode.Ldc_I4_1 => value == 1,
            CecilCode.Ldc_I4_2 => value == 2,
            CecilCode.Ldc_I4_S => instruction.Operand is sbyte s && s == value,
            CecilCode.Ldc_I4 => instruction.Operand is int v && v == value,
            _ => false,
        };

    private sealed class Workload : IDisposable
    {
        private readonly ModuleDefinition _module;

        private Workload(ModuleDefinition module, MethodDefinition[] methods, MethodReference callback)
        {
            _module = module;
            Methods = methods;
            Callback = callback;
        }

        public MethodDefinition[] Methods { get; }

        public MethodReference Callback { get; }

        public static Workload Create(int count, string suffix)
        {
            //合成模块会引用 Probe，所以 resolver 必须能找到 benchmark 自身程序集
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(AppContext.BaseDirectory);
            var module = ModuleDefinition.CreateModule(
                "MonoWeaver.Benchmarks." + Sanitize(suffix),
                new ModuleParameters { Kind = ModuleKind.Dll, AssemblyResolver = resolver });
            var type = new TypeDefinition(
                "MonoWeaver.Benchmarks.Generated",
                "Target",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract
                    | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.BeforeFieldInit,
                module.TypeSystem.Object);
            module.Types.Add(type);

            var methods = new MethodDefinition[count];
            for (var i = 0; i < count; i++)
            {
                var method = new MethodDefinition(
                    "Target" + i,
                    Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                    module.TypeSystem.Int32);
                method.Parameters.Add(new ParameterDefinition(
                    "value", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Int32));
                type.Methods.Add(method);

                var il = method.Body.GetILProcessor();
                il.Emit(CecilOpCodes.Ldarg_0);
                il.Emit(CecilOpCodes.Ldc_I4_1);
                il.Emit(CecilOpCodes.Add);
                il.Emit(CecilOpCodes.Ldc_I4_2);
                il.Emit(CecilOpCodes.Mul);
                il.Emit(CecilOpCodes.Ret);
                method.Body.MaxStackSize = 2;

                methods[i] = method;
            }

            return new Workload(module, methods, module.ImportReference(ProbeReflection));
        }

        private static string Sanitize(string value)
            => new(value.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

        public void Dispose() => _module.Dispose();
    }
}
