using System;
using System.Collections.Generic;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoWeaver.Patterns;

namespace MonoWeaver.MonoMod.Patterns;

/// <summary>
/// 普通 insertion point。它不假设 matched value 已经存在于 stack 上。
/// argument 会被显式加载；non-void delegate result 会留在 stack 上，除非通过
/// <see cref="DelegateCallResult"/> 选择 destination。
/// </summary>
public sealed class CilInsertionSite
{
    private readonly ILContext _context;
    private readonly Instruction _anchor;
    private readonly MoveType _moveType;

    internal CilInsertionSite(ILContext context, Instruction anchor, MoveType moveType)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
        _moveType = moveType;
    }

    /// <summary>加载配置好的 source，并在此 site 调用 delegate。</summary>
    public DelegateCallResult Call<TDelegate>(TDelegate callback,
        Action<DelegateArguments>? arguments = null)
        where TDelegate : Delegate
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));

        var sources = new DelegateArguments(_context);
        arguments?.Invoke(sources);
        var invoke = DelegateSignature.RequireInvoke(typeof(TDelegate));
        DelegateSignature.RequireParameterCount(invoke, sources.Count, "Call");

        var cursor = new ILCursor(_context).Goto(_anchor, _moveType);
        sources.Emit(cursor);
        cursor.EmitDelegate(callback);
        return new DelegateCallResult(cursor, invoke.ReturnType != typeof(void));
    }
}

/// <summary>
/// matched value insertion point。Transform 和 Observe 会把已经在 evaluation stack 上的
/// matched value 视为第一个 delegate argument；调用方只描述 additional arg/local load。
/// </summary>
public sealed class MatchedValueSite
{
    private readonly ILContext _context;
    private readonly Instruction _anchor;
    private readonly MoveType _moveType;

    internal MatchedValueSite(ILContext context, MatchedValue value, Instruction anchor, MoveType moveType)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        _anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
        _moveType = moveType;
    }

    /// <summary>
    /// 将 matched value 作为第一个 delegate parameter 消费，并默认把 delegate return value
    /// 留在 stack 上。使用返回对象可以显式 store 或 discard 它。
    /// </summary>
    public DelegateCallResult Transform<TDelegate>(TDelegate callback,
        Action<DelegateArguments>? additionalArguments = null)
        where TDelegate : Delegate
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));

        var arguments = BuildArguments(additionalArguments);
        var invoke = DelegateSignature.RequireInvoke(typeof(TDelegate));
        DelegateSignature.RequireParameterCount(invoke, arguments.Count + 1, "Transform");
        if (invoke.ReturnType == typeof(void))
            throw new ArgumentException("Transform requires a non-void delegate return type.", nameof(callback));

        var cursor = CreateCursor();
        arguments.Emit(cursor);
        cursor.EmitDelegate(callback);
        return new DelegateCallResult(cursor, hasValue: true);
    }

    /// <summary>
    /// 调用 void delegate，并把 matched value 的 duplicate 作为第一个 parameter。
    /// 原始 matched value 会留在 stack 上供原始 consumer 使用。
    /// </summary>
    public void Observe<TDelegate>(TDelegate callback,
        Action<DelegateArguments>? additionalArguments = null)
        where TDelegate : Delegate
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));

        var arguments = BuildArguments(additionalArguments);
        var invoke = DelegateSignature.RequireInvoke(typeof(TDelegate));
        DelegateSignature.RequireParameterCount(invoke, arguments.Count + 1, "Observe");
        if (invoke.ReturnType != typeof(void))
            throw new ArgumentException("Observe requires a void delegate.", nameof(callback));

        var cursor = CreateCursor();
        cursor.Emit(OpCodes.Dup);
        arguments.Emit(cursor);
        cursor.EmitDelegate(callback);
    }

    /// <summary>
    /// 调用 delegate，但不消费 matched value。non-void result 会留在 stack 上，
    /// 除非在返回对象上请求 StoreLocal、StoreArgument 或 Discard。
    /// </summary>
    public DelegateCallResult Call<TDelegate>(TDelegate callback,
        Action<DelegateArguments>? arguments = null)
        where TDelegate : Delegate
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));

        var sources = BuildArguments(arguments);
        var invoke = DelegateSignature.RequireInvoke(typeof(TDelegate));
        DelegateSignature.RequireParameterCount(invoke, sources.Count, "Call");

        var cursor = CreateCursor();
        sources.Emit(cursor);
        cursor.EmitDelegate(callback);
        return new DelegateCallResult(cursor, invoke.ReturnType != typeof(void));
    }

    private DelegateArguments BuildArguments(Action<DelegateArguments>? configure)
    {
        var result = new DelegateArguments(_context);
        configure?.Invoke(result);
        return result;
    }

    private ILCursor CreateCursor() => new ILCursor(_context).Goto(_anchor, _moveType);

}

internal static class DelegateSignature
{
    public static MethodInfo RequireInvoke(Type delegateType)
        => delegateType.GetMethod("Invoke")
           ?? throw new ArgumentException($"'{delegateType}' is not a delegate type.");

    public static void RequireParameterCount(MethodInfo invoke, int actual, string operation)
    {
        var expected = invoke.GetParameters().Length;
        if (expected != actual)
            throw new ArgumentException($"{operation} delegate expects {expected} parameters, but the site supplies {actual}.");
    }
}

/// <summary>收集 delegate call 前的 additional argument load。</summary>
public sealed class DelegateArguments
{
    private readonly ILContext _context;
    private readonly List<IArgumentSource> _sources = new();

    internal DelegateArguments(ILContext context) => _context = context;

    internal int Count => _sources.Count;

    /// <summary>加载目标 method 的 instance argument。</summary>
    public DelegateArguments This()
    {
        if (!_context.Method.HasThis)
            throw new InvalidOperationException("The target method has no instance argument.");
        _sources.Add(new EmitSource(static cursor => cursor.Emit(OpCodes.Ldarg_0)));
        return this;
    }

    /// <summary>按 index 加载显式 Cecil parameter；该 index 不包含 <c>this</c>。</summary>
    public DelegateArguments Arg(int parameterIndex)
    {
        if (parameterIndex < 0 || parameterIndex >= _context.Method.Parameters.Count)
            throw new ArgumentOutOfRangeException(nameof(parameterIndex));
        return Arg(_context.Method.Parameters[parameterIndex]);
    }

    /// <summary>加载显式 Cecil parameter。</summary>
    public DelegateArguments Arg(ParameterDefinition parameter)
    {
        if (parameter is null)
            throw new ArgumentNullException(nameof(parameter));
        if (!_context.Method.Parameters.Contains(parameter))
            throw new ArgumentException("The parameter does not belong to the target method.", nameof(parameter));
        _sources.Add(new EmitSource(cursor => cursor.Emit(OpCodes.Ldarg, parameter)));
        return this;
    }

    /// <summary>加载由 pattern 捕获的 argument。</summary>
    public DelegateArguments Arg(MatchedArgument argument)
    {
        if (argument is null)
            throw new ArgumentNullException(nameof(argument));
        return argument.IsThis ? This() : Arg(argument.Parameter
            ?? throw new InvalidOperationException("The captured parameter could not be resolved."));
    }

    /// <summary>加载 local variable。</summary>
    public DelegateArguments Local(VariableDefinition variable)
    {
        if (variable is null)
            throw new ArgumentNullException(nameof(variable));
        if (!_context.Body.Variables.Contains(variable))
            throw new ArgumentException("The local does not belong to the target method body.", nameof(variable));
        _sources.Add(new EmitSource(cursor => cursor.Emit(OpCodes.Ldloc, variable)));
        return this;
    }

    /// <summary>加载由 pattern 捕获的 local。</summary>
    public DelegateArguments Local(MatchedLocal local)
        => Local(local?.Variable ?? throw new ArgumentNullException(nameof(local)));

    /// <summary>添加 CIL emitter 支持的 literal。</summary>
    public DelegateArguments Constant<T>(T value)
    {
        _sources.Add(new EmitSource(cursor => EmitConstant(cursor, value)));
        return this;
    }

    /// <summary>
    /// 为一个 argument 添加自定义 raw emission。callback 必须在 stack 上恰好留下一个 value。
    /// </summary>
    public DelegateArguments Emit(Action<ILCursor> emitter)
    {
        if (emitter is null)
            throw new ArgumentNullException(nameof(emitter));
        _sources.Add(new EmitSource(emitter));
        return this;
    }

    internal void Emit(ILCursor cursor)
    {
        foreach (var source in _sources)
            source.Emit(cursor);
    }

    private static void EmitConstant<T>(ILCursor cursor, T value)
    {
        switch (value)
        {
            case null:
                cursor.Emit(OpCodes.Ldnull);
                break;
            case bool b:
                cursor.Emit(OpCodes.Ldc_I4, b ? 1 : 0);
                break;
            case byte b:
                cursor.Emit(OpCodes.Ldc_I4, b);
                break;
            case sbyte b:
                cursor.Emit(OpCodes.Ldc_I4, b);
                break;
            case short s:
                cursor.Emit(OpCodes.Ldc_I4, s);
                break;
            case ushort s:
                cursor.Emit(OpCodes.Ldc_I4, s);
                break;
            case int i:
                cursor.Emit(OpCodes.Ldc_I4, i);
                break;
            case uint i:
                cursor.Emit(OpCodes.Ldc_I4, unchecked((int)i));
                break;
            case long l:
                cursor.Emit(OpCodes.Ldc_I8, l);
                break;
            case ulong l:
                cursor.Emit(OpCodes.Ldc_I8, unchecked((long)l));
                break;
            case float f:
                cursor.Emit(OpCodes.Ldc_R4, f);
                break;
            case double d:
                cursor.Emit(OpCodes.Ldc_R8, d);
                break;
            case char c:
                cursor.Emit(OpCodes.Ldc_I4, c);
                break;
            case string s:
                cursor.Emit(OpCodes.Ldstr, s);
                break;
            case Enum e:
                EmitEnumConstant(cursor, e);
                break;
            default:
                throw new NotSupportedException($"Constant type '{typeof(T)}' is not supported. Use DelegateArguments.Emit for custom IL.");
        }
    }

    private static void EmitEnumConstant(ILCursor cursor, Enum value)
    {
        // 所有受支持 runtime 都可用 Enum.GetUnderlyingType。保留 64-bit underlying value，
        // 不要把所有 enum 都路由到 Convert.ToInt32。
        var underlying = Enum.GetUnderlyingType(value.GetType());
        switch (Type.GetTypeCode(underlying))
        {
            case TypeCode.SByte:
            case TypeCode.Int16:
            case TypeCode.Int32:
                cursor.Emit(OpCodes.Ldc_I4, Convert.ToInt32(value));
                return;
            case TypeCode.Byte:
            case TypeCode.UInt16:
            case TypeCode.UInt32:
                cursor.Emit(OpCodes.Ldc_I4, unchecked((int)Convert.ToUInt32(value)));
                return;
            case TypeCode.Int64:
                cursor.Emit(OpCodes.Ldc_I8, Convert.ToInt64(value));
                return;
            case TypeCode.UInt64:
                cursor.Emit(OpCodes.Ldc_I8, unchecked((long)Convert.ToUInt64(value)));
                return;
            default:
                throw new NotSupportedException($"Enum underlying type '{underlying}' is not supported.");
        }
    }

    private interface IArgumentSource
    {
        void Emit(ILCursor cursor);
    }

    private sealed class EmitSource : IArgumentSource
    {
        private readonly Action<ILCursor> _emitter;
        public EmitSource(Action<ILCursor> emitter) => _emitter = emitter;
        public void Emit(ILCursor cursor) => _emitter(cursor);
    }
}

/// <summary>
/// 控制 delegate result 的 destination。什么都不做会有意让 value 留在 stack 上。
/// </summary>
public sealed class DelegateCallResult
{
    private readonly ILCursor _cursor;
    private readonly bool _hasValue;
    private bool _consumed;

    internal DelegateCallResult(ILCursor cursor, bool hasValue)
    {
        _cursor = cursor;
        _hasValue = hasValue;
    }

    /// <summary>把 returned value 存入 local。</summary>
    public void StoreLocal(VariableDefinition variable)
    {
        EnsureAvailable();
        if (variable is null)
            throw new ArgumentNullException(nameof(variable));
        if (!_cursor.Body.Variables.Contains(variable))
            throw new ArgumentException("The local does not belong to the target method body.", nameof(variable));
        _cursor.Emit(OpCodes.Stloc, variable);
        _consumed = true;
    }

    /// <summary>把 returned value 存入显式 method parameter。</summary>
    public void StoreArgument(ParameterDefinition parameter)
    {
        EnsureAvailable();
        if (parameter is null)
            throw new ArgumentNullException(nameof(parameter));
        if (!_cursor.Method.Parameters.Contains(parameter))
            throw new ArgumentException("The parameter does not belong to the target method.", nameof(parameter));
        _cursor.Emit(OpCodes.Starg, parameter);
        _consumed = true;
    }

    /// <summary>丢弃 returned value。</summary>
    public void Discard()
    {
        EnsureAvailable();
        _cursor.Emit(OpCodes.Pop);
        _consumed = true;
    }

    /// <summary>标记默认行为，也就是把 result 留在 stack 上，是有意选择。</summary>
    public void LeaveOnStack()
    {
        EnsureAvailable();
        _consumed = true;
    }

    private void EnsureAvailable()
    {
        if (!_hasValue)
            throw new InvalidOperationException("The delegate has no return value.");
        if (_consumed)
            throw new InvalidOperationException("The delegate result destination was already selected.");
    }
}
