using Mono.Cecil;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MonoWeaver.Utils;

public static partial class CecilTypeSystem
{
    /// <summary>
    /// 按名字比较的类型签名 id：只看 namespace / name / 嵌套链 / 泛型-数组-指针结构，
    /// </summary>
    public readonly struct TypeNameSig : IEquatable<TypeNameSig>
    {
        private static readonly ConcurrentDictionary<TypeNameKey, int> _interner = new();
        private static readonly ConcurrentDictionary<Type, TypeNameSig> _runtimeCache = new();
        private static readonly ConditionalWeakTable<TypeReference, StrongBox<TypeNameSig>> _cecilCache = new();
        private static int _nextId;

        private readonly int _id;

        private TypeNameSig(int id) => _id = id;

        public bool IsValid => _id != 0;

        public static TypeNameSig Create(TypeReference type)
        {
            if (type is null)
                throw new ArgumentNullException(nameof(type));

            type = type.StripType();
            //TypeSpecification 会被 InflateDeclaringGenerics / new ByReferenceType 之类不断新建，
            //缓存它们只会泄漏；其元素类型都走缓存，重新组合本身很便宜。
            if (type is TypeSpecification || type is GenericParameter)
                return new TypeNameSig(Intern(CreateKey(type)));

            return _cecilCache.GetValue(type, static t => new StrongBox<TypeNameSig>(new TypeNameSig(Intern(CreateKey(t))))).Value;
        }

        public static TypeNameSig Create(Type type)
        {
            if (type is null)
                throw new ArgumentNullException(nameof(type));
            return _runtimeCache.GetOrAdd(type, static t => new TypeNameSig(Intern(CreateKey(t))));
        }

        private static int Intern(TypeNameKey key)
        {
            if (_interner.TryGetValue(key, out var id))
                return id;
            id = Interlocked.Increment(ref _nextId);
            return _interner.GetOrAdd(key, id);
        }

        private static TypeNameKey CreateKey(TypeReference type)
        {
            switch (type)
            {
                case GenericParameter gp:
                    return TypeNameKey.GenericParameter(gp.Position, gp.Type == GenericParameterType.Method);
                case GenericInstanceType git:
                {
                    var arguments = new TypeNameSig[git.GenericArguments.Count];
                    for (var i = 0; i < arguments.Length; i++)
                        arguments[i] = Create(git.GenericArguments[i]);
                    return TypeNameKey.GenericInstance(Create(git.ElementType), arguments);
                }
                case ArrayType array:
                    return TypeNameKey.Array(Create(array.ElementType), array.Rank, array.IsVector);
                case ByReferenceType byRef:
                    return TypeNameKey.ByRef(Create(byRef.ElementType));
                case PointerType pointer:
                    return TypeNameKey.Pointer(Create(pointer.ElementType));
                case FunctionPointerType functionPointer:
                {
                    var parameters = new TypeNameSig[functionPointer.Parameters.Count];
                    for (var i = 0; i < parameters.Length; i++)
                        parameters[i] = Create(functionPointer.Parameters[i].ParameterType);
                    return TypeNameKey.FunctionPointer(Create(functionPointer.ReturnType), parameters);
                }
                default:
                    return type.DeclaringType is { } declaring
                        ? TypeNameKey.Nested(type.Name, Create(declaring))
                        : TypeNameKey.TopLevel(type.Namespace ?? string.Empty, type.Name);
            }
        }

        private static TypeNameKey CreateKey(Type type)
        {
            if (type.IsGenericParameter)
                return TypeNameKey.GenericParameter(type.GenericParameterPosition, type.DeclaringMethod is not null);
            if (type.IsByRef)
                return TypeNameKey.ByRef(Create(type.GetElementType()!));
            if (type.IsPointer)
                return TypeNameKey.Pointer(Create(type.GetElementType()!));
            if (type.IsArray)
            {
                var rank = type.GetArrayRank();
                //int[] 是 vector，int[*] 不是；反射里只能从名字区分
                var isVector = rank == 1 && type.Name.EndsWith("[]", StringComparison.Ordinal);
                return TypeNameKey.Array(Create(type.GetElementType()!), rank, isVector);
            }
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                var runtimeArguments = type.GetGenericArguments();
                var arguments = new TypeNameSig[runtimeArguments.Length];
                for (var i = 0; i < arguments.Length; i++)
                    arguments[i] = Create(runtimeArguments[i]);
                return TypeNameKey.GenericInstance(Create(type.GetGenericTypeDefinition()), arguments);
            }

            //反射对嵌套类型的 Namespace 返回外层命名空间，这里和 Cecil 一样只用 DeclaringType 链
            return type.DeclaringType is { } declaring
                ? TypeNameKey.Nested(type.Name, Create(declaring))
                : TypeNameKey.TopLevel(type.Namespace ?? string.Empty, type.Name);
        }

        public bool Equals(TypeNameSig other) => _id == other._id;
        public override bool Equals(object? obj) => obj is TypeNameSig other && Equals(other);
        public override int GetHashCode() => _id;
        public override string ToString() => _id == 0 ? "<invalid>" : $"name#{_id}";
        public static bool operator ==(TypeNameSig left, TypeNameSig right) => left._id == right._id;
        public static bool operator !=(TypeNameSig left, TypeNameSig right) => left._id != right._id;
    }

    private enum TypeNameKind : byte
    {
        TopLevel = 1,
        Nested = 2,
        GenericParameter = 3,
        GenericInstance = 4,
        Array = 5,
        ByRef = 6,
        Pointer = 7,
        FunctionPointer = 8,
    }

    private readonly struct TypeNameKey : IEquatable<TypeNameKey>
    {
        private readonly TypeNameKind _kind;
        private readonly string? _namespace;
        private readonly string? _name;
        private readonly TypeNameSig _element;      // 嵌套链的外层 / 元素类型 / 函数指针返回类型
        private readonly TypeNameSig[]? _arguments;
        private readonly int _number;               // 数组 rank / 泛型参数位置
        private readonly bool _flag;                // 数组 isVector / 泛型参数属于方法
        private readonly int _hashCode;

        private TypeNameKey(TypeNameKind kind, string? ns, string? name, TypeNameSig element,
            TypeNameSig[]? arguments, int number, bool flag)
        {
            _kind = kind;
            _namespace = ns;
            _name = name;
            _element = element;
            _arguments = arguments;
            _number = number;
            _flag = flag;

            unchecked
            {
                var h = (int)kind;
                h = h * 31 + (ns is null ? 0 : StringComparer.Ordinal.GetHashCode(ns));
                h = h * 31 + (name is null ? 0 : StringComparer.Ordinal.GetHashCode(name));
                h = h * 31 + element.GetHashCode();
                h = h * 31 + number;
                h = h * 31 + (flag ? 1 : 0);
                if (arguments is not null)
                {
                    foreach (var argument in arguments)
                        h = h * 31 + argument.GetHashCode();
                }
                _hashCode = h;
            }
        }

        public static TypeNameKey TopLevel(string ns, string name)
            => new(TypeNameKind.TopLevel, ns, name, default, null, 0, false);
        public static TypeNameKey Nested(string name, TypeNameSig declaring)
            => new(TypeNameKind.Nested, null, name, declaring, null, 0, false);
        public static TypeNameKey GenericParameter(int position, bool ownedByMethod)
            => new(TypeNameKind.GenericParameter, null, null, default, null, position, ownedByMethod);
        public static TypeNameKey GenericInstance(TypeNameSig element, TypeNameSig[] arguments)
            => new(TypeNameKind.GenericInstance, null, null, element, arguments, 0, false);
        public static TypeNameKey Array(TypeNameSig element, int rank, bool isVector)
            => new(TypeNameKind.Array, null, null, element, null, rank, isVector);
        public static TypeNameKey ByRef(TypeNameSig element)
            => new(TypeNameKind.ByRef, null, null, element, null, 0, false);
        public static TypeNameKey Pointer(TypeNameSig element)
            => new(TypeNameKind.Pointer, null, null, element, null, 0, false);
        public static TypeNameKey FunctionPointer(TypeNameSig returnType, TypeNameSig[] parameters)
            => new(TypeNameKind.FunctionPointer, null, null, returnType, parameters, 0, false);

        public bool Equals(TypeNameKey other)
        {
            if (_hashCode != other._hashCode || _kind != other._kind)
                return false;
            if (!string.Equals(_namespace, other._namespace, StringComparison.Ordinal)
                || !string.Equals(_name, other._name, StringComparison.Ordinal)
                || _element != other._element
                || _number != other._number
                || _flag != other._flag)
            {
                return false;
            }

            if (_arguments is null || other._arguments is null)
                return _arguments is null && other._arguments is null;
            if (_arguments.Length != other._arguments.Length)
                return false;
            for (var i = 0; i < _arguments.Length; i++)
            {
                if (_arguments[i] != other._arguments[i])
                    return false;
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is TypeNameKey other && Equals(other);
        public override int GetHashCode() => _hashCode;
    }
}
