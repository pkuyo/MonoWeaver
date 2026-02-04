namespace MonoWeaver.Test
{
    using Mono.Cecil;
    using MonoWeaver.Utils;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;

    namespace CecilAssignabilityFuzz
    {
        // 少量自定义类型：更贴近真实业务（继承链 + 接口实现）
        public class Base { }
        public class Derived : Base { }
        public interface IMarker { }
        public class MarkerImpl : IMarker { }

        internal static class Program
        {
            static void Main(string[] args)
            {
                int iterations = GetArgInt(args, "--n", 200_0000);
                int seed = GetArgInt(args, "--seed", 20260304);
                int catalogSize = GetArgInt(args, "--catalog", 80);     // 候选类型数量（小）
                int hotSize = GetArgInt(args, "--hot", 14);             // 热门类型数量（更小）
                int maxMismatchesToPrint = GetArgInt(args, "--print", 50);

                var asmPath = Assembly.GetExecutingAssembly().Location;

                var resolver = new DefaultAssemblyResolver();
                AddTrustedPlatformAssemblyDirs(resolver);
                resolver.AddSearchDirectory(Path.GetDirectoryName(asmPath)!);

                var ass = AssemblyDefinition.ReadAssembly(asmPath, new ReaderParameters
                {
                    AssemblyResolver = resolver
                });

                RunFuzz(ass.MainModule, iterations, seed, catalogSize, hotSize, maxMismatchesToPrint);
            }

            static void RunFuzz(ModuleDefinition m, int iterations, int seed, int catalogSize, int hotSize, int maxMismatchesToPrint)
            {
                var rng = new Random(seed);

                // 更真实：先构建一个“小而固定”的候选类型集，然后不断从里面抽样（重复率高）
                var catalog = TypeWorkload.BuildCatalog(rng, catalogSize, hotSize);

                // 更真实：真实代码里很多比较会重复出现（缓存/热点）
                var seenPairs = new List<(Type target, Type source)>(capacity: 4096);

                // 更真实：同一批类型会被频繁 Import（这里做缓存，避免大量无意义对象 churn）
                var importCache = new Dictionary<Type, TypeReference>(capacity: 256);

                int mismatches = 0;
                int skipped = 0;

                long clrTicks = 0;
                long cecilTicks = 0;

                var sw = Stopwatch.StartNew();

                for (int i = 0; i < iterations; i++)
                {
                    var (target, source) = TypeWorkload.NextPair(rng, catalog, seenPairs);

                    // 兜底过滤（理论上不会触发太多）
                    if (target == null || source == null || target.ContainsGenericParameters || source.ContainsGenericParameters)
                    {
                        skipped++;
                        continue;
                    }

                    try
                    {
                        // CLR
                        long t0 = Stopwatch.GetTimestamp();
                        bool clr = target.IsAssignableFrom(source);
                        clrTicks += Stopwatch.GetTimestamp() - t0;

                        // Cecil
                        var trTarget = ImportCached(m, importCache, target);
                        var trSource = ImportCached(m, importCache, source);

                        t0 = Stopwatch.GetTimestamp();
                        bool cecil = trTarget.IsAssignableFrom(trSource);
                        cecilTicks += Stopwatch.GetTimestamp() - t0;

                        if (cecil != clr)
                        {
                            mismatches++;
                            Console.WriteLine($"Mismatch #{mismatches} (case {i}, seed {seed})");
                            Console.WriteLine($"  target: {Pretty(target)}");
                            Console.WriteLine($"  source: {Pretty(source)}");
                            Console.WriteLine($"  Cecil: {cecil}   CLR: {clr}");
                            Console.WriteLine();
                            trTarget.IsAssignableFrom(trSource);
                            if (mismatches >= maxMismatchesToPrint)
                                break;
                        }
                    }
                    catch
                    {
                        skipped++;
                    }
                }

                double tickToMs = 1000.0 / Stopwatch.Frequency;
                Console.WriteLine($"Done. iterations={iterations}, mismatches={mismatches}, skipped/errors={skipped}, seed={seed}");
                Console.WriteLine($"Catalog size={catalog.All.Count}, Hot size={catalog.Hot.Count}");
                Console.WriteLine($"Elapsed={sw.ElapsedMilliseconds}ms");
                Console.WriteLine($"CLR cost={(clrTicks * tickToMs):F2}ms, Cecil cost={(cecilTicks * tickToMs):F2}ms");
            }

            static TypeReference ImportCached(ModuleDefinition m, Dictionary<Type, TypeReference> cache, Type t)
            {
                if (cache.TryGetValue(t, out var tr)) return tr;
                tr = m.ImportReference(t);
                cache[t] = tr;
                return tr;
            }

            static string Pretty(Type t)
            {
                if (!t.IsGenericType) return t.FullName ?? t.ToString();
                var def = t.GetGenericTypeDefinition();
                var name = (def.FullName ?? def.Name);
                var tick = name.IndexOf('`');
                if (tick >= 0) name = name.Substring(0, tick);
                var args = string.Join(", ", t.GetGenericArguments().Select(Pretty));
                return $"{name}<{args}>";
            }

            static int GetArgInt(string[] args, string key, int defaultValue)
            {
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(args[i + 1], out var v))
                        return v;
                }
                return defaultValue;
            }

            static void AddTrustedPlatformAssemblyDirs(DefaultAssemblyResolver resolver)
            {
                var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
                if (string.IsNullOrEmpty(tpa))
                {
                    var coreDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
                    if (!string.IsNullOrEmpty(coreDir))
                        resolver.AddSearchDirectory(coreDir);
                    return;
                }

                var dirs = tpa.Split(Path.PathSeparator)
                              .Select(p => Path.GetDirectoryName(p))
                              .Where(d => !string.IsNullOrEmpty(d))
                              .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var d in dirs!)
                    resolver.AddSearchDirectory(d!);
            }
        }

        internal static class TypeWorkload
        {
            // 更现实：少量“常见叶子类型”
            private static readonly Type[] Leaf =
            {
            typeof(object),
            typeof(string),
            typeof(int),
            typeof(int?),
            typeof(Exception),
            typeof(ArgumentException),
            typeof(IDisposable),
            typeof(Stream),
            typeof(MemoryStream),

            typeof(Base),
            typeof(Derived),
            typeof(IMarker),
            typeof(MarkerImpl),
        };

            // 更现实：少量常见泛型模板（不要太多）
            private static readonly Type[] GenDef =
            {
            typeof(List<>),
            typeof(IEnumerable<>),        // out T
            typeof(IList<>),
            typeof(Dictionary<,>),
            typeof(Func<,>),              // in T, out TResult
            typeof(Action<>),             // in T
            typeof(Nullable<>),
        };

            // 更现实：工程里经常被拿来当“target”的类型（反复判断）
            private static readonly Type[] CommonTargets =
            {
            typeof(object),
            typeof(IDisposable),
            typeof(IEnumerable<object>),
            typeof(Exception),
            typeof(Base),
        };

            internal sealed class Catalog
            {
                public List<Type> All { get; } = new();
                public List<Type> Hot { get; } = new();
            }

            public static Catalog BuildCatalog(Random rng, int allSize, int hotSize)
            {
                var c = new Catalog();

                // 先放一些手工的高频闭合类型（更像真实项目）
                var fixedOnes = new[]
                {
                typeof(object),
                typeof(string),
                typeof(int),
                typeof(int?),
                typeof(Base),
                typeof(Derived),
                typeof(IMarker),
                typeof(MarkerImpl),
                typeof(Exception),
                typeof(ArgumentException),
                typeof(IDisposable),
                typeof(Stream),
                typeof(MemoryStream),

                typeof(Base[]),
                typeof(Derived[]),
                typeof(string[]),

                typeof(List<int>),
                typeof(List<string>),
                typeof(List<object>),
                typeof(IEnumerable<int>),
                typeof(IEnumerable<string>),
                typeof(IEnumerable<object>),
                typeof(IList<string>),
                typeof(Dictionary<string, object>),

                typeof(Action<object>),
                typeof(Action<string>),
                typeof(Func<object, string>),
                typeof(Func<string, object>),
            };

                foreach (var t in fixedOnes.Distinct())
                    c.All.Add(t);

                // 再补一些随机闭合类型，但规模小，且结构浅（重复率更高）
                while (c.All.Count < allSize)
                {
                    var t = BuildType(rng, depth: 2);
                    if (t == null) continue;
                    if (t.ContainsGenericParameters) continue;

                    if (!c.All.Contains(t))
                        c.All.Add(t);
                }

                // Hot：从 All 中挑一小撮（更高概率出现）
                // 让 fixedOnes 优先进入 hot，真实里就是这些最常比
                foreach (var t in fixedOnes)
                {
                    if (c.Hot.Count >= hotSize) break;
                    if (c.All.Contains(t) && !c.Hot.Contains(t))
                        c.Hot.Add(t);
                }

                while (c.Hot.Count < hotSize)
                {
                    var t = c.All[rng.Next(c.All.Count)];
                    if (!c.Hot.Contains(t))
                        c.Hot.Add(t);
                }

                return c;
            }

            public static (Type target, Type source) NextPair(Random rng, Catalog c, List<(Type target, Type source)> seenPairs)
            {
                // 更现实：大量重复判断（复用历史 pair）
                // 例如：同一个框架里反复判断某个 source 是否实现 IDisposable / IEnumerable<T> 等
                if (seenPairs.Count > 0 && rng.NextDouble() < 0.55)
                    return seenPairs[rng.Next(seenPairs.Count)];

                (Type target, Type source) pair;

                // 1) 常见：固定“target”反复测试不同“source”
                if (rng.NextDouble() < 0.45)
                {
                    var target = CommonTargets[rng.Next(CommonTargets.Length)];
                    var source = PickType(rng, c);
                    pair = (target, source);
                }
                // 2) 常见：相关 pair（更容易出现 true & 边界）
                else if (rng.NextDouble() < 0.70)
                {
                    pair = MakeRelatedPair(rng, c);
                }
                // 3) 少量完全随机
                else
                {
                    pair = (PickType(rng, c), PickType(rng, c));
                }

                // 记录一下，增强后续重复率（但别无限增长）
                if (seenPairs.Count < 8000)
                    seenPairs.Add(pair);

                return pair;
            }

            private static Type PickType(Random rng, Catalog c)
            {
                // 更现实：更倾向从 hot 池抽（重复率高）
                if (rng.NextDouble() < 0.78)
                    return c.Hot[rng.Next(c.Hot.Count)];
                return c.All[rng.Next(c.All.Count)];
            }

            private static (Type target, Type source) MakeRelatedPair(Random rng, Catalog c)
            {
                // 继承：Derived -> Base
                if (rng.NextDouble() < 0.20)
                    return (typeof(Base), typeof(Derived));

                // 接口实现：MarkerImpl -> IMarker
                if (rng.NextDouble() < 0.10)
                    return (typeof(IMarker), typeof(MarkerImpl));

                // 数组协变（引用类型数组）：Derived[] -> Base[]
                if (rng.NextDouble() < 0.12)
                    return (typeof(Base[]), typeof(Derived[]));

                // IEnumerable<out T> 协变：IEnumerable<Derived> -> IEnumerable<Base>
                if (rng.NextDouble() < 0.18)
                    return (typeof(IEnumerable<Base>), typeof(IEnumerable<Derived>));

                // Action<in T> 逆变：Action<object> -> Action<Base>（target=Action<Base>, source=Action<object>）
                if (rng.NextDouble() < 0.16)
                    return (typeof(Action<Base>), typeof(Action<object>));

                // Func<in T, out TResult>：Func<Base, Derived> -> Func<Derived, Base>
                if (rng.NextDouble() < 0.16)
                    return (typeof(Func<Derived, Base>), typeof(Func<Base, Derived>));

                // List<T> / IEnumerable<T>：IEnumerable<T> 是 target，List<T> 是 source
                if (rng.NextDouble() < 0.25)
                {
                    var t = PickReasonableElem(rng);
                    return (typeof(IEnumerable<>).MakeGenericType(t), typeof(List<>).MakeGenericType(t));
                }

                // 兜底：hot 里随便凑（仍然容易重复）
                return (PickType(rng, c), PickType(rng, c));
            }

            private static Type PickReasonableElem(Random rng)
            {
                // 元素类型尽量挑“常见且可泛型”的，避免稀奇古怪导致约束/构造失败
                var pool = new[]
                {
                typeof(int),
                typeof(string),
                typeof(object),
                typeof(Base),
                typeof(Derived),
                typeof(Exception),
            };
                return pool[rng.Next(pool.Length)];
            }

            private static Type? BuildType(Random rng, int depth)
            {
                if (depth <= 0)
                    return Leaf[rng.Next(Leaf.Length)];

                var roll = rng.Next(100);

                // 65%：叶子（更真实：大多数都不是复杂嵌套）
                if (roll < 65)
                    return Leaf[rng.Next(Leaf.Length)];

                // 15%：数组（一维）
                if (roll < 80)
                {
                    var elem = BuildType(rng, depth - 1);
                    if (elem == null) return null;
                    if (elem == typeof(void) || elem.IsByRef || elem.IsPointer) return null;
                    return elem.MakeArrayType();
                }

                // 20%：泛型（浅）
                var def = GenDef[rng.Next(GenDef.Length)];
                var ga = def.GetGenericArguments();
                var args = new Type[ga.Length];

                for (int i = 0; i < args.Length; i++)
                    args[i] = BuildType(rng, depth - 1) ?? typeof(object);

                if (def == typeof(Nullable<>))
                {
                    var t = args[0];
                    if (!t.IsValueType) return null;
                    if (IsNullableType(t)) return null;
                }

                try
                {
                    return def.MakeGenericType(args);
                }
                catch
                {
                    return null;
                }
            }

            private static bool IsNullableType(Type t) =>
                t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>);
        }
    }

}
