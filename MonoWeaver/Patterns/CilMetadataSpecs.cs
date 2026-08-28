using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using MonoWeaver.Utils;

namespace MonoWeaver.Patterns;

/// <summary>
/// 控制 pattern type 的匹配严格度；默认始终为 <see cref="Exact"/>。
/// </summary>
public enum CilTypeMatchMode
{
    /// <summary>
    /// metadata identity 必须一致。Boolean 不会匹配 Int32，enum 不会匹配底层整数。
    /// </summary>
    Exact,

    /// <summary>
    /// 允许正常的引用类型/继承赋值；不会把不同 value type 当成相同类型。
    /// </summary>
    Assignable,

    /// <summary>
    /// 显式允许 verification stack category 兼容，仅用于需要匹配编译器 lowering 的场景。
    /// </summary>
    VerificationStackCompatible,
}

/// <summary>
/// 不要求目标类型被 CLR 加载的类型签名。
/// 可以来自运行时 <see cref="Type"/>、Cecil <see cref="TypeReference"/>，
/// 也可以只描述 metadata full name。
/// </summary>
public sealed class CilTypeSpec
{
    private enum SpecKind
    {
        Runtime,
        Cecil,
        Named,
        Primitive,
        Array,
        ByReference,
        Pointer,
        GenericInstance,
    }

    private readonly SpecKind _kind;
    private readonly Type? _runtimeType;
    private readonly TypeReference? _cecilType;
    private readonly MetadataType _primitiveType;
    private readonly string? _fullName;
    private readonly string? _assemblyName;
    private readonly CilTypeSpec? _elementType;
    private readonly CilTypeSpec[] _genericArguments;
    private readonly int _arrayRank;
    private readonly bool _isValueType;
    private readonly CilTypeMatchMode _matchMode = CilTypeMatchMode.Exact;

    private CilTypeSpec(Type runtimeType)
    {
        _runtimeType = runtimeType ?? throw new ArgumentNullException(nameof(runtimeType));
        _kind = SpecKind.Runtime;
        _genericArguments = Array.Empty<CilTypeSpec>();
    }

    private CilTypeSpec(TypeReference cecilType)
    {
        _cecilType = cecilType ?? throw new ArgumentNullException(nameof(cecilType));
        _kind = SpecKind.Cecil;
        _genericArguments = Array.Empty<CilTypeSpec>();
    }

    private CilTypeSpec(MetadataType primitiveType)
    {
        _primitiveType = primitiveType;
        _kind = SpecKind.Primitive;
        _genericArguments = Array.Empty<CilTypeSpec>();
    }

    private CilTypeSpec(string fullName, string? assemblyName, bool isValueType)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            throw new ArgumentException("A metadata type full name is required.", nameof(fullName));

        _fullName = NormalizeFullName(fullName);
        _assemblyName = NormalizeAssemblyName(assemblyName);
        _isValueType = isValueType;
        _kind = SpecKind.Named;
        _genericArguments = Array.Empty<CilTypeSpec>();
    }

    private CilTypeSpec(SpecKind kind, CilTypeSpec elementType, int arrayRank = 0)
    {
        _kind = kind;
        _elementType = elementType ?? throw new ArgumentNullException(nameof(elementType));
        _arrayRank = arrayRank;
        _genericArguments = Array.Empty<CilTypeSpec>();
    }

    private CilTypeSpec(CilTypeSpec genericType, CilTypeSpec[] genericArguments)
    {
        _kind = SpecKind.GenericInstance;
        _elementType = genericType ?? throw new ArgumentNullException(nameof(genericType));
        _genericArguments = genericArguments ?? throw new ArgumentNullException(nameof(genericArguments));
        if (_genericArguments.Length == 0)
            throw new ArgumentException("At least one generic type argument is required.", nameof(genericArguments));
        if (_genericArguments.Any(static argument => argument is null))
            throw new ArgumentException("Generic type arguments cannot contain null.", nameof(genericArguments));
    }

    private CilTypeSpec(CilTypeSpec source, CilTypeMatchMode matchMode)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));
        _kind = source._kind;
        _runtimeType = source._runtimeType;
        _cecilType = source._cecilType;
        _primitiveType = source._primitiveType;
        _fullName = source._fullName;
        _assemblyName = source._assemblyName;
        _elementType = source._elementType;
        _genericArguments = source._genericArguments;
        _arrayRank = source._arrayRank;
        _isValueType = source._isValueType;
        _matchMode = matchMode;
    }

    public static CilTypeSpec From(Type runtimeType)
    {
        if (runtimeType is null)
            throw new ArgumentNullException(nameof(runtimeType));

        if (TryGetPrimitive(runtimeType, out var primitive))
            return new CilTypeSpec(primitive);
        if (runtimeType.IsArray)
            return From(runtimeType.GetElementType()!).MakeArrayType(runtimeType.GetArrayRank());
        if (runtimeType.IsByRef)
            return From(runtimeType.GetElementType()!).MakeByReferenceType();
        if (runtimeType.IsPointer)
            return From(runtimeType.GetElementType()!).MakePointerType();

        return new CilTypeSpec(runtimeType);
    }

    public static CilTypeSpec From(TypeReference typeReference)
        => new(typeReference ?? throw new ArgumentNullException(nameof(typeReference)));

    /// <summary>
    /// 仅按 metadata identity 描述类型。
    /// <paramref name="fullName"/> 使用 Cecil 格式，嵌套类型以 '/' 分隔；
    /// <paramref name="assemblyName"/> 只写程序集简单名，为 null 时不约束程序集。
    /// </summary>
    public static CilTypeSpec Named(string fullName, string? assemblyName = null, bool isValueType = false)
        => new(fullName, assemblyName, isValueType);

    public static CilTypeSpec Void { get; } = new(MetadataType.Void);
    public static CilTypeSpec Boolean { get; } = new(MetadataType.Boolean);
    public static CilTypeSpec Char { get; } = new(MetadataType.Char);
    public static CilTypeSpec SByte { get; } = new(MetadataType.SByte);
    public static CilTypeSpec Byte { get; } = new(MetadataType.Byte);
    public static CilTypeSpec Int16 { get; } = new(MetadataType.Int16);
    public static CilTypeSpec UInt16 { get; } = new(MetadataType.UInt16);
    public static CilTypeSpec Int32 { get; } = new(MetadataType.Int32);
    public static CilTypeSpec UInt32 { get; } = new(MetadataType.UInt32);
    public static CilTypeSpec Int64 { get; } = new(MetadataType.Int64);
    public static CilTypeSpec UInt64 { get; } = new(MetadataType.UInt64);
    public static CilTypeSpec Single { get; } = new(MetadataType.Single);
    public static CilTypeSpec Double { get; } = new(MetadataType.Double);
    public static CilTypeSpec String { get; } = new(MetadataType.String);
    public static CilTypeSpec IntPtr { get; } = new(MetadataType.IntPtr);
    public static CilTypeSpec UIntPtr { get; } = new(MetadataType.UIntPtr);
    public static CilTypeSpec Object { get; } = new(MetadataType.Object);
    public static CilTypeSpec TypedReference { get; } = new(MetadataType.TypedByReference);

    public bool IsVoid => _kind == SpecKind.Primitive && _primitiveType == MetadataType.Void
                          || string.Equals(DisplayName, "System.Void", StringComparison.Ordinal);

    public bool IsBoolean => _kind == SpecKind.Primitive && _primitiveType == MetadataType.Boolean
                             || string.Equals(DisplayName, "System.Boolean", StringComparison.Ordinal);

    public string DisplayName
    {
        get
        {
            switch (_kind)
            {
                case SpecKind.Runtime:
                    return _runtimeType!.FullName ?? _runtimeType.Name;
                case SpecKind.Cecil:
                    return _cecilType!.FullName;
                case SpecKind.Named:
                    return _fullName!;
                case SpecKind.Primitive:
                    return PrimitiveFullName(_primitiveType);
                case SpecKind.Array:
                    return _elementType!.DisplayName + (_arrayRank == 1 ? "[]" : "[" + new string(',', _arrayRank - 1) + "]");
                case SpecKind.ByReference:
                    return _elementType!.DisplayName + "&";
                case SpecKind.Pointer:
                    return _elementType!.DisplayName + "*";
                case SpecKind.GenericInstance:
                    return _elementType!.DisplayName + "<" + string.Join(", ", _genericArguments.Select(static argument => argument.DisplayName)) + ">";
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    public CilTypeMatchMode MatchMode => _matchMode;

    /// <summary>
    /// 返回使用指定匹配策略的新 spec；原对象保持不变。
    /// </summary>
    public CilTypeSpec WithMatchMode(CilTypeMatchMode matchMode)
    {
        if (!Enum.IsDefined(typeof(CilTypeMatchMode), matchMode))
            throw new ArgumentOutOfRangeException(nameof(matchMode));
        return matchMode == _matchMode ? this : new CilTypeSpec(this, matchMode);
    }

    public CilTypeSpec Assignable() => WithMatchMode(CilTypeMatchMode.Assignable);
    public CilTypeSpec StackCompatible() => WithMatchMode(CilTypeMatchMode.VerificationStackCompatible);

    public CilTypeSpec MakeArrayType(int rank = 1)
    {
        if (rank <= 0)
            throw new ArgumentOutOfRangeException(nameof(rank));
        return new CilTypeSpec(SpecKind.Array, this, rank);
    }

    public CilTypeSpec MakeByReferenceType() => new(SpecKind.ByReference, this);
    public CilTypeSpec MakePointerType() => new(SpecKind.Pointer, this);

    public CilTypeSpec MakeGenericType(params CilTypeSpec[] genericArguments)
        => new(this, genericArguments?.ToArray() ?? throw new ArgumentNullException(nameof(genericArguments)));

    public CilMethodSpec InstanceMethod(string name, CilTypeSpec returnType, params CilTypeSpec[] parameterTypes)
        => CilMethodSpec.Instance(this, name, returnType, parameterTypes);

    public CilMethodSpec StaticMethod(string name, CilTypeSpec returnType, params CilTypeSpec[] parameterTypes)
        => CilMethodSpec.Static(this, name, returnType, parameterTypes);

    public CilMethodSpec Constructor(params CilTypeSpec[] parameterTypes)
        => CilMethodSpec.Constructor(this, parameterTypes);

    public CilFieldSpec InstanceField(string name, CilTypeSpec fieldType)
        => CilFieldSpec.Instance(this, name, fieldType);

    public CilFieldSpec StaticField(string name, CilTypeSpec fieldType)
        => CilFieldSpec.Static(this, name, fieldType);

    public static implicit operator CilTypeSpec(Type runtimeType) => From(runtimeType);
    public static implicit operator CilTypeSpec(TypeReference typeReference) => From(typeReference);

    public override string ToString()
        => string.IsNullOrEmpty(_assemblyName) ? DisplayName : DisplayName + ", " + _assemblyName;

    internal bool Matches(TypeReference candidate)
    {
        if (candidate is null)
            return false;

        try
        {
            switch (_kind)
            {
                case SpecKind.Runtime:
                    return CecilHelper.TypeMatches(candidate, _runtimeType!);
                case SpecKind.Cecil:
                    return candidate.IsSameWith(_cecilType);
                case SpecKind.Named:
                    return NamedTypeMatches(candidate);
                case SpecKind.Primitive:
                    return PrimitiveMatches(candidate, _primitiveType);
                case SpecKind.Array:
                    return candidate is ArrayType array
                           && array.Rank == _arrayRank
                           && _elementType!.Matches(array.ElementType);
                case SpecKind.ByReference:
                    return candidate is ByReferenceType byReference
                           && _elementType!.Matches(byReference.ElementType);
                case SpecKind.Pointer:
                    return candidate is PointerType pointer
                           && _elementType!.Matches(pointer.ElementType);
                case SpecKind.GenericInstance:
                    if (candidate is not GenericInstanceType generic
                        || !_elementType!.Matches(generic.ElementType)
                        || generic.GenericArguments.Count != _genericArguments.Length)
                    {
                        return false;
                    }
                    for (var i = 0; i < _genericArguments.Length; i++)
                    {
                        if (!_genericArguments[i].Matches(generic.GenericArguments[i]))
                            return false;
                    }
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ModuleDefinition, TypeReference> _resolvedByModule = new();

    internal bool TryResolve(ModuleDefinition module, out TypeReference typeReference)
    {
        if (module is null)
            throw new ArgumentNullException(nameof(module));

        try
        {
            typeReference = _resolvedByModule.GetValue(module, Resolve);
            return true;
        }
        catch
        {
            typeReference = null!;
            return false;
        }
    }

    internal TypeReference Resolve(ModuleDefinition module)
    {
        if (module is null)
            throw new ArgumentNullException(nameof(module));

        switch (_kind)
        {
            case SpecKind.Runtime:
                return module.ImportReference(_runtimeType!);
            case SpecKind.Cecil:
                return ReferenceEquals(_cecilType!.Module, module) ? _cecilType : module.ImportReference(_cecilType);
            case SpecKind.Named:
                return ResolveNamedReference(module);
            case SpecKind.Primitive:
                return ResolvePrimitive(module, _primitiveType);
            case SpecKind.Array:
                return new ArrayType(_elementType!.Resolve(module), _arrayRank);
            case SpecKind.ByReference:
                return new ByReferenceType(_elementType!.Resolve(module));
            case SpecKind.Pointer:
                return new PointerType(_elementType!.Resolve(module));
            case SpecKind.GenericInstance:
            {
                var instance = new GenericInstanceType(_elementType!.Resolve(module));
                foreach (var argument in _genericArguments)
                    instance.GenericArguments.Add(argument.Resolve(module));
                return instance;
            }
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    internal bool AllowsAssignableMatching => _matchMode == CilTypeMatchMode.Assignable;
    internal bool AllowsStackCompatibility => _matchMode == CilTypeMatchMode.VerificationStackCompatible;

    private bool NamedTypeMatches(TypeReference candidate)
    {
        if (!string.Equals(NormalizeFullName(candidate.FullName), _fullName, StringComparison.Ordinal))
            return false;
        if (string.IsNullOrEmpty(_assemblyName))
            return true;

        var candidateAssembly = GetAssemblySimpleName(candidate);
        return string.Equals(candidateAssembly, _assemblyName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PrimitiveMatches(TypeReference candidate, MetadataType expected)
    {
        var current = candidate;
        while (current is OptionalModifierType optional)
            current = optional.ElementType;
        while (current is RequiredModifierType required)
            current = required.ElementType;
        return current.MetadataType == expected;
    }

    private static TypeReference ResolvePrimitive(ModuleDefinition module, MetadataType primitive)
    {
        return primitive switch
        {
            MetadataType.Void => module.TypeSystem.Void,
            MetadataType.Boolean => module.TypeSystem.Boolean,
            MetadataType.Char => module.TypeSystem.Char,
            MetadataType.SByte => module.TypeSystem.SByte,
            MetadataType.Byte => module.TypeSystem.Byte,
            MetadataType.Int16 => module.TypeSystem.Int16,
            MetadataType.UInt16 => module.TypeSystem.UInt16,
            MetadataType.Int32 => module.TypeSystem.Int32,
            MetadataType.UInt32 => module.TypeSystem.UInt32,
            MetadataType.Int64 => module.TypeSystem.Int64,
            MetadataType.UInt64 => module.TypeSystem.UInt64,
            MetadataType.Single => module.TypeSystem.Single,
            MetadataType.Double => module.TypeSystem.Double,
            MetadataType.String => module.TypeSystem.String,
            MetadataType.IntPtr => module.TypeSystem.IntPtr,
            MetadataType.UIntPtr => module.TypeSystem.UIntPtr,
            MetadataType.Object => module.TypeSystem.Object,
            MetadataType.TypedByReference => module.TypeSystem.TypedReference,
            _ => throw new NotSupportedException($"Metadata primitive '{primitive}' is not supported."),
        };
    }

    private TypeReference ResolveNamedReference(ModuleDefinition module)
    {
        var moduleAssemblyName = module.Assembly?.Name?.Name;
        var mayBeLocal = string.IsNullOrEmpty(_assemblyName)
                         || string.Equals(moduleAssemblyName, _assemblyName, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(module.Name, _assemblyName, StringComparison.OrdinalIgnoreCase);

        if (mayBeLocal)
        {
            var local = FindTypeDefinition(module.Types, _fullName!);
            if (local is not null)
                return local;

            if (string.IsNullOrEmpty(_assemblyName)
                || string.Equals(moduleAssemblyName, _assemblyName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Named type '{this}' is useful for matching, but it cannot be emitted because it was not found in target module '{module.Name}'. " +
                    "Supply a concrete Cecil TypeReference/MethodReference so the assembly identity is preserved.");
            }
        }

        var scope = module.AssemblyReferences.FirstOrDefault(reference =>
            string.Equals(reference.Name, _assemblyName, StringComparison.OrdinalIgnoreCase));
        if (scope is null)
        {
            throw new InvalidOperationException(
                $"Named type '{this}' cannot be emitted safely because target module '{module.Name}' has no existing reference to assembly '{_assemblyName}'. " +
                "MonoWeaver will not invent a 0.0.0.0 assembly reference (which is unsafe on .NET Framework). Supply a concrete Cecil TypeReference/MethodReference instead.");
        }

        return CreateNamedReference(module, _fullName!, scope, _isValueType);
    }

    private static TypeReference CreateNamedReference(ModuleDefinition module, string fullName,
        IMetadataScope scope, bool isValueType)
    {
        var nestedParts = fullName.Split('/');
        var top = nestedParts[0];
        var lastDot = top.LastIndexOf('.');
        var @namespace = lastDot < 0 ? string.Empty : top.Substring(0, lastDot);
        var name = lastDot < 0 ? top : top.Substring(lastDot + 1);
        TypeReference result = new TypeReference(@namespace, name, module, scope,
            isValueType && nestedParts.Length == 1);

        for (var i = 1; i < nestedParts.Length; i++)
        {
            result = new TypeReference(string.Empty, nestedParts[i], module, scope,
                isValueType && i == nestedParts.Length - 1)
            {
                DeclaringType = result,
            };
        }

        return result;
    }

    private static TypeDefinition? FindTypeDefinition(IEnumerable<TypeDefinition> roots, string fullName)
    {
        foreach (var type in roots)
        {
            if (string.Equals(NormalizeFullName(type.FullName), fullName, StringComparison.Ordinal))
                return type;

            var nested = FindTypeDefinition(type.NestedTypes, fullName);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static string? GetAssemblySimpleName(TypeReference type)
    {
        var element = type;
        while (element is TypeSpecification specification)
            element = specification.ElementType;

        if (element.Scope is AssemblyNameReference assemblyReference)
            return assemblyReference.Name;
        if (element.Scope is ModuleDefinition scopedModule)
            return scopedModule.Assembly?.Name?.Name ?? scopedModule.Name;
        return element.Module?.Assembly?.Name?.Name;
    }

    internal static string NormalizeFullName(string fullName)
        => fullName.Trim().Replace('+', '/');

    private static string? NormalizeAssemblyName(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
            return null;
        var value = assemblyName!.Trim();
        var comma = value.IndexOf(',');
        return comma < 0 ? value : value.Substring(0, comma).Trim();
    }

    private static bool TryGetPrimitive(Type type, out MetadataType metadataType)
    {
        if (type == typeof(void)) metadataType = MetadataType.Void;
        else if (type == typeof(bool)) metadataType = MetadataType.Boolean;
        else if (type == typeof(char)) metadataType = MetadataType.Char;
        else if (type == typeof(sbyte)) metadataType = MetadataType.SByte;
        else if (type == typeof(byte)) metadataType = MetadataType.Byte;
        else if (type == typeof(short)) metadataType = MetadataType.Int16;
        else if (type == typeof(ushort)) metadataType = MetadataType.UInt16;
        else if (type == typeof(int)) metadataType = MetadataType.Int32;
        else if (type == typeof(uint)) metadataType = MetadataType.UInt32;
        else if (type == typeof(long)) metadataType = MetadataType.Int64;
        else if (type == typeof(ulong)) metadataType = MetadataType.UInt64;
        else if (type == typeof(float)) metadataType = MetadataType.Single;
        else if (type == typeof(double)) metadataType = MetadataType.Double;
        else if (type == typeof(string)) metadataType = MetadataType.String;
        else if (type == typeof(IntPtr)) metadataType = MetadataType.IntPtr;
        else if (type == typeof(UIntPtr)) metadataType = MetadataType.UIntPtr;
        else if (type == typeof(object)) metadataType = MetadataType.Object;
        else if (type == typeof(TypedReference)) metadataType = MetadataType.TypedByReference;
        else
        {
            metadataType = default;
            return false;
        }
        return true;
    }

    private static string PrimitiveFullName(MetadataType metadataType)
    {
        return metadataType switch
        {
            MetadataType.Void => "System.Void",
            MetadataType.Boolean => "System.Boolean",
            MetadataType.Char => "System.Char",
            MetadataType.SByte => "System.SByte",
            MetadataType.Byte => "System.Byte",
            MetadataType.Int16 => "System.Int16",
            MetadataType.UInt16 => "System.UInt16",
            MetadataType.Int32 => "System.Int32",
            MetadataType.UInt32 => "System.UInt32",
            MetadataType.Int64 => "System.Int64",
            MetadataType.UInt64 => "System.UInt64",
            MetadataType.Single => "System.Single",
            MetadataType.Double => "System.Double",
            MetadataType.String => "System.String",
            MetadataType.IntPtr => "System.IntPtr",
            MetadataType.UIntPtr => "System.UIntPtr",
            MetadataType.Object => "System.Object",
            MetadataType.TypedByReference => "System.TypedReference",
            _ => metadataType.ToString(),
        };
    }
}

/// <summary>
/// 不要求 CLR 加载声明类型的方法签名。
/// </summary>
public sealed class CilMethodSpec
{
    private readonly MethodBase? _runtimeMethod;
    private readonly MethodReference? _cecilMethod;
    private readonly bool _symbolic;

    private CilMethodSpec(MethodBase runtimeMethod)
    {
        _runtimeMethod = runtimeMethod ?? throw new ArgumentNullException(nameof(runtimeMethod));
        Name = runtimeMethod.Name;
        DeclaringType = CilTypeSpec.From(runtimeMethod.DeclaringType
            ?? throw new ArgumentException("The runtime method has no declaring type.", nameof(runtimeMethod)));
        HasThis = !runtimeMethod.IsStatic;
        IsConstructor = runtimeMethod is ConstructorInfo;
        GenericArity = runtimeMethod.IsGenericMethod ? runtimeMethod.GetGenericArguments().Length : 0;
        ParameterTypes = runtimeMethod.GetParameters().Select(parameter => CilTypeSpec.From(parameter.ParameterType)).ToArray();
        ReturnType = runtimeMethod is MethodInfo methodInfo ? CilTypeSpec.From(methodInfo.ReturnType) : CilTypeSpec.Void;
    }

    private CilMethodSpec(MethodReference cecilMethod)
    {
        _cecilMethod = cecilMethod ?? throw new ArgumentNullException(nameof(cecilMethod));
        Name = cecilMethod.Name;
        DeclaringType = CilTypeSpec.From(cecilMethod.DeclaringType);
        HasThis = cecilMethod.HasThis;
        IsConstructor = cecilMethod.Name is ".ctor" or ".cctor";
        var elementMethod = cecilMethod is GenericInstanceMethod generic ? generic.ElementMethod : cecilMethod;
        GenericArity = elementMethod.GenericParameters.Count;
        ParameterTypes = cecilMethod.Parameters.Select(parameter => CilTypeSpec.From(parameter.ParameterType)).ToArray();
        ReturnType = CilTypeSpec.From(cecilMethod.ReturnType);
    }

    private CilMethodSpec(CilTypeSpec declaringType, string name, CilTypeSpec returnType,
        bool hasThis, bool isConstructor, int genericArity, CilTypeSpec[] parameterTypes)
    {
        DeclaringType = declaringType ?? throw new ArgumentNullException(nameof(declaringType));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A metadata method name is required.", nameof(name));
        Name = name;
        ReturnType = returnType ?? throw new ArgumentNullException(nameof(returnType));
        HasThis = hasThis;
        IsConstructor = isConstructor;
        if (genericArity < 0)
            throw new ArgumentOutOfRangeException(nameof(genericArity));
        GenericArity = genericArity;
        ParameterTypes = parameterTypes ?? throw new ArgumentNullException(nameof(parameterTypes));
        if (ParameterTypes.Any(static type => type is null))
            throw new ArgumentException("Method parameter types cannot contain null.", nameof(parameterTypes));
        _symbolic = true;
    }

    public string Name { get; }
    public CilTypeSpec DeclaringType { get; }
    public CilTypeSpec ReturnType { get; }
    public IReadOnlyList<CilTypeSpec> ParameterTypes { get; }
    public bool HasThis { get; }
    public bool IsConstructor { get; }
    public int GenericArity { get; }

    public static CilMethodSpec From(MethodBase runtimeMethod) => new(runtimeMethod);
    public static CilMethodSpec From(MethodReference methodReference) => new(methodReference);

    public static CilMethodSpec Instance(CilTypeSpec declaringType, string name, CilTypeSpec returnType,
        params CilTypeSpec[] parameterTypes)
        => new(declaringType, name, returnType, hasThis: true, isConstructor: false,
            genericArity: 0, parameterTypes?.ToArray() ?? throw new ArgumentNullException(nameof(parameterTypes)));

    public static CilMethodSpec Static(CilTypeSpec declaringType, string name, CilTypeSpec returnType,
        params CilTypeSpec[] parameterTypes)
        => new(declaringType, name, returnType, hasThis: false, isConstructor: false,
            genericArity: 0, parameterTypes?.ToArray() ?? throw new ArgumentNullException(nameof(parameterTypes)));

    public static CilMethodSpec Constructor(CilTypeSpec declaringType, params CilTypeSpec[] parameterTypes)
        => new(declaringType, ".ctor", CilTypeSpec.Void, hasThis: true, isConstructor: true,
            genericArity: 0, parameterTypes?.ToArray() ?? throw new ArgumentNullException(nameof(parameterTypes)));

    public CilMethodSpec WithGenericArity(int genericArity)
    {
        if (!_symbolic)
            throw new InvalidOperationException("Generic arity is already defined by the underlying runtime/Cecil method.");
        return new CilMethodSpec(DeclaringType, Name, ReturnType, HasThis, IsConstructor,
            genericArity, ParameterTypes.ToArray());
    }

    public static implicit operator CilMethodSpec(MethodBase runtimeMethod) => From(runtimeMethod);
    public static implicit operator CilMethodSpec(MethodReference methodReference) => From(methodReference);

    public override string ToString()
        => $"{ReturnType} {DeclaringType}::{Name}({string.Join(", ", ParameterTypes)})";

    internal bool Matches(MethodReference candidate)
    {
        if (candidate is null)
            return false;
        if (_runtimeMethod is not null)
            return CecilHelper.MethodMatches(candidate, _runtimeMethod);
        if (_cecilMethod is not null)
            return CecilHelper.MethodMatches(candidate, _cecilMethod);

        var element = candidate is GenericInstanceMethod generic ? generic.ElementMethod : candidate;
        if (!string.Equals(element.Name, Name, StringComparison.Ordinal)
            || element.HasThis != HasThis
            || !DeclaringType.Matches(element.DeclaringType)
            || element.GenericParameters.Count != GenericArity
            || element.Parameters.Count != ParameterTypes.Count
            || !ReturnType.Matches(element.ReturnType))
        {
            return false;
        }

        for (var i = 0; i < ParameterTypes.Count; i++)
        {
            if (!ParameterTypes[i].Matches(element.Parameters[i].ParameterType))
                return false;
        }
        return true;
    }

    internal MethodReference Resolve(ModuleDefinition module)
    {
        if (module is null)
            throw new ArgumentNullException(nameof(module));
        if (_runtimeMethod is not null)
            return module.ImportReference(_runtimeMethod);
        if (_cecilMethod is not null)
            return ReferenceEquals(_cecilMethod.Module, module) ? _cecilMethod : module.ImportReference(_cecilMethod);
        if (GenericArity != 0)
        {
            throw new InvalidOperationException(
                $"Symbolic method '{this}' is open generic and is match-only. Supply a concrete closed Cecil GenericInstanceMethod for emission.");
        }

        var method = new MethodReference(Name, ReturnType.Resolve(module), DeclaringType.Resolve(module))
        {
            HasThis = HasThis,
            ExplicitThis = false,
            CallingConvention = MethodCallingConvention.Default,
        };
        foreach (var parameterType in ParameterTypes)
            method.Parameters.Add(new ParameterDefinition(parameterType.Resolve(module)));
        return method;
    }
}

/// <summary>
/// 不要求 CLR 加载声明类型的字段签名。
/// </summary>
public sealed class CilFieldSpec
{
    private readonly FieldInfo? _runtimeField;
    private readonly FieldReference? _cecilField;

    private CilFieldSpec(FieldInfo runtimeField)
    {
        _runtimeField = runtimeField ?? throw new ArgumentNullException(nameof(runtimeField));
        Name = runtimeField.Name;
        DeclaringType = CilTypeSpec.From(runtimeField.DeclaringType
            ?? throw new ArgumentException("The runtime field has no declaring type.", nameof(runtimeField)));
        FieldType = CilTypeSpec.From(runtimeField.FieldType);
        IsStatic = runtimeField.IsStatic;
    }

    private CilFieldSpec(FieldReference cecilField)
    {
        _cecilField = cecilField ?? throw new ArgumentNullException(nameof(cecilField));
        Name = cecilField.Name;
        DeclaringType = CilTypeSpec.From(cecilField.DeclaringType);
        FieldType = CilTypeSpec.From(cecilField.FieldType);
        IsStatic = TryGetIsStatic(cecilField);
    }

    private CilFieldSpec(CilTypeSpec declaringType, string name, CilTypeSpec fieldType, bool isStatic)
    {
        DeclaringType = declaringType ?? throw new ArgumentNullException(nameof(declaringType));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A metadata field name is required.", nameof(name));
        Name = name;
        FieldType = fieldType ?? throw new ArgumentNullException(nameof(fieldType));
        IsStatic = isStatic;
    }

    public string Name { get; }
    public CilTypeSpec DeclaringType { get; }
    public CilTypeSpec FieldType { get; }
    public bool? IsStatic { get; }

    public static CilFieldSpec From(FieldInfo runtimeField) => new(runtimeField);
    public static CilFieldSpec From(FieldReference fieldReference) => new(fieldReference);
    public static CilFieldSpec Instance(CilTypeSpec declaringType, string name, CilTypeSpec fieldType)
        => new(declaringType, name, fieldType, false);
    public static CilFieldSpec Static(CilTypeSpec declaringType, string name, CilTypeSpec fieldType)
        => new(declaringType, name, fieldType, true);

    public static implicit operator CilFieldSpec(FieldInfo runtimeField) => From(runtimeField);
    public static implicit operator CilFieldSpec(FieldReference fieldReference) => From(fieldReference);

    public override string ToString() => $"{FieldType} {DeclaringType}::{Name}";

    internal bool Matches(FieldReference candidate)
    {
        if (_runtimeField is not null)
            return CecilHelper.FieldMatches(candidate, _runtimeField);
        if (_cecilField is not null)
            return CecilHelper.FieldMatches(candidate, _cecilField);
        return string.Equals(candidate.Name, Name, StringComparison.Ordinal)
               && DeclaringType.Matches(candidate.DeclaringType)
               && FieldType.Matches(candidate.FieldType)
               && (IsStatic is null || TryGetIsStatic(candidate) == IsStatic);
    }

    internal FieldReference Resolve(ModuleDefinition module)
    {
        if (_runtimeField is not null)
            return module.ImportReference(_runtimeField);
        if (_cecilField is not null)
            return ReferenceEquals(_cecilField.Module, module) ? _cecilField : module.ImportReference(_cecilField);
        return new FieldReference(Name, FieldType.Resolve(module), DeclaringType.Resolve(module));
    }

    private static bool? TryGetIsStatic(FieldReference field)
    {
        if (field is FieldDefinition definition)
            return definition.IsStatic;
        return MetadataResolution.TryResolve(field)?.IsStatic;
    }
}
