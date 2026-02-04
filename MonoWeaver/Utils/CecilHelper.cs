using Mono.Cecil;
using Mono.Cecil.Rocks;
using MonoMod.Utils;
using MonoWeaver.CFG;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Runtime.CompilerServices;

namespace MonoWeaver.Utils;

public static partial class CecilHelper
{
    private static TypeDefinition? _array;

    public static readonly LruCache<int, bool> _assignableTrueCache = new(4096);
    public static readonly LruCache<int, bool> _assignableFalseCache = new(4096);

}

public static partial class CecilHelper
{

    public readonly struct TypeKey : IEquatable<TypeKey>
    {
        private readonly KeyKind _kind;


        private readonly Guid _moduleMvid;
        private readonly int _fullNameHash;

        private readonly Guid _ownerModuleMvid;
        private readonly int _ownerFullNameHash;
        private readonly GenericParameterType _gpType;
        private readonly int _gpPosition;


        private TypeKey(Guid moduleMvid, int fullNameHash)
        {
            _kind = KeyKind.Normal;
            _moduleMvid = moduleMvid;
            _fullNameHash = fullNameHash;

            _ownerModuleMvid = default;
            _ownerFullNameHash = default;
            _gpType = default;
            _gpPosition = default;
        }

        private TypeKey(Guid ownerModuleMvid, int ownerFullNameHash, GenericParameterType gpType, int gpPosition)
        {
            _kind = KeyKind.GenericParameter;

            _ownerModuleMvid = ownerModuleMvid;
            _ownerFullNameHash = ownerFullNameHash;
            _gpType = gpType;
            _gpPosition = gpPosition;

            _moduleMvid = default;
            _fullNameHash = default;
        }

        public static TypeKey Create(TypeReference t)
        {
   
            if (t is GenericParameter gp)
            {

                if (gp.Owner is TypeReference tr)
                    return new TypeKey(tr.Resolve()?.Module?.Mvid ?? tr.Module?.Mvid ?? Guid.Empty,
                        HashUtils.GetTypeHash(tr), gp.Type, gp.Position);

                if (gp.Owner is MethodReference mr)
                    return new TypeKey(mr.DeclaringType?.Resolve()?.Module?.Mvid ?? mr.DeclaringType?.Module?.Mvid ?? Guid.Empty,
                        HashUtils.GetMethodHash(mr), gp.Type, gp.Position);

                return new TypeKey(Guid.Empty, HashUtils.GetTypeHash(gp), gp.Type, gp.Position);
            }

            return new TypeKey(t.Resolve()?.Module?.Mvid ?? t.Module?.Mvid ?? Guid.Empty, HashUtils.GetTypeHash(t));
        }

        public bool Equals(TypeKey other)
        {
            if (_kind != other._kind) return false;

            if (_kind == KeyKind.Normal)
            {
                return _moduleMvid == other._moduleMvid
                       && _fullNameHash == other._fullNameHash;
            }

            // GenericParameter
            return _ownerModuleMvid == other._ownerModuleMvid
                   && _ownerFullNameHash == other._ownerFullNameHash
                   && _gpType == other._gpType
                   && _gpPosition == other._gpPosition;
        }

        public override bool Equals(object? obj) => obj is TypeKey k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (int)_kind;

                if (_kind == KeyKind.Normal)
                {
                    h = h * 31 + _moduleMvid.GetHashCode();
                    h = h * 31 + _fullNameHash;
                }
                else
                {
                    h = h * 31 + _ownerModuleMvid.GetHashCode();
                    h = h * 31 + _ownerFullNameHash;
                    h = h * 31 + (int)_gpType;
                    h = h * 31 + _gpPosition;
                }

                return h;
            }
        }



        public static int CombineHashCodes(TypeKey a, TypeKey b)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + a.GetHashCode();
                hash = hash * 23 + b.GetHashCode();

                return hash;
            }
        }


        private enum KeyKind : byte
        {
            Normal = 0,
            GenericParameter = 1
        }
    }



    public static TypeReference? GetEnumUnderlyingType(this TypeReference typeRef)
    {
        TypeDefinition typeDef = typeRef.Resolve() ?? throw new ResolveFailedException(typeRef);

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

    //TODO: 进行性能优化
    //不考虑 Byte Sbyte Uint16都转化为I4这种的等价（在别的地方实现该功能）
    public static bool IsILStackAssignableFrom(this TypeReference to, TypeReference? from)
        => IsAssignableFromCore(to, from, true, new HashSet<int>());

    public static bool IsAssignableFrom(this TypeReference to, TypeReference? from)
        => IsAssignableFromCore(to, from, false, new HashSet<int>());

    private static bool IsAssignableFromCore(TypeReference to, TypeReference? from, bool strict, HashSet<int> guard)
    {
        var result = IsAssignableFromInteral(to, from, strict, guard);
        if (from != null)
        {
            var hash = TypeKey.CombineHashCodes(TypeKey.Create(to), TypeKey.Create(from));
            if (result)
            {
                _assignableTrueCache.Put(hash, result);
            }
            else
            {
                _assignableFalseCache.Put(hash, result);
            }
        }
        return result;
    }

    private static bool IsAssignableFromInteral(TypeReference to, TypeReference? from, bool strict, HashSet<int> guard)
    {
        while (true)
        {
            if (from == null) return false;

            to = to.StripType();
            from = from.StripType();

            //优先处理未闭合
            if (from is GenericParameter fromGp)
            {
                return IsAssignableFromGenericParam(to, fromGp, strict, guard);
            }



            if (strict)
            {
                if (!to.IsValueType && from.IsValueType) return false;
                if (to.IsValueType && !from.IsValueType) return false;
            }

            if (to.Name == "Object" && to.Namespace == "System")
            {
                return from is not ByReferenceType
                       && from is not PointerType
                       && from is not FunctionPointerType; //已经在上面排除值类型和引用类型严格判别
                                                           //这里不需要额外判断
            }


            var toKey = TypeKey.Create(to);
            var fromKey = TypeKey.Create(from);
            var hash = TypeKey.CombineHashCodes(toKey, fromKey);
            if (_assignableTrueCache.TryGetValue(hash, out var result))
                return result;

            if (_assignableFalseCache.TryGetValue(hash, out result))
                return result;

            if (!guard.Add(hash)) 
                return false;

            if (fromKey.Equals(toKey)) 
                return true;

            if (IsOpenGenericDefinition(from))
            {
                return IsAssignableFromCore(to, GenerateGenericInstanceType(from), strict, guard);
            }

            //byRef
            if (from is ByReferenceType fromRef)
            {
                if (to is ByReferenceType toRef)
                    return fromRef.ElementType.IsSameType(toRef.ElementType);
                return false;
            }

            //指针
            if (from is PointerType fromPtr)
            {
                if (to is PointerType toPtr)
                    return fromPtr.ElementType.IsSameType(toPtr.ElementType);
                return false;
            }


            //数组
            if (from is ArrayType fromArr)
            {
                if (IsAssignableFromArray(to, fromArr, strict, guard))
                    return true;
            }


            //泛型
            if (to is GenericInstanceType toGi && from is GenericInstanceType fromGi)
            {
                //处理逆变/协变
                if (IsSameType(toGi.ElementType, fromGi.ElementType) &&
                    GenericArgsAssignableWithVariance(toGi, fromGi, strict, guard, from.IsArray))
                {
                    return true;
                }
            }

            var toDef = to.Resolve();

            //nullable
            if (toDef?.Name == "Nullable`1" && toDef.Namespace == "System" && to is GenericInstanceType instTo)
            {
                return IsSameType(instTo.GenericArguments[0], from);
            }

            //接口
            if (toDef?.IsInterface == true)
            {
                if (from.IsValueType && strict)
                    return false;

                if (ImplementsInterface(from, to, strict, guard))
                    return true;
            }

            if (from.IsValueType && strict)
                return false;

            from = from.BaseType();
        }
    }

    private static bool ImplementsInterface(TypeReference from, TypeReference toInterface, bool strict, HashSet<int> guard)
    {
        foreach (var iface in EnumerateAllInterfaces(from))
        {
            var iface2 = iface;
            if (IsSameType(iface2, toInterface)) return true;
            if (from is GenericInstanceType fromG) iface2 = TryInflateGenericType(iface2, fromG);

            // 处理逆变/协变
            if (toInterface is GenericInstanceType toGi &&
                iface2 is GenericInstanceType fromGi &&
                IsSameType(toGi.ElementType, fromGi.ElementType) &&
                GenericArgsAssignableWithVariance(toGi, fromGi, strict, guard, from.IsArray))
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<TypeReference> EnumerateAllInterfaces(TypeReference type)
    {
        var seen = new HashSet<TypeKey>();
        var stack = new Stack<TypeReference>();
        stack.Push(type);

        while (stack.Count > 0)
        {
            var cur = stack.Pop().StripType();

            if (cur is ArrayType arr)
            {
                foreach (var iface in EnumerateArrayRuntimeInterfaces(arr))
                {
                    var i = iface.StripType();
                    if (seen.Add(TypeKey.Create(i)))
                        yield return i;
                }

                var mod = (arr.Module ?? cur.Module) ?? throw new InvalidOperationException("Module is null.");
                stack.Push(SystemArrayRef(mod));
                continue;
            }
            var curDef = cur.Resolve();

            if (curDef != null)
            {
                var curInst = cur as GenericInstanceType;

                // 当前类型声明的接口
                foreach (var ii in curDef.Interfaces)
                {
                    var iface = ii.InterfaceType;
                    if (curInst != null) iface = TryInflateGenericType(iface, curInst);

                    iface = iface.StripType();

                    if (seen.Add(TypeKey.Create(iface)))
                    {
                        yield return iface;
                        // 还要继续处理接口的父接口
                        stack.Push(iface);
                    }
                }

                // 再追基类
                var bt = cur.BaseType();
                if (bt != null) stack.Push(bt);
            }
        }
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
        if (arr is { Rank: 1, IsVector: true })
        {
            foreach (var g in new[]
                     {
                         ImportCorelibType(mod, "System.Collections.Generic", "IEnumerable`1"),
                         ImportCorelibType(mod, "System.Collections.Generic", "ICollection`1"),
                         ImportCorelibType(mod, "System.Collections.Generic", "IList`1"),
                         ImportCorelibType(mod, "System.Collections.Generic", "IReadOnlyCollection`1"),
                         ImportCorelibType(mod, "System.Collections.Generic", "IReadOnlyList`1"),
                     })
            {
                var gi = new GenericInstanceType(g);
                gi.GenericArguments.Add(elem);
                yield return gi;
            }
        }
    }

    private static TypeReference ImportCorelibType(ModuleDefinition mod, string @namespace, string name)
    {
        var td = new TypeReference(@namespace, name, mod, mod.TypeSystem.CoreLibrary);
        return mod.ImportReference(td);
    }

    private static TypeReference SystemArrayRef(ModuleDefinition module)
    {
        return _array ??= ImportCorelibType(module, "System", "Array").Resolve();
    }

    private static bool GenericArgsAssignableWithVariance(GenericInstanceType target, GenericInstanceType source,
        bool strict,
        HashSet<int> guard, bool isArrayType)
    {
        if (target.GenericArguments.Count != source.GenericArguments.Count)
            return false;

        var def = target.ElementType.Resolve();
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
                    if (!DefinitelyRef(tArg) || !DefinitelyRef(sArg))
                        return IsSameType(tArg, sArg);
                    if (!IsAssignableFromCore(tArg, sArg, strict, guard)) return false;
                    break;

                case GenericParameterAttributes.Contravariant: //逆变
                    if (!DefinitelyRef(tArg) || !DefinitelyRef(sArg))
                        return IsSameType(tArg, sArg);
                    if (!IsAssignableFromCore(sArg, tArg, strict, guard)) return false;
                    break;

                default: // 常规
                    if (!IsSameType(tArg, sArg)) return false;
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



    private static bool IsAssignableFromArray(TypeReference to, ArrayType fromArr, bool strict, HashSet<int> guard)
    {
        to = to.StripType();

        // T[] -> System.Array
        if (to.Namespace == "System")
        {
            if (to.Name is "Array" or "ICloneable")
                return true;
        }

        // T[] -> 非泛型 IEnumerable/ICollection/IList
        else if (to.Namespace == "System.Collections")
        { 
            if (to.Name is "IEnumerable"
                      or "ICollection"
                      or "IList"
                      or "SIStructuralEquatable")
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
                return IsAssignableFromCore(toElem, fromElem, strict, guard);
            }
            return IsSameType(toElem, fromElem);
        }
        return false;
    }

    private static bool IsAssignableFromGenericParam(TypeReference to, GenericParameter fromGp, bool strict,
        HashSet<int> guard)
    {
        // 对每个显式约束：where T : C, IFoo 处理，假定T为每一个约束类型进行赋值比较判断
        foreach (var c in fromGp.Constraints)
        {
            var ct = c.ConstraintType.StripType();

            if (IsAssignableFromCore(to, ct, strict, guard))
                return true;
        }

        var constraint = fromGp.Attributes & GenericParameterAttributes.SpecialConstraintMask;
        switch (constraint)
        {
            case GenericParameterAttributes.ReferenceTypeConstraint:
                return to.Name == "Object" && to.Namespace == "System";
            case GenericParameterAttributes.NotNullableValueTypeConstraint:
                if (strict) return false;
                return to.Name is "Object" or "ValueType" && to.Namespace is "System";
        }
        return to.Name == "Object" && to.Namespace == "System" && !strict;
    }



    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSameType(this TypeReference a, TypeReference b)
    {
        if (ReferenceEquals(a, b))
            return true;
        return TypeKey.Create(a).Equals(TypeKey.Create(b));
    }


    public static TypeReference? BaseType(this TypeReference? type)
    {
        if (type == null) return null;
        type = type.StripType();

        if (type is ArrayType)
        {
            var mod = type.Module ?? throw new InvalidOperationException("TypeReference.Module is null.");
            return SystemArrayRef(mod);
        }

        var typeDef = type.Resolve();
        if (typeDef?.BaseType == null) return null;

        var baseTypeRef = typeDef.BaseType; // 泛型TypeDefinition的BaseType可能是GenericInstanceType

        return type is not GenericInstanceType derivedInstance ?
            baseTypeRef :
            TryInflateGenericType(baseTypeRef, derivedInstance);
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
            if (gp.Owner is TypeReference ownerRef && IsSameType(ownerRef, context.ElementType) &&
                context.GenericArguments.Count > gp.Position) //来源一致获取泛型参数
            {
                return context.GenericArguments[gp.Position];
            }
            return typeToInflate;
        }

        if (typeToInflate is GenericInstanceType git)
        {
            var element = TryInflateGenericType(git.ElementType, context);
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
                default: return t; //这里不处理byref ptr等在il堆栈内不等价的类型
            }
        }
    }
}
