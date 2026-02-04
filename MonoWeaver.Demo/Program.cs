using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using MonoWeaver.Utils;

namespace CecilAssignabilityFuzz
{
    // ====== 为了制造更多可测关系：自定义类型 ======
    public class Base { }
    public class Derived : Base { }
    public class MoreDerived : Derived { }

    public class A<T> { }
    public class B<T> : List<T> { }               // B<T> : List<T> : IEnumerable<T>

    public interface ICovariant<out T> { }
    public interface IContravariant<in T> { }

    public class CovImpl<T> : ICovariant<T> { }
    public class ContraImpl<T> : IContravariant<T> { }

    internal static class Program
    {
        static void Main(string[] args)
        {
            int iterations = GetArgInt(args, "--n", 50_000);
            int maxDepth = GetArgInt(args, "--depth", 3);
            int seed = GetArgInt(args, "--seed", 20260204);
            int maxMismatchesToPrint = GetArgInt(args, "--print", 50);

            var asmPath = Assembly.GetExecutingAssembly().Location;

            var resolver = new DefaultAssemblyResolver();
            AddTrustedPlatformAssemblyDirs(resolver);
            resolver.AddSearchDirectory(Path.GetDirectoryName(asmPath)!);

            var ass = AssemblyDefinition.ReadAssembly(asmPath, new ReaderParameters
            {
                AssemblyResolver = resolver
            });

            var m = ass.MainModule;

            RunFuzz(m, iterations, maxDepth, seed, maxMismatchesToPrint);
        }

        static void RunFuzz(ModuleDefinition m, int iterations, int maxDepth, int seed, int maxMismatchesToPrint)
        {
            var rng = new Random(seed);

            int mismatches = 0;
            int skipped = 0;
            Stopwatch sw = new Stopwatch();
            sw.Start();
            long clrCost = 0;
            long cecilCost = 0;
            for (int i = 0; i < iterations; i++)
            {
                var (type1, type2) = TypeGen.RandomTypePair(rng, maxDepth);

                // 理论上不会为空；但生成失败时兜底
                if (type1 == null || type2 == null || type1.ContainsGenericParameters || type2.ContainsGenericParameters)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    // CLR
                    var start = sw.ElapsedMilliseconds;
                    bool clr = type1.IsAssignableFrom(type2);
                    clrCost += sw.ElapsedMilliseconds - start;
                    // Cecil
                    var tr1 = m.ImportReference(type1);
                    var tr2 = m.ImportReference(type2);
                    start = sw.ElapsedMilliseconds;
                    bool cecil = tr1.IsAssignableFrom(tr2);
                    cecilCost += sw.ElapsedMilliseconds - start;

                    if (cecil != clr)
                    {
                        mismatches++;
                        Console.WriteLine($"Mismatch #{mismatches} (case {i}, seed {seed})");
                        Console.WriteLine($"  type1 (target): {Pretty(type1)}");
                        Console.WriteLine($"  type2 (source): {Pretty(type2)}");
                        Console.WriteLine($"  Cecil: {cecil}   CLR: {clr}");
                        Console.WriteLine();
                        if (mismatches >= maxMismatchesToPrint)
                            break;
                    }
                }
                catch (Exception ex)
                {
                    skipped++;
                }
            }

            Console.WriteLine($"Done. iterations={iterations}, mismatches={mismatches}, skipped/errors={skipped}, seed={seed}, maxDepth={maxDepth}");
            Console.WriteLine($"Clr cost:{clrCost}ms, Cecil cost:{cecilCost}ms");
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

    internal static class TypeGen
    {
        // 基础叶子类型池：类、接口、值类型混一些
        private static readonly Type[] LeafTypes =
        {
            typeof(object),
            typeof(string),
            typeof(int),
            typeof(double),
            typeof(decimal),
            typeof(DateTime),
            typeof(Exception),
            typeof(ArgumentException),
            typeof(Stream),
            typeof(MemoryStream),
            typeof(IDisposable),
            typeof(Base),
            typeof(Derived),
            typeof(MoreDerived),
        };

        // 泛型模板池（只放 open generic defs）
        private static readonly Type[] GenericDefs =
        {
            typeof(List<>),
            typeof(IList<>),
            typeof(IEnumerable<>),        // out T
            typeof(IComparer<>),          // in T
            typeof(Func<,>),              // in T, out TResult
            typeof(Action<>),             // in T
            typeof(Dictionary<,>),
            typeof(KeyValuePair<,>),
            typeof(Nullable<>),
            typeof(A<>),
            typeof(B<>),
            typeof(ICovariant<>),         // out T
            typeof(IContravariant<>),     // in T
            typeof(CovImpl<>),
            typeof(ContraImpl<>),
        };

        public static (Type target, Type source) RandomTypePair(Random rng, int maxDepth)
        {
            // 60% 造“相关”pair（更容易产生 true / 打到边界）
            // 40% 完全随机 pair
            if (rng.NextDouble() < 0.60)
            {
                var baseT = RandomClosedType(rng, maxDepth);
                var (t1, t2) = MakeRelatedPair(rng, baseT, maxDepth);
                return (t1, t2);
            }
            else
            {
                var t1 = RandomClosedType(rng, maxDepth);
                var t2 = RandomClosedType(rng, maxDepth);
                return (t1, t2);
            }
        }

        public static Type RandomClosedType(Random rng, int maxDepth)
        {
            for (int tries = 0; tries < 50; tries++)
            {
                var t = BuildType(rng, maxDepth);
                if (t != null && !t.ContainsGenericParameters)
                    return t;
            }
            return typeof(object);
        }

        private static Type? BuildType(Random rng, int depth)
        {
            if (depth <= 0)
                return PickLeaf(rng);

            var roll = rng.Next(100);

            // 45% 叶子
            if (roll < 45)
                return PickLeaf(rng);

            // 20% 数组（只做一维 SZArray，够覆盖协变）
            if (roll < 65)
            {
                var elem = BuildType(rng, depth - 1);
                if (elem == null) return null;

                // void / byref / pointer 不允许
                if (elem == typeof(void) || elem.IsByRef || elem.IsPointer)
                    return null;

                return elem.MakeArrayType();
            }

            // 35% 泛型构造
            var def = GenericDefs[rng.Next(GenericDefs.Length)];
            var ga = def.GetGenericArguments();
            var args = new Type[ga.Length];

            for (int i = 0; i < args.Length; i++)
                args[i] = BuildType(rng, depth - 1) ?? typeof(object);

            // 处理 Nullable<T> 的特殊限制：T 必须是非 Nullable 的值类型
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

        private static Type PickLeaf(Random rng) => LeafTypes[rng.Next(LeafTypes.Length)];

        private static bool IsNullableType(Type t) =>
            t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>);

        private static (Type target, Type source) MakeRelatedPair(Random rng, Type baseT, int maxDepth)
        {
            // 这里目标：让 pair 更可能覆盖 “true/边界”，同时也会混入 false
            // baseT 作为起点，按情况构造 target/source

            // 1) 继承链：Base <- Derived <- MoreDerived
            if (baseT == typeof(Base))
                return rng.NextDouble() < 0.5 ? (typeof(Base), typeof(Derived)) : (typeof(Base), typeof(MoreDerived));

            if (baseT == typeof(Derived))
                return rng.NextDouble() < 0.5 ? (typeof(Derived), typeof(MoreDerived)) : (typeof(Base), typeof(Derived));

            // 2) 数组协变（引用类型数组才协变）：Derived[] -> Base[]
            if (baseT.IsArray && baseT.GetElementType() == typeof(Base))
                return (typeof(Base[]), typeof(Derived[]));

            // 3) IEnumerable<out T> 协变：IEnumerable<Derived> -> IEnumerable<Base>
            if (rng.NextDouble() < 0.20)
                return (typeof(IEnumerable<Base>), typeof(IEnumerable<Derived>));

            // 4) IComparer<in T> 逆变：IComparer<object> -> IComparer<Base>
            if (rng.NextDouble() < 0.20)
                return (typeof(IComparer<Base>), typeof(IComparer<object>));

            // 5) Func<in T, out TResult>：Func<Base, Derived> -> Func<Derived, Base>
            if (rng.NextDouble() < 0.20)
                return (typeof(Func<Derived, Base>), typeof(Func<Base, Derived>));

            // 6) 自定义 out/in 接口
            if (rng.NextDouble() < 0.20)
                return (typeof(ICovariant<Base>), typeof(ICovariant<Derived>));

            if (rng.NextDouble() < 0.20)
                return (typeof(IContravariant<Derived>), typeof(IContravariant<Base>));

            // 7) 让集合/实现类参与：List<T>/B<T> -> IEnumerable<T>
            if (rng.NextDouble() < 0.30)
            {
                var t = RandomClosedType(rng, Math.Max(0, maxDepth - 1));
                try
                {
                    var target = typeof(IEnumerable<>).MakeGenericType(t);
                    var source = typeof(List<>).MakeGenericType(t);
                    return (target, source);
                }
                catch { /* ignore */ }
            }

            // 兜底：随机 target/source（但保留 baseT 参与）
            var t1 = baseT;
            var t2 = RandomClosedType(rng, maxDepth);
            return (t1, t2);
        }
    }
}