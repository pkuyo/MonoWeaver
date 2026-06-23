using System;
using System.Linq;
using System.Reflection;
using MonoWeaver.Patterns;

namespace MonoWeaver.PatternTests;

internal static class RuntimeSymbols
{
    public static CilTypeSpec Type<T>(bool assignable = false)
    {
        var type = CilTypeSpec.From(typeof(T));
        return assignable ? type.Assignable() : type;
    }

    public static CilMethodSpec Method<TDeclaring>(string name, params Type[] parameterTypes)
        => CilMethodSpec.From(RequireMethod(typeof(TDeclaring), name, parameterTypes));

    public static CilMethodSpec GenericMethod<TDeclaring>(string name, int genericArity,
        params Type[] parameterTypes)
    {
        var method = typeof(TDeclaring).GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Static | BindingFlags.Instance)
            .Where(candidate => candidate.Name == name)
            .Where(candidate => candidate.IsGenericMethodDefinition)
            .Where(candidate => candidate.GetGenericArguments().Length == genericArity)
            .Where(candidate => ParametersMatch(candidate, parameterTypes))
            .Single();
        return CilMethodSpec.From(method);
    }

    public static CilMethodSpec ClosedGenericMethod<TDeclaring>(string name, Type[] genericArguments,
        params Type[] constructedParameterTypes)
    {
        if (genericArguments is null)
            throw new ArgumentNullException(nameof(genericArguments));

        var method = typeof(TDeclaring).GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Static | BindingFlags.Instance)
            .Where(candidate => candidate.Name == name)
            .Where(candidate => candidate.IsGenericMethodDefinition)
            .Where(candidate => candidate.GetGenericArguments().Length == genericArguments.Length)
            .Select(candidate => candidate.MakeGenericMethod(genericArguments))
            .Where(candidate => ParametersMatch(candidate, constructedParameterTypes))
            .Single();
        return CilMethodSpec.From(method);
    }

    public static CilMethodSpec Constructor<T>(params Type[] parameterTypes)
        => CilMethodSpec.From(typeof(T).GetConstructor(parameterTypes)
            ?? throw new MissingMethodException(typeof(T).FullName, ".ctor"));

    public static CilFieldSpec Field<TDeclaring>(string name)
        => CilFieldSpec.From(typeof(TDeclaring).GetField(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            ?? throw new MissingFieldException(typeof(TDeclaring).FullName, name));

    private static MethodInfo RequireMethod(Type declaringType, string name, Type[] parameterTypes)
        => declaringType.GetMethod(name,
               BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance,
               binder: null, types: parameterTypes, modifiers: null)
           ?? throw new MissingMethodException(declaringType.FullName, name);

    private static bool ParametersMatch(MethodInfo method, Type[] parameterTypes)
    {
        var parameters = method.GetParameters();
        if (parameters.Length != parameterTypes.Length)
            return false;
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType != parameterTypes[i])
                return false;
        }
        return true;
    }
}
