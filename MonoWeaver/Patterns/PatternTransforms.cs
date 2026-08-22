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
/// Pattern match 的统一改写 API。根 match 已按 Value/Effect/Condition 强类型区分；
/// 所有操作直接挂在对应语义目标上，不再暴露 producer/use/branch-exit 等实现点位。
/// </summary>
public static partial class PatternTransformExtensions
{
    public static CilMatchSet<ValueMatch<T>> Match<T>(this MethodDefinition method, ValuePattern<T> pattern)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        return PatternMatcher.For(method).Find(pattern);
    }

    public static CilMatchSet<ValueMatch> Match(this MethodDefinition method, ValuePattern pattern)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        return PatternMatcher.For(method).Find(pattern);
    }

    public static CilMatchSet<EffectMatch> Match(this MethodDefinition method, EffectPattern pattern)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        return PatternMatcher.For(method).Find(pattern);
    }

    public static CilMatchSet<ConditionMatch> Match(this MethodDefinition method, ConditionPattern pattern)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        return PatternMatcher.For(method).Find(pattern);
    }

    public static PatternMatcher For(this MethodDefinition method)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        return PatternMatcher.For(method);
    }

    // ---------------------------- common before hooks ----------------------------

    /// <summary>在 value occurrence 开始求值前调用 callback；非 Void 返回值由 RewritePlan 决定去向。</summary>
    public static RewritePlan Before(this ValueTarget target, MethodReference callback,
        Action<CallArguments>? arguments = null)
        => CreateCallBefore(Require(target), target.FirstInstruction, callback, arguments,
            "Value.Before");

    public static RewritePlan Before(this ValueTarget target, CilMethodSpec callback,
        Action<CallArguments>? arguments = null)
    {
        target = Require(target);
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return target.Before(callback.Resolve(target.Method.Module), arguments);
    }

    public static RewritePlan Before<TDelegate>(this ValueTarget target, TDelegate callback,
        Action<CallArguments>? arguments = null)
        where TDelegate : Delegate
        => CreateDelegateCall(Require(target), target.FirstInstruction,
            InsertPosition.Before, callback, arguments, "Value.Before");

    public static RewritePlan Before(this ValueTarget target, Action callback)
        => target.Before<Action>(callback);

    /// <summary>在 value 求值完成后、原 consumer 使用它之前调用 callback；非 Void 返回值由 RewritePlan 决定去向。</summary>
    public static RewritePlan After(this ValueTarget target, MethodReference callback,
        Action<CallArguments>? arguments = null)
    {
        target = RequireValueRewritable(target, "Value.After");
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        var callArguments = CallArguments.ConfigAndValidateCall(target.Method, callback, arguments,
            implicitValueType: null, implicitValueIsNull: false, "Value.After");
        var site = new EmissionSite(target.Method, target.ResultInstruction, InsertPosition.After);
        callArguments.ValidateForSite(site, "Value.After");
        return new RewritePlan(site, callArguments, callback.ReturnType,
            CreateMethodCallEmitter(callback), extraStackSlots: 0);
    }

    public static RewritePlan After(this ValueTarget target, CilMethodSpec callback,
        Action<CallArguments>? arguments = null)
    {
        target = Require(target);
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return target.After(callback.Resolve(target.Method.Module), arguments);
    }

    public static RewritePlan After<TDelegate>(this ValueTarget target, TDelegate callback,
        Action<CallArguments>? arguments = null)
        where TDelegate : Delegate
        => CreateDelegateCall(RequireValueRewritable(target, "Value.After"), target.ResultInstruction,
            InsertPosition.After, callback, arguments, "Value.After");

    public static RewritePlan After(this ValueTarget target, Action callback)
        => target.After<Action>(callback);

    /// <summary>在 effect 开始前调用 callback；非 Void 返回值由 RewritePlan 决定去向。</summary>
    public static RewritePlan Before(this EffectTarget target, MethodReference callback,
        Action<CallArguments>? arguments = null)
        => CreateCallBefore(Require(target), target.FirstInstruction, callback, arguments,
            "Effect.Before");

    public static RewritePlan Before(this EffectTarget target, CilMethodSpec callback,
        Action<CallArguments>? arguments = null)
    {
        target = Require(target);
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return target.Before(callback.Resolve(target.Method.Module), arguments);
    }

    public static RewritePlan Before<TDelegate>(this EffectTarget target, TDelegate callback,
        Action<CallArguments>? arguments = null)
        where TDelegate : Delegate
        => CreateDelegateCall(Require(target), target.FirstInstruction,
            InsertPosition.Before, callback, arguments, "Effect.Before");

    public static RewritePlan Before(this EffectTarget target, Action callback)
        => target.Before<Action>(callback);

    /// <summary>在 condition 开始求值前调用 callback；非 Void 返回值由 RewritePlan 决定去向。</summary>
    public static RewritePlan Before(this ConditionTarget target, MethodReference callback,
        Action<CallArguments>? arguments = null)
        => CreateCallBefore(Require(target), target.FirstInstruction, callback, arguments,
            "Condition.Before");

    public static RewritePlan Before(this ConditionTarget target, CilMethodSpec callback,
        Action<CallArguments>? arguments = null)
    {
        target = Require(target);
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return target.Before(callback.Resolve(target.Method.Module), arguments);
    }

    public static RewritePlan Before<TDelegate>(this ConditionTarget target, TDelegate callback,
        Action<CallArguments>? arguments = null)
        where TDelegate : Delegate
        => CreateDelegateCall(Require(target), target.FirstInstruction,
            InsertPosition.Before, callback, arguments, "Condition.Before");

    public static RewritePlan Before(this ConditionTarget target, Action callback)
        => target.Before<Action>(callback);

    // ---------------------------- value ----------------------------

    /// <summary>
    /// callback 的第一个参数接收当前 occurrence 的原值，返回值成为新的 stack value。
    /// </summary>
    public static RewritePlan Transform(this ValueTarget target, MethodReference callback,
        Action<CallArguments>? additionalArguments = null)
    {
        target = RequireValueRewritable(target, "Value.Transform");
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        RequireReturn(callback, requireVoid: false, "Value.Transform");
        RequireAssignable(callback.ReturnType, target.ValueType, actualIsNull: false,
            "transform return value", "matched value type");

        var arguments = CallArguments.ConfigAndValidateCall(target.Method, callback,
            additionalArguments, target.ValueType, implicitValueIsNull: false, "Value.Transform");
        var site = new EmissionSite(target.Method, target.ResultInstruction, InsertPosition.After);
        arguments.ValidateForSite(site, "Value.Transform");
        return new RewritePlan(site, arguments, callback.ReturnType,
            CreateMethodCallEmitter(callback), extraStackSlots: 0);
    }

    public static RewritePlan Transform(this ValueTarget target, CilMethodSpec callback,
        Action<CallArguments>? additionalArguments = null)
    {
        target = Require(target);
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return target.Transform(callback.Resolve(target.Method.Module), additionalArguments);
    }

    public static RewritePlan Transform<TDelegate>(this ValueTarget target, TDelegate callback,
        Action<CallArguments>? additionalArguments = null)
        where TDelegate : Delegate
    {
        target = RequireValueRewritable(target, "Value.Transform");
        var call = CecilDelegateEmission.Prepare(target.Method,
            callback ?? throw new ArgumentNullException(nameof(callback)));
        return CreateValueTransform(target, call, additionalArguments, "Value.Transform");
    }

    public static RewritePlan Transform<T>(this ValueMatch<T> target, Func<T, T> callback)
        => ((ValueTarget)Require(target)).Transform<Func<T, T>>(callback);

    /// <summary>把原值复制给 callback，原值继续交给原 consumer；callback 返回值可 Discard 或 Store。</summary>
    public static RewritePlan Observe(this ValueTarget target, MethodReference callback,
        Action<CallArguments>? additionalArguments = null)
    {
        target = RequireValueRewritable(target, "Value.Observe");
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        var arguments = CallArguments.ConfigAndValidateCall(target.Method, callback,
            additionalArguments, target.ValueType, implicitValueIsNull: false, "Value.Observe");
        var site = new EmissionSite(target.Method, target.ResultInstruction, InsertPosition.After);
        arguments.ValidateForSite(site, "Value.Observe");
        return new RewritePlan(site, arguments, callback.ReturnType,
            CreateMethodCallEmitter(callback), extraStackSlots: 0,
            emitDupBeforeArguments: true, allowLeaveOnStack: false);
    }

    public static RewritePlan Observe(this ValueTarget target, CilMethodSpec callback,
        Action<CallArguments>? additionalArguments = null)
    {
        target = Require(target);
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return target.Observe(callback.Resolve(target.Method.Module), additionalArguments);
    }

    public static RewritePlan Observe<TDelegate>(this ValueTarget target, TDelegate callback,
        Action<CallArguments>? additionalArguments = null)
        where TDelegate : Delegate
    {
        target = RequireValueRewritable(target, "Value.Observe");
        var call = CecilDelegateEmission.Prepare(target.Method,
            callback ?? throw new ArgumentNullException(nameof(callback)));
        return CreateValueObserve(target, call, additionalArguments, "Value.Observe");
    }

    public static RewritePlan Observe<T>(this ValueMatch<T> target, Action<T> callback)
        => ((ValueTarget)Require(target)).Observe<Action<T>>(callback);

    /// <summary>用一段自包含、最终留下一个值的 IL 替换当前 value occurrence。</summary>
    public static RewritePlan Replace(this ValueTarget target, Instruction first,
        params Instruction[] remaining)
        => Replace(target, JoinReplacement(first, remaining));

    public static RewritePlan Replace(this ValueTarget target, IEnumerable<Instruction> replacement,
        int extraStackSlots = 0)
    {
        if (replacement is null)
            throw new ArgumentNullException(nameof(replacement));
        return Replace(target, _ => replacement, extraStackSlots);
    }

    public static RewritePlan Replace(this ValueTarget target,
        Func<ModuleDefinition, IEnumerable<Instruction>> replacement, int extraStackSlots = 0)
    {
        target = RequireValueRewritable(target, "Value.Replace");
        if (replacement is null)
            throw new ArgumentNullException(nameof(replacement));
        if (extraStackSlots < 0)
            throw new ArgumentOutOfRangeException(nameof(extraStackSlots));

        return new RewritePlan(target.Method, () => ApplyLinearReplacement(target.Method,
            target.FirstInstruction, target.ResultInstruction, replacement,
            expectedFinalDepth: 1, extraStackSlots, "Value.Replace"));
    }

    /// <summary>跳过原 value expression，改为调用一个无隐式原值参数的 producer callback。</summary>
    public static RewritePlan Replace(this ValueTarget target, MethodReference callback,
        Action<CallArguments>? arguments = null)
    {
        target = RequireValueRewritable(target, "Value.Replace");
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        RequireReturn(callback, requireVoid: false, "Value.Replace");
        RequireAssignable(callback.ReturnType, target.ValueType, actualIsNull: false,
            "replacement return value", "matched value type");

        var callArguments = CallArguments.ConfigAndValidateCall(target.Method, callback, arguments,
            implicitValueType: null, implicitValueIsNull: false, "Value.Replace");
        callArguments.ValidateForReplacement(target.FirstInstruction,
            target.ResultInstruction, "Value.Replace");
        return CreateCallbackReplacement(target.Method, target.FirstInstruction,
            target.ResultInstruction, callArguments,
            CreateMethodCallEmitter(callback), extraStackSlots: 0,
            expectedFinalDepth: 1, "Value.Replace");
    }

    public static RewritePlan Replace(this ValueTarget target, CilMethodSpec callback,
        Action<CallArguments>? arguments = null)
    {
        target = Require(target);
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return target.Replace(callback.Resolve(target.Method.Module), arguments);
    }

    public static RewritePlan Replace<TDelegate>(this ValueTarget target, TDelegate callback,
        Action<CallArguments>? arguments = null)
        where TDelegate : Delegate
    {
        target = RequireValueRewritable(target, "Value.Replace");
        var call = CecilDelegateEmission.Prepare(target.Method,
            callback ?? throw new ArgumentNullException(nameof(callback)));
        RequireReturn(call.ReturnType, requireVoid: false, "Value.Replace");
        RequireAssignable(call.ReturnType, target.ValueType, actualIsNull: false,
            "replacement return value", "matched value type");
        var callArguments = CallArguments.ConfigAndValidateCall(target.Method, call, arguments,
            implicitValueType: null, implicitValueIsNull: false, "Value.Replace");
        callArguments.ValidateForReplacement(target.FirstInstruction,
            target.ResultInstruction, "Value.Replace");
        return CreateCallbackReplacement(target.Method, target.FirstInstruction,
            target.ResultInstruction, callArguments,
            _ => call.CreateInstructions(), call.ExtraStackSlots,
            expectedFinalDepth: 1, "Value.Replace", call.PrepareForApply);
    }

    public static RewritePlan Replace<T>(this ValueMatch<T> target, Func<T> callback)
        => ((ValueTarget)Require(target)).Replace<Func<T>>(callback);

    // ---------------------------- effect ----------------------------

    /// <summary>在完整 effect 执行完之后调用 callback；非 Void 返回值由 RewritePlan 决定去向。</summary>
    public static RewritePlan After(this EffectTarget target, MethodReference callback,
        Action<CallArguments>? arguments = null)
    {
        target = Require(target);
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        var callArguments = CallArguments.ConfigAndValidateCall(target.Method, callback, arguments,
            implicitValueType: null, implicitValueIsNull: false, "Effect.After");
        var site = new EmissionSite(target.Method, target.LastInstruction, InsertPosition.After);
        callArguments.ValidateForSite(site, "Effect.After");
        return new RewritePlan(site, callArguments, callback.ReturnType,
            CreateMethodCallEmitter(callback), extraStackSlots: 0);
    }

    public static RewritePlan After(this EffectTarget target, CilMethodSpec callback,
        Action<CallArguments>? arguments = null)
    {
        target = Require(target);
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return target.After(callback.Resolve(target.Method.Module), arguments);
    }

    public static RewritePlan After<TDelegate>(this EffectTarget target, TDelegate callback,
        Action<CallArguments>? arguments = null)
        where TDelegate : Delegate
        => CreateDelegateCall(Require(target), target.LastInstruction,
            InsertPosition.After, callback, arguments, "Effect.After");

    public static RewritePlan After(this EffectTarget target, Action callback)
        => target.After<Action>(callback);

    /// <summary>用一段自包含、最终不留下 stack value 的 IL 替换整个 effect。</summary>
    public static RewritePlan Replace(this EffectTarget target, Instruction first,
        params Instruction[] remaining)
        => Replace(target, JoinReplacement(first, remaining));

    public static RewritePlan Replace(this EffectTarget target, IEnumerable<Instruction> replacement,
        int extraStackSlots = 0)
    {
        if (replacement is null)
            throw new ArgumentNullException(nameof(replacement));
        return Replace(target, _ => replacement, extraStackSlots);
    }

    public static RewritePlan Replace(this EffectTarget target,
        Func<ModuleDefinition, IEnumerable<Instruction>> replacement, int extraStackSlots = 0)
    {
        target = Require(target);
        if (replacement is null)
            throw new ArgumentNullException(nameof(replacement));
        if (extraStackSlots < 0)
            throw new ArgumentOutOfRangeException(nameof(extraStackSlots));

        return new RewritePlan(target.Method, () => ApplyLinearReplacement(target.Method,
            target.FirstInstruction, target.LastInstruction, replacement,
            expectedFinalDepth: 0, extraStackSlots, "Effect.Replace"));
    }

    /// <summary>跳过原 effect，改为调用一个 void callback。</summary>
    public static RewritePlan Replace(this EffectTarget target, MethodReference callback,
        Action<CallArguments>? arguments = null)
    {
        target = Require(target);
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        RequireReturn(callback, requireVoid: true, "Effect.Replace");
        var callArguments = CallArguments.ConfigAndValidateCall(target.Method, callback, arguments,
            implicitValueType: null, implicitValueIsNull: false, "Effect.Replace");
        callArguments.ValidateForReplacement(target.FirstInstruction,
            target.LastInstruction, "Effect.Replace");
        return CreateCallbackReplacement(target.Method, target.FirstInstruction,
            target.LastInstruction, callArguments,
            CreateMethodCallEmitter(callback), extraStackSlots: 0,
            expectedFinalDepth: 0, "Effect.Replace");
    }

    public static RewritePlan Replace(this EffectTarget target, CilMethodSpec callback,
        Action<CallArguments>? arguments = null)
    {
        target = Require(target);
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return target.Replace(callback.Resolve(target.Method.Module), arguments);
    }

    public static RewritePlan Replace<TDelegate>(this EffectTarget target, TDelegate callback,
        Action<CallArguments>? arguments = null)
        where TDelegate : Delegate
    {
        target = Require(target);
        var call = CecilDelegateEmission.Prepare(target.Method,
            callback ?? throw new ArgumentNullException(nameof(callback)));
        RequireReturn(call.ReturnType, requireVoid: true, "Effect.Replace");
        var callArguments = CallArguments.ConfigAndValidateCall(target.Method, call, arguments,
            implicitValueType: null, implicitValueIsNull: false, "Effect.Replace");
        callArguments.ValidateForReplacement(target.FirstInstruction,
            target.LastInstruction, "Effect.Replace");
        return CreateCallbackReplacement(target.Method, target.FirstInstruction,
            target.LastInstruction, callArguments,
            _ => call.CreateInstructions(), call.ExtraStackSlots,
            expectedFinalDepth: 0, "Effect.Replace", call.PrepareForApply);
    }

    public static RewritePlan Replace(this EffectTarget target, Action callback)
        => target.Replace<Action>(callback);

    /// <summary>删除 effect。为保持所有 incoming target 有效，内部会留下一个 nop anchor。</summary>
    public static RewritePlan Remove(this EffectTarget target)
    {
        target = Require(target);
        return new RewritePlan(target.Method, () => ApplyLinearReplacement(target.Method,
            target.FirstInstruction, target.LastInstruction,
            _ => Array.Empty<Instruction>(), expectedFinalDepth: 0, extraStackSlots: 0,
            "Effect.Remove", allowEmptyReplacement: true));
    }

    // ---------------------------- condition ----------------------------

    /// <summary>callback 接收原逻辑结果并返回新的 Boolean decision。</summary>
    public static RewritePlan Transform(this ConditionTarget target, MethodReference callback,
        Action<CallArguments>? additionalArguments = null)
    {
        target = Require(target);
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        RequireBooleanReturn(callback.ReturnType, "Condition.Transform");
        RequireConditionRewrite(target, "transform");

        var arguments = CallArguments.ConfigAndValidateCall(target.Method, callback,
            additionalArguments, target.Method.Module.TypeSystem.Boolean,
            implicitValueIsNull: false, "Condition.Transform");
        arguments.ValidateForConditionExits(target, "Condition.Transform");
        return new RewritePlan(target.Method, () =>
        {
            arguments.ValidateForConditionExits(target, "Condition.Transform");
            arguments.MaterializeCapturedValues(target.Method.Body.GetILProcessor());
            ApplyConditionTransform(target, arguments,
                CreateMethodCallEmitter(callback), extraStackSlots: 0);
        }, arguments: arguments);
    }

    public static RewritePlan Transform(this ConditionTarget target, CilMethodSpec callback,
        Action<CallArguments>? additionalArguments = null)
    {
        target = Require(target);
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return target.Transform(callback.Resolve(target.Method.Module), additionalArguments);
    }

    public static RewritePlan Transform<TDelegate>(this ConditionTarget target, TDelegate callback,
        Action<CallArguments>? additionalArguments = null)
        where TDelegate : Delegate
    {
        target = Require(target);
        var call = CecilDelegateEmission.Prepare(target.Method,
            callback ?? throw new ArgumentNullException(nameof(callback)));
        RequireBooleanReturn(call.ReturnType, "Condition.Transform");
        RequireConditionRewrite(target, "transform");
        var arguments = CallArguments.ConfigAndValidateCall(target.Method, call,
            additionalArguments,
            target.Method.Module.TypeSystem.Boolean, implicitValueIsNull: false,
            "Condition.Transform");
        arguments.ValidateForConditionExits(target, "Condition.Transform");
        return new RewritePlan(target.Method, () =>
        {
            arguments.ValidateForConditionExits(target, "Condition.Transform");
            arguments.MaterializeCapturedValues(target.Method.Body.GetILProcessor());
            ApplyConditionTransform(target, arguments,
                _ => call.CreateInstructions(), call.ExtraStackSlots);
        }, call.PrepareForApply, arguments);
    }

    public static RewritePlan Transform(this ConditionTarget target, Func<bool, bool> callback)
        => Require(target).Transform<Func<bool, bool>>(callback);

    /// <summary>把原逻辑结果传给 callback，不改变 true/false continuation；callback 返回值可 Discard 或 Store。</summary>
    public static RewritePlan Observe(this ConditionTarget target, MethodReference callback,
        Action<CallArguments>? additionalArguments = null)
    {
        target = Require(target);
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        RequireConditionRewrite(target, "observe");

        var arguments = CallArguments.ConfigAndValidateCall(target.Method, callback,
            additionalArguments, target.Method.Module.TypeSystem.Boolean,
            implicitValueIsNull: false, "Condition.Observe");
        arguments.ValidateForConditionExits(target, "Condition.Observe");
        return new RewritePlan(target.Method, arguments, callback.ReturnType, plan =>
        {
            arguments.ValidateForConditionExits(target, "Condition.Observe");
            arguments.MaterializeCapturedValues(target.Method.Body.GetILProcessor());
            ApplyConditionObserve(target, arguments,
                CreateMethodCallEmitter(callback), extraStackSlots: 0, plan);
        }, allowLeaveOnStack: false);
    }

    public static RewritePlan Observe(this ConditionTarget target, CilMethodSpec callback,
        Action<CallArguments>? additionalArguments = null)
    {
        target = Require(target);
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return target.Observe(callback.Resolve(target.Method.Module), additionalArguments);
    }

    public static RewritePlan Observe<TDelegate>(this ConditionTarget target, TDelegate callback,
        Action<CallArguments>? additionalArguments = null)
        where TDelegate : Delegate
    {
        target = Require(target);
        var call = CecilDelegateEmission.Prepare(target.Method,
            callback ?? throw new ArgumentNullException(nameof(callback)));
        RequireConditionRewrite(target, "observe");
        var arguments = CallArguments.ConfigAndValidateCall(target.Method, call,
            additionalArguments,
            target.Method.Module.TypeSystem.Boolean, implicitValueIsNull: false,
            "Condition.Observe");
        arguments.ValidateForConditionExits(target, "Condition.Observe");
        return new RewritePlan(target.Method, arguments, call.ReturnType, plan =>
        {
            arguments.ValidateForConditionExits(target, "Condition.Observe");
            arguments.MaterializeCapturedValues(target.Method.Body.GetILProcessor());
            ApplyConditionObserve(target, arguments,
                _ => call.CreateInstructions(), call.ExtraStackSlots, plan);
        }, allowLeaveOnStack: false, beforeApply: call.PrepareForApply);
    }

    public static RewritePlan Observe(this ConditionTarget target, Action<bool> callback)
        => Require(target).Observe<Action<bool>>(callback);

    /// <summary>
    /// 跳过原 condition fragment，用一段自包含、最终留下一个 Boolean 的 IL 决定原 true/false continuation。
    /// </summary>
    public static RewritePlan Replace(this ConditionTarget target, Instruction first,
        params Instruction[] remaining)
        => Replace(target, JoinReplacement(first, remaining));

    public static RewritePlan Replace(this ConditionTarget target, IEnumerable<Instruction> replacement,
        int extraStackSlots = 0)
    {
        if (replacement is null)
            throw new ArgumentNullException(nameof(replacement));
        return Replace(target, _ => replacement, extraStackSlots);
    }

    public static RewritePlan Replace(this ConditionTarget target,
        Func<ModuleDefinition, IEnumerable<Instruction>> replacement, int extraStackSlots = 0)
    {
        target = Require(target);
        if (replacement is null)
            throw new ArgumentNullException(nameof(replacement));
        if (extraStackSlots < 0)
            throw new ArgumentOutOfRangeException(nameof(extraStackSlots));
        RequireConditionRewrite(target, "replace");
        return new RewritePlan(target.Method, () => ApplyConditionReplacement(target,
            replacement, extraStackSlots));
    }

    /// <summary>跳过原 condition，改为调用一个无原值参数、返回 Boolean 的 predicate。</summary>
    public static RewritePlan Replace(this ConditionTarget target, MethodReference callback,
        Action<CallArguments>? arguments = null)
    {
        target = Require(target);
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        RequireBooleanReturn(callback.ReturnType, "Condition.Replace");
        RequireConditionRewrite(target, "replace");
        var callArguments = CallArguments.ConfigAndValidateCall(target.Method, callback, arguments,
            implicitValueType: null, implicitValueIsNull: false, "Condition.Replace");
        callArguments.ValidateForConditionReplacement(target, "Condition.Replace");
        return new RewritePlan(target.Method, () =>
        {
            callArguments.ValidateForConditionReplacement(target, "Condition.Replace");
            callArguments.MaterializeCapturedValues(target.Method.Body.GetILProcessor());
            ApplyConditionReplacement(target,
                module => callArguments.CreateLoadInstructions(module)
                    .Concat(CreateMethodCallEmitter(callback)(module)), extraStackSlots: 0);
        }, arguments: callArguments);
    }

    public static RewritePlan Replace(this ConditionTarget target, CilMethodSpec callback,
        Action<CallArguments>? arguments = null)
    {
        target = Require(target);
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return target.Replace(callback.Resolve(target.Method.Module), arguments);
    }

    public static RewritePlan Replace<TDelegate>(this ConditionTarget target, TDelegate callback,
        Action<CallArguments>? arguments = null)
        where TDelegate : Delegate
    {
        target = Require(target);
        var call = CecilDelegateEmission.Prepare(target.Method,
            callback ?? throw new ArgumentNullException(nameof(callback)));
        RequireBooleanReturn(call.ReturnType, "Condition.Replace");
        RequireConditionRewrite(target, "replace");
        var callArguments = CallArguments.ConfigAndValidateCall(target.Method, call, arguments,
            implicitValueType: null, implicitValueIsNull: false, "Condition.Replace");
        callArguments.ValidateForConditionReplacement(target, "Condition.Replace");
        return new RewritePlan(target.Method, () =>
        {
            callArguments.ValidateForConditionReplacement(target, "Condition.Replace");
            callArguments.MaterializeCapturedValues(target.Method.Body.GetILProcessor());
            ApplyConditionReplacement(target,
                module => callArguments.CreateLoadInstructions(module)
                    .Concat(call.CreateInstructions()), call.ExtraStackSlots);
        }, call.PrepareForApply, callArguments);
    }

    public static RewritePlan Replace(this ConditionTarget target, Func<bool> callback)
        => Require(target).Replace<Func<bool>>(callback);

    // ---------------------------- API construction helpers ----------------------------

    private static RewritePlan CreateCallBefore(MatchCapture target, Instruction anchor,
        MethodReference callback, Action<CallArguments>? configure, string operation)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        var arguments = CallArguments.ConfigAndValidateCall(target.Method, callback, configure,
            implicitValueType: null, implicitValueIsNull: false, operation);
        var site = new EmissionSite(target.Method, anchor, InsertPosition.Before);
        arguments.ValidateForSite(site, operation);
        return new RewritePlan(site, arguments, callback.ReturnType,
            CreateMethodCallEmitter(callback), extraStackSlots: 0);
    }

    private static RewritePlan CreateDelegateCall<TDelegate>(MatchCapture target,
        Instruction anchor, InsertPosition position, TDelegate callback,
        Action<CallArguments>? configure, string operation)
        where TDelegate : Delegate
    {
        var call = CecilDelegateEmission.Prepare(target.Method,
            callback ?? throw new ArgumentNullException(nameof(callback)));
        return CreateDelegateCall(target.Method, anchor, position,
            call, configure, operation);
    }

    private static RewritePlan CreateDelegateCall(MethodDefinition method, Instruction anchor,
        InsertPosition position, CecilDelegateCall call,
        Action<CallArguments>? configure, string operation)
    {
        var arguments = CallArguments.ConfigAndValidateCall(method, call, configure,
            implicitValueType: null, implicitValueIsNull: false, operation);
        var site = new EmissionSite(method, anchor, position);
        arguments.ValidateForSite(site, operation);
        return new RewritePlan(site, arguments, call.ReturnType,
            _ => call.CreateInstructions(), call.ExtraStackSlots,
            beforeApply: call.PrepareForApply);
    }

    private static RewritePlan CreateValueTransform(ValueTarget target, CecilDelegateCall call,
        Action<CallArguments>? configure, string operation)
    {
        RequireReturn(call.ReturnType, requireVoid: false, operation);
        RequireAssignable(call.ReturnType, target.ValueType, actualIsNull: false,
            "transform return value", "matched value type");
        var arguments = CallArguments.ConfigAndValidateCall(target.Method, call, configure,
            target.ValueType, implicitValueIsNull: false, operation);
        var site = new EmissionSite(target.Method, target.ResultInstruction, InsertPosition.After);
        arguments.ValidateForSite(site, operation);
        return new RewritePlan(site, arguments, call.ReturnType,
            _ => call.CreateInstructions(), call.ExtraStackSlots,
            beforeApply: call.PrepareForApply);
    }

    private static RewritePlan CreateValueObserve(ValueTarget target, CecilDelegateCall call,
        Action<CallArguments>? configure, string operation)
    {
        var arguments = CallArguments.ConfigAndValidateCall(target.Method, call, configure,
            target.ValueType, implicitValueIsNull: false, operation);
        var site = new EmissionSite(target.Method, target.ResultInstruction, InsertPosition.After);
        arguments.ValidateForSite(site, operation);
        return new RewritePlan(site, arguments, call.ReturnType,
            _ => call.CreateInstructions(), call.ExtraStackSlots,
            emitDupBeforeArguments: true, allowLeaveOnStack: false,
            beforeApply: call.PrepareForApply);
    }

    private static RewritePlan CreateCallbackReplacement(MethodDefinition method,
        Instruction first, Instruction last, CallArguments arguments,
        Func<ModuleDefinition, IReadOnlyList<Instruction>> callbackEmitter,
        int extraStackSlots, int expectedFinalDepth, string operation,
        Action? beforeApply = null)
    {
        return new RewritePlan(method, () =>
        {
            arguments.ValidateForReplacement(first, last, operation);
            arguments.MaterializeCapturedValues(method.Body.GetILProcessor());
            ApplyLinearReplacement(method, first, last,
                module => arguments.CreateLoadInstructions(module).Concat(callbackEmitter(module)),
                expectedFinalDepth, extraStackSlots, operation);
        }, beforeApply, arguments);
    }

    private static IReadOnlyList<Instruction> JoinReplacement(Instruction first,
        IReadOnlyList<Instruction>? remaining)
    {
        if (first is null)
            throw new ArgumentNullException(nameof(first));
        if (remaining is null)
            throw new ArgumentNullException(nameof(remaining));

        var result = new Instruction[remaining.Count + 1];
        result[0] = first;
        for (var i = 0; i < remaining.Count; i++)
            result[i + 1] = remaining[i];
        return result;
    }

    private static TTarget Require<TTarget>(TTarget? target) where TTarget : class
        => target ?? throw new ArgumentNullException(nameof(target));

    //取地址（ldarga/ldloca/ldelema）到达的 occurrence 栈上是 managed pointer，
    //在该位置做占位改写会把指针当值处理，产生非法 IL。
    private static ValueTarget RequireValueRewritable(ValueTarget? target, string operation)
    {
        var required = Require(target);
        if (required.IsAddressBacked)
        {
            throw new NotSupportedException(
                $"{operation} cannot rewrite a value captured through an address instruction (ldarga/ldloca/ldelema): " +
                "the evaluation stack holds a managed pointer at this site, not the value. " +
                "Rewrite the enclosing expression instead, or pass the value to the callback via args.Arg/args.Local.");
        }
        return required;
    }
}
