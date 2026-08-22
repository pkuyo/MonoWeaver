using Mono.Cecil;
using MonoWeaver.CFG;

namespace MonoWeaver.Benchmarks;

internal sealed record VerifyRow(
    string Assembly,
    string Mode,
    int Methods,
    int Instructions,
    double BatchMs,
    Distribution PerMethod,
    long AllocatedBytes);

/// <summary>
/// 主指标：对真实程序集里的每个方法体跑验证器。
/// 关心的是"启动时多花多少时间"和最坏单方法耗时，而不是合成方法的微基准。
/// </summary>
internal static class VerificationBenchmark
{
    private static readonly (string Name, VerifyOptions Options)[] DefaultModes =
    [
        ("Light", VerifyOptions.Light),
        ("Full", VerifyOptions.Full),
    ];

    private static readonly (string Name, VerifyOptions Options)[] FlagModes =
    [
        ("Instructions", VerifyOptions.Instructions),
        ("StackBalance", VerifyOptions.StackBalance),
        ("StackTypes", VerifyOptions.StackTypes),
        ("LocalInit", VerifyOptions.LocalInit),
        ("AccessTest", VerifyOptions.AccessTest),
        ("Light", VerifyOptions.Light),
        ("Full", VerifyOptions.Full),
    ];

    public static List<VerifyRow> Run(IReadOnlyList<string> assemblyPaths, BenchmarkOptions options)
    {
        var modes = options.PerFlag ? FlagModes : DefaultModes;
        var rows = new List<VerifyRow>();

        foreach (var path in assemblyPaths)
        {
            using var corpus = AssemblyCorpus.Open(path);
            if (corpus is null)
            {
                Console.Error.WriteLine($"  跳过 {Path.GetFileName(path)}：无法读取");
                continue;
            }

            var methods = corpus.Methods;
            if (methods.Count == 0)
                continue;

            var instructions = methods.Sum(m => m.Body.Instructions.Count);

            foreach (var (name, verifyOptions) in modes)
            {
                // 预热：让类型系统缓存和 JIT 都进入稳态
                foreach (var method in methods.Take(Math.Min(methods.Count, options.WarmupMethods)))
                    Verify(method, verifyOptions);

                var batchMs = Stats.BestOf(options.Rounds, () =>
                {
                    foreach (var method in methods)
                        Verify(method, verifyOptions);
                });

                var allocated = Stats.MeasureAllocated(() =>
                {
                    foreach (var method in methods)
                        Verify(method, verifyOptions);
                });

                var perMethod = Stats.MeasureEach(methods, m => Verify(m, verifyOptions), options.SampleRounds);

                rows.Add(new VerifyRow(
                    corpus.Name, name, methods.Count, instructions, batchMs, perMethod, allocated));
            }
        }

        return rows;
    }

    /// <summary>非法 IL 会抛验证中止异常，这里只测耗时，诊断结果不参与。</summary>
    private static void Verify(MethodDefinition method, VerifyOptions options)
    {
        try
        {
            new ILMethodVerifier(method, options).Verify();
        }
        catch (ILMethodVerifier.CfgVerifyException)
        {
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>按 Cecil 打开一个程序集，枚举出所有有方法体的方法。</summary>
internal sealed class AssemblyCorpus : IDisposable
{
    private readonly ModuleDefinition _module;

    private AssemblyCorpus(string name, ModuleDefinition module, List<MethodDefinition> methods)
    {
        Name = name;
        _module = module;
        Methods = methods;
    }

    public string Name { get; }

    public List<MethodDefinition> Methods { get; }

    public static AssemblyCorpus? Open(string path)
    {
        try
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            resolver.AddSearchDirectory(AppContext.BaseDirectory);
            var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (!string.IsNullOrEmpty(runtimeDirectory))
                resolver.AddSearchDirectory(runtimeDirectory);

            var module = ModuleDefinition.ReadModule(path, new ReaderParameters
            {
                AssemblyResolver = resolver,
                ReadSymbols = false,
                InMemory = true,
            });

            var methods = Flatten(module.Types)
                .SelectMany(t => t.Methods)
                .Where(m => m.HasBody && m.Body.Instructions.Count > 0)
                .ToList();

            return new AssemblyCorpus(Path.GetFileName(path), module, methods);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IEnumerable<TypeDefinition> Flatten(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in Flatten(type.NestedTypes))
                yield return nested;
        }
    }

    public void Dispose() => _module.Dispose();
}
