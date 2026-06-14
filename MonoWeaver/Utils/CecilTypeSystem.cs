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
    => TypeSig.Create(type) == TypeSig.Void;



}



/// <summary>
/// 类型关系判断实现部分以及其他私有函数
/// </summary>
public static partial class CecilTypeSystem
{
    private static bool IsAssignableFromRoot(TypeReference? to, TypeReference? from, bool strict)
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

        var hash = PackAssignableKey(toKey, fromKey, strict);
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
            return IsAssignableFromCore(to, from, strict, context);
        }
        finally
        {
            context.Active.Clear();
            context.InUse = false;
        }
    }

    private static bool IsAssignableFromCore(TypeReference? to, TypeReference? from, bool strict, AssignabilityContext context)
    {
        var cycleVersion = context.CycleVersion;
        var result = IsAssignableFromInternal(to, from, strict, context);
        if (from != null && to != null && context.CycleVersion == cycleVersion)
        {
             _assignableCache.GetOrAdd(PackAssignableKey(TypeSig.Create(to.StripType()), TypeSig.Create(from.StripType()), strict), result);
        }
        return result;
    }
    
    private static bool IsAssignableFromInternal(TypeReference? to, TypeReference? from, bool strict, AssignabilityContext context)
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
                return IsAssignableFromGenericParam(to, fromGp, strict, context);
            }

            if (strict)
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


            var hash = PackAssignableKey(toKey, fromKey, strict);

          
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
                    return IsAssignableFromCore(to, GenerateGenericInstanceType(from), strict, context);
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
                    if (IsAssignableFromArray(to, fromArr, strict, context))
                        return true;
                }


                //泛型
                if (to is GenericInstanceType toGi && from is GenericInstanceType fromGi)
                {
                    //处理逆变/协变
                    if (IsSameWith(toGi.ElementType, fromGi.ElementType) &&
                        GenericArgsAssignableWithVariance(toGi, fromGi, strict, context, from.IsArray))
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
                    if (from.IsValueType && strict) return false;

                    return ImplementsInterface(from, to, strict, context);
                }

                if (from.IsValueType && strict)
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
                GenericArgsAssignableWithVariance(toGi, fromGi, strict, context, from.IsArray))
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
        bool strict,
        AssignabilityContext context, bool isArrayType)
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

            if (isArrayType && !sArg.IsValueType) //引用数组对于其他接口按协变处理
            {
                variance = GenericParameterAttributes.Covariant;
            }

            switch (variance)
            {
                case GenericParameterAttributes.Covariant: //协变
                    if ((!DefinitelyRef(tArg) || !DefinitelyRef(sArg)) && !IsSameWith(tArg, sArg))
                        return false;
                    if (!IsAssignableFromCore(tArg, sArg, strict, context))
                        return false;
                    break;

                case GenericParameterAttributes.Contravariant: //逆变
                    if ((!DefinitelyRef(tArg) || !DefinitelyRef(sArg)) && !IsSameWith(tArg, sArg))
                        return false;
                         
                    if (!IsAssignableFromCore(sArg, tArg, strict, context)) 
                        return false;
                    break;

                default: // 常规
                    if (!IsSameWith(tArg, sArg)) return false;
                    break;
            }
        }

        return true;

        bool DefinitelyRef(TypeReference x)
        {
            //IFoo<int> -> IFoo<object> x
            x = x.StripType();
            if (x.IsValueType) return false;
            if (x is GenericParameter gp)
            {
                var sc = gp.Attributes & GenericParameterAttributes.SpecialConstraintMask;
                if (sc == GenericParameterAttributes.NotNullableValueTypeConstraint) return false;
                if (sc == GenericParameterAttributes.ReferenceTypeConstraint) return true;
                return false; // 不确定就按不允许 variance 处理 （可能为值类型）
            }
            return true;
        }
    }



    private static bool IsAssignableFromArray(TypeReference to, ArrayType fromArr, bool strict, AssignabilityContext context)
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
        if (to is ArrayType toArr)
        {
            if (toArr.Rank != fromArr.Rank || toArr.IsVector != fromArr.IsVector) return false;

            var toElem = toArr.ElementType.StripType();
            var fromElem = fromArr.ElementType.StripType();

            if (!toElem.IsValueType && !fromElem.IsValueType) //引用类型支持协变
            {
                return IsAssignableFromCore(toElem, fromElem, strict, context);
            }
            return IsSameWith(toElem, fromElem);
        }
        return false;
    }

    private static bool IsAssignableFromGenericParam(TypeReference to, GenericParameter fromGp, bool strict,
        AssignabilityContext context)
    {
        // 对每个显式约束：where T : C, IFoo 处理，假定T为每一个约束类型进行赋值比较判断
        foreach (var c in fromGp.Constraints)
        {
            var ct = c.ConstraintType.StripType();

            if (IsAssignableFromCore(to, ct, strict, context))
                return true;
        }

        var constraint = fromGp.Attributes & GenericParameterAttributes.SpecialConstraintMask;
        var toKey = TypeSig.Create(to);
        switch (constraint)
        {
            case GenericParameterAttributes.ReferenceTypeConstraint:
                return toKey == TypeSig.Object;
            case GenericParameterAttributes.NotNullableValueTypeConstraint:
                if (strict) return false;
                return toKey == TypeSig.Object || toKey == TypeSig.ValueType;
        }
        return toKey == TypeSig.Object && !strict;
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
