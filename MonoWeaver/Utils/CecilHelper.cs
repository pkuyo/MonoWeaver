using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using GenericParameterAttributes = Mono.Cecil.GenericParameterAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using COpCodes = Mono.Cecil.Cil.OpCodes;
using TypeAttributes = Mono.Cecil.TypeAttributes;
using OpCodes = System.Reflection.Emit.OpCodes;


namespace MonoWeaver.Utils;

public static partial class CecilHelper
{
    private static readonly Dictionary<ModuleDefinition, TypeDefinition> _arrayDefs = new();
    private static readonly Dictionary<ModuleDefinition, TypeReference[]> _arrayInfs = new();

    private static readonly LruCache<(TypeSig from, TypeSig to), bool> _assignableCache = new(8192);
    private static readonly LruCache<TypeReference, TypeSig> _typeSigCache = new(8192);
    private static readonly LruCache<(int metaToken, Hash128 hash), TypeDefinition> _typeCache = new(8192);

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

public static partial class CecilHelper
{
    
    public readonly struct TypeSig : IEquatable<TypeSig>
    {
        private readonly KeyKind _kind;


        private readonly Guid _moduleMvid;
        private readonly Hash128 _fullNameHash;

        private readonly Guid _ownerModuleMvid;
        private readonly Hash128 _ownerFullNameHash;
        private readonly GenericParameterType _gpType;
        private readonly int _gpPosition;


        private TypeSig(Guid moduleMvid, Hash128 fullNameHash)
        {
            _kind = KeyKind.Normal;
            _moduleMvid = moduleMvid;
            _fullNameHash = fullNameHash;

            _ownerModuleMvid = Guid.Empty;
            _ownerFullNameHash = default;
            _gpType = default;
            _gpPosition = 0;
        }

        private TypeSig(Guid ownerModuleMvid, Hash128 ownerFullNameHash, GenericParameterType gpType, int gpPosition)
        {
            _kind = KeyKind.GenericParameter;

            _ownerModuleMvid = ownerModuleMvid;
            _ownerFullNameHash = ownerFullNameHash;
            _gpType = gpType;
            _gpPosition = gpPosition;

            _moduleMvid = Guid.Empty;
            _fullNameHash = default;
        }

        public static TypeSig Create(TypeReference t)
        {
            if (_typeSigCache.TryGetValue(t, out TypeSig typeSig))
                return typeSig;

            if (t is GenericParameter gp)
            {

                if (gp.Owner is TypeReference tr)
                    return new TypeSig(ResolveWithCache(t)?.Module?.Mvid ?? tr.Module?.Mvid ?? Guid.Empty,
                        HashUtils.GetTypeHash(tr), gp.Type, gp.Position);

                if (gp.Owner is MethodReference mr)
                    return new TypeSig(ResolveWithCache(mr.DeclaringType)?.Module?.Mvid ?? mr.DeclaringType?.Module?.Mvid ?? Guid.Empty,
                        HashUtils.GetMethodHash(mr), gp.Type, gp.Position);

                typeSig = new TypeSig(Guid.Empty, HashUtils.GetTypeHash(gp), gp.Type, gp.Position);
            }

            typeSig = new TypeSig(ResolveWithCache(t)?.Module?.Mvid ?? t.Module?.Mvid ?? Guid.Empty, HashUtils.GetTypeHash(t));
            _typeSigCache.Put(t, typeSig);
            return typeSig;
        }

        public bool Equals(TypeSig other)
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

        public override bool Equals(object? obj) => obj is TypeSig k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (int)_kind;

                if (_kind == KeyKind.Normal)
                {
                    h = h * 31 + _moduleMvid.GetHashCode();
                    h = h * 31 + _fullNameHash.GetHashCode();
                }
                else
                {
                    h = h * 31 + _ownerModuleMvid.GetHashCode();
                    h = h * 31 + _ownerFullNameHash.GetHashCode();
                    h = h * 31 + (int)_gpType;
                    h = h * 31 + _gpPosition;
                }

                return h;
            }
        }
        
        private enum KeyKind : byte
        {
            Normal = 0,
            GenericParameter = 1
        }
    }
    
    public static ILMethodAnalyzer Analyze(this MethodDefinition method, VerifyOptions options = VerifyOptions.Default) 
        => new ILMethodAnalyzer(method, options);

    public static bool IsILStackAssignableTo(this TypeReference from, TypeReference? to)
    => IsAssignableFromCore(to, from, true, new HashSet<(TypeSig from, TypeSig to)>());

    public static bool IsAssignableTo(this TypeReference from, TypeReference? to)
        => IsAssignableFromCore(to, from, false, new HashSet<(TypeSig from, TypeSig to)>());

    //不考虑 Byte Sbyte Uint16都转化为I4这种的等价（在别的地方实现该功能）
    public static bool IsILStackAssignableFrom(this TypeReference to, TypeReference? from)
        => IsAssignableFromCore(to, from, true, new HashSet<(TypeSig from, TypeSig to)>());

    public static bool IsAssignableFrom(this TypeReference to, TypeReference? from)
        => IsAssignableFromCore(to, from, false, new HashSet<(TypeSig from, TypeSig to)>());

    public static TypeReference? BaseType(this TypeReference? type)
    {
        if (type == null) return null;
        type = type.StripType();

        if (type is ArrayType)
        {
            var mod = type.Module ?? throw new InvalidOperationException("TypeReference.Module is null.");
            return SystemArrayRef(mod);
        }

        var typeDef = ResolveWithCache(type);
        if (typeDef?.BaseType == null) return null;

        var baseTypeRef = typeDef.BaseType; // 泛型TypeDefinition的BaseType可能是GenericInstanceType

        return type is not GenericInstanceType derivedInstance ?
            baseTypeRef :
            TryInflateGenericType(baseTypeRef, derivedInstance);
    }

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

    public static TypeReference? FindCommonBaseType(TypeReference? a0, TypeReference? b0)
    {
        if(a0 is null || b0 is null) return null;

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

    private static bool IsAssignableFromCore(TypeReference? to, TypeReference? from, bool strict, HashSet<(TypeSig from, TypeSig to)> guard)
    {
        var result = IsAssignableFromInternal(to, from, strict, guard);
        if (from != null && to != null)
        {
             _assignableCache.Put((TypeSig.Create(to), TypeSig.Create(from)), result);
        }
        return result;
    }
    
    private static bool IsAssignableFromInternal(TypeReference? to, TypeReference? from, bool strict, HashSet<(TypeSig from, TypeSig to)> guard)
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

            if (from.Name == "Object" && from.Namespace == "System")
            {
                return false;
            }

            var toKey = TypeSig.Create(to);
            var fromKey = TypeSig.Create(from);
            var hash = (toKey, fromKey);

          
            if (fromKey.Equals(toKey)) 
                return true;

            if (_assignableCache.TryGetValue(hash, out var result))
            {
                return result;
            }

    
            if (!guard.Add(hash))
                return false;

            if (IsOpenGenericDefinition(from))
            {
                return IsAssignableFromCore(to, GenerateGenericInstanceType(from), strict, guard);
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
                if (IsAssignableFromArray(to, fromArr, strict, guard))
                    return true;
            }


            //泛型
            if (to is GenericInstanceType toGi && from is GenericInstanceType fromGi)
            {
                //处理逆变/协变
                if (IsSameWith(toGi.ElementType, fromGi.ElementType) &&
                    GenericArgsAssignableWithVariance(toGi, fromGi, strict, guard, from.IsArray))
                {
                    return true;
                }
            }

            var toDef = ResolveWithCache(to);

            //nullable
            if (toDef?.Name == "Nullable`1" && toDef.Namespace == "System" && to is GenericInstanceType instTo)
            {
                return IsSameWith(instTo.GenericArguments[0], from);
            }

            //接口
            if (toDef?.IsInterface == true)
            {
                if (from.IsValueType && strict) return false;

                return ImplementsInterface(from, to, strict, guard);
            }

            if (from.IsValueType && strict)
            {
                return false;
            }
            from = from.BaseType();
        }
    }

    private static bool ImplementsInterface(TypeReference from, TypeReference toInterface, bool strict, HashSet<(TypeSig from, TypeSig to)> guard)
    {
        var list = new List<TypeReference>();
        CollectAllInterfaces(from, list);
        foreach (var iface in list)
        {
            var iface2 = iface;
            if (IsSameWith(iface2, toInterface)) return true;

            // 处理逆变/协变
            if (toInterface is GenericInstanceType toGi &&
                iface2 is GenericInstanceType fromGi &&
                IsSameWith(toGi.ElementType, fromGi.ElementType) &&
                GenericArgsAssignableWithVariance(toGi, fromGi, strict, guard, from.IsArray))
            {
                return true;
            }
        }
        return false;
    }

    public static void CollectAllInterfaces(TypeReference? type, List<TypeReference> resultBuffer)
    {
        if (type == null) return;

        if (type is ArrayType arr)
        {
            foreach (var iface in EnumerateArrayRuntimeInterfaces(arr))
            {
                resultBuffer.Add(iface);
            }
            return;
        }

        var seen = InterfaceTraversalCache.GetClearedSet();
        var stack = InterfaceTraversalCache.GetClearedStack();

        stack.Push(type);

        while (stack.Count > 0)
        {
            var cur = stack.Pop();

            var curDef = ResolveWithCache(cur);

            if (curDef != null)
            {
                var curInst = cur as GenericInstanceType;
                var interfaces = curDef.Interfaces;

                for (int i = 0; i < interfaces.Count; i++)
                {
                    var ii = interfaces[i];
                    var iface = ii.InterfaceType;

                    if (curInst != null)
                        iface = TryInflateGenericType(iface, curInst);

                    if (seen.Add(TypeSig.Create(iface)))
                    {
                        resultBuffer.Add(iface);
                        stack.Push(iface); 
                    }
                }

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
            _arrayInfs.Add(mod, array = new TypeReference[5]);
            var index = 0;
            foreach (var g in new[]
                     {
                         ImportCorelibType(mod, "System.Collections.Generic", "IEnumerable`1"),
                         ImportCorelibType(mod, "System.Collections.Generic", "ICollection`1"),
                         ImportCorelibType(mod, "System.Collections.Generic", "IList`1"),
                         ImportCorelibType(mod, "System.Collections.Generic", "IReadOnlyCollection`1"),
                         ImportCorelibType(mod, "System.Collections.Generic", "IReadOnlyList`1"),
                     })
            {
                array[index++] = g;
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
        if (_arrayDefs.TryGetValue(module, out var array))
            return array;
        _arrayDefs[module] = array = ImportCorelibType(module, "System", "Array").Resolve();
        return array;
    }

    private static bool GenericArgsAssignableWithVariance(GenericInstanceType target, GenericInstanceType source,
        bool strict,
        HashSet<(TypeSig from, TypeSig to)> guard, bool isArrayType)
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
                    if (!IsAssignableFromCore(tArg, sArg, strict, guard))
                        return false;
                    break;

                case GenericParameterAttributes.Contravariant: //逆变
                    if ((!DefinitelyRef(tArg) || !DefinitelyRef(sArg)) && !IsSameWith(tArg, sArg))
                        return false;
                         
                    if (!IsAssignableFromCore(sArg, tArg, strict, guard)) 
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



    private static bool IsAssignableFromArray(TypeReference to, ArrayType fromArr, bool strict, HashSet<(TypeSig from, TypeSig to)> guard)
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
                      or "IStructuralComparable")
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
            return IsSameWith(toElem, fromElem);
        }
        return false;
    }

    private static bool IsAssignableFromGenericParam(TypeReference to, GenericParameter fromGp, bool strict,
        HashSet<(TypeSig from, TypeSig to)> guard)
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
    public static bool IsSameWith(this TypeReference? a, TypeReference? b)
    {
        if (b is null || a is null) return false;
        if (ReferenceEquals(a, b))
            return true;
        return TypeSig.Create(a).Equals(TypeSig.Create(b));
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
        if (_typeCache.TryGetValue((t.MetadataToken.ToInt32(), HashUtils.GetTypeHash(t)), out var def1))
        {
            return def1;
        }
        var def = t.Resolve();
        if(def is not null)
            _typeCache.Put((t.MetadataToken.ToInt32(), HashUtils.GetTypeHash(t)), def);
        return def;
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
                default: return t; //这里不处理byref等在il堆栈内不等价的类型
            }
        }
    }

    public static TypeReference? GetEnumType(this TypeReference typeRef)
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

    private static Func<object, Instruction>? _monoModresolveStrategy = null!;

    private static void BuildMonoModResolveStrategy(Type type)
    {

        try
        {
            DynamicMethod method = new DynamicMethod("Target", typeof(Instruction), [typeof(object)]);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Isinst, type);
            il.Emit(OpCodes.Ldfld, type.GetField("Target"));
            il.Emit(OpCodes.Ret);
            _monoModresolveStrategy = (Func<object, Instruction>)method.CreateDelegate(typeof(Func<object, Instruction>), null);
        }
        catch
        {

            if (!File.Exists(type.Assembly.Location))
            {
                throw new Exception(); //TODO 完善异常说明
            }
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(type.Assembly.Location);

            using AssemblyDefinition assDef = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("MonoWeaver.Monomod", new Version()),
                "module", new ModuleParameters()
                {
                    Kind = ModuleKind.Dll,
                    AssemblyResolver = resolver
                });



            var module = assDef.MainModule;
            TypeDefinition typeDef = new TypeDefinition("MonoWeaver.Monomod", "Helper",
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract | TypeAttributes.Class);
            MethodDefinition mothodDef = new MethodDefinition("Target", MethodAttributes.Public | MethodAttributes.Static,
                module.ImportReference(typeof(Instruction)));

            mothodDef.Parameters.Add(new ParameterDefinition(module.ImportReference(type)));
            var il = mothodDef.Body.GetILProcessor();
            il.Emit(COpCodes.Ldarg_0);
            il.Emit(COpCodes.Isinst, module.ImportReference(type));
            il.Emit(COpCodes.Ldfld, module.ImportReference(type.GetField("Target")));
            il.Emit(COpCodes.Ret);

            using MemoryStream ms = new MemoryStream();
            assDef.Write(ms);

            var ass = Assembly.Load(ms.ToArray());

            _monoModresolveStrategy =
                (Func<object, Instruction>)Delegate.CreateDelegate(typeof(Func<object, Instruction>),
                ass.ManifestModule.GetType("Helper").GetMethod("Target"));
        }
    }

    internal static IEnumerable<Instruction> OperandToTargets(object operand)
    {
        if( operand is Instruction inst)
            yield return inst;
        else if(operand is Instruction[] insts)
        {
            foreach(var i in insts)
                yield return i;
        }
        else
        {
            var type = operand.GetType();
            if(type.IsArray)
            {
                var eleType = type.GetElementType();
                var array = (Array)operand;
                if(eleType.FullName == "MonoMod.Cil.ILLabel")
                {
                    if (_monoModresolveStrategy is null)
                        BuildMonoModResolveStrategy(eleType);
                    foreach (var i in array)
                    {
                        yield return _monoModresolveStrategy!(i);
                    }
                }
            }
            else if(type.FullName == "MonoMod.Cil.ILLabel")
            {
                if (_monoModresolveStrategy is null)
                    BuildMonoModResolveStrategy(type);
                yield return _monoModresolveStrategy!(operand);
            }
        }
    }
}
