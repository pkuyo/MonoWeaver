using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Mono.Cecil;

namespace MonoWeaver.Utils;

/// <summary>
/// 按"当前进程里已加载的程序集"解析 <see cref="AssemblyNameReference"/>。
/// </summary>
/// <remarks>
/// 运行时打补丁时（MonoMod ILHook 等），目标模块自带的 resolver 只描述磁盘目录，
/// 而这段 IL 最终由 CLR 按已加载程序集绑定。本 resolver 让校验器和 CLR 使用同一个范围：
/// <list type="bullet">
/// <item>MonoWeaver 自己生成并通过 <see cref="Assembly.Load(byte[])"/> 加载的内存程序集，直接返回登记的 <see cref="AssemblyDefinition"/>；</item>
/// <item>其它已加载程序集按 <see cref="Assembly.Location"/> 读取一次并缓存；</item>
/// <item>没有加载的程序集返回 null（运行时同样无法绑定），不去磁盘搜索。</item>
/// </list>
/// 进程级单例；作为 <see cref="ReaderParameters.AssemblyResolver"/> 传入模块时 <see cref="Dispose"/> 是空操作。
/// </remarks>
public sealed class RuntimeAssemblyResolver : IAssemblyResolver
{
    public static RuntimeAssemblyResolver Instance { get; } = new();

    private readonly ConcurrentDictionary<string, AssemblyDefinition> _inMemory =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<Assembly, AssemblyDefinition?> _loaded = new();

    private RuntimeAssemblyResolver()
    {
    }

    /// <summary>登记一个只存在于内存里的程序集定义（已通过 <see cref="Assembly.Load(byte[])"/> 加载）。</summary>
    public void RegisterInMemory(AssemblyDefinition assembly)
    {
        if (assembly is null)
            throw new ArgumentNullException(nameof(assembly));
        _inMemory[assembly.Name.Name] = assembly;
    }

    public AssemblyDefinition? Resolve(AssemblyNameReference name)
        => Resolve(name, null);

    public AssemblyDefinition? Resolve(AssemblyNameReference name, ReaderParameters? parameters)
    {
        if (name is null)
            throw new ArgumentNullException(nameof(name));

        if (_inMemory.TryGetValue(name.Name, out var inMemory))
            return inMemory;

        var assembly = FindLoadedAssembly(name);
        if (assembly is null)
            return null;

        return _loaded.GetOrAdd(assembly, asm => Read(asm, parameters));
    }

    private static Assembly? FindLoadedAssembly(AssemblyNameReference name)
    {
        Assembly? byName = null;
        Assembly? byVersion = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
                continue;
            var loadedName = assembly.GetName();
            if (!string.Equals(loadedName.Name, name.Name, StringComparison.Ordinal))
                continue;
            if (string.Equals(loadedName.FullName, name.FullName, StringComparison.Ordinal))
                return assembly;
            if (loadedName.Version == name.Version)
                byVersion ??= assembly;
            byName ??= assembly;
        }

        return byVersion ?? byName;
    }

    private static AssemblyDefinition? Read(Assembly assembly, ReaderParameters? parameters)
    {
        string location;
        try
        {
            location = assembly.Location;
        }
        catch (NotSupportedException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(location) || !File.Exists(location))
            return null;

        var readerParameters = new ReaderParameters(parameters?.ReadingMode ?? ReadingMode.Deferred)
        {
            AssemblyResolver = Instance,
            ReadSymbols = false,
        };
        return AssemblyDefinition.ReadAssembly(location, readerParameters);
    }

    /// <summary>单例不持有可释放状态；Cecil 在释放模块时会调用本方法，故为空操作。</summary>
    public void Dispose()
    {
    }
}
