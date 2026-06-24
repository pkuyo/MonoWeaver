using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace MonoWeaver.Utils;

/// <summary>
/// 在 Mono.Cecil 生成的 IL 中发出对运行时委托的调用。
/// </summary>
/// <remarks>
/// 参考 Monomod (https://github.com/MonoMod/MonoMod) 中的 IILReferenceBag。
/// </remarks>
public static class CecilDelegateEmission
{
    public static CecilDelegateCall Prepare<TDelegate>(MethodDefinition target, TDelegate callback)
        where TDelegate : Delegate
    {
        if (target is null)
            throw new ArgumentNullException(nameof(target));
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));

        var invoke = typeof(TDelegate).GetMethod("Invoke")
                     ?? throw new ArgumentException($"'{typeof(TDelegate)}' 不是委托类型。",
                         nameof(callback));

        if (callback.GetInvocationList().Length == 1 && callback.Target is null)
            return PrepareDirectCall(target.Module, callback.Method);
        return PrepareRuntimeCall(target.Module, callback, invoke);
    }

    private static CecilDelegateCall PrepareDirectCall(ModuleDefinition module, MethodInfo method)
    {
        var imported = module.ImportReference(method);
        var parameterTypes = new List<TypeReference>();
        if (imported.HasThis)
        {
            var thisType = imported.DeclaringType.IsValueType
                ? (TypeReference)new ByReferenceType(imported.DeclaringType)
                : imported.DeclaringType;
            parameterTypes.Add(thisType);
        }

        foreach (var parameter in imported.Parameters)
            parameterTypes.Add(parameter.ParameterType);

        return new CecilDelegateCall(imported.ReturnType, parameterTypes, extraStackSlots: 0,
            () => new[] { Instruction.Create(OpCodes.Call, imported) });
    }

    private static CecilDelegateCall PrepareRuntimeCall<TDelegate>(ModuleDefinition module,
        TDelegate callback, MethodInfo invoke)
        where TDelegate : Delegate
    {
        var parameterTypes = invoke.GetParameters()
            .Select(parameter => module.ImportReference(parameter.ParameterType))
            .ToArray();
        var returnType = module.ImportReference(invoke.ReturnType);

        var referenceBag = RuntimeCecilDelegateReferenceBag.Instance;
        var emissionModule = referenceBag.GetEmissionModule(module);
        var referenceId = referenceBag.Store(callback);
        var getReference = referenceBag.GetGetter<TDelegate>(module);
        var invoker = referenceBag.GetDelegateInvoker<TDelegate>(emissionModule.Module, invoke);
        if (!ReferenceEquals(emissionModule.Module, module))
            invoker = module.ImportReference(invoker);

        return new CecilDelegateCall(returnType, parameterTypes, extraStackSlots: 1,
            () => new Instruction[]
            {
                Instruction.Create(OpCodes.Ldc_I4, referenceId),
                Instruction.Create(OpCodes.Call, getReference),
                Instruction.Create(OpCodes.Call, invoker),
            },
            emissionModule.EnsureLoaded);
    }
}

/// <summary>
/// 描述一次已经准备好的委托调用，包括真实签名、额外栈位和待插入指令。
/// </summary>
public sealed class CecilDelegateCall
{
    private readonly Func<IReadOnlyList<Instruction>> _instructionFactory;
    private readonly Action? _beforeApply;

    internal CecilDelegateCall(TypeReference returnType, IReadOnlyList<TypeReference> parameterTypes,
        int extraStackSlots, Func<IReadOnlyList<Instruction>> instructionFactory,
        Action? beforeApply = null)
    {
        ReturnType = returnType ?? throw new ArgumentNullException(nameof(returnType));
        ParameterTypes = parameterTypes ?? throw new ArgumentNullException(nameof(parameterTypes));
        ExtraStackSlots = extraStackSlots;
        _instructionFactory = instructionFactory ?? throw new ArgumentNullException(nameof(instructionFactory));
        _beforeApply = beforeApply;
    }

    public TypeReference ReturnType { get; }
    public IReadOnlyList<TypeReference> ParameterTypes { get; }
    public int ExtraStackSlots { get; }
    public IReadOnlyList<Instruction> CreateInstructions() => _instructionFactory();
    internal void PrepareForApply() => _beforeApply?.Invoke();
}

/// <summary>
/// 文件一内部使用的引用包抽象。
/// </summary>
/// <remarks>
/// 参考 Monomod (https://github.com/MonoMod/MonoMod) 中的 IILReferenceBag。
/// </remarks>
internal interface ICecilDelegateReferenceBag
{
    int Store<TDelegate>(TDelegate callback)
        where TDelegate : Delegate;

    MethodReference GetGetter<TDelegate>(ModuleDefinition module)
        where TDelegate : Delegate;

    MethodReference GetDelegateInvoker<TDelegate>(ModuleDefinition module, MethodInfo invoke)
        where TDelegate : Delegate;
}

/// <summary>
/// 运行时引用包：保存委托，并为每个目标模块和委托类型缓存强类型调用器。
/// </summary>
/// <remarks>
/// 【参考来源】整体结构参考用户提供的文件二中的 RuntimeILReferenceBag 与 GetDelegateInvoker&lt;T&gt;。
/// 文件二通过 DynamicMethodDefinition 在运行时生成调用器；本实现需要让 Cecil 产物可写入程序集，
/// 因而把等价调用器直接生成为目标模块中的内部静态方法。
/// </remarks>
internal sealed class RuntimeCecilDelegateReferenceBag : ICecilDelegateReferenceBag
{
    public static readonly RuntimeCecilDelegateReferenceBag Instance = new();

    private readonly ConditionalWeakTable<ModuleDefinition, ModuleInvokerCache> _moduleCaches = new();
    private readonly ConditionalWeakTable<ModuleDefinition, GeneratedDelegateAssemblyState> _generatedAssemblies = new();

    private RuntimeCecilDelegateReferenceBag()
    {
    }

    public int Store<TDelegate>(TDelegate callback)
        where TDelegate : Delegate
        => CecilDelegateReferenceStore<TDelegate>.Store(callback);

    public DelegateEmissionModule GetEmissionModule(ModuleDefinition module)
    {
        if (module is null)
            throw new ArgumentNullException(nameof(module));

        if (!RequiresGeneratedAssembly(module))
            return new DelegateEmissionModule(module, ensureLoaded: null);

        var state = _generatedAssemblies.GetValue(module,
            static key => new GeneratedDelegateAssemblyState(key));
        var generated = state.GetWritableAssembly();
        return new DelegateEmissionModule(generated.Module, generated.EnsureLoaded);
    }

    public MethodReference GetGetter<TDelegate>(ModuleDefinition module)
        where TDelegate : Delegate
    {
        if (module is null)
            throw new ArgumentNullException(nameof(module));
        return module.ImportReference(CecilDelegateReferenceStore<TDelegate>.GetterMethod);
    }

    public MethodReference GetDelegateInvoker<TDelegate>(ModuleDefinition module, MethodInfo invoke)
        where TDelegate : Delegate
    {
        if (module is null)
            throw new ArgumentNullException(nameof(module));
        if (invoke is null)
            throw new ArgumentNullException(nameof(invoke));

        var cache = _moduleCaches.GetValue(module, static _ => new ModuleInvokerCache());
        var delegateType = typeof(TDelegate);

        lock (cache.Gate)
        {
            if (cache.Invokers.TryGetValue(delegateType, out var cached))
                return cached;

            cache.Container ??= CreateInvokerContainer(module);
            var created = CreateDelegateInvoker<TDelegate>(module, cache.Container, invoke,
                cache.Invokers.Count);
            cache.Invokers.Add(delegateType, created);
            return created;
        }
    }

    private static TypeDefinition CreateInvokerContainer(ModuleDefinition module)
    {
        const string generatedNamespace = "MonoWeaver.Generated";
        const string baseName = "__CecilDelegateInvokers";

        var name = baseName;
        var suffix = 0;
        while (module.Types.Any(type => type.Namespace == generatedNamespace && type.Name == name))
            name = $"{baseName}_{++suffix}";

        var container = new TypeDefinition(generatedNamespace, name,
            Mono.Cecil.TypeAttributes.Public |
            Mono.Cecil.TypeAttributes.Abstract |
            Mono.Cecil.TypeAttributes.Sealed |
            Mono.Cecil.TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        module.Types.Add(container);
        return container;
    }

    private static MethodDefinition CreateDelegateInvoker<TDelegate>(ModuleDefinition module,
        TypeDefinition container, MethodInfo invoke, int index)
        where TDelegate : Delegate
    {
        var importedInvoke = module.ImportReference(invoke);
        var returnType = module.ImportReference(invoke.ReturnType);
        var delegateType = module.ImportReference(typeof(TDelegate));

        var invoker = new MethodDefinition($"Invoke_{index}",
            Mono.Cecil.MethodAttributes.Public |
            Mono.Cecil.MethodAttributes.Static |
            Mono.Cecil.MethodAttributes.HideBySig,
            returnType);

        var reflectionParameters = invoke.GetParameters();
        for (var i = 0; i < reflectionParameters.Length; i++)
        {
            var reflectionParameter = reflectionParameters[i];
            invoker.Parameters.Add(new ParameterDefinition(
                reflectionParameter.Name ?? $"arg{i + 1}",
                Mono.Cecil.ParameterAttributes.None,
                module.ImportReference(reflectionParameter.ParameterType)));
        }

        var callbackParameter = new ParameterDefinition("callback",
            Mono.Cecil.ParameterAttributes.None, delegateType);
        invoker.Parameters.Add(callbackParameter);
        container.Methods.Add(invoker);

        invoker.Body.InitLocals = false;
        invoker.Body.MaxStackSize = checked(reflectionParameters.Length + 1);
        var il = invoker.Body.GetILProcessor();

        il.Append(Instruction.Create(OpCodes.Ldarg, callbackParameter));
        for (var i = 0; i < reflectionParameters.Length; i++)
            il.Append(Instruction.Create(OpCodes.Ldarg, invoker.Parameters[i]));

        il.Append(Instruction.Create(OpCodes.Callvirt, importedInvoke));
        il.Append(Instruction.Create(OpCodes.Ret));
        return invoker;
    }

    private sealed class ModuleInvokerCache
    {
        public object Gate { get; } = new();
        public Dictionary<Type, MethodReference> Invokers { get; } = new();
        public TypeDefinition? Container { get; set; }
    }

    private static bool RequiresGeneratedAssembly(ModuleDefinition module)
        => ContainsInvalidPathChar(module.Name) ||
           ContainsInvalidPathChar(module.Assembly?.Name.Name);

    private static bool ContainsInvalidPathChar(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        var invalid = Path.GetInvalidPathChars();
        return value.Any(ch => invalid.Contains(ch));
    }

    internal readonly struct DelegateEmissionModule
    {
        public DelegateEmissionModule(ModuleDefinition module, Action? ensureLoaded)
        {
            Module = module ?? throw new ArgumentNullException(nameof(module));
            EnsureLoaded = ensureLoaded;
        }

        public ModuleDefinition Module { get; }
        public Action? EnsureLoaded { get; }
    }

    private sealed class GeneratedDelegateAssemblyState
    {
        private readonly ModuleDefinition _sourceModule;
        private readonly object _gate = new();
        private GeneratedDelegateAssembly? _current;

        public GeneratedDelegateAssemblyState(ModuleDefinition sourceModule)
            => _sourceModule = sourceModule ?? throw new ArgumentNullException(nameof(sourceModule));

        public GeneratedDelegateAssembly GetWritableAssembly()
        {
            lock (_gate)
            {
                if (_current is null || _current.IsFrozen)
                    _current = GeneratedDelegateAssembly.Create(_sourceModule);
                return _current;
            }
        }
    }

    private sealed class GeneratedDelegateAssembly
    {
        private static int _nextAssemblyId;

        private readonly object _gate = new();
        private Assembly? _loadedAssembly;
        private bool _isFrozen;

        private GeneratedDelegateAssembly(AssemblyDefinition assembly)
            => Assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));

        public AssemblyDefinition Assembly { get; }
        public ModuleDefinition Module => Assembly.MainModule;

        public bool IsFrozen
        {
            get
            {
                lock (_gate)
                    return _isFrozen;
            }
        }

        public static GeneratedDelegateAssembly Create(ModuleDefinition sourceModule)
        {
            var name = CreateAssemblyName(sourceModule);
            var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition(name, new Version(0, 0, 0, 0)),
                name,
                ModuleKind.Dll);
            return new GeneratedDelegateAssembly(assembly);
        }

        public void EnsureLoaded()
        {
            lock (_gate)
            {
                if (_loadedAssembly is not null)
                    return;

                _isFrozen = true;
                using var stream = new MemoryStream();
                Assembly.Write(stream);
                _loadedAssembly = System.Reflection.Assembly.Load(stream.ToArray());
            }
        }

        private static string CreateAssemblyName(ModuleDefinition sourceModule)
        {
            var baseName = SanitizeModuleName(sourceModule.Name);
            var id = Interlocked.Increment(ref _nextAssemblyId);
            return $"MonoWeaver.Generated.{baseName}.{id}";
        }

        private static string SanitizeModuleName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Module";

            var invalid = Path.GetInvalidPathChars();
            var sanitized = new string(name.Where(ch => !invalid.Contains(ch)).ToArray()).Trim();
            if (sanitized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                sanitized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                sanitized = sanitized.Substring(0, sanitized.Length - 4);
            }

            return string.IsNullOrWhiteSpace(sanitized) ? "Module" : sanitized;
        }
    }
}

/// <summary>
/// 按委托类型隔离的运行时引用表。
/// </summary>
public static class CecilDelegateReferenceStore<TDelegate>
    where TDelegate : Delegate
{
    internal static MethodInfo GetterMethod { get; } =
        typeof(CecilDelegateReferenceStore<TDelegate>).GetMethod(nameof(Get),
            BindingFlags.Public | BindingFlags.Static)
        ?? throw new MissingMethodException(typeof(CecilDelegateReferenceStore<TDelegate>).FullName,
            nameof(Get));

    private static readonly object Gate = new();
    private static TDelegate?[] _items = new TDelegate?[4];
    private static int _count;

    public static int Store(TDelegate callback)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));

        lock (Gate)
        {
            if (_count == _items.Length)
                Array.Resize(ref _items, checked(_items.Length * 2));
            _items[_count] = callback;
            return _count++;
        }
    }

    public static TDelegate Get(int id)
    {
        lock (Gate)
        {
            if (id < 0 || id >= _count || _items[id] is null)
                throw new ArgumentOutOfRangeException(nameof(id));
            return _items[id]!;
        }
    }

    public static void Clear(int id)
    {
        lock (Gate)
        {
            if (id < 0 || id >= _count)
                throw new ArgumentOutOfRangeException(nameof(id));
            _items[id] = null;
        }
    }
}
