using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;
using MonoWeaver.Utils;

namespace MonoWeaver.Cecil;

internal enum InsertPosition
{
    Before,
    After,
}

/// <summary>
/// 只依赖 Mono.Cecil 的 matcher/transform 入口。不会创建 delegate、动态方法、
/// AssemblyLoadContext 或加载目标程序集，适用于 net48 宿主和离线程序集改写。
/// </summary>
public static partial class PatternTransformExtensions
{
    public static CilMatchSet Match(this MethodDefinition method, ExpressionPattern pattern)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        return PatternMatcher.For(method).Find(pattern);
    }

    /// <summary>在该 value occurrence 的具体 use 后创建 transform site。</summary>
    public static MatchedValueSite AfterUse(this MatchedValue value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        return new MatchedValueSite(value.Method, value, value.AfterUseInstruction,
            InsertPosition.After);
    }

    /// <summary>在该 value 的原始 producer 后创建 transform site。</summary>
    public static MatchedValueSite AfterProducer(this MatchedValue value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        return new MatchedValueSite(value.Method, value, value.ProducerInstruction,
            InsertPosition.After);
    }

    /// <summary>在该 value expression 开始求值前创建普通 insertion site。</summary>
    public static MatchedEffectSite BeforeEvaluation(this MatchedValue value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        return new MatchedEffectSite(value.Method, value.FirstInstruction, InsertPosition.Before);
    }

    /// <summary>在 condition fragment 开始求值前创建普通 insertion site。</summary>
    public static MatchedEffectSite BeforeEvaluation(this MatchedCondition condition)
    {
        if (condition is null)
            throw new ArgumentNullException(nameof(condition));
        return new MatchedEffectSite(condition.Method, condition.EntryInstruction,
            InsertPosition.Before);
    }

    /// <summary>使用 static MethodReference 改写 condition；callback 首参数接收原 Boolean。</summary>
    public static CallResultPlan Transform(this MatchedCondition condition, MethodReference callback,
        Action<CallArguments>? additionalArguments = null)
        => Transform(condition, condition.Method, callback, additionalArguments);
    

    public static CallResultPlan Transform(this MatchedCondition condition, CilMethodSpec callback,
        Action<CallArguments>? additionalArguments = null)
    {
        if (condition is null)
            throw new ArgumentNullException(nameof(condition));
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return condition.Transform(callback.Resolve(condition.Method.Module), additionalArguments);
    }

    public static CallResultPlan Transform<TDelegate>(this MatchedCondition condition, TDelegate callback,
        Action<CallArguments>? additionalArguments = null)
        where TDelegate : Delegate
    {
        if (condition is null)
            throw new ArgumentNullException(nameof(condition));
        var call = CecilDelegateEmission.Prepare(condition.Method, callback);
        return Transform(condition, condition.Method, call, additionalArguments);
    }

    /// <summary>在 root 或指定 value capture 的具体 use 后创建 value site。</summary>
    public static MatchedValueSite AfterUse(this CilMatch match, string? captureName = null)
        => RequireValue(match, captureName).AfterUse();

    /// <summary>在 root 或指定 value capture 的原始 producer 后创建 value site。</summary>
    public static MatchedValueSite AfterProducer(this CilMatch match, string? captureName = null)
        => RequireValue(match, captureName).AfterProducer();

    /// <summary>在 root 或指定 value capture 开始求值前创建普通 insertion site。</summary>
    public static MatchedEffectSite BeforeEvaluation(this CilMatch match, string? captureName = null)
        => RequireValue(match, captureName).BeforeEvaluation();

    public static MatchedEffectSite Before(this CilMatch match)
    {
        if (match is null)
            throw new ArgumentNullException(nameof(match));
        return new MatchedEffectSite(match.Method, match.FirstInstruction, InsertPosition.Before);
    }

    public static MatchedEffectSite After(this CilMatch match)
    {
        if (match is null)
            throw new ArgumentNullException(nameof(match));
        if (match.Pattern.Kind == PatternKind.Condition)
        {
            throw new InvalidOperationException(
                "A branch-based condition has no single after-site. Use TransformCondition instead.");
        }
        return new MatchedEffectSite(match.Method, match.LastInstruction, InsertPosition.After);
    }

    public static CallResultPlan Transform(MatchedCondition condition, MethodDefinition method,
       MethodReference callback, Action<CallArguments>? additionalArguments)
    {
        if (condition is null)
            throw new ArgumentNullException(nameof(condition));
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        if (!ReferenceEquals(condition.Method, method))
            throw new ArgumentException("The captured condition does not belong to the target method.", nameof(condition));
        RequireReturn(callback.ReturnType, false, "Condition Transform");
        if (callback.ReturnType.MetadataType != MetadataType.Boolean)
            throw new ArgumentException("A condition transform callback must return System.Boolean.", nameof(callback));
        if (!condition.CanRewrite)
            throw new NotSupportedException(condition.RewriteFailureReason
                                            ?? "The captured condition cannot be safely rewritten.");

        var arguments = CallArguments.ConfigAndValidateCall(method, callback, additionalArguments,
            method.Module.TypeSystem.Boolean, implicitValueIsNull: false, "Condition Transform");
        return new CallResultPlan(method, () => ApplyConditionTransform(condition, method,
            arguments, CreateMethodCallEmitter(callback), extraStackSlots: 0));
    }

    public static CallResultPlan Transform(MatchedCondition condition, MethodDefinition method,
        CecilDelegateCall callback, Action<CallArguments>? additionalArguments)
    {
        if (condition is null)
            throw new ArgumentNullException(nameof(condition));
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        if (!ReferenceEquals(condition.Method, method))
            throw new ArgumentException("The captured condition does not belong to the target method.", nameof(condition));
        RequireReturn(callback.ReturnType, false, "Condition Transform");
        if (callback.ReturnType.MetadataType != MetadataType.Boolean)
            throw new ArgumentException("A condition transform callback must return System.Boolean.", nameof(callback));
        if (!condition.CanRewrite)
            throw new NotSupportedException(condition.RewriteFailureReason
                                            ?? "The captured condition cannot be safely rewritten.");

        var arguments = CallArguments.ConfigAndValidateCall(method, callback, additionalArguments,
            method.Module.TypeSystem.Boolean, implicitValueIsNull: false, "Condition Transform");
        return new CallResultPlan(method, () => ApplyConditionTransform(condition, method,
            arguments, _ => callback.CreateInstructions(), callback.ExtraStackSlots));
    }

}

/// <summary>普通 Cecil insertion point；不假定 stack 上已经有 matched value。</summary>
public sealed class MatchedEffectSite
{
    private readonly EmissionSite _site;

    internal MatchedEffectSite(MethodDefinition method, Instruction anchor, InsertPosition position)
        => _site = new EmissionSite(method, anchor, position);

    /// <summary>调用返回 void 的 static method。</summary>
    public CallResultPlan CallVoid(MethodReference callback, Action<CallArguments>? arguments = null)
    {
        PatternTransformExtensions.RequireReturn(callback, true, "CallVoid");
        var callArguments = CallArguments.ConfigAndValidateCall(_site.Method, callback, arguments,
            implicitValueType: null, implicitValueIsNull: false, "CallVoid");
        return new CallResultPlan(_site, callArguments, callback.ReturnType,
            PatternTransformExtensions.CreateMethodCallEmitter(callback), extraStackSlots: 0);
    }

    public CallResultPlan CallVoid(CilMethodSpec callback, Action<CallArguments>? arguments = null)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return CallVoid(callback.Resolve(_site.Method.Module), arguments);
    }

    public CallResultPlan CallVoid<TDelegate>(TDelegate callback, Action<CallArguments>? arguments = null)
        where TDelegate : Delegate
    {
        var call = CecilDelegateEmission.Prepare(_site.Method, callback);
        PatternTransformExtensions.RequireReturn(call.ReturnType, true, "CallVoid");
        var callArguments = CallArguments.ConfigAndValidateCall(_site.Method, call, arguments,
            implicitValueType: null, implicitValueIsNull: false, "CallVoid");
        return new CallResultPlan(_site, callArguments, call.ReturnType,
            _ => call.CreateInstructions(), call.ExtraStackSlots);
    }

    /// <summary>
    /// 创建 non-void call plan。直到调用 LeaveOnStack/Discard/StoreLocal/StoreArgument 之前，
    /// method body 不会被修改。
    /// </summary>
    public CallResultPlan CallValue(MethodReference callback, Action<CallArguments>? arguments = null)
    {
        PatternTransformExtensions.RequireReturn(callback, false, "CallValue");
        var callArguments = CallArguments.ConfigAndValidateCall(_site.Method, callback, arguments,
            implicitValueType: null, implicitValueIsNull: false, "CallValue");
        return new CallResultPlan(_site, callArguments, callback.ReturnType,
            PatternTransformExtensions.CreateMethodCallEmitter(callback), extraStackSlots: 0);
    }

    public CallResultPlan CallValue(CilMethodSpec callback, Action<CallArguments>? arguments = null)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return CallValue(callback.Resolve(_site.Method.Module), arguments);
    }

    public CallResultPlan CallValue<TDelegate>(TDelegate callback, Action<CallArguments>? arguments = null)
        where TDelegate : Delegate
    {
        var call = CecilDelegateEmission.Prepare(_site.Method, callback);
        PatternTransformExtensions.RequireReturn(call.ReturnType, false, "CallValue");
        var callArguments = CallArguments.ConfigAndValidateCall(_site.Method, call, arguments,
            implicitValueType: null, implicitValueIsNull: false, "CallValue");
        return new CallResultPlan(_site, callArguments, call.ReturnType,
            _ => call.CreateInstructions(), call.ExtraStackSlots);
    }
}



public sealed class MatchedValueSite
{
    private readonly EmissionSite _site;
    private readonly MatchedValue _value;

    internal MatchedValueSite(MethodDefinition method, MatchedValue value,
        Instruction anchor, InsertPosition position)
    {
        _value = value ?? throw new ArgumentNullException(nameof(value));
        if (!ReferenceEquals(_value.Method, method))
            throw new ArgumentException("The matched value does not belong to the target method.", nameof(value));
        _site = new EmissionSite(method, anchor, position);
    }


    public CallResultPlan Transform(MethodReference callback, Action<CallArguments>? additionalArguments = null)
    {
        var valueType = RequireValueType();
        PatternTransformExtensions.RequireReturn(callback, false, "Transform");
        PatternTransformExtensions.RequireAssignable(callback.ReturnType, valueType, actualIsNull: false,
            "Transform return value", "matched value type");
        var arguments = CallArguments.ConfigAndValidateCall(_value.Method, callback, additionalArguments,
            valueType, false, "Transform");
        return new CallResultPlan(_site, arguments, callback.ReturnType,
            PatternTransformExtensions.CreateMethodCallEmitter(callback), extraStackSlots: 0);
    }

    public CallResultPlan Transform(CilMethodSpec callback, Action<CallArguments>? additionalArguments = null)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return Transform(callback.Resolve(_site.Method.Module), additionalArguments);
    }

    public CallResultPlan Transform<TDelegate>(TDelegate callback, Action<CallArguments>? additionalArguments = null)
        where TDelegate : Delegate
    {
        var valueType = RequireValueType();
        var call = CecilDelegateEmission.Prepare(_site.Method, callback);
        PatternTransformExtensions.RequireReturn(call.ReturnType, false, "Transform");
        PatternTransformExtensions.RequireAssignable(call.ReturnType, valueType, actualIsNull: false,
            "Transform return value", "matched value type");
        var arguments = CallArguments.ConfigAndValidateCall(_value.Method, call, additionalArguments,
            valueType, false, "Transform");
        return new CallResultPlan(_site, arguments, call.ReturnType,
            _ => call.CreateInstructions(), call.ExtraStackSlots);
    }

    /// <summary>
    /// duplicate matched value 后调用返回 void 的 static callback；原值继续交给原 consumer。
    /// </summary>
    public CallResultPlan Observe(MethodReference callback, Action<CallArguments>? additionalArguments = null)
    {
        var valueType = RequireValueType();
        PatternTransformExtensions.RequireReturn(callback, true, "Observe");
        var arguments = CallArguments.ConfigAndValidateCall(_value.Method, callback, additionalArguments,
            valueType, false, "Observe");
        return new CallResultPlan(_site, arguments, callback.ReturnType,
            PatternTransformExtensions.CreateMethodCallEmitter(callback), extraStackSlots: 0, true);
    }

    public CallResultPlan Observe(CilMethodSpec callback, Action<CallArguments>? additionalArguments = null)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return Observe(callback.Resolve(_site.Method.Module), additionalArguments);
    }

    public CallResultPlan Observe<TDelegate>(TDelegate callback, Action<CallArguments>? additionalArguments = null)
        where TDelegate : Delegate
    {
        var valueType = RequireValueType();
        var call = CecilDelegateEmission.Prepare(_site.Method, callback);
        PatternTransformExtensions.RequireReturn(call.ReturnType, true, "Observe");
        var arguments = CallArguments.ConfigAndValidateCall(_value.Method, call, additionalArguments,
            valueType, false, "Observe");
        return new CallResultPlan(_site, arguments, call.ReturnType,
            _ => call.CreateInstructions(), call.ExtraStackSlots,  true);
    }

    private TypeReference RequireValueType()
        => _value.ValueType
           ?? throw new InvalidOperationException("The matched occurrence is an effect and has no stack value type.");
}

public sealed class CallResultPlan
{
    private record MehtodBodySnapShot(Instruction[] Instructions, OpCode[] OpCodes, object?[] Operands,
            VariableDefinition[] Variables, ExceptionHandler[] Handlers, int MaxStack);

    private enum DestBehaivor
    {
        None,
        LeaveOnStack,
        Discard,
        StoreLocal,
        StoreArgument
    }

    private readonly MethodDefinition _method;
    private readonly EmissionSite? _site;
    private readonly CallArguments? _arguments;
    private readonly TypeReference? _returnType;
    private readonly Func<ModuleDefinition, IReadOnlyList<Instruction>>? _callbackEmitter;
    private readonly Action? _customApply;
    private readonly int _extraStackSlots;
    private readonly bool _emitDupBeforeArguments;

    private DestBehaivor _destination = DestBehaivor.LeaveOnStack;
    private VariableDefinition? _destinationLocal;
    private ParameterDefinition? _destinationArgument;

    private bool _applied;

    internal CallResultPlan(EmissionSite site, CallArguments arguments, TypeReference returnType,
        Func<ModuleDefinition, IReadOnlyList<Instruction>> callbackEmitter, int extraStackSlots,
        bool emitDupBeforeArguments = false)
    {
        _site = site ?? throw new ArgumentNullException(nameof(site));
        _method = site.Method;
        _arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        _returnType = returnType ?? throw new ArgumentNullException(nameof(returnType));
        _callbackEmitter = callbackEmitter ?? throw new ArgumentNullException(nameof(callbackEmitter));
        if (extraStackSlots < 0)
            throw new ArgumentOutOfRangeException(nameof(extraStackSlots));
        _extraStackSlots = extraStackSlots;
        _emitDupBeforeArguments = emitDupBeforeArguments;
    }

    internal CallResultPlan(MethodDefinition method, Action customApply)
    {
        _method = method ?? throw new ArgumentNullException(nameof(method));
        _customApply = customApply ?? throw new ArgumentNullException(nameof(customApply));
    }

    public TypeReference? ReturnType => _returnType;

    public CallResultPlan LeaveOnStack()
    {
        RequireValueResult();
        _destination = DestBehaivor.LeaveOnStack;
        _destinationLocal = null;
        _destinationArgument = null;
        return this;
    }

    public CallResultPlan Discard()
    {
        RequireValueResult();
        _destination = DestBehaivor.Discard;
        _destinationLocal = null;
        _destinationArgument = null;
        return this;
    }


    public CallResultPlan StoreLocal(int variableIndex)
    {
        if (_method.Body.Variables.Count <= variableIndex || variableIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(variableIndex));
        return StoreLocal(_method.Body.Variables[variableIndex]);
    }

    public CallResultPlan StoreLocal(VariableDefinition variable)
    {
        if (variable is null)
            throw new ArgumentNullException(nameof(variable));
        RequireValueResult();
        if (!_method.Body.Variables.Contains(variable))
            throw new ArgumentException("The local does not belong to the target method body.", nameof(variable));
        PatternTransformExtensions.RequireAssignable(_returnType!, variable.VariableType, actualIsNull: false,
            "call return value", "local type");
        _destination = DestBehaivor.StoreLocal;
        _destinationLocal = variable;
        _destinationArgument = null;
        return this;
    }

    public CallResultPlan StoreArgument(int parameterIndex)
    {
        if (_method.Parameters.Count <= parameterIndex || parameterIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(parameterIndex));
        return StoreArgument(_method.Parameters[parameterIndex]);
    }

    public CallResultPlan StoreArgument(ParameterDefinition parameter)
    {
        if (parameter is null)
            throw new ArgumentNullException(nameof(parameter));
        RequireValueResult();
        if (!_method.Parameters.Contains(parameter))
            throw new ArgumentException("The parameter does not belong to the target method.", nameof(parameter));
        PatternTransformExtensions.RequireAssignable(_returnType!, parameter.ParameterType, actualIsNull: false,
            "call return value", "argument type");
        _destination = DestBehaivor.StoreArgument;
        _destinationLocal = null;
        _destinationArgument = parameter;
        return this;
    }

    public CallResultPlan Apply()
    {
        if (_applied)
            throw new InvalidOperationException("This call plan was already applied.");

        if (_customApply is not null)
        {
            _customApply();
            _applied = true;
            return this;
        }

        //防止越界
        _method.Body.MaxStackSize = checked(_method.Body.MaxStackSize + AdditionalStackSlots);
        BranchModifier.ExpandShortBranches(_method.Body);
        var processor = _method.Body.GetILProcessor();

        var current = _site!.Anchor;
        Instruction? firstInserted = null;
        Emit(processor, ref current, ref firstInserted);

        _applied = true;
        return this;
    }

    public CallResultPlan ApplyWithVerify(VerifyOptions options)
    {
        var snapshot = CaptureBody(_method.Body);
        try
        {
            Apply();
            _method.Verify(options).ThrowIfHasErrors();
            return this;
        }
        catch
        {
            RestoreBody(_method.Body, snapshot);
            _arguments?.ResetStores();
            _applied = false;
            throw;
        }
    }

    internal int AdditionalStackSlots
    {
        get
        {
            var slots = _extraStackSlots + (_arguments?.ArgPlans.Count ?? 0);
            if (_emitDupBeforeArguments)
                slots++;
            if (_site is not null && _returnType is not null && !_returnType.IsVoid()
                && !_emitDupBeforeArguments)
                slots = Math.Max(slots, 1);
            return slots;
        }
    }

    internal void Emit(ILProcessor processor, ref Instruction current, ref Instruction? firstInserted)
    {
        if (_site is null || _arguments is null || _callbackEmitter is null)
            throw new InvalidOperationException("This call plan does not target a single emission site.");

        foreach (var argument in _arguments.ArgPlans)
            argument.EmitStore(processor, _site.Position);

        if (_emitDupBeforeArguments)
            _site.Insert(processor, Instruction.Create(OpCodes.Dup), ref current, ref firstInserted);

        foreach (var argument in _arguments.ArgPlans)
            argument.EmitLoad(_site, processor, ref current, ref firstInserted);

        foreach (var instruction in _callbackEmitter(_site.Method.Module))
            _site.Insert(processor, instruction, ref current, ref firstInserted);

        foreach (var instruction in CreateDestinationInstructions())
            _site.Insert(processor, instruction, ref current, ref firstInserted);
    }

    private IReadOnlyList<Instruction> CreateDestinationInstructions()
    {
        if (_returnType is null || _returnType.IsVoid())
            return Array.Empty<Instruction>();

        return _destination switch
        {
            DestBehaivor.LeaveOnStack => Array.Empty<Instruction>(),
            DestBehaivor.Discard => new[] { Instruction.Create(OpCodes.Pop) },
            DestBehaivor.StoreLocal => new[] { Instruction.Create(OpCodes.Stloc, _destinationLocal!) },
            DestBehaivor.StoreArgument => new[] { Instruction.Create(OpCodes.Starg, _destinationArgument!) },
            _ => throw new InvalidOperationException("Unknown call result destination."),
        };
    }

    private void RequireValueResult()
    {
        if (_customApply is not null)
            throw new InvalidOperationException("This plan does not expose a single stack result.");
        if (_returnType is null || _returnType.IsVoid())
            throw new InvalidOperationException("This call does not return a value.");
    }

    private static MehtodBodySnapShot CaptureBody(MethodBody body)
    {
        var instructions = body.Instructions.ToArray();
        return new MehtodBodySnapShot(instructions,
            instructions.Select(static instruction => instruction.OpCode).ToArray(),
            instructions.Select(static instruction => instruction.Operand).ToArray(),
            body.Variables.ToArray(),
            body.ExceptionHandlers.Select(CloneHandler).ToArray(),
            body.MaxStackSize);
    }

    private static void RestoreBody(MethodBody body, MehtodBodySnapShot snapshot)
    {
        body.Instructions.Clear();
        for (var i = 0; i < snapshot.Instructions.Length; i++)
        {
            var instruction = snapshot.Instructions[i];
            instruction.OpCode = snapshot.OpCodes[i];
            instruction.Operand = snapshot.Operands[i];
            body.Instructions.Add(instruction);
        }

        body.Variables.Clear();
        foreach (var variable in snapshot.Variables)
            body.Variables.Add(variable);

        body.ExceptionHandlers.Clear();
        foreach (var handler in snapshot.Handlers)
            body.ExceptionHandlers.Add(CloneHandler(handler));

        body.MaxStackSize = snapshot.MaxStack;
    }

    private static ExceptionHandler CloneHandler(ExceptionHandler handler)
    {
        return new ExceptionHandler(handler.HandlerType)
        {
            TryStart = handler.TryStart,
            TryEnd = handler.TryEnd,
            HandlerStart = handler.HandlerStart,
            HandlerEnd = handler.HandlerEnd,
            FilterStart = handler.FilterStart,
            CatchType = handler.CatchType,
        };

    }
}
/// <summary>
/// non-void call 的待提交结果。选择 destination 前不修改 IL；每个 plan 只能提交一次。
/// </summary>
/// <summary>描述 static MethodReference 调用前需要显式加载的参数。</summary>
public sealed class CallArguments
{
    public static CallArguments ConfigAndValidateCall(MethodDefinition target, MethodReference callback,
      Action<CallArguments>? configure, TypeReference? implicitValueType,
      bool implicitValueIsNull, string operation = "Call")
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        if (callback.HasThis)
            throw new ArgumentException($"{operation} currently accepts only static MethodReference callbacks.", nameof(callback));
        if (callback.Name is ".ctor" or ".cctor")
            throw new ArgumentException($"{operation} cannot call a constructor.", nameof(callback));
        if (callback.CallingConvention == MethodCallingConvention.VarArg)
            throw new NotSupportedException("VarArg callback methods are not supported.");
        if (callback is not GenericInstanceMethod && callback.GenericParameters.Count != 0)
            throw new ArgumentException("Open generic callback methods are not supported. Supply a GenericInstanceMethod.", nameof(callback));

        if (callback.ContainsGenericParameter)
            throw new ArgumentException("Callback signatures containing unbound generic parameters are not supported.", nameof(callback));

        return ConfigAndValidateCall(target,
            callback.Parameters.Select(static parameter => parameter.ParameterType).ToArray(),
            configure, implicitValueType, implicitValueIsNull, operation);
    }

    internal static CallArguments ConfigAndValidateCall(MethodDefinition target, CecilDelegateCall callback,
      Action<CallArguments>? configure, TypeReference? implicitValueType,
      bool implicitValueIsNull, string operation = "Call")
    {
        if (target is null)
            throw new ArgumentNullException(nameof(target));
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        if (!target.HasBody)
            throw new ArgumentException("The target method has no IL body.", nameof(target));
        return ConfigAndValidateCall(target, callback.ParameterTypes, configure,
            implicitValueType, implicitValueIsNull, operation);
    }

    private static CallArguments ConfigAndValidateCall(MethodDefinition target,
        IReadOnlyList<TypeReference> parameterTypes, Action<CallArguments>? configure,
        TypeReference? implicitValueType, bool implicitValueIsNull, string operation)
    {
        var arguments = new CallArguments(target);
        configure?.Invoke(arguments);

        var implicitCount = implicitValueType is null ? 0 : 1;
        var suppliedCount = implicitCount + arguments.ArgPlans.Count;
        if (parameterTypes.Count != suppliedCount)
        {
            throw new ArgumentException(
                $"{operation} callback expects {parameterTypes.Count} parameters, but the site supplies {suppliedCount}.");
        }

        var parameterOffset = 0;
        if (implicitValueType is not null)
        {
            PatternTransformExtensions.RequireAssignable(implicitValueType, parameterTypes[0], implicitValueIsNull,
                "matched value", "callback parameter 0");
            parameterOffset = 1;
        }

        for (var i = 0; i < arguments.ArgPlans.Count; i++)
        {
            var source = arguments.ArgPlans[i];
            PatternTransformExtensions.RequireAssignable(source.ArgType, parameterTypes[i + parameterOffset], source.IsNull,
                $"argument source {i}", $"callback parameter {i + parameterOffset}");
        }

        return arguments;
    }

    private readonly MethodDefinition _target;
    private readonly List<IArgumenPlan> _argPlans = new();

    internal CallArguments(MethodDefinition target)
        => _target = target ?? throw new ArgumentNullException(nameof(target));

    internal IReadOnlyList<IArgumenPlan> ArgPlans => _argPlans;

    internal void ResetStores()
    {
        foreach (var plan in _argPlans)
            plan.ResetStore();
    }

    internal IReadOnlyList<Instruction> CreateLoadInstructions(ModuleDefinition module)
    {
        var instructions = new List<Instruction>();
        foreach (var plan in _argPlans)
            instructions.AddRange(plan.CreateLoad(module));
        return instructions;
    }

    public CallArguments This()
    {
        if (!_target.HasThis)
            throw new InvalidOperationException("The target method has no instance argument.");

        TypeReference thisType = _target.DeclaringType;
        if (_target.DeclaringType.IsValueType)
            thisType = new ByReferenceType(_target.DeclaringType);

        _argPlans.Add(new ArgumenPlan(thisType, isNull: false,
            static _ => new[] { Instruction.Create(OpCodes.Ldarg_0) }));
        return this;
    }


    public CallArguments Capture(MatchedValue value)
    {
        if (!ReferenceEquals(value.Method, _target))
            throw new ArgumentException("The captured argument belongs to a different method.", nameof(value));
        _argPlans.Add(new CapturedArgumentPlan(value));
        return this;

    }


    /// <summary>显式参数 index，不包含 this。</summary>
    public CallArguments Arg(int parameterIndex)
    {
        if (parameterIndex < 0 || parameterIndex >= _target.Parameters.Count)
            throw new ArgumentOutOfRangeException(nameof(parameterIndex));
        return Arg(_target.Parameters[parameterIndex]);
    }

    public CallArguments Arg(ParameterDefinition parameter)
    {
        if (parameter is null)
            throw new ArgumentNullException(nameof(parameter));
        if (!_target.Parameters.Contains(parameter))
            throw new ArgumentException("The parameter does not belong to the target method.", nameof(parameter));

        _argPlans.Add(new ArgumenPlan(parameter.ParameterType, isNull: false,
            _ => new[] { Instruction.Create(OpCodes.Ldarg, parameter) }));
        return this;
    }

    public CallArguments Arg(MatchedArgument argument)
    {
        if (argument is null)
            throw new ArgumentNullException(nameof(argument));
        if (!ReferenceEquals(argument.Method, _target))
            throw new ArgumentException("The captured argument belongs to a different method.", nameof(argument));
        return argument.IsThis
            ? This()
            : Arg(argument.Parameter
                  ?? throw new InvalidOperationException("The captured parameter could not be resolved."));
    }

    public CallArguments Local(VariableDefinition variable)
    {
        if (variable is null)
            throw new ArgumentNullException(nameof(variable));
        if (!_target.Body.Variables.Contains(variable))
            throw new ArgumentException("The local does not belong to the target method body.", nameof(variable));

        _argPlans.Add(new ArgumenPlan(variable.VariableType, isNull: false,
            _ => new[] { Instruction.Create(OpCodes.Ldloc, variable) }));
        return this;
    }

    public CallArguments Local(MatchedLocal local)
    {
        if (local is null)
            throw new ArgumentNullException(nameof(local));
        if (!ReferenceEquals(local.Method, _target))
            throw new ArgumentException("The captured local belongs to a different method.", nameof(local));
        return Local(local.Variable);
    }

    public CallArguments Null(TypeReference? nominalType = null)
    {
        var type = nominalType is null ? _target.Module.TypeSystem.Object : Import(nominalType);
        _argPlans.Add(new ArgumenPlan(type, isNull: true,
            static _ => new[] { Instruction.Create(OpCodes.Ldnull) }));
        return this;
    }

    public CallArguments Null(CilTypeSpec? nominalType = null)
        => Null(nominalType?.Resolve(_target.Module));
    

    public CallArguments Constant(bool value)
        => AddConstant(_target.Module.TypeSystem.Boolean, () => Instruction.Create(OpCodes.Ldc_I4, value ? 1 : 0));
    public CallArguments Constant(byte value)
        => AddConstant(_target.Module.TypeSystem.Byte, () => Instruction.Create(OpCodes.Ldc_I4, value));
    public CallArguments Constant(sbyte value)
        => AddConstant(_target.Module.TypeSystem.SByte, () => Instruction.Create(OpCodes.Ldc_I4, value));
    public CallArguments Constant(short value)
        => AddConstant(_target.Module.TypeSystem.Int16, () => Instruction.Create(OpCodes.Ldc_I4, value));
    public CallArguments Constant(ushort value)
        => AddConstant(_target.Module.TypeSystem.UInt16, () => Instruction.Create(OpCodes.Ldc_I4, value));
    public CallArguments Constant(int value)
        => AddConstant(_target.Module.TypeSystem.Int32, () => Instruction.Create(OpCodes.Ldc_I4, value));
    public CallArguments Constant(uint value)
        => AddConstant(_target.Module.TypeSystem.UInt32, () => Instruction.Create(OpCodes.Ldc_I4, unchecked((int)value)));
    public CallArguments Constant(long value)
        => AddConstant(_target.Module.TypeSystem.Int64, () => Instruction.Create(OpCodes.Ldc_I8, value));
    public CallArguments Constant(ulong value)
        => AddConstant(_target.Module.TypeSystem.UInt64, () => Instruction.Create(OpCodes.Ldc_I8, unchecked((long)value)));
    public CallArguments Constant(float value)
        => AddConstant(_target.Module.TypeSystem.Single, () => Instruction.Create(OpCodes.Ldc_R4, value));
    public CallArguments Constant(double value)
        => AddConstant(_target.Module.TypeSystem.Double, () => Instruction.Create(OpCodes.Ldc_R8, value));
    public CallArguments Constant(char value)
        => AddConstant(_target.Module.TypeSystem.Char, () => Instruction.Create(OpCodes.Ldc_I4, value));
    public CallArguments Constant(string value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        return AddConstant(_target.Module.TypeSystem.String, () => Instruction.Create(OpCodes.Ldstr, value));
    }

    /// <summary>
    /// 为 enum/小整数等显式声明 nominal parameter type；value 仍按 int32 压栈。
    /// </summary>
    public CallArguments ConstantI4(int value, TypeReference nominalType)
    {
        if (nominalType is null)
            throw new ArgumentNullException(nameof(nominalType));
        return AddConstant(Import(nominalType), () => Instruction.Create(OpCodes.Ldc_I4, value));
    }

    public CallArguments ConstantI4(int value, CilTypeSpec nominalType)
    {
        if (nominalType is null)
            throw new ArgumentNullException(nameof(nominalType));
        return ConstantI4(value, nominalType.Resolve(_target.Module));
    }

    private CallArguments AddConstant(TypeReference type, Func<Instruction> factory)
    {
        _argPlans.Add(new ArgumenPlan(type, isNull: false,
            _ => new[] { factory() }));
        return this;
    }

    private TypeReference Import(TypeReference type)
        => ReferenceEquals(type.Module, _target.Module) ? type : _target.Module.ImportReference(type);
}


internal sealed class EmissionSite
{
    public EmissionSite(MethodDefinition method, Instruction anchor, InsertPosition position)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
        Position = position;
    }

    public MethodDefinition Method { get; }
    public Instruction Anchor { get; }
    public InsertPosition Position { get; }

    internal void Insert(ILProcessor processor, Instruction instruction,
        ref Instruction current, ref Instruction? firstInserted)
    {
        if (Position == InsertPosition.After)
        {
            processor.InsertAfter(current, instruction);
            current = instruction;
            firstInserted ??= instruction;
            return;
        }

        if (firstInserted is null)
        {
            processor.InsertBefore(Anchor, instruction);
            firstInserted = instruction;
            current = instruction;
            return;
        }

        processor.InsertAfter(current, instruction);
        current = instruction;
    }
}


internal interface IArgumenPlan
{
    public void EmitLoad(EmissionSite site, ILProcessor il,
        ref Instruction start, ref Instruction? firstInserted);

    public void EmitStore(ILProcessor il, InsertPosition position);

    public TypeReference ArgType { get;}

    public bool IsNull { get; }

    public IReadOnlyList<Instruction> CreateLoad(ModuleDefinition module);

    public void ResetStore();
}

internal class NullArgumenPlan : IArgumenPlan
{
    public void EmitLoad(EmissionSite site, ILProcessor il,
        ref Instruction start, ref Instruction? firstInserted)
    {
        site.Insert(il, Instruction.Create(OpCodes.Ldnull), ref start, ref firstInserted);
    }

    public void EmitStore(ILProcessor il, InsertPosition position) { }

    public TypeReference ArgType => null!;

    public bool IsNull => true;

    public IReadOnlyList<Instruction> CreateLoad(ModuleDefinition module)
        => new[] { Instruction.Create(OpCodes.Ldnull) };

    public void ResetStore() { }
}

internal class ArgumenPlan : IArgumenPlan
{
    private readonly Func<ModuleDefinition, IReadOnlyList<Instruction>> _emitter;

    public ArgumenPlan(TypeReference valueType, bool isNull,
        Func<ModuleDefinition, IReadOnlyList<Instruction>> emitter)
    {
        ArgType = valueType ?? throw new ArgumentNullException(nameof(valueType));
        IsNull = isNull;
        _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
    }

    public TypeReference ArgType { get; }
    public bool IsNull { get; }
    public virtual void EmitLoad(EmissionSite site, ILProcessor il,
        ref Instruction start, ref Instruction? firstInserted)
    {
        foreach (var inst in _emitter(il.Body.Method.Module))
            site.Insert(il, inst, ref start, ref firstInserted);
    }

    public void EmitStore(ILProcessor il, InsertPosition position) { }

    public IReadOnlyList<Instruction> CreateLoad(ModuleDefinition module) => _emitter(module);

    public void ResetStore() { }
}

internal class CapturedArgumentPlan : IArgumenPlan
{
    public CapturedArgumentPlan(MatchedValue captureValue)
    {
        Value = captureValue ?? throw new ArgumentNullException(nameof(captureValue));
        if (Value.ValueType is null)
             throw new ArgumentNullException(nameof(captureValue));
        ArgType = Value.ValueType;
    }

    public MatchedValue Value { get; }

    public TypeReference ArgType { get; }

    public bool IsNull => false;

    private VariableDefinition? _variable;

    public void EmitLoad(EmissionSite site, ILProcessor il,
        ref Instruction start, ref Instruction? firstInserted)
    {
        if (_variable == null)
            throw new NullReferenceException(nameof(_variable));

        if (il.Body.Variables[_variable.Index] != _variable)
            throw new ArgumentOutOfRangeException(nameof(_variable));

        site.Insert(il, Instruction.Create(OpCodes.Ldloc, _variable), ref start, ref firstInserted);
    }

    public void EmitStore(ILProcessor il, InsertPosition position)
    {
        if (_variable is not null && Value.Method.Body.Variables.Contains(_variable))
            return;

        _variable = new VariableDefinition(Value.ValueType);
        Value.Method.Body.Variables.Add(_variable);
        var dup = Instruction.Create(OpCodes.Dup);
        var store = Instruction.Create(OpCodes.Stloc, _variable);
        il.InsertAfter(Value.AfterUseInstruction, dup);
        il.InsertAfter(dup, store);

    }

    public IReadOnlyList<Instruction> CreateLoad(ModuleDefinition module)
    {
        if (_variable == null)
            throw new NullReferenceException(nameof(_variable));
        return new[] { Instruction.Create(OpCodes.Ldloc, _variable) };
    }

    public void ResetStore()
        => _variable = null;
}


internal static class BranchModifier
{
    public static void RetargetIncoming(MethodBody body,
        Instruction oldTarget, Instruction newTarget)
    {
        foreach (var instruction in body.Instructions)
        {
            if (ReferenceEquals(instruction, newTarget))
                continue;

            if (ReferenceEquals(instruction.Operand, oldTarget))
            {
                instruction.Operand = newTarget;
            }
            else if (instruction.Operand is Instruction[] targets)
            {
                for (var i = 0; i < targets.Length; i++)
                {
                    if (ReferenceEquals(targets[i], oldTarget))
                        targets[i] = newTarget;
                }
            }
        }

        foreach (var handler in body.ExceptionHandlers)
        {
            if (ReferenceEquals(handler.TryStart, oldTarget)) handler.TryStart = newTarget;
            if (ReferenceEquals(handler.TryEnd, oldTarget)) handler.TryEnd = newTarget;
            if (ReferenceEquals(handler.HandlerStart, oldTarget)) handler.HandlerStart = newTarget;
            if (ReferenceEquals(handler.HandlerEnd, oldTarget)) handler.HandlerEnd = newTarget;
            if (ReferenceEquals(handler.FilterStart, oldTarget)) handler.FilterStart = newTarget;
        }
    }

    public static void ExpandShortBranches(MethodBody body)
    {
        if (body is null)
            throw new ArgumentNullException(nameof(body));

        foreach (var instruction in body.Instructions)
        {
            instruction.OpCode = instruction.OpCode.Code switch
            {
                Code.Br_S => OpCodes.Br,
                Code.Brfalse_S => OpCodes.Brfalse,
                Code.Brtrue_S => OpCodes.Brtrue,
                Code.Beq_S => OpCodes.Beq,
                Code.Bge_S => OpCodes.Bge,
                Code.Bge_Un_S => OpCodes.Bge_Un,
                Code.Bgt_S => OpCodes.Bgt,
                Code.Bgt_Un_S => OpCodes.Bgt_Un,
                Code.Ble_S => OpCodes.Ble,
                Code.Ble_Un_S => OpCodes.Ble_Un,
                Code.Blt_S => OpCodes.Blt,
                Code.Blt_Un_S => OpCodes.Blt_Un,
                Code.Bne_Un_S => OpCodes.Bne_Un,
                Code.Leave_S => OpCodes.Leave,
                _ => instruction.OpCode,
            };
        }
    }
}

public static partial class PatternTransformExtensions
{
    internal static void RequireReturn(MethodReference callback, bool requireVoid, string operation)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        RequireReturn(callback.ReturnType, requireVoid, operation);
    }

    internal static void RequireReturn(TypeReference returnType, bool requireVoid, string operation)
    {
        if (returnType is null)
            throw new ArgumentNullException(nameof(returnType));
        var isVoid = returnType.IsVoid();
        if (requireVoid != isVoid)
        {
            var requirement = requireVoid ? "Void" : "a non-Void value";
            throw new ArgumentException($"{operation} requires a callback returning {requirement}.");
        }
    }

 

    internal static Func<ModuleDefinition, IReadOnlyList<Instruction>> CreateMethodCallEmitter(
        MethodReference callback)
        => module =>
        {
            var imported = ReferenceEquals(callback.Module, module)
                ? callback
                : module.ImportReference(callback);
            return new[] { Instruction.Create(OpCodes.Call, imported) };
        };

    internal static void RequireAssignable(TypeReference actual, TypeReference expected,
        bool actualIsNull, string actualName, string expectedName)
    {
        if (StackType.Create(actual).StackValueEqualsTo(StackType.Create(expected)))
            return;

        throw new ArgumentException(
            $"{actualName} '{actual.FullName}' is not compatible with {expectedName} '{expected.FullName}'.");
    }


    private static void ApplyConditionTransform(MatchedCondition condition, MethodDefinition method,
        CallArguments arguments, Func<ModuleDefinition, IReadOnlyList<Instruction>> callbackEmitter,
        int extraStackSlots)
    {
        if (condition is null)
            throw new ArgumentNullException(nameof(condition));
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (!ReferenceEquals(condition.Method, method))
            throw new ArgumentException("The captured condition does not belong to the target method.", nameof(condition));
        if (!condition.CanRewrite)
            throw new InvalidOperationException(condition.RewriteFailureReason
                ?? "The captured condition cannot be safely rewritten.");

        var fragment = condition.Fragment;
        var trueTarget = fragment.TrueContinuation.Leader;
        var falseTarget = fragment.FalseContinuation.Leader;
        var exits = fragment.TrueExits.Select(static edge => (Edge: edge, Value: true))
            .Concat(fragment.FalseExits.Select(static edge => (Edge: edge, Value: false)))
            .ToArray();

        if (exits.Length == 0)
            throw new InvalidOperationException("The captured condition has no exit edges.");

        var groups = exits.GroupBy(static exit => exit.Edge.From).ToArray();
        foreach (var group in groups)
        {
            var fallExits = group.Where(static exit => exit.Edge.IsFallThrough).ToArray();
            var branchExits = group.Where(static exit => !exit.Edge.IsFallThrough).ToArray();
            if (fallExits.Length > 1 || branchExits.Length > 1)
            {
                throw new NotSupportedException(
                    $"Condition block IL_{group.Key.Leader.Offset:X4} has an unsupported exit shape.");
            }

            EnsureAnchor(method, group.Key.Terminator);
            EnsureSameExceptionRegion(method.Body, group.Key.Terminator, trueTarget);
            EnsureSameExceptionRegion(method.Body, group.Key.Terminator, falseTarget);
        }

        method.Body.MaxStackSize = checked(method.Body.MaxStackSize + 1
            + arguments.ArgPlans.Count + extraStackSlots);
        BranchModifier.ExpandShortBranches(method.Body);
        var processor = method.Body.GetILProcessor();
        foreach (var argument in arguments.ArgPlans)
            argument.EmitStore(processor, InsertPosition.After);

        foreach (var group in groups)
        {
            var fallExits = group.Where(static exit => exit.Edge.IsFallThrough).ToArray();
            var branchExits = group.Where(static exit => !exit.Edge.IsFallThrough).ToArray();
            var emitted = new List<Instruction>();

            if (fallExits.Length == 0 && branchExits.Length != 0)
            {
                var allFallThrough = group.Key.Successors.SingleOrDefault(static edge => edge.IsFallThrough)
                                     ?? throw new InvalidOperationException("The source condition has no fall-through edge.");
                EnsureSameExceptionRegion(method.Body, group.Key.Terminator, allFallThrough.To.Leader);
                emitted.Add(Instruction.Create(OpCodes.Br, allFallThrough.To.Leader));
            }

            if (fallExits.Length != 0)
                emitted.AddRange(CreateConditionBridge(method, arguments, callbackEmitter,
                    fallExits[0].Value, trueTarget, falseTarget));

            Instruction? branchBridge = null;
            if (branchExits.Length != 0)
            {
                var bridge = CreateConditionBridge(method, arguments, callbackEmitter,
                    branchExits[0].Value, trueTarget, falseTarget);
                branchBridge = bridge[0];
                emitted.AddRange(bridge);
            }

            InsertAfter(processor, group.Key.Terminator, emitted);
            if (branchBridge is not null)
                RetargetTakenBranch(group.Key.Terminator, branchBridge);
        }
    }

    private static IReadOnlyList<Instruction> CreateConditionBridge(MethodDefinition method,
        CallArguments arguments, Func<ModuleDefinition, IReadOnlyList<Instruction>> callbackEmitter,
        bool originalValue, Instruction trueTarget, Instruction falseTarget)
    {
        var result = new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldc_I4, originalValue ? 1 : 0),
        };
        result.AddRange(arguments.CreateLoadInstructions(method.Module));
        result.AddRange(callbackEmitter(method.Module));
        result.Add(Instruction.Create(OpCodes.Brtrue, trueTarget));
        result.Add(Instruction.Create(OpCodes.Br, falseTarget));
        return result;
    }

    private static void InsertAfter(ILProcessor processor, Instruction anchor,
        IReadOnlyList<Instruction> instructions)
    {
        var current = anchor;
        foreach (var instruction in instructions)
        {
            processor.InsertAfter(current, instruction);
            current = instruction;
        }
    }

    private static MatchedValue RequireValue(CilMatch match, string? captureName)
    {
        if (match is null)
            throw new ArgumentNullException(nameof(match));
        return captureName is null ? match.Value() : match.Value(captureName);
    }



    private static void RetargetTakenBranch(Instruction terminator, Instruction newTarget)
    {
        if (terminator.Operand is Instruction)
        {
            terminator.Operand = newTarget;
            return;
        }

        throw new NotSupportedException(
            $"Branch operand type '{terminator.Operand?.GetType()}' is not supported for condition rewriting.");
    }

    private static void EnsureAnchor(MethodDefinition method, Instruction instruction)
    {
        if (!method.HasBody || !method.Body.Instructions.Contains(instruction))
            throw new InvalidOperationException("The condition match is stale. Re-run the matcher after modifying IL.");
    }

    private static void EnsureSameExceptionRegion(MethodBody body,
        Instruction source, Instruction target)
    {
        if (GetRegionSignature(body, source) == GetRegionSignature(body, target))
            return;

        throw new NotSupportedException(
            $"Condition rewriting would branch from IL_{source.Offset:X4} to IL_{target.Offset:X4} across an exception-region boundary.");
    }

    private static string GetRegionSignature(MethodBody body, Instruction instruction)
    {
        var index = body.Instructions.IndexOf(instruction);
        var parts = new List<string>();
        for (var i = 0; i < body.ExceptionHandlers.Count; i++)
        {
            var handler = body.ExceptionHandlers[i];
            Add("T", handler.TryStart, handler.TryEnd);
            Add("F", handler.FilterStart, handler.HandlerStart);
            Add("H", handler.HandlerStart, handler.HandlerEnd);

            void Add(string kind, Instruction? start, Instruction? end)
            {
                if (start is null)
                    return;
                var startIndex = body.Instructions.IndexOf(start);
                var endIndex = end is null ? body.Instructions.Count : body.Instructions.IndexOf(end);
                if (index >= startIndex && index < endIndex)
                    parts.Add(i + ":" + kind);
            }
        }
        return string.Join("|", parts);
    }

}
