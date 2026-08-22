using System.Text;

namespace MonoWeaver.Benchmarks;

internal sealed record BenchmarkOptions(
    int Iterations,
    int WarmupIterations,
    int Rounds,
    int SampleRounds,
    int WarmupMethods,
    bool PerFlag,
    bool VerifyOnly,
    bool PatchOnly,
    double MaxMethodUs,
    IReadOnlyList<string> Assemblies)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        var iterations = Math.Max(1, GetInt(args, "--iterations", 5_000));
        return new BenchmarkOptions(
            Iterations: iterations,
            WarmupIterations: Math.Max(0, GetInt(args, "--warmup", Math.Min(500, iterations / 10))),
            Rounds: Math.Max(1, GetInt(args, "--rounds", 3)),
            SampleRounds: Math.Max(1, GetInt(args, "--sample-rounds", 2)),
            WarmupMethods: Math.Max(0, GetInt(args, "--warmup-methods", 300)),
            PerFlag: HasFlag(args, "--flags"),
            VerifyOnly: HasFlag(args, "--verify-only"),
            PatchOnly: HasFlag(args, "--patch-only"),
            MaxMethodUs: GetDouble(args, "--max-method-us", 0),
            Assemblies: GetAll(args, "--assembly"));
    }

    private static bool HasFlag(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static int GetInt(string[] args, string name, int defaultValue)
        => int.TryParse(GetOne(args, name), out var value) ? value : defaultValue;

    private static double GetDouble(string[] args, string name, double defaultValue)
        => double.TryParse(GetOne(args, name), out var value) ? value : defaultValue;

    private static string? GetOne(string[] args, string name)
        => GetAll(args, name).FirstOrDefault();

    private static List<string> GetAll(string[] args, string name)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                values.Add(args[i + 1]);
        }

        return values;
    }
}

internal static class Program
{
    /// <summary>没有 --assembly 时，默认拿输出目录里这些真实程序集当语料。</summary>
    private static readonly string[] DefaultCorpus =
    [
        "Mono.Cecil.dll",
        "MonoMod.Utils.dll",
        "MonoWeaver.dll",
    ];

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }

        var options = BenchmarkOptions.Parse(args);
        var exitCode = 0;

        if (!options.PatchOnly)
            exitCode |= RunVerification(options);

        if (!options.VerifyOnly)
        {
            try
            {
                RunPatching(options);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"打补丁基准失败: {ex.GetType().Name}: {ex.Message}");
                exitCode |= 1;
            }
        }

        return exitCode;
    }

    private static int RunVerification(BenchmarkOptions options)
    {
        var assemblies = ResolveAssemblies(options);
        if (assemblies.Count == 0)
        {
            Console.Error.WriteLine("没有可用的语料程序集，用 --assembly <path> 指定。");
            return 1;
        }

        Console.WriteLine("== IL 验证吞吐 ==");
        Console.WriteLine($"语料: {assemblies.Count} 个程序集, 每项取 {options.Rounds} 轮最快值");
        Console.WriteLine();

        var rows = VerificationBenchmark.Run(assemblies, options);

        Console.WriteLine("| 程序集 | 模式 | 方法 | 指令 | 整体 | us/方法 | p50 | p95 | p99 | max | B/方法 |");
        Console.WriteLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var row in rows)
        {
            Console.WriteLine(
                $"| {row.Assembly} | {row.Mode} | {row.Methods:N0} | {row.Instructions:N0} "
                + $"| {row.BatchMs:F1} ms | {row.BatchMs * 1000 / row.Methods:F1} "
                + $"| {row.PerMethod.P50:F1} | {row.PerMethod.P95:F1} | {row.PerMethod.P99:F1} "
                + $"| {row.PerMethod.Max:F1} | {row.AllocatedBytes / (double)row.Methods:F0} |");
        }

        Console.WriteLine();

        if (options.MaxMethodUs <= 0)
            return 0;

        // 回归闸门：单方法最坏耗时超阈值就失败，用来在 CI 里拦住病态用例
        var offenders = rows.Where(r => r.PerMethod.Max > options.MaxMethodUs).ToList();
        if (offenders.Count == 0)
        {
            Console.WriteLine($"闸门通过: 所有单方法耗时 <= {options.MaxMethodUs:N0} us");
            return 0;
        }

        foreach (var row in offenders)
        {
            Console.Error.WriteLine(
                $"闸门失败: {row.Assembly} [{row.Mode}] 单方法最坏 {row.PerMethod.Max:N0} us "
                + $"> 阈值 {options.MaxMethodUs:N0} us");
        }

        return 1;
    }

    private static void RunPatching(BenchmarkOptions options)
    {
        Console.WriteLine("== 打补丁耗时（横向对比） ==");
        Console.WriteLine($"迭代 {options.Iterations:N0} 次, 预热 {options.WarmupIterations:N0} 次");
        Console.WriteLine("负载: 匹配 `arg0 + 1` 并在其前插入一次调用。");
        Console.WriteLine("MonoMod 不做验证，故与 MonoWeaver Apply() 对等；带验证的单列。");
        Console.WriteLine();

        var rows = PatchBenchmark.Run(options);

        Console.WriteLine("| 用例 | 整体 | ns/次 | 次/秒 | B/次 |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var row in rows)
        {
            Console.WriteLine(
                $"| {row.Name} | {row.BatchMs:F1} ms "
                + $"| {row.BatchMs * 1_000_000 / row.Iterations:F0} "
                + $"| {row.Iterations / (row.BatchMs / 1000):N0} "
                + $"| {row.AllocatedBytes / (double)row.Iterations:F0} |");
        }

        Console.WriteLine();
    }

    private static List<string> ResolveAssemblies(BenchmarkOptions options)
    {
        if (options.Assemblies.Count > 0)
            return options.Assemblies.Where(File.Exists).ToList();

        return DefaultCorpus
            .Select(name => Path.Combine(AppContext.BaseDirectory, name))
            .Where(File.Exists)
            .ToList();
    }
}
