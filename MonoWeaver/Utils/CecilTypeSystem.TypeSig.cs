using Mono.Cecil;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MonoWeaver.Utils;

public static partial class CecilTypeSystem
{
    /// <summary>
    /// 类型签名 id：
    /// <list type="bullet">
    /// <item><see cref="NameId"/>：名称嵌套匹配，
    /// <item><see cref="Id"/>：完整表示的 id，带metadataToken和module信息。</item>
    /// </list>
    /// </summary>
    public readonly struct TypeSig : IEquatable<TypeSig>
    {
        private static readonly ConcurrentDictionary<TypeSigKey, TypeSig> _interner = new();
        private static readonly ConcurrentDictionary<int, TypeSigKey> _keysById = new();
        private static readonly ConcurrentDictionary<DefinitionIdentity, int> _definitionInterner = new();
        private static readonly ConcurrentDictionary<Type, TypeSig> _runtimeCache = new();
        private static readonly ConditionalWeakTable<TypeReference, StrongBox<TypeSig>> _specificationCache = new();
        private static int _nextId;
        private static int _nextDefinitionId;

        private readonly int _id;
        private readonly int _nameId;

        private TypeSig(int id, int nameId)
        {
            _id = id;
            _nameId = nameId;
        }

        internal int Id => _id;
        internal int NameId => _nameId;
        public bool IsValid => _id != 0;

        internal TypeSig NameOnly => new(_nameId, _nameId);
        internal bool IsNameOnly => _id == _nameId;

        public static TypeSig Object { get; } = FromName("System", "Object");
        public static TypeSig Void { get; } = FromName("System", "Void");
        public static TypeSig String { get; } = FromName("System", "String");
        public static TypeSig Array { get; } = FromName("System", "Array");
        public static TypeSig Nullable { get; } = FromName("System", "Nullable`1");
        public static TypeSig ICloneable { get; } = FromName("System", "ICloneable");
        public static TypeSig ValueType { get; } = FromName("System", "ValueType");
        public static TypeSig Enum { get; } = FromName("System", "Enum");
        public static TypeSig Delegate { get; } = FromName("System", "Delegate");
        public static TypeSig MulticastDelegate { get; } = FromName("System", "MulticastDelegate");

        internal static class SystemCollections
        {
            public static TypeSig IEnumerable { get; } = FromName("System.Collections", "IEnumerable");
            public static TypeSig ICollection { get; } = FromName("System.Collections", "ICollection");
            public static TypeSig IList { get; } = FromName("System.Collections", "IList");
            public static TypeSig IStructuralComparable { get; } = FromName("System.Collections", "IStructuralComparable");
        }

        internal static class SystemThreading
        {
            public static TypeSig Task { get; } = FromName("System.Threading.Tasks", "Task");
            public static TypeSig ValueTask { get; } = FromName("System.Threading.Tasks", "ValueTask");
            public static TypeSig TaskT { get; } = FromName("System.Threading.Tasks", "Task`1");
            public static TypeSig ValueTaskT { get; } = FromName("System.Threading.Tasks", "ValueTask`1");
        }

        public static TypeSig Create(TypeReference t)
        {
            if (t == null) throw new ArgumentNullException(nameof(t));

            if (t is TypeSpecification)
                return _specificationCache.GetValue(t, static r => new StrongBox<TypeSig>(Intern(TypeSigKey.Create(r)))).Value;

            if (_typeSigCache.TryGetValue(t, out var cached))
                return cached;

            var sig = Intern(TypeSigKey.Create(t));
            return _typeSigCache.GetOrAdd(t, sig);
        }

        /// <summary>运行时类型的签名：只有名字身份</summary>
        public static TypeSig Create(Type t)
        {
            if (t == null) throw new ArgumentNullException(nameof(t));
            return _runtimeCache.GetOrAdd(t, static type => Intern(TypeSigKey.Create(type)));
        }


        public static TypeSig FromName(string @namespace, string name)
            => Intern(TypeSigKey.Named(TypeDefKey.FromName(@namespace ?? string.Empty, name, default)));

        private static TypeSig Intern(TypeSigKey key)
        {
            if (_interner.TryGetValue(key, out var existing))
                return existing;

            var nameId = key.IsNameOnly ? 0 : Intern(key.Strip()).Id;
            var id = Interlocked.Increment(ref _nextId);
            if (id <= 0)
                throw new InvalidOperationException("TypeSig id space exhausted.");
            var sig = new TypeSig(id, nameId == 0 ? id : nameId);

            var result = _interner.GetOrAdd(key, sig);
            if (result._id == id)
                _keysById[id] = key;
            return result;
        }

        private static int InternDefinition(Guid moduleMvid, int metadataToken)
        {
            var identity = new DefinitionIdentity(moduleMvid, metadataToken);
            if (_definitionInterner.TryGetValue(identity, out var id))
                return id;
            id = Interlocked.Increment(ref _nextDefinitionId);
            return _definitionInterner.GetOrAdd(identity, id);
        }

        /// <summary>同一个定义：两边都解析到了定义就比定义身份，否则退化到名字身份。</summary>
        public bool Equals(TypeSig other)
        {
            if (_id == other._id)
                return true;
            if (_nameId != other._nameId)
                return false;
            return !DefinitelyDifferent(_id, other._id);
        }

        /// <summary>只比名字/结构身份。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SameName(TypeSig other) => _nameId == other._nameId;

        /// <summary>完整表示逐位相同（缓存 key 用）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool ExactEquals(TypeSig other) => _id == other._id;

        private static bool DefinitelyDifferent(int leftId, int rightId)
        {
            if (leftId == rightId)
                return false;
            if (!_keysById.TryGetValue(leftId, out var left) || !_keysById.TryGetValue(rightId, out var right))
                return true;
            return TypeSigKey.DefinitelyDifferent(left, right);
        }

        internal static bool DefinitelyDifferent(TypeSig left, TypeSig right)
        {
            if (left._id == right._id)
                return false;
            if (left._nameId != right._nameId)
                return true;
            return DefinitelyDifferent(left._id, right._id);
        }

        public override bool Equals(object? obj) => obj is TypeSig other && Equals(other);
        //哈希使用名字身份
        public override int GetHashCode() => _nameId;
        public override string ToString() => _id == 0 ? "<invalid>" : _id == _nameId ? $"sig#{_id}" : $"sig#{_id}(name#{_nameId})";

        public static bool operator ==(TypeSig left, TypeSig right) => left.Equals(right);
        public static bool operator !=(TypeSig left, TypeSig right) => !left.Equals(right);
    }

    private readonly struct DefinitionIdentity : IEquatable<DefinitionIdentity>
    {
        private readonly Guid _moduleMvid;
        private readonly int _metadataToken;

        public DefinitionIdentity(Guid moduleMvid, int metadataToken)
        {
            _moduleMvid = moduleMvid;
            _metadataToken = metadataToken;
        }

        public bool Equals(DefinitionIdentity other)
            => _metadataToken == other._metadataToken && _moduleMvid == other._moduleMvid;
        public override bool Equals(object? obj) => obj is DefinitionIdentity other && Equals(other);
        public override int GetHashCode() => unchecked(_moduleMvid.GetHashCode() * 31 + _metadataToken);
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

    /// <summary>名字身份 + 可选的定义身份（0 表示未解析）。</summary>
    private readonly struct TypeDefKey : IEquatable<TypeDefKey>
    {
        private readonly string? _namespace;   //嵌套类型为 null
        private readonly string? _name;
        private readonly TypeSig _declaringType;
        private readonly int _definitionId;

        private TypeDefKey(string? @namespace, string? name, TypeSig declaringType, int definitionId)
        {
            _namespace = @namespace;
            _name = name;
            _declaringType = declaringType;
            _definitionId = definitionId;
        }

        public int DefinitionId => _definitionId;
        public bool IsNameOnly => _definitionId == 0;
        public TypeDefKey Strip() => _definitionId == 0 ? this : new TypeDefKey(_namespace, _name, _declaringType, 0);

        public static TypeDefKey FromName(string? @namespace, string name, TypeSig declaringType)
            => new(declaringType.IsValid ? null : @namespace ?? string.Empty, name, declaringType, 0);

        public static TypeDefKey Create(TypeReference reference)
        {
            var definition = TryResolve(reference);
            var definitionId = definition?.Module is { } module
                ? TypeSig_InternDefinition(module.Mvid, definition.MetadataToken.ToInt32())
                : 0;
            //名字取自引用本身；嵌套类型和 Cecil 一样只看 DeclaringType 链
            var declaring = reference.DeclaringType is { } declaringType
                ? TypeSig.Create(declaringType).NameOnly
                : default;
            return new TypeDefKey(declaring.IsValid ? null : reference.Namespace ?? string.Empty,
                reference.Name, declaring, definitionId);
        }

        public static TypeDefKey Create(Type type)
        {
            //反射对嵌套类型的 Namespace 返回外层命名空间，这里只用 DeclaringType 链
            var declaring = type.DeclaringType is { } declaringType && !type.IsGenericParameter
                ? TypeSig.Create(declaringType)
                : default;
            return new TypeDefKey(declaring.IsValid ? null : type.Namespace ?? string.Empty, type.Name, declaring, 0);
        }

        public static bool DefinitelyDifferent(TypeDefKey left, TypeDefKey right)
            => left._definitionId != 0 && right._definitionId != 0 && left._definitionId != right._definitionId;

        public bool Equals(TypeDefKey other)
            => _definitionId == other._definitionId
               && _declaringType.ExactEquals(other._declaringType)
               && string.Equals(_name, other._name, StringComparison.Ordinal)
               && string.Equals(_namespace, other._namespace, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is TypeDefKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + _definitionId;
                h = h * 31 + _declaringType.Id;
                h = h * 31 + (_name is null ? 0 : StringComparer.Ordinal.GetHashCode(_name));
                h = h * 31 + (_namespace is null ? 0 : StringComparer.Ordinal.GetHashCode(_namespace));
                return h;
            }
        }
    }

    private static int TypeSig_InternDefinition(Guid moduleMvid, int metadataToken)
        => TypeSigDefinitionInterner.Intern(moduleMvid, metadataToken);

    private static class TypeSigDefinitionInterner
    {
        private static readonly ConcurrentDictionary<DefinitionIdentity, int> _interner = new();
        private static int _nextId;

        public static int Intern(Guid moduleMvid, int metadataToken)
        {
            var identity = new DefinitionIdentity(moduleMvid, metadataToken);
            if (_interner.TryGetValue(identity, out var id))
                return id;
            id = Interlocked.Increment(ref _nextId);
            return _interner.GetOrAdd(identity, id);
        }
    }

    private readonly struct MethodOwnerKey : IEquatable<MethodOwnerKey>
    {
        private readonly TypeSig _declaringType;
        private readonly string? _name;
        private readonly int _genericParameterCount;
        private readonly int _parameterCount;
        private readonly bool _hasThis;
        private readonly MethodCallingConvention _callingConvention;
        private readonly int _definitionId;

        private MethodOwnerKey(TypeSig declaringType, string? name, int genericParameterCount, int parameterCount,
            bool hasThis, MethodCallingConvention callingConvention, int definitionId)
        {
            _declaringType = declaringType;
            _name = name;
            _genericParameterCount = genericParameterCount;
            _parameterCount = parameterCount;
            _hasThis = hasThis;
            _callingConvention = callingConvention;
            _definitionId = definitionId;
        }

        public bool IsValid => _name is not null;
        public bool IsNameOnly => _definitionId == 0 && (!_declaringType.IsValid || _declaringType.IsNameOnly);

        public MethodOwnerKey Strip()
            => IsNameOnly ? this : new MethodOwnerKey(_declaringType.NameOnly, _name, _genericParameterCount,
                _parameterCount, _hasThis, _callingConvention, 0);

        public static MethodOwnerKey Create(MethodReference reference)
        {
            var definition = TryResolve(reference);
            var definitionId = definition?.Module is { } module
                ? TypeSig_InternDefinition(module.Mvid, definition.MetadataToken.ToInt32())
                : 0;
            return new MethodOwnerKey(
                reference.DeclaringType != null ? TypeSig.Create(reference.DeclaringType) : default,
                reference.Name,
                reference.GenericParameters.Count,
                reference.Parameters.Count,
                reference.HasThis,
                reference.CallingConvention,
                definitionId);
        }

        public static MethodOwnerKey Create(MethodBase method)
        {
            var genericCount = method.IsGenericMethodDefinition || method.IsGenericMethod
                ? method.GetGenericArguments().Length
                : 0;
            return new MethodOwnerKey(
                method.DeclaringType != null ? TypeSig.Create(method.DeclaringType) : default,
                method.Name,
                genericCount,
                method.GetParameters().Length,
                !method.IsStatic,
                genericCount != 0 ? MethodCallingConvention.Generic : MethodCallingConvention.Default,
                0);
        }

        public static bool DefinitelyDifferent(MethodOwnerKey left, MethodOwnerKey right)
        {
            if (left._definitionId != 0 && right._definitionId != 0)
                return left._definitionId != right._definitionId;
            return TypeSig.DefinitelyDifferent(left._declaringType, right._declaringType);
        }

        public bool Equals(MethodOwnerKey other)
            => _definitionId == other._definitionId
               && _declaringType.ExactEquals(other._declaringType)
               && string.Equals(_name, other._name, StringComparison.Ordinal)
               && _genericParameterCount == other._genericParameterCount
               && _parameterCount == other._parameterCount
               && _hasThis == other._hasThis
               && _callingConvention == other._callingConvention;

        public override bool Equals(object? obj) => obj is MethodOwnerKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + _definitionId;
                h = h * 31 + _declaringType.Id;
                h = h * 31 + (_name is null ? 0 : StringComparer.Ordinal.GetHashCode(_name));
                h = h * 31 + _genericParameterCount;
                h = h * 31 + _parameterCount;
                h = h * 31 + (_hasThis ? 1 : 0);
                h = h * 31 + (int)_callingConvention;
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
        private readonly bool _isNameOnly;

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
            _isNameOnly = ComputeIsNameOnly();
        }

        public bool IsNameOnly => _isNameOnly;

        public static TypeSigKey Named(TypeDefKey defKey) => new(TypeSigKind.Named, defKey: defKey);

        /// <summary>去掉所有定义身份（和数组边界）后的名字键。</summary>
        public TypeSigKey Strip()
        {
            if (_isNameOnly)
                return this;

            return new TypeSigKey(
                _kind,
                defKey: _defKey.Strip(),
                element: _element.IsValid ? _element.NameOnly : default,
                modifier: _modifier.IsValid ? _modifier.NameOnly : default,
                arguments: StripArray(_arguments),
                rank: _rank,
                isVector: _isVector,
                genericParameterType: _genericParameterType,
                genericParameterPosition: _genericParameterPosition,
                genericParameterOwnerKind: _genericParameterOwnerKind,
                genericParameterOwnerType: _genericParameterOwnerType.IsValid ? _genericParameterOwnerType.NameOnly : default,
                genericParameterOwnerMethod: _genericParameterOwnerMethod.IsValid ? _genericParameterOwnerMethod.Strip() : default,
                callingConvention: _callingConvention,
                hasThis: _hasThis,
                explicitThis: _explicitThis,
                returnType: _returnType.IsValid ? _returnType.NameOnly : default);
        }

        private static TypeSig[]? StripArray(TypeSig[]? sigs)
        {
            if (sigs is null)
                return null;
            var result = new TypeSig[sigs.Length];
            for (var i = 0; i < sigs.Length; i++)
                result[i] = sigs[i].NameOnly;
            return result;
        }

        private bool ComputeIsNameOnly()
        {
            if (!_defKey.IsNameOnly) return false;
            if (_element.IsValid && !_element.IsNameOnly) return false;
            if (_modifier.IsValid && !_modifier.IsNameOnly) return false;
            if (_returnType.IsValid && !_returnType.IsNameOnly) return false;
            if (_genericParameterOwnerType.IsValid && !_genericParameterOwnerType.IsNameOnly) return false;
            if (_genericParameterOwnerMethod.IsValid && !_genericParameterOwnerMethod.IsNameOnly) return false;
            if (_lowerBounds is not null || _upperBounds is not null) return false;
            if (_arguments is not null)
            {
                foreach (var argument in _arguments)
                    if (!argument.IsNameOnly) return false;
            }
            return true;
        }

        /// <summary>两个同名键是否在某个叶子上带有不同的定义身份。</summary>
        public static bool DefinitelyDifferent(TypeSigKey left, TypeSigKey right)
        {
            if (left._kind != right._kind)
                return true;

            switch (left._kind)
            {
                case TypeSigKind.Named:
                    return TypeDefKey.DefinitelyDifferent(left._defKey, right._defKey);

                case TypeSigKind.GenericParameter:
                    if (left._genericParameterOwnerKind != right._genericParameterOwnerKind)
                        return true;
                    return left._genericParameterOwnerKind switch
                    {
                        1 => TypeSig.DefinitelyDifferent(left._genericParameterOwnerType, right._genericParameterOwnerType),
                        2 => MethodOwnerKey.DefinitelyDifferent(left._genericParameterOwnerMethod, right._genericParameterOwnerMethod),
                        _ => false,
                    };

                case TypeSigKind.GenericInstance:
                    return TypeSig.DefinitelyDifferent(left._element, right._element)
                           || ArrayDefinitelyDifferent(left._arguments, right._arguments);

                case TypeSigKind.RequiredModifier:
                case TypeSigKind.OptionalModifier:
                    return TypeSig.DefinitelyDifferent(left._element, right._element)
                           || TypeSig.DefinitelyDifferent(left._modifier, right._modifier);

                case TypeSigKind.FunctionPointer:
                    return TypeSig.DefinitelyDifferent(left._returnType, right._returnType)
                           || ArrayDefinitelyDifferent(left._arguments, right._arguments);

                default:
                    return TypeSig.DefinitelyDifferent(left._element, right._element);
            }
        }

        private static bool ArrayDefinitelyDifferent(TypeSig[]? left, TypeSig[]? right)
        {
            if (left is null || right is null)
                return !(left is null && right is null);
            if (left.Length != right.Length)
                return true;
            for (var i = 0; i < left.Length; i++)
            {
                if (TypeSig.DefinitelyDifferent(left[i], right[i]))
                    return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- Cecil

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

        // ---------------------------------------------------------------- 反射（只有名字身份）

        public static TypeSigKey Create(Type type)
        {
            if (type.IsGenericParameter)
            {
                var gpType = type.DeclaringMethod is not null ? GenericParameterType.Method : GenericParameterType.Type;
                if (type.DeclaringMethod is { } method)
                {
                    return new TypeSigKey(
                        TypeSigKind.GenericParameter,
                        genericParameterType: gpType,
                        genericParameterPosition: type.GenericParameterPosition,
                        genericParameterOwnerKind: 2,
                        genericParameterOwnerMethod: MethodOwnerKey.Create(method));
                }
                return new TypeSigKey(
                    TypeSigKind.GenericParameter,
                    genericParameterType: gpType,
                    genericParameterPosition: type.GenericParameterPosition,
                    genericParameterOwnerKind: 1,
                    genericParameterOwnerType: type.DeclaringType is { } owner ? TypeSig.Create(owner) : default);
            }

            if (type.IsByRef)
                return new TypeSigKey(TypeSigKind.ByRef, element: TypeSig.Create(type.GetElementType()!));
            if (type.IsPointer)
                return new TypeSigKey(TypeSigKind.Pointer, element: TypeSig.Create(type.GetElementType()!));
            if (type.IsArray)
            {
                var rank = type.GetArrayRank();
                //int[] 是 vector，int[*] 不是；反射里只能从名字区分。边界不进名字键
                var isVector = rank == 1 && type.Name.EndsWith("[]", StringComparison.Ordinal);
                return new TypeSigKey(TypeSigKind.Array, element: TypeSig.Create(type.GetElementType()!),
                    rank: rank, isVector: isVector);
            }
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                var runtimeArguments = type.GetGenericArguments();
                var arguments = new TypeSig[runtimeArguments.Length];
                for (var i = 0; i < arguments.Length; i++)
                    arguments[i] = TypeSig.Create(runtimeArguments[i]);
                return new TypeSigKey(TypeSigKind.GenericInstance,
                    element: TypeSig.Create(type.GetGenericTypeDefinition()), arguments: arguments);
            }

            return new TypeSigKey(TypeSigKind.Named, defKey: TypeDefKey.Create(type));
        }

        // ---------------------------------------------------------------- 相等（完整表示，嵌套签名按 Id 严格比较）

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
                           && _genericParameterOwnerType.ExactEquals(other._genericParameterOwnerType)
                           && _genericParameterOwnerMethod.Equals(other._genericParameterOwnerMethod);

                case TypeSigKind.GenericInstance:
                    return _element.ExactEquals(other._element)
                           && TypeSigArrayEquals(_arguments, other._arguments);

                case TypeSigKind.Array:
                    return _element.ExactEquals(other._element)
                           && _rank == other._rank
                           && _isVector == other._isVector
                           && NullableIntArrayEquals(_lowerBounds, other._lowerBounds)
                           && NullableIntArrayEquals(_upperBounds, other._upperBounds);

                case TypeSigKind.ByRef:
                case TypeSigKind.Pointer:
                case TypeSigKind.Pinned:
                case TypeSigKind.Sentinel:
                    return _element.ExactEquals(other._element);

                case TypeSigKind.RequiredModifier:
                case TypeSigKind.OptionalModifier:
                    return _element.ExactEquals(other._element)
                           && _modifier.ExactEquals(other._modifier);

                case TypeSigKind.FunctionPointer:
                    return _callingConvention == other._callingConvention
                           && _hasThis == other._hasThis
                           && _explicitThis == other._explicitThis
                           && _returnType.ExactEquals(other._returnType)
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
                h = h * 31 + _element.Id;
                h = h * 31 + _modifier.Id;
                h = h * 31 + TypeSigArrayHash(_arguments);
                h = h * 31 + _rank;
                h = h * 31 + _isVector.GetHashCode();
                h = h * 31 + NullableIntArrayHash(_lowerBounds);
                h = h * 31 + NullableIntArrayHash(_upperBounds);
                h = h * 31 + (int)_genericParameterType;
                h = h * 31 + _genericParameterPosition;
                h = h * 31 + _genericParameterOwnerKind;
                h = h * 31 + _genericParameterOwnerType.Id;
                h = h * 31 + _genericParameterOwnerMethod.GetHashCode();
                h = h * 31 + (int)_callingConvention;
                h = h * 31 + _hasThis.GetHashCode();
                h = h * 31 + _explicitThis.GetHashCode();
                h = h * 31 + _returnType.Id;
                return h;
            }
        }

        private static bool TypeSigArrayEquals(TypeSig[]? a, TypeSig[]? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!a[i].ExactEquals(b[i])) return false;
            return true;
        }

        private static int TypeSigArrayHash(TypeSig[]? array)
        {
            if (array == null) return 0;
            unchecked
            {
                int h = array.Length;
                for (int i = 0; i < array.Length; i++)
                    h = h * 31 + array[i].Id;
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
        => MetadataResolution.TryResolve(reference);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static MethodDefinition? TryResolve(MethodReference reference)
        => MetadataResolution.TryResolve(reference);
}
