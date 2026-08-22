using System.Diagnostics;

namespace MonoWeaver.Benchmarks;

/// <summary>一批样本的耗时分布。时间单位统一为微秒。</summary>
internal sealed record Distribution(int Count, double[] SortedUs)
{
    public double Total => SortedUs.Sum();
    public double Mean => Count == 0 ? 0 : Total / Count;
    public double P50 => Percentile(0.50);
    public double P95 => Percentile(0.95);
    public double P99 => Percentile(0.99);
    public double Max => Count == 0 ? 0 : SortedUs[Count - 1];

    public double Percentile(double q)
    {
        if (Count == 0)
            return 0;
        var index = (int)Math.Min(Count - 1, Math.Max(0, q * Count));
        return SortedUs[index];
    }
}

internal static class Stats
{
    /// <summary>整批跑 rounds 轮取最快一轮，代表稳态吞吐；返回毫秒。</summary>
    public static double BestOf(int rounds, Action body)
    {
        var best = double.MaxValue;
        for (var i = 0; i < rounds; i++)
        {
            var sw = Stopwatch.StartNew();
            body();
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }

        return best;
    }

    /// <summary>
    /// 逐个样本计时，用于分位数。每个样本取 rounds 轮最小值：
    /// 单次计时会把首次触碰的冷路径算进去，让 max 抖到几毫秒，闸门就没法用了。
    /// </summary>
    public static Distribution MeasureEach<T>(IReadOnlyList<T> items, Action<T> body, int rounds = 2)
    {
        var samples = new double[items.Count];
        for (var i = 0; i < items.Count; i++)
            samples[i] = double.MaxValue;

        for (var round = 0; round < Math.Max(1, rounds); round++)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var sw = Stopwatch.StartNew();
                body(items[i]);
                sw.Stop();
                samples[i] = Math.Min(samples[i], sw.Elapsed.TotalMilliseconds * 1000.0);
            }
        }

        Array.Sort(samples);
        return new Distribution(samples.Length, samples);
    }

    public static long MeasureAllocated(Action body)
    {
        Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        body();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    public static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
