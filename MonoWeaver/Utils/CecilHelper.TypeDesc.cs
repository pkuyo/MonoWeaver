using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.Threading;

namespace MonoWeaver.Utils;

public static partial class CecilHelper
{
    private sealed class TypeDesc
    {
        public TypeDefinition? Definition;
        public TypeReference? BaseType;
        public TypeReference[]? RuntimeInterfaces;
        public int BaseTypeInitialized;
    }

    private static readonly object _typeDescSlotsSync = new();
    private static readonly List<TypeDesc?> _typeDescSlots = new(128);

    private static TypeDesc GetTypeDesc(TypeSig sig)
    {
        var id = sig.Id;

        lock (_typeDescSlotsSync)
        {
            while (id >= _typeDescSlots.Count)
                _typeDescSlots.Add(null);

            var desc = _typeDescSlots[id];
            if (desc != null)
                return desc;

            desc = new TypeDesc();
            _typeDescSlots[id] = desc;
            return desc;
        }
    }

    private static TypeDefinition? ResolveWithTypeDescCache(TypeReference keyType)
    {
        var desc = GetTypeDesc(TypeSig.Create(keyType));
        var cached = Volatile.Read(ref desc.Definition);
        if (cached != null)
            return cached;

        var resolved = TryResolve(keyType);
        if (resolved == null)
            return null;

        var existing = Interlocked.CompareExchange(ref desc.Definition, resolved, null);
        return existing ?? resolved;
    }

    private static TypeReference? GetBaseTypeWithCache(TypeReference type)
    {
        type = type.StripType();

        if (type is ArrayType)
        {
            var mod = type.Module ?? throw new InvalidOperationException("TypeReference.Module is null.");
            return SystemArrayRef(mod);
        }

        var desc = GetTypeDesc(TypeSig.Create(type));
        if (Volatile.Read(ref desc.BaseTypeInitialized) != 0)
            return Volatile.Read(ref desc.BaseType);

        var computed = ComputeBaseType(type);

        lock (desc)
        {
            if (desc.BaseTypeInitialized != 0)
                return desc.BaseType;

            desc.BaseType = computed;
            Volatile.Write(ref desc.BaseTypeInitialized, 1);
            return computed;
        }
    }

    private static TypeReference? ComputeBaseType(TypeReference type)
    {
        var typeDef = ResolveWithCache(type);
        if (typeDef?.BaseType == null)
            return null;

        var baseTypeRef = typeDef.BaseType;
        return type is not GenericInstanceType derivedInstance
            ? baseTypeRef
            : TryInflateGenericType(baseTypeRef, derivedInstance);
    }

    private static TypeReference[] GetRuntimeInterfacesWithCache(TypeReference type)
    {
        type = type.StripType();

        if (type is ArrayType arr)
            return BuildRuntimeInterfaces(arr);

        var desc = GetTypeDesc(TypeSig.Create(type));
        var cached = Volatile.Read(ref desc.RuntimeInterfaces);
        if (cached != null)
            return cached;

        var built = BuildRuntimeInterfaces(type);
        var existing = Interlocked.CompareExchange(ref desc.RuntimeInterfaces, built, null);
        return existing ?? built;
    }

    private static TypeReference[] BuildRuntimeInterfaces(ArrayType arr)
    {
        var result = new System.Collections.Generic.List<TypeReference>();
        foreach (var iface in EnumerateArrayRuntimeInterfaces(arr))
            result.Add(iface);
        return result.ToArray();
    }

    private static TypeReference[] BuildRuntimeInterfaces(TypeReference type)
    {
        var result = new System.Collections.Generic.List<TypeReference>();
        var seen = InterfaceTraversalCache.GetClearedSet();
        var stack = InterfaceTraversalCache.GetClearedStack();

        stack.Push(type);

        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            var curDef = ResolveWithCache(cur);
            if (curDef == null)
                continue;

            var curInst = cur as GenericInstanceType;
            var interfaces = curDef.Interfaces;

            for (int i = 0; i < interfaces.Count; i++)
            {
                var iface = interfaces[i].InterfaceType;

                if (curInst != null)
                    iface = TryInflateGenericType(iface, curInst);

                if (seen.Add(TypeSig.Create(iface)))
                {
                    result.Add(iface);
                    stack.Push(iface);
                }
            }

            var bt = cur.BaseType();
            if (bt != null)
                stack.Push(bt);
        }

        return result.ToArray();
    }
}
