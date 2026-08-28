using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;
using MonoWeaver.Utils;
using Xunit;

namespace MonoWeaver.PatternTests;

/// <summary>
/// 运行时打补丁场景下的元数据解析回归测试。
/// MonoMod 的 DMD 模块自带的 resolver 只搜索磁盘目录，找不到 mod 目录里的 MonoWeaver.dll，
/// 也找不到只存在于内存里的 MonoWeaver.Generated.* 程序集；这里模拟这一环境。
/// </summary>
public sealed class RuntimeResolutionTests
{
    public static class Targets
    {
        public static float Scale(float value) => value * 1.4f;
    }

    [Fact]
    public void FullVerificationInsideMonoModDmdResolvesAssembliesLoadedInProcess()
    {
        var method = typeof(Targets).GetMethod(nameof(Targets.Scale))
                     ?? throw new InvalidOperationException("Missing fixture method.");

        using var dmd = new DynamicMethodDefinition(method);
        using var context = new ILContext(dmd.Definition);

        using (TemporaryCurrentDirectory.Enter())
        {
            // 前置条件：DMD 模块自己的 resolver 在这个工作目录下找不到 MonoWeaver
            //（MonoMod 19.x 在这里抛 NullReferenceException，游戏里是 AssemblyResolutionException）。
            var monoWeaverName = AssemblyNameReference.Parse(typeof(CallArguments).Assembly.FullName);
            Assert.ThrowsAny<Exception>(
                () => dmd.Definition.Module.AssemblyResolver.Resolve(monoWeaverName));

            context.Invoke(il =>
            {
                try
                {
                    il.Method.Match(Cil.Value(() => 1.4f))
                        .Single()
                        .Transform((float orig) => 300f)
                        .Apply(VerifyOptions.Full);
                }
                catch (ILMethodVerifier.CfgVerifyException e)
                {
                    Assert.Fail(e.Message + Environment.NewLine +
                                string.Join(Environment.NewLine, e.Diagnostics.Select(d => "  " + d)));
                }
            });
        }

        var generated = dmd.Generate();
        Assert.Equal(600f, (float)generated.Invoke(null, new object[] { 2f })!);
    }

    [Fact]
    public void RuntimeAssemblyResolverBindsToAssembliesLoadedInProcess()
    {
        var resolver = RuntimeAssemblyResolver.Instance;

        var monoWeaver = resolver.Resolve(AssemblyNameReference.Parse(typeof(CallArguments).Assembly.FullName));
        Assert.NotNull(monoWeaver);
        Assert.Equal(typeof(CallArguments).Assembly.GetName().Name, monoWeaver!.Name.Name);
        Assert.Same(monoWeaver, resolver.Resolve(AssemblyNameReference.Parse(typeof(CallArguments).Assembly.FullName)));

        Assert.Null(resolver.Resolve(new AssemblyNameReference("MonoWeaver.Tests.DoesNotExist", new Version(1, 0, 0, 0))));
    }

    [Fact]
    public void RuntimeMetadataResolverResolvesDelegateStoreGetter()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var getter = typeof(CecilDelegateReferenceStore<Func<float, float>>).GetMethod(
            nameof(CecilDelegateReferenceStore<Func<float, float>>.Get))
            ?? throw new InvalidOperationException("Missing getter.");
        var reference = module.ImportReference(getter);

        var metadata = new MetadataResolver(RuntimeAssemblyResolver.Instance);
        var definition = metadata.Resolve(reference);

        Assert.NotNull(definition);
        Assert.Equal("Get", definition!.Name);
        Assert.True(definition.IsStatic);
    }

    [Fact]
    public void UnresolvableReferenceIsReportedAsDiagnosticInsteadOfThrowing()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var missingAssembly = new AssemblyNameReference("MonoWeaver.Tests.DoesNotExist", new Version(1, 0, 0, 0));
        module.AssemblyReferences.Add(missingAssembly);
        var missingType = new TypeReference("Missing", "Helper", module, missingAssembly);
        var missingMethod = new MethodReference("Run", module.TypeSystem.Void, missingType);

        var host = new TypeDefinition("MonoWeaver.Tests", "Host",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
        module.Types.Add(host);
        var method = new MethodDefinition("Call", MethodAttributes.Public | MethodAttributes.Static,
            module.TypeSystem.Void);
        host.Methods.Add(method);
        var il = method.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Call, missingMethod));
        il.Append(Instruction.Create(OpCodes.Ret));

        var verifier = method.Verify(VerifyOptions.Full);

        Assert.Contains(verifier.Diagnostics,
            diagnostic => diagnostic.Type == CFGExceptionType.ResolveFailed);
    }

    private sealed class TemporaryCurrentDirectory : IDisposable
    {
        private readonly string _previous;
        private readonly string _directory;

        private TemporaryCurrentDirectory()
        {
            _previous = Directory.GetCurrentDirectory();
            _directory = Path.Combine(Path.GetTempPath(), "MonoWeaver.Tests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Directory.SetCurrentDirectory(_directory);
        }

        public static TemporaryCurrentDirectory Enter() => new();

        public void Dispose()
        {
            Directory.SetCurrentDirectory(_previous);
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
