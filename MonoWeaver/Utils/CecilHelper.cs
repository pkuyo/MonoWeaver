using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using MonoMod.Utils;
using MonoWeaver.CFG;

namespace MonoWeaver.Utils;

public static class CecilHelper
{
    
    internal struct TypeKey
    {
        public string TypeName;
        public string ScopeName;
        public int Position;
        public GenericParameterType  GenericParameterType; 
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
    //TODO: 测试 测试逻辑正确后进行性能优化
    //不考虑 Byte Sbyte Uint16都转化为I4这种的等价（在别的地方实现该功能）
    public static bool IsILStackAssignableFrom(this TypeReference to, TypeReference? from) 
        => IsAssignableFromCore(to, from, true, new HashSet<(string To, string From)>());
    
     public static bool IsAssignableFrom(this TypeReference to, TypeReference? from) 
         => IsAssignableFromCore(to, from, false, new HashSet<(string To, string From)>());

    private static bool IsAssignableFromCore(TypeReference to, TypeReference? from,bool strict, HashSet<(string To, string From)> guard)
    {
        while (true)
        {
            if (from == null) return false;

            to   = to.StripType();
            from = from.StripType();
            
            //优先处理未闭合泛型参数
            if (from is GenericParameter gpFrom)
            {
                return IsAssignableFromGenericParam(to, gpFrom, strict, guard); 
            }



            if (strict)
            {
                if (!to.IsValueType && from.IsValueType) return false;
                if (to.IsValueType && !from.IsValueType) return false;
            }
            
            if (to.FullName == "System.Object")
            {
                return from is not ByReferenceType
                       && from is not PointerType
                       && from is not FunctionPointerType; //已经在上面排除值类型和引用类型严格判别
                                                           //这里不需要额外判断
            }

           
            var key = (MakeKey(to), MakeKey(from));
            if (!guard.Add(key)) return false;
            
            if (IsSameType(to, from)) return true;
            
            //byRef
            if (from is ByReferenceType fromRef)
            {
                if (to is ByReferenceType toRef)
                {
                    return fromRef.ElementType.IsSameType(toRef.ElementType);
                }
                return false;
            }

            //指针
            if (from is PointerType fromPtr)
            {
                if (to is PointerType toPtr)
                {
                    return fromPtr.ElementType.IsSameType(toPtr.ElementType);
                }
                return false;
            }
            
     
            
            //处理数组
            if (from is ArrayType fromArr)
            {
                if (IsAssignableFromArray(to, fromArr, strict, guard))
                    return true;
            }

            
            //均为泛型
            if (to is GenericInstanceType toGi && from is GenericInstanceType fromGi)
            {
                //处理逆变/协变
                if (IsSameType(toGi.ElementType, fromGi.ElementType) &&
                    GenericArgsAssignableWithVariance(toGi, fromGi, strict, guard))
                {
                    return true;
                }
            }
            if (to.HasGenericParameters)
            {
                //TODO
            }


            var toDef = TryResolve(to);

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

    private static bool ImplementsInterface(TypeReference from, TypeReference toInterface, bool strict, HashSet<(string To, string From)> guard)
    {
        foreach (var iface in EnumerateAllInterfaces(from))
        {
            if (IsSameType(iface, toInterface)) return true;

            // 处理逆变/协变
            if (toInterface is GenericInstanceType toGi && iface is GenericInstanceType fromGi &&
                IsSameType(toGi.ElementType, fromGi.ElementType) &&
                GenericArgsAssignableWithVariance(toGi, fromGi, strict, guard))
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<TypeReference> EnumerateAllInterfaces(TypeReference type)
    {
        var seen = new HashSet<(Guid mvid, string fullName)>();
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
                    if (seen.Add((iface.Module.Mvid, iface.FullName)))
                        yield return i;
                }
                
                var mod = (arr.Module ?? cur.Module) ?? throw new InvalidOperationException("Module is null.");
                stack.Push(SystemArrayRef(mod));
                continue;
            }
            var curDef = TryResolve(cur);

            if (curDef != null)
            {
                var curInst = cur as GenericInstanceType;

                // 当前类型声明的接口
                foreach (var ii in curDef.Interfaces)
                {
                    var iface = ii.InterfaceType;
                    if (curInst != null) iface = InflateGenericType(iface, curInst);

                    iface = iface.StripType();

                    if (seen.Add((iface.Module.Mvid, iface.FullName)))
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
    
    private static IEnumerable<TypeReference> EnumerateArrayRuntimeInterfaces(ArrayType arr)
    {
        var mod = arr.Module ?? throw new InvalidOperationException("ArrayType.Module is null.");
        var elem = arr.ElementType.StripType();

        // 非泛型：所有数组都支持
        yield return ImportCorelibType(mod, "System", "ICloneable");
        yield return ImportCorelibType(mod, "System.Collections", "IEnumerable");
        yield return ImportCorelibType(mod, "System.Collections", "ICollection");
        yield return ImportCorelibType(mod, "System.Collections", "IList");
        yield return ImportCorelibType(mod, "System.Collections", "IStructuralComparable");
        yield return ImportCorelibType(mod, "System.Collections", "IStructuralEquatable");

        // 只有 一位数组才有这些泛型接口
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

    private static TypeReference SystemArrayRef(ModuleDefinition module) => ImportCorelibType(module, "System", "Array");

    private static bool GenericArgsAssignableWithVariance(GenericInstanceType target, GenericInstanceType source,
        bool strict,
        HashSet<(string To, string From)> guard)
    {
        if (target.GenericArguments.Count != source.GenericArguments.Count)
            return false;

        var def = TryResolve(target.ElementType);
        if (def == null) //无法解析 TODO:更宽松的方式
        {
            return false;
        }
        for (int i = 0; i < target.GenericArguments.Count; i++)
        {
            var param = def.GenericParameters[i];
            var variance = param.Attributes & GenericParameterAttributes.VarianceMask;

            var tArg = target.GenericArguments[i].StripType();
            var sArg = source.GenericArguments[i].StripType();

            
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
    
    

    private static bool IsAssignableFromArray(TypeReference to, ArrayType fromArr, bool strict, HashSet<(string To, string From)> guard)
    {
        to = to.StripType();

        // T[] -> System.Array
        if (to.FullName == "System.Array")
            return true;

        // T[] -> 非泛型 IEnumerable/ICollection/IList
        if (to.FullName is "System.Collections.IEnumerable"
                      or "System.Collections.ICollection"
                      or "System.Collections.IList")
            return true;

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
        HashSet<(string To, string From)> guard)
    {
        // 如果目标也是同一个泛型参数（来源和位置一致）
        if (to is GenericParameter toGp && toGp.Position == fromGp.Position && Equals(toGp.Owner, fromGp.Owner)) 
            return true;

        // 对每个显式约束：where T : C, IFoo 处理，假定T为每一个约束类型进行赋值比较判断
        foreach (var c in fromGp.Constraints)
        {
            var ct = c.ConstraintType.StripType();
            // 如果 to 是约束本身或其父类型/接口
            if (IsAssignableFromCore(to, ct, strict, guard))
                return true;
        }

        var constraint = fromGp.Attributes & GenericParameterAttributes.SpecialConstraintMask;
        switch (constraint)
        {
            case GenericParameterAttributes.ReferenceTypeConstraint:
                return to.FullName == "System.Object";
            /*
            case GenericParameterAttributes.DefaultConstructorConstraint:
                break;
            */
            case GenericParameterAttributes.NotNullableValueTypeConstraint:
                if (strict) return false;
                return to.FullName is "System.Object" or "System.ValueType";
        }
        return to.FullName == "System.Object" && !strict;
    }

    private static TypeDefinition? TryResolve(TypeReference t)
    {
        try
        {
            return t.Resolve();
        }
        catch
        {
            //TODO:日志or处理
            return null;
        }
    }
    
    public static bool IsSameType(this TypeReference a, TypeReference b)
    {
        a = StripType(a);
        b = StripType(b);
        if (a is FunctionPointerType f1 && b is FunctionPointerType f2)
        {
            if (f1.CallingConvention != f2.CallingConvention)
                return false;
            if (f1.Parameters.Count != f2.Parameters.Count)
                return false;
            for (int i = 0; i < f1.Parameters.Count; i++)
            {
                if(!IsSameType(f1.Parameters[i].ParameterType, f2.Parameters[i].ParameterType))
                    return false;
            }

            if (!IsSameType(f1.ReturnType, f2.ReturnType))
                return false;
            return true;
        }
        
        if (a is GenericParameter agp && b is GenericParameter bgp)
        {
            return agp.Position == bgp.Position
                   && agp.Type == bgp.Type          // Type / Method
                   && Equals(agp.Owner, bgp.Owner); // 所属类型/方法
        }
        
        if (ReferenceEquals(a, b)) return true;
        if (a.FullName == b.FullName && 
            (Same(a.Scope?.Name, b.Scope?.Name) || 
            (IsSystem(a.Scope?.Name) && IsSystem(b.Scope?.Name)))) 
            return true;
        
        if (a is GenericInstanceType ag && b is GenericInstanceType bg)
        {
            if (!IsSameType(ag.ElementType, bg.ElementType)) return false;
            if (ag.GenericArguments.Count != bg.GenericArguments.Count) return false;
            for (int i = 0; i < ag.GenericArguments.Count; i++)
                if (!IsSameType(ag.GenericArguments[i], bg.GenericArguments[i])) return false;
            return true;
        }

        if (a is ArrayType aa && b is ArrayType ba)
            return aa.Rank == ba.Rank && aa.IsVector == ba.IsVector && IsSameType(aa.ElementType, ba.ElementType);

        return false;
    }

    public static bool Same(string? str1, string? str2)
    {
        if (str1 == null || str2 == null) return str1 == str2;

        if (str1.Length == str2.Length)
        {
            return string.Equals(str1, str2, StringComparison.OrdinalIgnoreCase);
        }

        if (str1.Length < str2.Length)
        {
            return CheckMatch(str2, str1);
        }

        return CheckMatch(str1, str2);

        bool CheckMatch(string longer, string shorter)
        {

            if (longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase))
            {
                if (longer[shorter.Length] == '.')
                {
                    return true;
                }
            }
            return false;
        }
    }

    private static bool IsSystem(string? str1)
    {
        if (str1 == null) return false;
        foreach (var a in CoreLibLike)
            if (str1.Contains(a))
                return true;
        return false;
    }


    private static readonly string[] CoreLibLike = new[]
    {
        "System.Private.CoreLib",
        "mscorlib",
        "System.Runtime",
        "netstandard"
    };



    public static TypeReference? BaseType(this TypeReference? type)
    {
        if (type == null) return null;
        type = type.StripType();
        
        if (type is ArrayType)
        {
            var mod = type.Module ?? throw new InvalidOperationException("TypeReference.Module is null.");
            return SystemArrayRef(mod);
        }
        
        var typeDef = TryResolve(type);
        if (typeDef?.BaseType == null) return null;

        var baseTypeRef = typeDef.BaseType; // 泛型TypeDefinition的BaseType可能是GenericInstanceType
        
        return type is not GenericInstanceType derivedInstance ? 
            baseTypeRef :
            InflateGenericType(baseTypeRef, derivedInstance);
    }

    private static TypeReference InflateGenericType(TypeReference typeToInflate, GenericInstanceType context)
    {
        switch (typeToInflate)
        {
            case ByReferenceType byRef:
                return new ByReferenceType(InflateGenericType(byRef.ElementType, context));
            case PointerType ptr:
                return new PointerType(InflateGenericType(ptr.ElementType, context));
            case OptionalModifierType modifier:
                return new OptionalModifierType(modifier.ModifierType, InflateGenericType(modifier.ElementType, context));
            case RequiredModifierType required:
                return new RequiredModifierType(required.ModifierType, InflateGenericType(required.ElementType, context));
        }
        
        if (typeToInflate is GenericParameter gp)
        {
            if (gp.Owner is TypeReference ownerRef && IsSameType(ownerRef, context.ElementType)) //来源一致获取泛型参数
            {
                return context.GenericArguments[gp.Position];
            }
            return typeToInflate;
        }
        
        if (typeToInflate is GenericInstanceType git)
        {
            var element = InflateGenericType(git.ElementType, context); 
            var result = new GenericInstanceType(element); //创建空泛型，并填充内容
            foreach (var argument in git.GenericArguments)
            {
                var inflatedArgument = InflateGenericType(argument, context);
                result.GenericArguments.Add(inflatedArgument);
            }

            return result;
        }
        
        if (typeToInflate is ArrayType arrayType)
        {
            var inflatedElement = InflateGenericType(arrayType.ElementType, context);
            ArrayType res = arrayType.IsVector
                ? new ArrayType(inflatedElement)
                : new ArrayType(inflatedElement, arrayType.Rank);
            foreach (var d in arrayType.Dimensions)
                res.Dimensions.Add(new ArrayDimension(d.LowerBound, d.UpperBound));
            return res;
        }
        return typeToInflate;
    }
    
    public static TypeReference StripType(this TypeReference t)
    {
        while (true)
        {
            switch (t)
            {
                case OptionalModifierType omt: t = omt.ElementType; continue;
                case RequiredModifierType rmt: t = rmt.ElementType; continue;
                case PinnedType pt:           t = pt.ElementType;  continue;
                case SentinelType st:         t = st.ElementType;  continue;
                default: return t; //这里不处理byref ptr等在il堆栈内不等价的类型
            }
        }
    }
    //TODO:更换为struct
    private static string MakeKey(TypeReference t)
    {
        t = t.StripType();
        if (t is GenericParameter gp)
        {
            var ownerKey = gp.Owner switch
            {
                TypeReference tr  => $"{tr.Module.Mvid}:{tr.FullName}",
                MethodReference mr => $"{mr.DeclaringType.Module.Mvid}:{mr.FullName}",
                _ => gp.Owner?.ToString() ?? ""
            };
            return $"{ownerKey}|{gp.Type}|{gp.Position}";
        }
        return $"{t.Module.Mvid}:{t.FullName}";
    }
}