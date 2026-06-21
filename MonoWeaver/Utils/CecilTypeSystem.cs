using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using COpCodes = Mono.Cecil.Cil.OpCodes;
using GenericParameterAttributes = Mono.Cecil.GenericParameterAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using OpCodes = System.Reflection.Emit.OpCodes;
using StackBehaviour = Mono.Cecil.Cil.StackBehaviour;
using TypeAttributes = Mono.Cecil.TypeAttributes;


namespace MonoWeaver.Utils;


/// <summary>
/// 提供类型系统相关的功能，例如类型比较、类型关系判断、获取基类和接口等。
/// 字段、类型定义
/// </summary>
public static partial class CecilTypeSystem
{
    private static readonly ConcurrentDictionary<ModuleDefinition, TypeDefinition> _arrayDefs;
    private static readonly ConcurrentDictionary<ModuleDefinition, TypeReference[]> _arrayInfs;

    private static readonly FixedSizeDictionary<ulong, bool> _assignableCache;
    private static readonly IEqualityComparer<ulong> _assignableKeyComparer = new AssignableKeyComparer();
    private static readonly ConcurrentDictionary<TypeReference, TypeSig> _typeSigCache;

    static CecilTypeSystem()
    {
        int concurrencyLevel = Math.Max(1, Environment.ProcessorCount * 2);
        _typeSigCache = new ConcurrentDictionary<TypeReference, TypeSig>(concurrencyLevel, 128);
        _arrayDefs = new ConcurrentDictionary<ModuleDefinition, TypeDefinition>(concurrencyLevel, 16);
        _arrayInfs = new ConcurrentDictionary<ModuleDefinition, TypeReference[]>(concurrencyLevel, 16);
        _assignableCache = new FixedSizeDictionary<ulong, bool>(8192, _assignableKeyComparer);
    }

    [ThreadStatic]
    private static AssignabilityContext? _assignabilityContext;

    private sealed class AssignabilityContext
    {
        public readonly HashSet<ulong> Active = new(_assignableKeyComparer);
        public int CycleVersion;
        public bool InUse;
    }

    private sealed class AssignableKeyComparer : IEqualityComparer<ulong>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(ulong x, ulong y) => x == y;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetHashCode(ulong value)
        {
            value ^= value >> 30;
            value *= 0xbf58476d1ce4e5b9UL;
            value ^= value >> 27;
            value *= 0x94d049bb133111ebUL;
            value ^= value >> 31;
            return (int)(value ^ (value >> 32));
        }
    }


    internal static class InterfaceTraversalCache
    {
        [ThreadStatic]
        private static HashSet<TypeSig>? _reusableSet;

        [ThreadStatic]
        private static Stack<TypeReference>? _reusableStack;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static HashSet<TypeSig> GetClearedSet()
        {
            if (_reusableSet == null)
                _reusableSet = new HashSet<TypeSig>();
            else
                _reusableSet.Clear();
            return _reusableSet;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Stack<TypeReference> GetClearedStack()
        {
            if (_reusableStack == null)
                _reusableStack = new Stack<TypeReference>();
            else
                _reusableStack.Clear();
            return _reusableStack;
        }
    }
}


/// <summary>
/// 提供类型系统相关的功能，例如类型比较、类型关系判断、获取基类和接口等。
/// 公共接口部分
/// </summary>
public static partial class CecilTypeSystem
{
    /// <summary>
    /// 判断 from 是否可以赋值给 to，考虑 IL 栈上的类型转换（例如 byte -> int）
    /// 但不考虑 Byte Sbyte Uint16都转化为I4这种的等价（在 `MonoWeaver.CFG.StackType` 实现该功能）
    /// </summary>
    /// <param name="from"></param>
    /// <param name="to"></param>
    /// <returns></returns>
    public static bool IsILStackAssignableTo(this TypeReference from, TypeReference? to)
    => IsAssignableFromRoot(to, from, true);

    /// <summary>
    /// 判断 from 是否可以赋值给 to，不考虑 IL 栈上的类型转换（例如 byte -> int）
    /// </summary>
    /// <param name="from"></param>
    /// <param name="to"></param>
    /// <returns></returns>
    public static bool IsAssignableTo(this TypeReference from, TypeReference? to)
        => IsAssignableFromRoot(to, from, false);

    /// <summary>
    /// 判断 from 是否可以赋值给 to，考虑 IL 栈上的类型转换（例如 byte -> int）
    /// 但不考虑 Byte Sbyte Uint16都转化为I4这种的等价（在 `MonoWeaver.CFG.StackType` 实现该功能）
    /// </summary>
    /// <param name="to"></param>
    /// <param name="from"></param>
    /// <returns></returns>
    public static bool IsILStackAssignableFrom(this TypeReference to, TypeReference? from)
        => IsAssignableFromRoot(to, from, true);

    /// <summary>
    /// 判断 from 是否可以赋值给 to，不考虑 IL 栈上的类型转换（例如 byte -> int）
    /// </summary>
    /// <param name="to"></param>
    /// <param name="from"></param>
    /// <returns></returns>
    public static bool IsAssignableFrom(this TypeReference to, TypeReference? from)
        => IsAssignableFromRoot(to, from, false);

    /// <summary>
    /// 去除类型修饰符，获取实际类型，例如 List`1& -> List`1，List`1 modopt(int32) -> List`1
    /// </summary>
    /// <param name="t"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TypeReference StripType(this TypeReference t)
    {
        while (true)
        {
            switch (t)
            {
                case OptionalModifierType omt: t = omt.ElementType; continue;
                case RequiredModifierType rmt: t = rmt.ElementType; continue;
                case PinnedType pt: t = pt.ElementType; continue;
                case SentinelType st: t = st.ElementType; continue;
                default: return t; //这里不处理byref等在il堆栈内不等价的类型
            }
        }
    }

    /// <summary>
    /// 获取枚举的底层类型，如果不是枚举则返回 null
    /// </summary>
    /// <param name="typeRef"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public static TypeReference? GetEnumBackingFieldType(this TypeReference typeRef)
    {
        TypeDefinition? typeDef = ResolveWithCache(typeRef);

        if (typeDef is not { IsEnum: true })
        {
            return null;
        }

        FieldDefinition? valueField = typeDef.Fields.FirstOrDefault(f => !f.IsStatic);

        if (valueField == null)
        {
            throw new ArgumentException($"Enum {typeDef.FullName} does not have a value field!");
        }

        return valueField.FieldType;
    }

    /// <summary>
    /// 获取基类，支持处理数组和泛型参数等特殊情况
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static TypeReference? BaseType(this TypeReference? type)
    {
        if (type == null) return null;
        type = type.StripType();

        if (type is ArrayType)
        {
            var mod = type.Module ?? throw new InvalidOperationException("TypeReference.Module is null.");
            return SystemArrayRef(mod);
        }

        return GetBaseTypeWithCache(type);
    }

    /// <summary>
    /// 获取所有基类，按照从近到远的顺序返回，不包含自身
    /// </summary>
    /// <param name="type"></param>
    /// <param name="withBoxing"></param>
    /// <returns></returns>
    internal static IEnumerable<TypeReference> AllBaseTypes(this TypeReference? type, bool withBoxing)
    {
        if (type is null || type.IsValueType)
            yield break;
        do
        {
            type = type.BaseType();
            if (type is null)
                yield break;
            yield return type;
        } while (true);
    }

    /// <summary>
    /// 查找共有基类
    /// </summary>
    /// <param name="a0"></param>
    /// <param name="b0"></param>
    /// <returns></returns>
    public static TypeReference? FindCommonBaseType(TypeReference? a0, TypeReference? b0)
    {
        if (a0 is null || b0 is null) return null;

        var a = a0;
        var b = b0;
        bool sa = false, sb = false;

        while (true)
        {
            if (a == null && b == null) return null;
            if (a != null && b != null && a.IsSameWith(b)) return a;

            a = a?.BaseType();
            b = b?.BaseType();

            if (a == null && !sa) { a = b0; sa = true; }
            if (b == null && !sb) { b = a0; sb = true; }

            if (sa && sb && a == null && b == null) return null;
        }
    }


    /// <summary>
    /// 收集所有接口，包含直接实现的和间接实现的（基类实现的），不包含重复项，结果放在 resultBuffer 中
    /// 会填充泛型参数
    /// </summary>
    /// <param name="type"></param>
    /// <param name="resultBuffer"></param>
    public static void CollectAllInterfaces(TypeReference? type, List<TypeReference> resultBuffer)
    {
        if (type == null) return;
        resultBuffer.AddRange(GetRuntimeInterfacesWithCache(type));
    }


    /// <summary>
    /// 判断两个类型是否相同
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSameWith(this TypeReference? a, TypeReference? b)
    {
        if (b is null || a is null) return false;
        if (ReferenceEquals(a, b))
            return true;
        return TypeSig.Create(a).Equals(TypeSig.Create(b));
    }


    /// <summary>
    /// 判断是否为枚举类型
    /// </summary>
    /// <param name="typeRef"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEnum(this TypeReference typeRef)
    {
        TypeDefinition? typeDef = ResolveWithCache(typeRef);
        return typeDef?.IsEnum ?? false;
    }


    /// <summary>
    /// 判断是否为 void 类型
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsVoid(this TypeReference type)
    => type.StripType().MetadataType == MetadataType.Void;

    /// <summary>
    /// 检查构造类型中的泛型实参是否满足对应泛型参数约束。
    /// </summary>
    /// <param name="typeReference"></param>
    /// <returns></returns>
    public static bool CheckConstraints(this TypeReference typeReference)
    {
        if (typeReference == null) throw new ArgumentNullException(nameof(typeReference));
        return CheckTypeConstraints(typeReference, null, null, new HashSet<TypeSig>());
    }

    /// <summary>
    /// 检查方法引用中的声明类型泛型实参和方法泛型实参是否满足对应泛型参数约束。
    /// </summary>
    /// <param name="methodReference"></param>
    /// <returns></returns>
    public static bool CheckConstraints(this MethodReference methodReference)
    {
        if (methodReference == null) throw new ArgumentNullException(nameof(methodReference));
        return CheckMethodConstraints(methodReference, new HashSet<TypeSig>());
    }

    /// <summary>
    /// 判断 localType 是否可以访问 toType
    /// </summary>
    /// <param name="localType"></param>
    /// <param name="toType"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static bool CanAccess(this TypeReference localType, TypeReference toType)
    {
        if (localType == null) throw new ArgumentNullException(nameof(localType));
        if (toType == null) throw new ArgumentNullException(nameof(toType));
        return CanAccessType(localType, toType, new HashSet<TypeSig>());
    }

    /// <summary>
    /// 判断 localType 是否可以访问 toMethod
    /// </summary>
    /// <param name="localType"></param>
    /// <param name="toMethod"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static bool CanAccess(this TypeReference localType, MethodDefinition toMethod)
    {
        if (localType == null) throw new ArgumentNullException(nameof(localType));
        if (toMethod == null) throw new ArgumentNullException(nameof(toMethod));

        return CanAccessMethodReference(localType, toMethod, toMethod);
    }

    /// <summary>
    /// 判断 localType 是否可以访问 toMethod
    /// </summary>
    /// <param name="localType"></param>
    /// <param name="toMethod"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static bool CanAccess(this TypeReference localType, MethodReference toMethod)
    {
        if (localType == null) throw new ArgumentNullException(nameof(localType));
        if (toMethod == null) throw new ArgumentNullException(nameof(toMethod));

        return CanAccessMethodReference(localType, toMethod, TryResolve(toMethod));
    }

    /// <summary>
    /// 判断 localType 是否可以访问 toMethod
    /// </summary>
    /// <param name="localType"></param>
    /// <param name="toMethod"></param>
    /// <param name="resolvedMethod"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static bool CanAccess(this TypeReference localType, MethodReference toMethod, MethodDefinition resolvedMethod)
    {
        if (localType == null) throw new ArgumentNullException(nameof(localType));
        if (toMethod == null) throw new ArgumentNullException(nameof(toMethod));
        if (resolvedMethod == null) throw new ArgumentNullException(nameof(resolvedMethod));

        return CanAccessMethodReference(localType, toMethod, resolvedMethod);
    }

    /// <summary>
    /// 判断 localType 是否可以访问 toField
    /// </summary>
    /// <param name="localType"></param>
    /// <param name="toField"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static bool CanAccess(this TypeReference localType, FieldDefinition toField)
    {
        if (localType == null) throw new ArgumentNullException(nameof(localType));
        if (toField == null) throw new ArgumentNullException(nameof(toField));

        return CanAccessFieldReference(localType, toField, toField);
    }

    /// <summary>
    /// 判断 localType 是否可以访问 toField
    /// </summary>
    /// <param name="localType"></param>
    /// <param name="toField"></param>
    /// <param name="resolvedField"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static bool CanAccess(this TypeReference localType, FieldReference toField, FieldDefinition resolvedField)
    {
        if (localType == null) throw new ArgumentNullException(nameof(localType));
        if (toField == null) throw new ArgumentNullException(nameof(toField));
        if (resolvedField == null) throw new ArgumentNullException(nameof(resolvedField));

        return CanAccessFieldReference(localType, toField, resolvedField);
    }

    public static bool IsAnyDelegate(this TypeReference reference)
    {
        var rSig = TypeSig.Create(reference);
        if (rSig == TypeSig.Delegate || rSig == TypeSig.MulticastDelegate)
            return true;

        if (ResolveWithCache(reference) is TypeDefinition td && td.BaseType() is { } pt)
        {
            var pSig = TypeSig.Create(pt);
            if (pSig == TypeSig.Delegate || pSig == TypeSig.MulticastDelegate)
                return true;
        }
        return false;
    }
    
}



/// <summary>
/// 类型关系判断实现部分以及其他私有函数
/// </summary>
public static partial class CecilTypeSystem
{
    private static bool CanAccessType(TypeReference localType, TypeReference toType, HashSet<TypeSig> visiting)
    {
        if (toType.MetadataType == MetadataType.Void)
            return true;

        var sig = TypeSig.Create(toType);
        if (!visiting.Add(sig))
            return true;

        try
        {
            switch (toType.StripType())
            {
                case GenericParameter genericParameter:
                    for (int i = 0; i < genericParameter.Constraints.Count; i++)
                    {
                        if (!CanAccessType(localType, genericParameter.Constraints[i].ConstraintType, visiting))
                            return false;
                    }
                    return true;

                case GenericInstanceType genericInstance:
                    if (!CanAccessType(localType, genericInstance.ElementType, visiting))
                        return false;

                    for (int i = 0; i < genericInstance.GenericArguments.Count; i++)
                    {
                        if (!CanAccessType(localType, genericInstance.GenericArguments[i], visiting))
                            return false;
                    }
                    return true;

                case ArrayType array:
                    return CanAccessType(localType, array.ElementType, visiting);

                case ByReferenceType byRef:
                    return CanAccessType(localType, byRef.ElementType, visiting);

                case PointerType pointer:
                    return CanAccessType(localType, pointer.ElementType, visiting);

                case FunctionPointerType functionPointer:
                    if (!CanAccessType(localType, functionPointer.ReturnType, visiting))
                        return false;

                    for (int i = 0; i < functionPointer.Parameters.Count; i++)
                    {
                        if (!CanAccessType(localType, functionPointer.Parameters[i].ParameterType, visiting))
                            return false;
                    }
                    return true;
            }

            var typeDefinition = ResolveWithCache(toType);
            return typeDefinition == null || CanAccessTypeDefinition(localType, typeDefinition);
        }
        finally
        {
            visiting.Remove(sig);
        }
    }

    private static bool CanAccessMethodReference(TypeReference localType, MethodReference toMethod,
        MethodDefinition? toMethodDefinition)
    {
        var typeContext = toMethod.DeclaringType as GenericInstanceType;
        var methodContext = toMethod as GenericInstanceMethod;

        if (toMethod.DeclaringType != null &&
            !CanAccessType(localType, toMethod.DeclaringType, new HashSet<TypeSig>()))
            return false;

        if (toMethodDefinition != null && !CanAccessMethodVisibility(localType, toMethodDefinition))
            return false;

        var returnType = TryInflateGenericType(toMethod.ReturnType, typeContext, methodContext);
        if (!CanAccessType(localType, returnType, new HashSet<TypeSig>()))
            return false;

        for (int i = 0; i < toMethod.Parameters.Count; i++)
        {
            var parameterType = TryInflateGenericType(toMethod.Parameters[i].ParameterType, typeContext, methodContext);
            if (!CanAccessType(localType, parameterType, new HashSet<TypeSig>()))
                return false;
        }

        if (methodContext != null)
        {
            for (int i = 0; i < methodContext.GenericArguments.Count; i++)
            {
                var argument = TryInflateGenericType(methodContext.GenericArguments[i], typeContext, methodContext);
                if (!CanAccessType(localType, argument, new HashSet<TypeSig>()))
                    return false;
            }
        }

        var genericParameters = toMethodDefinition?.GenericParameters ?? toMethod.GenericParameters;
        for (int i = 0; i < genericParameters.Count; i++)
        {
            var genericParameter = genericParameters[i];
            for (int j = 0; j < genericParameter.Constraints.Count; j++)
            {
                var constraintType = TryInflateGenericType(genericParameter.Constraints[j].ConstraintType,
                    typeContext, methodContext);
                if (!CanAccessType(localType, constraintType, new HashSet<TypeSig>()))
                    return false;
            }
        }

        return true;
    }

    private static bool CanAccessFieldReference(TypeReference localType, FieldReference toField,
        FieldDefinition toFieldDefinition)
    {
        var typeContext = toField.DeclaringType as GenericInstanceType;

        if (toField.DeclaringType != null &&
            !CanAccessType(localType, toField.DeclaringType, new HashSet<TypeSig>()))
            return false;

        if (!CanAccessFieldVisibility(localType, toFieldDefinition))
            return false;

        var fieldType = TryInflateGenericType(toField.FieldType, typeContext, null);
        return CanAccessType(localType, fieldType, new HashSet<TypeSig>());
    }

    private static bool CanAccessTypeDefinition(TypeReference localType, TypeDefinition toType)
    {
        if (toType.IsNested)
        {
            var declaringType = toType.DeclaringType;
            if (declaringType == null)
                return false;

            if (!CanAccessType(localType, declaringType, new HashSet<TypeSig>()))
                return false;

            var localDefinition = ResolveWithCache(localType);
            if (localDefinition == null)
                return false;

            if (toType.IsNestedPublic)
                return true;

            if (toType.IsNestedPrivate)
                return IsInPrivateAccessScope(localDefinition, declaringType);

            if (toType.IsNestedAssembly)
                return HasAssemblyAccess(localType, toType);

            if (toType.IsNestedFamily)
                return HasFamilyAccess(localDefinition, declaringType);

            if (toType.IsNestedFamilyOrAssembly)
                return HasAssemblyAccess(localType, toType) ||
                       HasFamilyAccess(localDefinition, declaringType);

            if (toType.IsNestedFamilyAndAssembly)
                return IsSameAssembly(localType, toType) &&
                       HasFamilyAccess(localDefinition, declaringType);

            return false;
        }

        return toType.IsPublic || HasAssemblyAccess(localType, toType);
    }

    private static bool CanAccessMethodVisibility(TypeReference localType, MethodDefinition toMethod)
    {
        var declaringType = toMethod.DeclaringType;
        var localDefinition = ResolveWithCache(localType);

        if (declaringType == null || localDefinition == null)
            return false;

        if (toMethod.IsPublic)
            return true;

        if (toMethod.IsPrivate)
            return IsInPrivateAccessScope(localDefinition, declaringType);

        if (toMethod.IsAssembly)
            return HasAssemblyAccess(localType, declaringType);

        if (toMethod.IsFamily)
            return HasFamilyAccess(localDefinition, declaringType);

        if (toMethod.IsFamilyOrAssembly)
            return HasAssemblyAccess(localType, declaringType) ||
                   HasFamilyAccess(localDefinition, declaringType);

        if (toMethod.IsFamilyAndAssembly)
            return IsSameAssembly(localType, declaringType) &&
                   HasFamilyAccess(localDefinition, declaringType);

        return false;
    }


    private static bool CanAccessFieldVisibility(TypeReference localType, FieldDefinition toField)
    {
        var declaringType = toField.DeclaringType;
        var localDefinition = ResolveWithCache(localType);

        if (declaringType == null || localDefinition == null)
            return false;

        if (toField.IsPublic)
            return true;

        if (toField.IsPrivate)
            return IsInPrivateAccessScope(localDefinition, declaringType);

        if (toField.IsAssembly)
            return HasAssemblyAccess(localType, declaringType);

        if (toField.IsFamily)
            return HasFamilyAccess(localDefinition, declaringType);

        if (toField.IsFamilyOrAssembly)
            return HasAssemblyAccess(localType, declaringType) ||
                   HasFamilyAccess(localDefinition, declaringType);

        if (toField.IsFamilyAndAssembly)
            return IsSameAssembly(localType, declaringType) &&
                   HasFamilyAccess(localDefinition, declaringType);

        return true; //TODO:不明确原因存在均为false的public
    }
    private static bool IsInPrivateAccessScope(TypeDefinition localType, TypeDefinition targetDeclaringType)
    {
        for (var local = localType; local != null; local = local.DeclaringType)
        {
            for (var target = targetDeclaringType; target != null; target = target.DeclaringType)
            {
                if (local.IsSameWith(target))
                    return true;
            }
        }

        return false;
    }

    private static bool HasFamilyAccess(TypeDefinition localType, TypeDefinition targetDeclaringType)
    {
        for (var local = localType; local != null; local = local.DeclaringType)
        {
            if (local.IsSameWith(targetDeclaringType) || local.IsAssignableTo(targetDeclaringType))
                return true;
        }

        return false;
    }

    private static bool HasAssemblyAccess(TypeReference localType, TypeDefinition toType)
    {
        return IsSameAssembly(localType, toType) || HasInternalsVisibleTo(toType, localType);
    }

    internal static bool IsSameAssembly(TypeReference localType, TypeDefinition toType)
    {
        var localAssembly = ResolveWithCache(localType)?.Module?.Assembly ?? localType.Module?.Assembly;
        var targetAssembly = toType.Module?.Assembly;
        return AssemblyNamesEqual(localAssembly?.Name, targetAssembly?.Name);
    }

    internal static bool HasInternalsVisibleTo(TypeDefinition toType, TypeReference localType)
    {
        var targetAssembly = toType.Module?.Assembly;
        var localAssemblyName = ResolveWithCache(localType)?.Module?.Assembly?.Name?.Name ??
                                localType.Module?.Assembly?.Name?.Name;

        if (targetAssembly == null || string.IsNullOrWhiteSpace(localAssemblyName))
            return false;

        for (int i = 0; i < targetAssembly.CustomAttributes.Count; i++)
        {
            var attribute = targetAssembly.CustomAttributes[i];
            if (attribute.AttributeType.FullName != "System.Runtime.CompilerServices.InternalsVisibleToAttribute" ||
                attribute.ConstructorArguments.Count != 1 ||
                attribute.ConstructorArguments[0].Value is not string friendAssembly)
            {
                continue;
            }

            var commaIndex = friendAssembly.IndexOf(',');
            var friendName = (commaIndex >= 0 ? friendAssembly.Substring(0, commaIndex) : friendAssembly).Trim();
            if (string.Equals(friendName, localAssemblyName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool AssemblyNamesEqual(AssemblyNameDefinition? left, AssemblyNameDefinition? right)
    {
        if (left == null || right == null)
            return false;

        return string.Equals(left.Name, right.Name, StringComparison.Ordinal);
    }

    private static bool IsAssignableFromRoot(TypeReference? to, TypeReference? from, bool ilRule)
    {
        var context = _assignabilityContext;

        if (from == null) return false;
        if (to == null) return false;

        to = to.StripType();
        from = from.StripType();

        var toKey = TypeSig.Create(to);
        var fromKey = TypeSig.Create(from);

        if (fromKey.Equals(toKey))
            return true;

        var hash = PackAssignableKey(toKey, fromKey, ilRule);
        if (_assignableCache.TryGetValue(hash, out var cached))
            return cached;
            
        if (context == null)
        {
            context = new AssignabilityContext();
            _assignabilityContext = context;
        }
        else if (context.InUse)
        {
            context = new AssignabilityContext();
        }

        context.InUse = true;
        context.Active.Clear();
        context.CycleVersion = 0;

        try
        {
            return IsAssignableFromCore(to, from, ilRule, context);
        }
        finally
        {
            context.Active.Clear();
            context.InUse = false;
        }
    }

    private static bool IsAssignableFromCore(TypeReference? to, TypeReference? from, bool ilRule, AssignabilityContext context)
    {
        var cycleVersion = context.CycleVersion;
        var result = IsAssignableFromInternal(to, from, ilRule, context);
        if (from != null && to != null && context.CycleVersion == cycleVersion)
        {
             _assignableCache.GetOrAdd(PackAssignableKey(TypeSig.Create(to.StripType()), TypeSig.Create(from.StripType()), ilRule), result);
        }
        return result;
    }
    
    private static bool IsAssignableFromInternal(TypeReference? to, TypeReference? from, bool ilRule, AssignabilityContext context)
    {
        while (true)
        {
            if (from == null) return false;
            if (to == null) return false;
            to = to.StripType();
            from = from.StripType();

            //优先处理未闭合
            if (from is GenericParameter fromGp)
            {
                return IsAssignableFromGenericParam(to, fromGp, ilRule, context);
            }

            if (ilRule)
            {
                if (!to.IsValueType && from.IsValueType) return false;
                if (to.IsValueType && !from.IsValueType) return false;
            }

            var toKey = TypeSig.Create(to);
            var fromKey = TypeSig.Create(from);

            if (toKey == TypeSig.Object)
            {
                return from is not ByReferenceType
                       && from is not PointerType
                       && from is not FunctionPointerType; //已经在上面排除值类型和引用类型严格判别
                                                           //这里不需要额外判断
            }

            if (fromKey == TypeSig.Object)
            {
                return false;
            }


            var hash = PackAssignableKey(toKey, fromKey, ilRule);

          
            if (fromKey.Equals(toKey)) 
                return true;

            if (_assignableCache.TryGetValue(hash, out var result))
            {
                return result;
            }

    
            if (!context.Active.Add(hash))
            {
                context.CycleVersion++;
                return false;
            }

            try
            {
                if (IsOpenGenericDefinition(from))
                {
                    return IsAssignableFromCore(to, GenerateGenericInstanceType(from), ilRule, context);
                }

                //byRef
                if (from is ByReferenceType fromRef)
                {
                    if (to is ByReferenceType toRef)
                        return fromRef.ElementType.IsSameWith(toRef.ElementType);
                    return false;
                }

                //指针
                if (from is PointerType fromPtr)
                {
                    if (to is PointerType toPtr)
                        return fromPtr.ElementType.IsSameWith(toPtr.ElementType);
                    return false;
                }


                //数组
                if (from is ArrayType fromArr)
                {
                    if (IsAssignableFromArray(to, fromArr, ilRule, context))
                        return true;
                }


                //泛型
                if (to is GenericInstanceType toGi && from is GenericInstanceType fromGi)
                {
                    //处理逆变/协变
                    if (IsSameWith(toGi.ElementType, fromGi.ElementType) &&
                        GenericArgsAssignableWithVariance(toGi, fromGi, ilRule, context))
                    {
                        return true;
                    }
                }

                var toDef = ResolveWithCache(to);

                //nullable
                if (toDef != null && TypeSig.Create(toDef) == TypeSig.Nullable && to is GenericInstanceType instTo)
                {
                    return IsSameWith(instTo.GenericArguments[0], from);
                }

                //接口
                if (toDef?.IsInterface == true)
                {
                    if (from.IsValueType && ilRule) return false;

                    return ImplementsInterface(from, to, ilRule, context);
                }

                if (from.IsValueType && ilRule)
                {
                    return false;
                }
                from = from.BaseType();
            }
            finally
            {
                context.Active.Remove(hash);
            }
        }
    }

    private static bool ImplementsInterface(TypeReference from, TypeReference toInterface, bool strict, AssignabilityContext context)
    {
        var interfaces = GetRuntimeInterfacesWithCache(from);
        var toInterfaceSig = TypeSig.Create(toInterface);
        foreach (var iface in interfaces)
        {
            if (TypeSig.Create(iface) == toInterfaceSig) return true;

            // 处理逆变/协变
            if (toInterface is GenericInstanceType toGi &&
                iface is GenericInstanceType fromGi &&
                IsSameWith(toGi.ElementType, fromGi.ElementType) &&
                GenericArgsAssignableWithVariance(toGi, fromGi, strict, context))
            {
                return true;
            }
        }
        return false;
    }



    private static GenericInstanceType GenerateGenericInstanceType(TypeReference type)
    {
        type = type.StripType();

        if (type is GenericInstanceType gi) return gi;

        // 这里的语义是：把 “泛型定义” 变成 “用自身 GenericParameter 作为参数的实例”
        // 例如 IEnumerable`1 => IEnumerable<!0>
        var result = new GenericInstanceType(type);
        foreach (var gp in type.GenericParameters)
            result.GenericArguments.Add(gp);

        return result;
    }


    private static IEnumerable<TypeReference> EnumerateArrayRuntimeInterfaces(ArrayType arr)
    {
        var mod = arr.Module ?? throw new InvalidOperationException("ArrayType.Module is null.");
        var elem = arr.ElementType.StripType();

        // 只有一位数组才有这些泛型接口
        if (arr is { Rank: 1 })
        {
            if(_arrayInfs.TryGetValue(mod, out var array))
            {
                foreach(var i in array)
                {
                    var gi = new GenericInstanceType(i);
                    gi.GenericArguments.Add(elem);
                    yield return gi;
                }
                yield break;
            }
            _arrayInfs.GetOrAdd(mod, array = new TypeReference[5]);
            var index = 0;
            foreach (var g in new[]
                     {
                         mod.ImportReference(typeof(IEnumerable<>)),
                         mod.ImportReference(typeof(ICollection<>)),
                         mod.ImportReference(typeof(IList<>)),
                         mod.ImportReference(typeof(IReadOnlyCollection<>)),
                         mod.ImportReference(typeof(IReadOnlyList<>)),
                     })
            {
                array[index++] = g;
                var gi = new GenericInstanceType(g);
                gi.GenericArguments.Add(elem);
                yield return gi;
            }
        }
    }

    private static TypeReference SystemArrayRef(ModuleDefinition module)
    {
        if (_arrayDefs.TryGetValue(module, out var array))
            return array;
        _arrayDefs[module] = array = module.ImportReference(typeof(Array)).Resolve();
        return array;
    }

    private static bool GenericArgsAssignableWithVariance(GenericInstanceType target, GenericInstanceType source,
        bool ilRule,
        AssignabilityContext context)
    {
        if (target.GenericArguments.Count != source.GenericArguments.Count)
            return false;

        var def = ResolveWithCache(target.ElementType);
        if (def == null)
        {
            return false;
        }
        for (int i = 0; i < target.GenericArguments.Count; i++)
        {
            var param = def.GenericParameters[i];
            var variance = param.Attributes & GenericParameterAttributes.VarianceMask;
            var tArg = target.GenericArguments[i].StripType();
            var sArg = source.GenericArguments[i].StripType();


            if (sArg.IsArray && !sArg.IsValueType && tArg.IsArray && !tArg.IsValueType) //引用数组对于其他接口按协变处理
            {
                variance = GenericParameterAttributes.Covariant;
            }

            switch (variance)
            {
                case GenericParameterAttributes.Covariant: //协变
                    if ((!IsDefinitelyReferenceType(tArg) || !IsDefinitelyReferenceType(sArg)) && !IsSameWith(tArg, sArg))
                        return false;
                    if (!IsAssignableFromCore(tArg, sArg, ilRule, context))
                        return false;
                    break;

                case GenericParameterAttributes.Contravariant: //逆变
                    if ((!IsDefinitelyReferenceType(tArg) || !IsDefinitelyReferenceType(sArg)) && !IsSameWith(tArg, sArg))
                        return false;
                         
                    if (!IsAssignableFromCore(sArg, tArg, ilRule, context)) 
                        return false;
                    break;

                default: // 常规
                    if (!IsSameWith(tArg, sArg)) return false;
                    break;
            }
        }

        return true;
    }

    private static bool IsDefinitelyReferenceType(TypeReference type)
        => IsDefinitelyReferenceType(type, new HashSet<TypeSig>());

    private static bool IsDefinitelyReferenceType(TypeReference type, HashSet<TypeSig> seenGenericParams)
    {
        type = type.StripType();

        if (type is ByReferenceType or PointerType or FunctionPointerType)
            return false;

        if (type is not GenericParameter gp)
            return !type.IsValueType; //非泛型可直接确定

        var sig = TypeSig.Create(gp);
        if (!seenGenericParams.Add(sig))
            return false;

        var constraints = gp.Attributes & GenericParameterAttributes.SpecialConstraintMask;
        if ((constraints & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
            return false; //struct() 直接非ref

        if ((constraints & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
            return true; //class() 直接ref

        foreach (var constraint in gp.Constraints)
        {
            var constraintType = constraint.ConstraintType.StripType();
            //查看约束进一步判别
            if (constraintType is GenericParameter constraintGp)//还是泛型参数则递归
            {
                if (IsDefinitelyReferenceType(constraintGp, seenGenericParams))
                    return true;
                continue;
            }

            var constraintDef = ResolveWithCache(constraintType);
            if (constraintDef == null || constraintDef.IsInterface || constraintDef.IsValueType)
                continue;

            var constraintSig = TypeSig.Create(constraintDef);
            if (constraintSig != TypeSig.Object && constraintSig != TypeSig.ValueType && constraintSig != TypeSig.Enum)
                return true;
        }

        return false;
    }



    private static bool IsAssignableFromArray(TypeReference to, ArrayType fromArr, bool ilRule, AssignabilityContext context)
    {
        to = to.StripType();
        var toKey = TypeSig.Create(to);

        // T[] -> System.Array
        if (toKey == TypeSig.Array || toKey == TypeSig.ICloneable)
        {
            return true;
        }

        // T[] -> 非泛型 IEnumerable/ICollection/IList
        if (toKey == TypeSig.SystemCollections.IEnumerable
            || toKey == TypeSig.SystemCollections.ICollection
            || toKey == TypeSig.SystemCollections.IList
            || toKey == TypeSig.SystemCollections.IStructuralComparable)
        {
            return true;
        }

        // 数组 -> 数组 (处理协变)
        // T[] -> IEnumerable<T>/ICollection<T>/IList<T>/IReadOnlyCollection<T>/IReadOnlyList<T>
        // CLR also allows reference-array covariance here, e.g. string[] -> IList<object>.
        if (fromArr is { Rank: 1 } &&
            to is GenericInstanceType toGi &&
            toGi.GenericArguments.Count == 1 &&
            IsArrayRuntimeGenericInterfaceDefinition(toGi.ElementType))
        {
            var toElem = toGi.GenericArguments[0].StripType();
            var fromElem = fromArr.ElementType.StripType();

            if (IsSameWith(toElem, fromElem))
                return true;

            if (!IsDefinitelyReferenceType(toElem) || !IsDefinitelyReferenceType(fromElem))
                return false;

            return IsAssignableFromCore(toElem, fromElem, ilRule, context);
        }
        if (to is ArrayType toArr)
        {
            if (toArr.Rank != fromArr.Rank || toArr.IsVector != fromArr.IsVector) return false;

            var toElem = toArr.ElementType.StripType();
            var fromElem = fromArr.ElementType.StripType();

            if (!toElem.IsValueType && !fromElem.IsValueType) //引用类型支持协变
            {
                return IsAssignableFromCore(toElem, fromElem, ilRule, context);
            }
            return IsSameWith(toElem, fromElem);
        }
        return false;
    }

    private static bool IsArrayRuntimeGenericInterfaceDefinition(TypeReference type)
    {
        type = type.StripType();
        var def = ResolveWithCache(type) ?? type;
        return def.Namespace == "System.Collections.Generic" &&
               def.Name is "IEnumerable`1" or "ICollection`1" or "IList`1" or "IReadOnlyCollection`1" or "IReadOnlyList`1";
    }

    private static bool IsAssignableFromGenericParam(TypeReference to, GenericParameter fromGp, bool ilRule,
        AssignabilityContext context)
    {
        // 对每个显式约束：where T : C, IFoo 处理，假定T为每一个约束类型进行赋值比较判断
        foreach (var c in fromGp.Constraints)
        {
            var ct = c.ConstraintType.StripType();

            if (IsAssignableFromCore(to, ct, ilRule, context))
                return true;
        }

        var constraint = fromGp.Attributes & GenericParameterAttributes.SpecialConstraintMask;
        var toKey = TypeSig.Create(to);
        if ((constraint & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
        {
            return toKey == TypeSig.Object;
        }

        if ((constraint & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
        {
            if (ilRule) return false;
            return toKey == TypeSig.Object || toKey == TypeSig.ValueType;
        }
        return toKey == TypeSig.Object && !ilRule;
    }


    private static bool CheckMethodConstraints(MethodReference methodReference, HashSet<TypeSig> visiting)
    {
        var typeContext = methodReference.DeclaringType as GenericInstanceType;
        var methodContext = methodReference as GenericInstanceMethod;

        if (methodReference.DeclaringType != null &&
            !CheckTypeConstraints(methodReference.DeclaringType, null, null, visiting))
        {
            return false;
        }

        if (methodContext != null && !CheckGenericInstanceMethodConstraints(methodContext, typeContext, visiting))
            return false;

        if (!CheckTypeConstraints(methodReference.ReturnType, typeContext, methodContext, visiting))
            return false;

        for (int i = 0; i < methodReference.Parameters.Count; i++)
        {
            if (!CheckTypeConstraints(methodReference.Parameters[i].ParameterType, typeContext, methodContext, visiting))
                return false;
        }

        return true;
    }

    private static bool CheckTypeConstraints(TypeReference type, GenericInstanceType? typeContext,
        GenericInstanceMethod? methodContext, HashSet<TypeSig> visiting)
    {
        if (type == null) return false;

        type = TryInflateGenericType(type, typeContext, methodContext);

        switch (type)
        {
            case OptionalModifierType optional:
                return CheckTypeConstraints(optional.ModifierType, typeContext, methodContext, visiting) &&
                       CheckTypeConstraints(optional.ElementType, typeContext, methodContext, visiting);

            case RequiredModifierType required:
                return CheckTypeConstraints(required.ModifierType, typeContext, methodContext, visiting) &&
                       CheckTypeConstraints(required.ElementType, typeContext, methodContext, visiting);

            case PinnedType pinned:
                return CheckTypeConstraints(pinned.ElementType, typeContext, methodContext, visiting);

            case SentinelType sentinel:
                return CheckTypeConstraints(sentinel.ElementType, typeContext, methodContext, visiting);
        }

        type = type.StripType();

        switch (type)
        {
            case GenericParameter:
                return true;

            case GenericInstanceType genericInstance:
            {
                var sig = TypeSig.Create(genericInstance);
                if (!visiting.Add(sig))
                    return true;

                try
                {
                    return CheckGenericInstanceTypeConstraints(genericInstance, typeContext, methodContext, visiting);
                }
                finally
                {
                    visiting.Remove(sig);
                }
            }

            case ArrayType array:
                return CheckTypeConstraints(array.ElementType, typeContext, methodContext, visiting);

            case ByReferenceType byRef:
                return CheckTypeConstraints(byRef.ElementType, typeContext, methodContext, visiting);

            case PointerType pointer:
                return CheckTypeConstraints(pointer.ElementType, typeContext, methodContext, visiting);

            case FunctionPointerType functionPointer:
                if (!CheckTypeConstraints(functionPointer.ReturnType, typeContext, methodContext, visiting))
                    return false;

                for (int i = 0; i < functionPointer.Parameters.Count; i++)
                {
                    if (!CheckTypeConstraints(functionPointer.Parameters[i].ParameterType, typeContext, methodContext, visiting))
                        return false;
                }

                return true;
        }

        return type.DeclaringType == null ||
               CheckTypeConstraints(type.DeclaringType, typeContext, methodContext, visiting);
    }

    private static bool CheckGenericInstanceTypeConstraints(GenericInstanceType genericInstance,
        GenericInstanceType? typeContext, GenericInstanceMethod? methodContext, HashSet<TypeSig> visiting)
    {
        if (genericInstance.DeclaringType != null &&
            !CheckTypeConstraints(genericInstance.DeclaringType, typeContext, methodContext, visiting))
        {
            return false;
        }

        var typeDefinition = ResolveWithCache(genericInstance.ElementType);
        var genericParameters = typeDefinition?.GenericParameters ?? genericInstance.ElementType.GenericParameters;
        if (genericParameters.Count != genericInstance.GenericArguments.Count)
            return false;

        for (int i = 0; i < genericParameters.Count; i++)
        {
            var argument = TryInflateGenericType(genericInstance.GenericArguments[i], typeContext, methodContext);
            if (!CheckGenericArgumentSatisfiesParameter(argument, genericParameters[i], genericInstance, methodContext, visiting))
                return false;
        }

        return true;
    }

    private static bool CheckGenericInstanceMethodConstraints(GenericInstanceMethod genericMethod,
        GenericInstanceType? typeContext, HashSet<TypeSig> visiting)
    {
        var methodDefinition = TryResolve(genericMethod.ElementMethod);
        var genericParameters = methodDefinition?.GenericParameters ?? genericMethod.ElementMethod.GenericParameters;
        if (genericParameters.Count != genericMethod.GenericArguments.Count)
            return false;

        for (int i = 0; i < genericParameters.Count; i++)
        {
            var argument = TryInflateGenericType(genericMethod.GenericArguments[i], typeContext, genericMethod);
            if (!CheckGenericArgumentSatisfiesParameter(argument, genericParameters[i], typeContext, genericMethod, visiting))
                return false;
        }

        return true;
    }

    private static bool CheckGenericArgumentSatisfiesParameter(TypeReference argument, GenericParameter parameter,
        GenericInstanceType? typeContext, GenericInstanceMethod? methodContext, HashSet<TypeSig> visiting)
    {
        argument = TryInflateGenericType(argument, typeContext, methodContext).StripType();

        if (!CheckTypeConstraints(argument, typeContext, methodContext, visiting))
            return false;

        var special = parameter.Attributes & GenericParameterAttributes.SpecialConstraintMask;
        if ((special & GenericParameterAttributes.ReferenceTypeConstraint) != 0 &&
            !IsDefinitelyReferenceType(argument))
        {
            return false;
        }

        if ((special & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0 &&
            !IsDefinitelyNonNullableValueType(argument))
        {
            return false;
        }

        if ((special & GenericParameterAttributes.DefaultConstructorConstraint) != 0 &&
            !HasPublicDefaultConstructor(argument))
        {
            return false;
        }

        for (int i = 0; i < parameter.Constraints.Count; i++)
        {
            var constraintType = TryInflateGenericType(parameter.Constraints[i].ConstraintType, typeContext, methodContext).StripType();
            if (!CheckTypeConstraints(constraintType, typeContext, methodContext, visiting))
                return false;

            if (!constraintType.IsAssignableFrom(argument))
                return false;
        }

        return true;
    }

    private static TypeReference TryInflateGenericType(TypeReference typeToInflate, GenericInstanceType? typeContext,
        GenericInstanceMethod? methodContext)
    {
        switch (typeToInflate)
        {
            case ByReferenceType byRef:
                return new ByReferenceType(TryInflateGenericType(byRef.ElementType, typeContext, methodContext));

            case PointerType ptr:
                return new PointerType(TryInflateGenericType(ptr.ElementType, typeContext, methodContext));

            case OptionalModifierType modifier:
                return new OptionalModifierType(
                    TryInflateGenericType(modifier.ModifierType, typeContext, methodContext),
                    TryInflateGenericType(modifier.ElementType, typeContext, methodContext));

            case RequiredModifierType required:
                return new RequiredModifierType(
                    TryInflateGenericType(required.ModifierType, typeContext, methodContext),
                    TryInflateGenericType(required.ElementType, typeContext, methodContext));

            case PinnedType pinned:
                return new PinnedType(TryInflateGenericType(pinned.ElementType, typeContext, methodContext));

            case SentinelType sentinel:
                return new SentinelType(TryInflateGenericType(sentinel.ElementType, typeContext, methodContext));

            case GenericParameter gp:
                return TryGetGenericParameterArgument(gp, typeContext, methodContext, out var argument)
                    ? argument
                    : typeToInflate;

            case GenericInstanceType git:
            {
                var element = TryInflateGenericType(git.ElementType, typeContext, methodContext);
                var result = new GenericInstanceType(element);
                for (int i = 0; i < git.GenericArguments.Count; i++)
                    result.GenericArguments.Add(TryInflateGenericType(git.GenericArguments[i], typeContext, methodContext));
                return result;
            }

            case ArrayType arrayType:
            {
                var inflatedElement = TryInflateGenericType(arrayType.ElementType, typeContext, methodContext);
                var result = arrayType.IsVector
                    ? new ArrayType(inflatedElement)
                    : new ArrayType(inflatedElement, arrayType.Rank);
                for (int i = 0; i < arrayType.Dimensions.Count; i++)
                {
                    var dimension = arrayType.Dimensions[i];
                    result.Dimensions.Add(new ArrayDimension(dimension.LowerBound, dimension.UpperBound));
                }
                return result;
            }
        }

        return typeToInflate;
    }

    private static bool TryGetGenericParameterArgument(GenericParameter genericParameter,
        GenericInstanceType? typeContext, GenericInstanceMethod? methodContext, out TypeReference argument)
    {
        argument = null!;

        if (genericParameter.Owner is TypeReference ownerType)
            return TryGetTypeGenericArgument(ownerType, genericParameter.Position, typeContext, out argument);

        if (genericParameter.Owner is MethodReference ownerMethod)
            return TryGetMethodGenericArgument(ownerMethod, genericParameter.Position, methodContext, out argument);

        return false;
    }

    private static bool TryGetTypeGenericArgument(TypeReference ownerType, int position,
        GenericInstanceType? typeContext, out TypeReference argument)
    {
        argument = null!;
        var current = typeContext;
        var guard = 0;

        while (current != null && guard++ < 64)
        {
            if (IsSameGenericParameterOwner(ownerType, current.ElementType) &&
                current.GenericArguments.Count > position)
            {
                argument = current.GenericArguments[position];
                return true;
            }

            if (current.DeclaringType is GenericInstanceType declaringInstance)
            {
                current = declaringInstance;
                continue;
            }

            if (current.ElementType.DeclaringType is GenericInstanceType elementDeclaringInstance)
            {
                current = elementDeclaringInstance;
                continue;
            }

            break;
        }

        return false;
    }

    private static bool TryGetMethodGenericArgument(MethodReference ownerMethod, int position,
        GenericInstanceMethod? methodContext, out TypeReference argument)
    {
        argument = null!;
        if (methodContext == null || methodContext.GenericArguments.Count <= position)
            return false;

        if (!IsSameGenericParameterOwner(ownerMethod, methodContext.ElementMethod))
            return false;

        argument = methodContext.GenericArguments[position];
        return true;
    }

    private static bool IsSameGenericParameterOwner(TypeReference ownerType, TypeReference contextType)
    {
        ownerType = GetGenericOwnerElementType(ownerType);
        contextType = GetGenericOwnerElementType(contextType);
        return ownerType.IsSameWith(contextType);
    }

    private static TypeReference GetGenericOwnerElementType(TypeReference type)
    {
        try
        {
            return type.GetElementType();
        }
        catch
        {
            return type;
        }
    }

    private static bool IsSameGenericParameterOwner(MethodReference ownerMethod, MethodReference contextMethod)
    {
        if (ReferenceEquals(ownerMethod, contextMethod))
            return true;

        var ownerDefinition = TryResolve(ownerMethod);
        var contextDefinition = TryResolve(contextMethod);
        if (ownerDefinition != null && contextDefinition != null)
        {
            return ownerDefinition.MetadataToken.ToInt32() == contextDefinition.MetadataToken.ToInt32() &&
                   ownerDefinition.Module?.Mvid == contextDefinition.Module?.Mvid;
        }

        return ownerMethod.Name == contextMethod.Name &&
               ownerMethod.GenericParameters.Count == contextMethod.GenericParameters.Count &&
               ownerMethod.Parameters.Count == contextMethod.Parameters.Count &&
               ownerMethod.HasThis == contextMethod.HasThis &&
               ownerMethod.CallingConvention == contextMethod.CallingConvention &&
               ownerMethod.DeclaringType.IsSameWith(contextMethod.DeclaringType);
    }

    private static bool IsDefinitelyNonNullableValueType(TypeReference type)
    {
        type = type.StripType();

        if (type is ByReferenceType or PointerType or FunctionPointerType)
            return false;

        if (type is GenericParameter gp)
        {
            var special = gp.Attributes & GenericParameterAttributes.SpecialConstraintMask;
            return (special & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0;
        }

        var definition = ResolveWithCache(type);
        if (!type.IsValueType && definition?.IsValueType != true)
            return false;

        return !IsNullableValueType(type);
    }

    private static bool HasPublicDefaultConstructor(TypeReference type)
    {
        type = type.StripType();

        if (type is ByReferenceType or PointerType or FunctionPointerType)
            return false;

        if (type is GenericParameter gp)
        {
            var special = gp.Attributes & GenericParameterAttributes.SpecialConstraintMask;
            return (special & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0 ||
                   (special & GenericParameterAttributes.DefaultConstructorConstraint) != 0;
        }

        var definition = ResolveWithCache(type);
        if (type.IsValueType || definition?.IsValueType == true)
            return true;

        if (definition == null || definition.IsInterface || definition.IsAbstract)
            return false;

        for (int i = 0; i < definition.Methods.Count; i++)
        {
            var method = definition.Methods[i];
            if (method.IsConstructor && !method.IsStatic && method.IsPublic && method.Parameters.Count == 0)
                return true;
        }

        return false;
    }

    private static bool IsNullableValueType(TypeReference type)
    {
        type = type.StripType();
        if (type is not GenericInstanceType genericInstance)
            return false;

        var nullableType = ResolveWithCache(genericInstance.ElementType) ?? genericInstance.ElementType;
        return TypeSig.Create(nullableType) == TypeSig.Nullable;
    }



    /// <summary>
    /// 尝试填充泛型
    /// </summary>
    /// <param name="typeToInflate"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    private static TypeReference TryInflateGenericType(TypeReference typeToInflate, GenericInstanceType context)
    {
        switch (typeToInflate)
        {
            case ByReferenceType byRef:
                return new ByReferenceType(TryInflateGenericType(byRef.ElementType, context));
            case PointerType ptr:
                return new PointerType(TryInflateGenericType(ptr.ElementType, context));
            case OptionalModifierType modifier:
                return new OptionalModifierType(modifier.ModifierType, TryInflateGenericType(modifier.ElementType, context));
            case RequiredModifierType required:
                return new RequiredModifierType(required.ModifierType, TryInflateGenericType(required.ElementType, context));
        }

        if (typeToInflate is GenericParameter gp)
        {
            if (gp.Owner is TypeReference ownerRef && IsSameWith(ownerRef, context.ElementType) &&
                context.GenericArguments.Count > gp.Position) //来源一致获取泛型参数
            {
                return context.GenericArguments[gp.Position];
            }
            return typeToInflate;
        }

        if (typeToInflate is GenericInstanceType git)
        {
            var element = TryInflateGenericType(git.ElementType, context); //替换外部参数 Outer<T>.Inner<U> ->  Outer<int>.Inner<U>
            var result = new GenericInstanceType(element); //创建空泛型，并填充内容
            foreach (var argument in git.GenericArguments)
            {
                var inflatedArgument = TryInflateGenericType(argument, context);
                result.GenericArguments.Add(inflatedArgument);
            }

            return result;
        }

        if (typeToInflate is ArrayType arrayType)
        {
            var inflatedElement = TryInflateGenericType(arrayType.ElementType, context);
            ArrayType res = arrayType.IsVector
                ? new ArrayType(inflatedElement)
                : new ArrayType(inflatedElement, arrayType.Rank);
            foreach (var d in arrayType.Dimensions)
                res.Dimensions.Add(new ArrayDimension(d.LowerBound, d.UpperBound));
            return res;
        }
        return typeToInflate;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsOpenGenericDefinition(TypeReference t)
    {
        return t is not GenericInstanceType && t.HasGenericParameters;
    }



    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TypeDefinition? ResolveWithCache(TypeReference? t)
    {
        if (t is null)
            return null;

        // Mono.Cecil 的Resolve()针对TypeSpecification会调用GetElementType().
        // 缓存的话需要List`1这种def而不是元素def.
        TypeReference keyType;
        try
        {
            keyType = t.GetElementType();
        }
        catch
        {
            keyType = t;
        }

        return ResolveWithTypeDescCache(keyType);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong PackAssignableKey(TypeSig to, TypeSig from, bool strict)
    {
        var key = ((ulong)(uint)to.Id << 32) | (uint)from.Id;
        return strict ? key | 0x8000000000000000UL : key;
    }
}

