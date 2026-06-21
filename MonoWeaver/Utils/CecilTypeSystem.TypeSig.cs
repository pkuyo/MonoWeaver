using Mono.Cecil;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MonoWeaver.Utils;

public static partial class CecilTypeSystem
{
    /// <summary>
    ///类型签名 id。
    /// </summary>
    public readonly struct TypeSig : IEquatable<TypeSig>
    {
        private static readonly Lazy<CoreTypeSigs> _coreTypes =
            new(CreateCoreTypeSigs, LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly int _id;

        internal int Id => _id;
        public bool IsValid => _id != 0;
        public static TypeSig Object => _coreTypes.Value.Object;
        public static TypeSig Void => _coreTypes.Value.Void;
        public static TypeSig String => _coreTypes.Value.String;
        public static TypeSig Array => _coreTypes.Value.Array;
        public static TypeSig Nullable => _coreTypes.Value.Nullable;
        public static TypeSig ICloneable => _coreTypes.Value.ICloneable;
        public static TypeSig ValueType => _coreTypes.Value.ValueType;
        public static TypeSig Enum => _coreTypes.Value.Enum;
        public static TypeSig Delegate => _coreTypes.Value.Delegate;
        public static TypeSig MulticastDelegate => _coreTypes.Value.MulticastDelegate;
        private TypeSig(int id) => _id = id;

        public static TypeSig Create(TypeReference t)
        {
            if (t == null) throw new ArgumentNullException(nameof(t));


            if (_typeSigCache.TryGetValue(t, out var cached))
                return cached;


            var key = TypeSigKey.Create(t);
            var sig = new TypeSig(InternTypeSigKey(key));

            return _typeSigCache.GetOrAdd(t, sig);

        }

        public bool Equals(TypeSig other) => _id == other._id;
        public override bool Equals(object? obj) => obj is TypeSig other && Equals(other);
        public override int GetHashCode() => _id;
        public override string ToString() => _id == 0 ? "<invalid>" : $"sig#{_id}";

        public static bool operator ==(TypeSig left, TypeSig right) => left.Equals(right);
        public static bool operator !=(TypeSig left, TypeSig right) => !left.Equals(right);

        private static CoreTypeSigs CreateCoreTypeSigs()
        {
            using var assembly = CreateCoreTypeAssembly();
            var module = assembly.MainModule;
            return new CoreTypeSigs(
                Create(module.ImportReference(typeof(object))),
                Create(module.ImportReference(typeof(void))),
                Create(module.ImportReference(typeof(string))),
                Create(module.ImportReference(typeof(Array))),
                Create(module.ImportReference(typeof(Nullable<>))),
                Create(module.ImportReference(typeof(ICloneable))),
                Create(module.ImportReference(typeof(ValueType))),
                Create(module.ImportReference(typeof(Enum))), 
                Create(module.ImportReference(typeof(Delegate))),
                Create(module.ImportReference(typeof(MulticastDelegate))));
        }

        private static AssemblyDefinition CreateCoreTypeAssembly()
        {
            return AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("MonoWeaver.TypeSig.CoreTypes", new Version()),
                "CoreTypes",
                ModuleKind.Dll);
        }

        internal static class SystemCollections
        {
            private static readonly Lazy<SystemCollectionsTypeSigs> _types =
                new(CreateTypeSigs, LazyThreadSafetyMode.ExecutionAndPublication);

            public static TypeSig IEnumerable => _types.Value.IEnumerable;
            public static TypeSig ICollection => _types.Value.ICollection;
            public static TypeSig IList => _types.Value.IList;
            public static TypeSig IStructuralComparable => _types.Value.IStructuralComparable;

            private static SystemCollectionsTypeSigs CreateTypeSigs()
            {
                using var assembly = CreateCoreTypeAssembly();
                var module = assembly.MainModule;
                return new SystemCollectionsTypeSigs(
                    Create(module.ImportReference(typeof(global::System.Collections.IEnumerable))),
                    Create(module.ImportReference(typeof(global::System.Collections.ICollection))),
                    Create(module.ImportReference(typeof(global::System.Collections.IList))),
                    Create(module.ImportReference(typeof(global::System.Collections.IStructuralComparable))));
            }
        }

        internal static class SystemThreading
        {
            private static readonly Lazy<SystemThreadingTypeSigs> _types =
                new(CreateTypeSigs, LazyThreadSafetyMode.ExecutionAndPublication);

            public static TypeSig Task => _types.Value.Task;
            public static TypeSig ValueTask => _types.Value.ValueTask;
            public static TypeSig TaskT => _types.Value.TaskT;
            public static TypeSig ValueTaskT => _types.Value.ValueTaskT;

            private static SystemThreadingTypeSigs CreateTypeSigs()
            {
                using var assembly = CreateCoreTypeAssembly();
                var module = assembly.MainModule;
                var task = module.ImportReference(typeof(global::System.Threading.Tasks.Task));
                var taskT = module.ImportReference(typeof(global::System.Threading.Tasks.Task<>));
                var valueTask = CreateValueTaskReference(module, "ValueTask", task.Scope, false);
                var valueTaskT = CreateValueTaskReference(module, "ValueTask`1", task.Scope, true);

                return new SystemThreadingTypeSigs(
                    Create(task),
                    Create(valueTask),
                    Create(taskT),
                    Create(valueTaskT));
            }

            private static TypeReference CreateValueTaskReference(
                ModuleDefinition module,
                string name,
                IMetadataScope scope,
                bool hasGenericParameter)
            {
                var type = new TypeReference(
                    "System.Threading.Tasks",
                    name,
                    module,
                    scope,
                    true);

                if (hasGenericParameter)
                    type.GenericParameters.Add(new GenericParameter("TResult", type));

                return type;
            }
        }
    }

    private readonly struct CoreTypeSigs
    {
        public readonly TypeSig Object;
        public readonly TypeSig Void;
        public readonly TypeSig String;
        public readonly TypeSig Array;
        public readonly TypeSig Nullable;
        public readonly TypeSig ICloneable;
        public readonly TypeSig ValueType;
        public readonly TypeSig Enum;
        public readonly TypeSig Delegate;
        public readonly TypeSig MulticastDelegate;

        public CoreTypeSigs(
            TypeSig @object,
            TypeSig @void,
            TypeSig @string,
            TypeSig array,
            TypeSig nullable,
            TypeSig iCloneable,
            TypeSig valueType,
            TypeSig @enum,
            TypeSig @delegate,
            TypeSig multicastDelegate)
        {
            Object = @object;
            Void = @void;
            String = @string;
            Array = array;
            Nullable = nullable;
            ICloneable = iCloneable;
            ValueType = valueType;
            Enum = @enum;
            Delegate = @delegate;
            MulticastDelegate = multicastDelegate;
        }
    }
    private readonly struct SystemThreadingTypeSigs
    {
        public readonly TypeSig Task;
        public readonly TypeSig ValueTask;
        public readonly TypeSig TaskT;
        public readonly TypeSig ValueTaskT;

        public SystemThreadingTypeSigs(
          TypeSig task,
          TypeSig valueTask,
          TypeSig taskT,
          TypeSig valueTaskT)
        {
            Task = task;
            ValueTask = valueTask;
            TaskT = taskT;
            ValueTaskT = valueTaskT;
        }
    }
    private readonly struct SystemCollectionsGenericTypeSigs
    {
        public readonly TypeSig IEnumerable;
        public readonly TypeSig ICollection;
        public readonly TypeSig IList;
        public readonly TypeSig IReadOnlyCollection;
        public readonly TypeSig IReadOnlyList;

        public SystemCollectionsGenericTypeSigs(
            TypeSig iEnumerable,
            TypeSig iCollection,
            TypeSig iList,
            TypeSig iReadOnlyCollection,
            TypeSig iReadOnlyList)
        {
            IEnumerable = iEnumerable;
            ICollection = iCollection;
            IList = iList;
            IReadOnlyCollection = iReadOnlyCollection;
            IReadOnlyList = iReadOnlyList;
        }
    }

    private readonly struct SystemCollectionsTypeSigs
    {
        public readonly TypeSig IEnumerable;
        public readonly TypeSig ICollection;
        public readonly TypeSig IList;
        public readonly TypeSig IStructuralComparable;

        public SystemCollectionsTypeSigs(
            TypeSig iEnumerable,
            TypeSig iCollection,
            TypeSig iList,
            TypeSig iStructuralComparable)
        {
            IEnumerable = iEnumerable;
            ICollection = iCollection;
            IList = iList;
            IStructuralComparable = iStructuralComparable;
        }
    }

    private static readonly ConcurrentDictionary<TypeSigKey, int> _typeSigInterner = new();
    private static int _nextTypeSigId = 1;

    private static int InternTypeSigKey(TypeSigKey key)
    {
        if (_typeSigInterner.TryGetValue(key, out var id))
            return id;

        id = Interlocked.Increment(ref _nextTypeSigId);
        if (id <= 0)
            throw new InvalidOperationException("TypeSig id space exhausted.");

        return _typeSigInterner.GetOrAdd(key, id);
    }

    private enum TypeSigKind : byte
    {
        Named = 1,
        GenericParameter = 2,
        GenericInstance = 3,
        Array = 4,
        ByRef = 5,
        Pointer = 6,
        Pinned = 7,
        Sentinel = 8,
        RequiredModifier = 9,
        OptionalModifier = 10,
        FunctionPointer = 11,
    }

    private readonly struct TypeDefKey : IEquatable<TypeDefKey>
    {
        private readonly bool _resolved;
        private readonly Guid _moduleMvid;
        private readonly int _metadataToken;

        // 兜底身份标识。验证器中这条路径应该很少出现，因为未解析类型直接抛出（
        private readonly string? _scopeName;
        private readonly string? _namespace;
        private readonly string? _name;
        private readonly TypeSig _declaringType;

        private TypeDefKey(TypeDefinition definition)
        {
            _resolved = true;
            _moduleMvid = definition.Module?.Mvid ?? Guid.Empty;
            _metadataToken = definition.MetadataToken.ToInt32();
            _scopeName = null;
            _namespace = null;
            _name = null;
            _declaringType = default;
        }

        private TypeDefKey(TypeReference reference)
        {
            _resolved = false;
            _moduleMvid = reference.Module?.Mvid ?? Guid.Empty;
            _metadataToken = reference.MetadataToken.ToInt32();
            _scopeName = reference.Scope?.Name;
            _namespace = reference.Namespace;
            _name = reference.Name;
            _declaringType = reference.IsNested && reference.DeclaringType != null
                ? TypeSig.Create(reference.DeclaringType)
                : default;
        }

        public static TypeDefKey Create(TypeReference reference)
        {
            var definition = TryResolve(reference);
            return definition != null ? new TypeDefKey(definition) : new TypeDefKey(reference);
        }

        public bool Equals(TypeDefKey other)
        {
            if (_resolved != other._resolved) return false;

            if (_resolved)
            {
                return _moduleMvid == other._moduleMvid
                       && _metadataToken == other._metadataToken;
            }

            //兜底
            return _moduleMvid == other._moduleMvid
                   && _metadataToken == other._metadataToken
                   && _scopeName == other._scopeName
                   && _namespace == other._namespace
                   && _name == other._name
                   && _declaringType == other._declaringType;
        }

        public override bool Equals(object? obj) => obj is TypeDefKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + _resolved.GetHashCode();
                h = h * 31 + _moduleMvid.GetHashCode();
                h = h * 31 + _metadataToken;
                if (!_resolved)
                {
                    h = h * 31 + (_scopeName?.GetHashCode() ?? 0);
                    h = h * 31 + (_namespace?.GetHashCode() ?? 0);
                    h = h * 31 + (_name?.GetHashCode() ?? 0);
                    h = h * 31 + _declaringType.GetHashCode();
                }
                return h;
            }
        }
    }

    private readonly struct MethodOwnerKey : IEquatable<MethodOwnerKey>
    {
        private readonly bool _resolved;
        private readonly Guid _moduleMvid;
        private readonly int _metadataToken;

        // 兜底逻辑。这里不要包含参数 TypeSig，否则方法泛型参数
        // 可能递归到其所属方法签名。
        private readonly TypeSig _declaringType;
        private readonly string? _name;
        private readonly int _genericParameterCount;
        private readonly int _parameterCount;
        private readonly bool _hasThis;
        private readonly MethodCallingConvention _callingConvention;

        private MethodOwnerKey(MethodDefinition definition)
        {
            _resolved = true;
            _moduleMvid = definition.Module?.Mvid ?? Guid.Empty;
            _metadataToken = definition.MetadataToken.ToInt32();
            _declaringType = default;
            _name = null;
            _genericParameterCount = 0;
            _parameterCount = 0;
            _hasThis = false;
            _callingConvention = default;
        }

        private MethodOwnerKey(MethodReference reference)
        {
            _resolved = false;
            _moduleMvid = reference.Module?.Mvid ?? Guid.Empty;
            _metadataToken = reference.MetadataToken.ToInt32();
            _declaringType = reference.DeclaringType != null ? TypeSig.Create(reference.DeclaringType) : default;
            _name = reference.Name;
            _genericParameterCount = reference.GenericParameters.Count;
            _parameterCount = reference.Parameters.Count;
            _hasThis = reference.HasThis;
            _callingConvention = reference.CallingConvention;
        }

        public static MethodOwnerKey Create(MethodReference reference)
        {
            var definition = TryResolve(reference);
            return definition != null ? new MethodOwnerKey(definition) : new MethodOwnerKey(reference);
        }

        public bool Equals(MethodOwnerKey other)
        {
            if (_resolved != other._resolved) return false;

            if (_resolved)
            {
                return _moduleMvid == other._moduleMvid
                       && _metadataToken == other._metadataToken;
            }

            //兜底
            return _moduleMvid == other._moduleMvid
                   && _metadataToken == other._metadataToken
                   && _declaringType == other._declaringType
                   && _name == other._name
                   && _genericParameterCount == other._genericParameterCount
                   && _parameterCount == other._parameterCount
                   && _hasThis == other._hasThis
                   && _callingConvention == other._callingConvention;
        }

        public override bool Equals(object? obj) => obj is MethodOwnerKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + _resolved.GetHashCode();
                h = h * 31 + _moduleMvid.GetHashCode();
                h = h * 31 + _metadataToken;
                if (!_resolved)
                {
                    h = h * 31 + _declaringType.GetHashCode();
                    h = h * 31 + (_name?.GetHashCode() ?? 0);
                    h = h * 31 + _genericParameterCount;
                    h = h * 31 + _parameterCount;
                    h = h * 31 + _hasThis.GetHashCode();
                    h = h * 31 + (int)_callingConvention;
                }
                return h;
            }
        }
    }

    private sealed class TypeSigKey : IEquatable<TypeSigKey>
    {
        private readonly TypeSigKind _kind;
        private readonly TypeDefKey _defKey;

        private readonly TypeSig _element;
        private readonly TypeSig _modifier;
        private readonly TypeSig[]? _arguments;

        private readonly int _rank;
        private readonly bool _isVector;
        private readonly int?[]? _lowerBounds;
        private readonly int?[]? _upperBounds;

        private readonly GenericParameterType _genericParameterType;
        private readonly int _genericParameterPosition;
        private readonly byte _genericParameterOwnerKind;
        private readonly TypeSig _genericParameterOwnerType;
        private readonly MethodOwnerKey _genericParameterOwnerMethod;

        private readonly MethodCallingConvention _callingConvention;
        private readonly bool _hasThis;
        private readonly bool _explicitThis;
        private readonly TypeSig _returnType;

        private readonly int _hashCode;

        private TypeSigKey(
            TypeSigKind kind,
            TypeDefKey defKey = default,
            TypeSig element = default,
            TypeSig modifier = default,
            TypeSig[]? arguments = null,
            int rank = 0,
            bool isVector = false,
            int?[]? lowerBounds = null,
            int?[]? upperBounds = null,
            GenericParameterType genericParameterType = default,
            int genericParameterPosition = 0,
            byte genericParameterOwnerKind = 0,
            TypeSig genericParameterOwnerType = default,
            MethodOwnerKey genericParameterOwnerMethod = default,
            MethodCallingConvention callingConvention = default,
            bool hasThis = false,
            bool explicitThis = false,
            TypeSig returnType = default)
        {
            _kind = kind;
            _defKey = defKey;
            _element = element;
            _modifier = modifier;
            _arguments = arguments;
            _rank = rank;
            _isVector = isVector;
            _lowerBounds = lowerBounds;
            _upperBounds = upperBounds;
            _genericParameterType = genericParameterType;
            _genericParameterPosition = genericParameterPosition;
            _genericParameterOwnerKind = genericParameterOwnerKind;
            _genericParameterOwnerType = genericParameterOwnerType;
            _genericParameterOwnerMethod = genericParameterOwnerMethod;
            _callingConvention = callingConvention;
            _hasThis = hasThis;
            _explicitThis = explicitThis;
            _returnType = returnType;
            _hashCode = ComputeHashCode();
        }

        public static TypeSigKey Create(TypeReference type)
        {
            switch (type)
            {
                case GenericParameter gp:
                    return CreateGenericParameter(gp);

                case GenericInstanceType git:
                    return new TypeSigKey(
                        TypeSigKind.GenericInstance,
                        element: TypeSig.Create(git.ElementType),
                        arguments: CreateTypeSigArray(git.GenericArguments));

                case ArrayType array:
                    return CreateArray(array);

                case ByReferenceType byRef:
                    return new TypeSigKey(TypeSigKind.ByRef, element: TypeSig.Create(byRef.ElementType));

                case PointerType pointer:
                    return new TypeSigKey(TypeSigKind.Pointer, element: TypeSig.Create(pointer.ElementType));

                case PinnedType pinned:
                    return new TypeSigKey(TypeSigKind.Pinned, element: TypeSig.Create(pinned.ElementType));

                case SentinelType sentinel:
                    return new TypeSigKey(TypeSigKind.Sentinel, element: TypeSig.Create(sentinel.ElementType));

                case RequiredModifierType required:
                    return new TypeSigKey(
                        TypeSigKind.RequiredModifier,
                        element: TypeSig.Create(required.ElementType),
                        modifier: TypeSig.Create(required.ModifierType));

                case OptionalModifierType optional:
                    return new TypeSigKey(
                        TypeSigKind.OptionalModifier,
                        element: TypeSig.Create(optional.ElementType),
                        modifier: TypeSig.Create(optional.ModifierType));

                case FunctionPointerType functionPointer:
                    return new TypeSigKey(
                        TypeSigKind.FunctionPointer,
                        arguments: CreateParameterTypeSigArray(functionPointer.Parameters),
                        callingConvention: functionPointer.CallingConvention,
                        hasThis: functionPointer.HasThis,
                        explicitThis: functionPointer.ExplicitThis,
                        returnType: TypeSig.Create(functionPointer.ReturnType));

                default:
                    return new TypeSigKey(TypeSigKind.Named, defKey: TypeDefKey.Create(type));
            }
        }

        private static TypeSigKey CreateArray(ArrayType array)
        {
            var count = array.Dimensions.Count;
            int?[]? lowers = null;
            int?[]? uppers = null;

            if (count > 0)
            {
                lowers = new int?[count];
                uppers = new int?[count];
                for (int i = 0; i < count; i++)
                {
                    lowers[i] = array.Dimensions[i].LowerBound;
                    uppers[i] = array.Dimensions[i].UpperBound;
                }
            }

            return new TypeSigKey(
                TypeSigKind.Array,
                element: TypeSig.Create(array.ElementType),
                rank: array.Rank,
                isVector: array.IsVector,
                lowerBounds: lowers,
                upperBounds: uppers);
        }

        private static TypeSigKey CreateGenericParameter(GenericParameter gp)
        {
            if (gp.Owner is TypeReference ownerType)
            {
                return new TypeSigKey(
                    TypeSigKind.GenericParameter,
                    genericParameterType: gp.Type,
                    genericParameterPosition: gp.Position,
                    genericParameterOwnerKind: 1,
                    genericParameterOwnerType: TypeSig.Create(ownerType));
            }

            if (gp.Owner is MethodReference ownerMethod)
            {
                return new TypeSigKey(
                    TypeSigKind.GenericParameter,
                    genericParameterType: gp.Type,
                    genericParameterPosition: gp.Position,
                    genericParameterOwnerKind: 2,
                    genericParameterOwnerMethod: MethodOwnerKey.Create(ownerMethod));
            }

            return new TypeSigKey(
                TypeSigKind.GenericParameter,
                genericParameterType: gp.Type,
                genericParameterPosition: gp.Position,
                genericParameterOwnerKind: 0);
        }

        private static TypeSig[] CreateTypeSigArray(Mono.Collections.Generic.Collection<TypeReference> types)
        {
            var result = new TypeSig[types.Count];
            for (int i = 0; i < result.Length; i++)
                result[i] = TypeSig.Create(types[i]);
            return result;
        }

        private static TypeSig[] CreateParameterTypeSigArray(Mono.Collections.Generic.Collection<ParameterDefinition> parameters)
        {
            var result = new TypeSig[parameters.Count];
            for (int i = 0; i < result.Length; i++)
                result[i] = TypeSig.Create(parameters[i].ParameterType);
            return result;
        }

        public bool Equals(TypeSigKey? other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;
            if (_hashCode != other._hashCode) return false;
            if (_kind != other._kind) return false;

            switch (_kind)
            {
                case TypeSigKind.Named:
                    return _defKey.Equals(other._defKey);

                case TypeSigKind.GenericParameter:
                    return _genericParameterType == other._genericParameterType
                           && _genericParameterPosition == other._genericParameterPosition
                           && _genericParameterOwnerKind == other._genericParameterOwnerKind
                           && _genericParameterOwnerType == other._genericParameterOwnerType
                           && _genericParameterOwnerMethod.Equals(other._genericParameterOwnerMethod);

                case TypeSigKind.GenericInstance:
                    return _element == other._element
                           && TypeSigArrayEquals(_arguments, other._arguments);

                case TypeSigKind.Array:
                    return _element == other._element
                           && _rank == other._rank
                           && _isVector == other._isVector
                           && NullableIntArrayEquals(_lowerBounds, other._lowerBounds)
                           && NullableIntArrayEquals(_upperBounds, other._upperBounds);

                case TypeSigKind.ByRef:
                case TypeSigKind.Pointer:
                case TypeSigKind.Pinned:
                case TypeSigKind.Sentinel:
                    return _element == other._element;

                case TypeSigKind.RequiredModifier:
                case TypeSigKind.OptionalModifier:
                    return _element == other._element
                           && _modifier == other._modifier;

                case TypeSigKind.FunctionPointer:
                    return _callingConvention == other._callingConvention
                           && _hasThis == other._hasThis
                           && _explicitThis == other._explicitThis
                           && _returnType == other._returnType
                           && TypeSigArrayEquals(_arguments, other._arguments);

                default:
                    return false;
            }
        }

        public override bool Equals(object? obj) => obj is TypeSigKey other && Equals(other);
        public override int GetHashCode() => _hashCode;

        private int ComputeHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (int)_kind;
                h = h * 31 + _defKey.GetHashCode();
                h = h * 31 + _element.GetHashCode();
                h = h * 31 + _modifier.GetHashCode();
                h = h * 31 + TypeSigArrayHash(_arguments);
                h = h * 31 + _rank;
                h = h * 31 + _isVector.GetHashCode();
                h = h * 31 + NullableIntArrayHash(_lowerBounds);
                h = h * 31 + NullableIntArrayHash(_upperBounds);
                h = h * 31 + (int)_genericParameterType;
                h = h * 31 + _genericParameterPosition;
                h = h * 31 + _genericParameterOwnerKind;
                h = h * 31 + _genericParameterOwnerType.GetHashCode();
                h = h * 31 + _genericParameterOwnerMethod.GetHashCode();
                h = h * 31 + (int)_callingConvention;
                h = h * 31 + _hasThis.GetHashCode();
                h = h * 31 + _explicitThis.GetHashCode();
                h = h * 31 + _returnType.GetHashCode();
                return h;
            }
        }

        private static bool TypeSigArrayEquals(TypeSig[]? a, TypeSig[]? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static int TypeSigArrayHash(TypeSig[]? array)
        {
            if (array == null) return 0;
            unchecked
            {
                int h = array.Length;
                for (int i = 0; i < array.Length; i++)
                    h = h * 31 + array[i].GetHashCode();
                return h;
            }
        }

        private static bool NullableIntArrayEquals(int?[]? a, int?[]? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static int NullableIntArrayHash(int?[]? array)
        {
            if (array == null) return 0;
            unchecked
            {
                int h = array.Length;
                for (int i = 0; i < array.Length; i++)
                    h = h * 31 + (array[i]?.GetHashCode() ?? 0);
                return h;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TypeDefinition? TryResolve(TypeReference reference)
    {
        try
        {
            return reference.Resolve();
        }
        catch
        {
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static MethodDefinition? TryResolve(MethodReference reference)
    {
        try
        {
            return reference.Resolve();
        }
        catch
        {
            return null;
        }
    }
}
