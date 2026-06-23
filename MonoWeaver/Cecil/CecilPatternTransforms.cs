using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;
using MonoWeaver.Utils;

namespace MonoWeaver.Cecil;

/// <summary>
/// 只依赖 Mono.Cecil 的 matcher/transform 入口。不会创建 delegate、动态方法、
/// AssemblyLoadContext 或加载目标程序集，适用于 net48 宿主和离线程序集改写。
/// </summary>
public static class CecilPatternTransformExtensions
{
    public static CilMatchSet Match(this MethodDefinition method, ExpressionPattern pattern)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        return PatternMatcher.For(method).Find(pattern);
    }

    /// <summary>在该 value occurrence 的具体 use 后创建 transform site。</summary>
    public static CecilMatchedValueSite AfterUse(this MatchedValue value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        return new CecilMatchedValueSite(value.Method, value, value.AfterUseInstruction,
            CecilInsertPosition.After);
    }

    /// <summary>在该 value 的原始 producer 后创建 transform site。</summary>
    public static CecilMatchedValueSite AfterProducer(this MatchedValue value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        return new CecilMatchedValueSite(value.Method, value, value.ProducerInstruction,
            CecilInsertPosition.After);
    }

    /// <summary>在该 value expression 开始求值前创建普通 insertion site。</summary>
    public static CecilInsertionSite BeforeEvaluation(this MatchedValue value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        return new CecilInsertionSite(value.Method, value.FirstInstruction, CecilInsertPosition.Before);
    }

    /// <summary>在 condition fragment 开始求值前创建普通 insertion site。</summary>
    public static CecilInsertionSite BeforeEvaluation(this MatchedCondition condition)
    {
        if (condition is null)
            throw new ArgumentNullException(nameof(condition));
        return new CecilInsertionSite(condition.Method, condition.EntryInstruction,
            CecilInsertPosition.Before);
    }

    /// <summary>使用 static MethodReference 改写 condition；callback 首参数接收原 Boolean。</summary>
    public static void Transform(this MatchedCondition condition, MethodReference callback,
        Action<CecilCallArguments>? additionalArguments = null)
    {
        if (condition is null)
            throw new ArgumentNullException(nameof(condition));
        CecilConditionTransformer.Transform(condition, condition.Method, callback, additionalArguments);
    }

    public static void Transform(this MatchedCondition condition, CilMethodSpec callback,
        Action<CecilCallArguments>? additionalArguments = null)
    {
        if (condition is null)
            throw new ArgumentNullException(nameof(condition));
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        condition.Transform(callback.Resolve(condition.Method.Module), additionalArguments);
    }

    /// <summary>在 root 或指定 value capture 的具体 use 后创建 value site。</summary>
    public static CecilMatchedValueSite AfterUse(this CilMatch match, string? captureName = null)
        => RequireValue(match, captureName).AfterUse();

    /// <summary>在 root 或指定 value capture 的原始 producer 后创建 value site。</summary>
    public static CecilMatchedValueSite AfterProducer(this CilMatch match, string? captureName = null)
        => RequireValue(match, captureName).AfterProducer();

    /// <summary>在 root 或指定 value capture 开始求值前创建普通 insertion site。</summary>
    public static CecilInsertionSite BeforeEvaluation(this CilMatch match, string? captureName = null)
        => RequireValue(match, captureName).BeforeEvaluation();

    public static CecilInsertionSite Before(this CilMatch match)
    {
        if (match is null)
            throw new ArgumentNullException(nameof(match));
        return new CecilInsertionSite(match.Method, match.FirstInstruction, CecilInsertPosition.Before);
    }

    public static CecilInsertionSite After(this CilMatch match)
    {
        if (match is null)
            throw new ArgumentNullException(nameof(match));
        if (match.Pattern.Kind == PatternKind.Condition)
        {
            throw new InvalidOperationException(
                "A branch-based condition has no single after-site. Use TransformCondition instead.");
        }
        return new CecilInsertionSite(match.Method, match.LastInstruction, CecilInsertPosition.After);
    }

    public static void TransformCondition(this CilMatch match, MethodReference callback,
        Action<CecilCallArguments>? additionalArguments = null)
        => RequireCondition(match, captureName: null).Transform(callback, additionalArguments);

    public static void TransformCondition(this CilMatch match, string captureName, MethodReference callback,
        Action<CecilCallArguments>? additionalArguments = null)
        => RequireCondition(match, captureName).Transform(callback, additionalArguments);

    public static void TransformCondition(this CilMatch match, CilMethodSpec callback,
        Action<CecilCallArguments>? additionalArguments = null)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        RequireCondition(match, captureName: null).Transform(callback, additionalArguments);
    }

    public static void TransformCondition(this CilMatch match, string captureName, CilMethodSpec callback,
        Action<CecilCallArguments>? additionalArguments = null)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        RequireCondition(match, captureName).Transform(callback, additionalArguments);
    }

    private static MatchedValue RequireValue(CilMatch match, string? captureName)
    {
        if (match is null)
            throw new ArgumentNullException(nameof(match));
        return captureName is null ? match.Value() : match.Value(captureName);
    }

    private static MatchedCondition RequireCondition(CilMatch match, string? captureName)
    {
        if (match is null)
            throw new ArgumentNullException(nameof(match));
        return captureName is null ? match.Condition() : match.Condition(captureName);
    }
}

/// <summary>普通 Cecil insertion point；不假定 stack 上已经有 matched value。</summary>
public sealed class CecilInsertionSite
{
    private readonly CecilEmissionSite _site;

    internal CecilInsertionSite(MethodDefinition method, Instruction anchor, CecilInsertPosition position)
        => _site = new CecilEmissionSite(method, anchor, position);

    /// <summary>调用返回 void 的 static method。</summary>
    public void CallVoid(MethodReference callback, Action<CecilCallArguments>? arguments = null)
    {
        CecilCallBuilder.RequireReturn(callback, requireVoid: true, operation: "CallVoid");
        var call = CecilCallBuilder.Build(_site.Method, callback, arguments, implicitValueType: null,
            implicitValueIsNull: false, operation: "CallVoid");
        _site.Emit(call.Instructions, call.SourceCount);
    }

    public void CallVoid(CilMethodSpec callback, Action<CecilCallArguments>? arguments = null)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        CallVoid(callback.Resolve(_site.Method.Module), arguments);
    }

    /// <summary>
    /// 创建 non-void call plan。直到调用 LeaveOnStack/Discard/StoreLocal/StoreArgument 之前，
    /// method body 不会被修改。
    /// </summary>
    public CecilCallValuePlan CallValue(MethodReference callback, Action<CecilCallArguments>? arguments = null)
    {
        CecilCallBuilder.RequireReturn(callback, requireVoid: false, operation: "CallValue");
        var call = CecilCallBuilder.Build(_site.Method, callback, arguments, implicitValueType: null,
            implicitValueIsNull: false, operation: "CallValue");
        return new CecilCallValuePlan(_site, call.Instructions, call.Method.ReturnType,
            Math.Max(call.SourceCount, 1));
    }

    public CecilCallValuePlan CallValue(CilMethodSpec callback, Action<CecilCallArguments>? arguments = null)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return CallValue(callback.Resolve(_site.Method.Module), arguments);
    }
}

/// <summary>
/// matched value site。Transform 固定消费原值并留下兼容的 replacement；Observe 固定保留原值，
/// 因而不会出现“忘记处理返回值”导致的隐式 stack contract。
/// </summary>
public sealed class CecilMatchedValueSite
{
    private readonly CecilEmissionSite _site;
    private readonly MatchedValue _value;

    internal CecilMatchedValueSite(MethodDefinition method, MatchedValue value,
        Instruction anchor, CecilInsertPosition position)
    {
        _value = value ?? throw new ArgumentNullException(nameof(value));
        if (!ReferenceEquals(_value.Method, method))
            throw new ArgumentException("The matched value does not belong to the target method.", nameof(value));
        _site = new CecilEmissionSite(method, anchor, position);
    }

    /// <summary>
    /// callback 必须为 static，首参数可接收 matched value，返回值可替代 matched value。
    /// callback return 会自动留在原 consumer 所需的 stack 位置。
    /// </summary>
    public void Transform(MethodReference callback, Action<CecilCallArguments>? additionalArguments = null)
    {
        var valueType = RequireValueType();
        CecilCallBuilder.RequireReturn(callback, requireVoid: false, operation: "Transform");
        CecilTransformTypeRules.RequirePassable(callback.ReturnType, valueType,
            actualIsNull: false, "Transform return value", "matched value type");
        var call = CecilCallBuilder.Build(_site.Method, callback, additionalArguments,
            valueType, implicitValueIsNull: false, operation: "Transform");
        _site.Emit(call.Instructions, call.SourceCount);
    }

    public void Transform(CilMethodSpec callback, Action<CecilCallArguments>? additionalArguments = null)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        Transform(callback.Resolve(_site.Method.Module), additionalArguments);
    }

    /// <summary>
    /// duplicate matched value 后调用返回 void 的 static callback；原值继续交给原 consumer。
    /// </summary>
    public void Observe(MethodReference callback, Action<CecilCallArguments>? additionalArguments = null)
    {
        var valueType = RequireValueType();
        CecilCallBuilder.RequireReturn(callback, requireVoid: true, operation: "Observe");
        var call = CecilCallBuilder.Build(_site.Method, callback, additionalArguments,
            valueType, implicitValueIsNull: false, operation: "Observe");

        var instructions = new List<Instruction>(call.Instructions.Count + 1)
        {
            Instruction.Create(OpCodes.Dup),
        };
        instructions.AddRange(call.Instructions);
        _site.Emit(instructions, checked(1 + call.SourceCount));
    }

    public void Observe(CilMethodSpec callback, Action<CecilCallArguments>? additionalArguments = null)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        Observe(callback.Resolve(_site.Method.Module), additionalArguments);
    }

    /// <summary>在 matched value 仍位于 stack 时额外调用一个 void method，但不把原值传入。</summary>
    public void CallVoid(MethodReference callback, Action<CecilCallArguments>? arguments = null)
    {
        CecilCallBuilder.RequireReturn(callback, requireVoid: true, operation: "CallVoid");
        var call = CecilCallBuilder.Build(_site.Method, callback, arguments,
            implicitValueType: null, implicitValueIsNull: false, operation: "CallVoid");
        _site.Emit(call.Instructions, call.SourceCount);
    }

    public void CallVoid(CilMethodSpec callback, Action<CecilCallArguments>? arguments = null)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        CallVoid(callback.Resolve(_site.Method.Module), arguments);
    }

    public CecilCallValuePlan CallValue(MethodReference callback,
        Action<CecilCallArguments>? arguments = null)
    {
        CecilCallBuilder.RequireReturn(callback, requireVoid: false, operation: "CallValue");
        var call = CecilCallBuilder.Build(_site.Method, callback, arguments,
            implicitValueType: null, implicitValueIsNull: false, operation: "CallValue");
        return new CecilCallValuePlan(_site, call.Instructions, call.Method.ReturnType,
            Math.Max(call.SourceCount, 1));
    }

    public CecilCallValuePlan CallValue(CilMethodSpec callback,
        Action<CecilCallArguments>? arguments = null)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return CallValue(callback.Resolve(_site.Method.Module), arguments);
    }

    private TypeReference RequireValueType()
        => _value.ValueType
           ?? throw new InvalidOperationException("The matched occurrence is an effect and has no stack value type.");
}

/// <summary>
/// non-void call 的待提交结果。选择 destination 前不修改 IL；每个 plan 只能提交一次。
/// </summary>
public sealed class CecilCallValuePlan
{
    private readonly CecilEmissionSite _site;
    private readonly IReadOnlyList<Instruction> _callInstructions;
    private readonly int _additionalStackSlots;
    private bool _committed;

    internal CecilCallValuePlan(CecilEmissionSite site, IReadOnlyList<Instruction> callInstructions,
        TypeReference returnType, int additionalStackSlots)
    {
        _site = site;
        _callInstructions = callInstructions;
        ReturnType = returnType;
        if (additionalStackSlots < 0)
            throw new ArgumentOutOfRangeException(nameof(additionalStackSlots));
        _additionalStackSlots = additionalStackSlots;
    }

    public TypeReference ReturnType { get; }

    public void LeaveOnStack()
        => Commit(Array.Empty<Instruction>());

    public void Discard()
        => Commit(new[] { Instruction.Create(OpCodes.Pop) });

    public void StoreLocal(VariableDefinition variable)
    {
        if (variable is null)
            throw new ArgumentNullException(nameof(variable));
        if (!_site.Method.Body.Variables.Contains(variable))
            throw new ArgumentException("The local does not belong to the target method body.", nameof(variable));
        CecilTransformTypeRules.RequirePassable(ReturnType, variable.VariableType,
            actualIsNull: false, "call return value", "local type");
        Commit(new[] { Instruction.Create(OpCodes.Stloc, variable) });
    }

    public void StoreArgument(ParameterDefinition parameter)
    {
        if (parameter is null)
            throw new ArgumentNullException(nameof(parameter));
        if (!_site.Method.Parameters.Contains(parameter))
            throw new ArgumentException("The parameter does not belong to the target method.", nameof(parameter));
        CecilTransformTypeRules.RequirePassable(ReturnType, parameter.ParameterType,
            actualIsNull: false, "call return value", "argument type");
        Commit(new[] { Instruction.Create(OpCodes.Starg, parameter) });
    }

    private void Commit(IReadOnlyList<Instruction> destination)
    {
        if (_committed)
            throw new InvalidOperationException("This call plan was already committed.");

        var all = new List<Instruction>(_callInstructions.Count + destination.Count);
        all.AddRange(_callInstructions);
        all.AddRange(destination);
        _site.Emit(all, _additionalStackSlots);
        _committed = true;
    }
}

/// <summary>描述 static MethodReference 调用前需要显式加载的参数。</summary>
public sealed class CecilCallArguments
{
    private readonly MethodDefinition _target;
    private readonly List<CecilArgumentSource> _sources = new();

    internal CecilCallArguments(MethodDefinition target)
        => _target = target ?? throw new ArgumentNullException(nameof(target));

    internal IReadOnlyList<CecilArgumentSource> Sources => _sources;

    public CecilCallArguments This()
    {
        if (!_target.HasThis)
            throw new InvalidOperationException("The target method has no instance argument.");

        TypeReference thisType = _target.DeclaringType;
        if (_target.DeclaringType.IsValueType)
            thisType = new ByReferenceType(_target.DeclaringType);

        _sources.Add(new CecilArgumentSource(thisType, isNull: false,
            static _ => new[] { Instruction.Create(OpCodes.Ldarg_0) }));
        return this;
    }

    /// <summary>显式参数 index，不包含 this。</summary>
    public CecilCallArguments Arg(int parameterIndex)
    {
        if (parameterIndex < 0 || parameterIndex >= _target.Parameters.Count)
            throw new ArgumentOutOfRangeException(nameof(parameterIndex));
        return Arg(_target.Parameters[parameterIndex]);
    }

    public CecilCallArguments Arg(ParameterDefinition parameter)
    {
        if (parameter is null)
            throw new ArgumentNullException(nameof(parameter));
        if (!_target.Parameters.Contains(parameter))
            throw new ArgumentException("The parameter does not belong to the target method.", nameof(parameter));

        _sources.Add(new CecilArgumentSource(parameter.ParameterType, isNull: false,
            _ => new[] { Instruction.Create(OpCodes.Ldarg, parameter) }));
        return this;
    }

    public CecilCallArguments Arg(MatchedArgument argument)
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

    public CecilCallArguments Local(VariableDefinition variable)
    {
        if (variable is null)
            throw new ArgumentNullException(nameof(variable));
        if (!_target.Body.Variables.Contains(variable))
            throw new ArgumentException("The local does not belong to the target method body.", nameof(variable));

        _sources.Add(new CecilArgumentSource(variable.VariableType, isNull: false,
            _ => new[] { Instruction.Create(OpCodes.Ldloc, variable) }));
        return this;
    }

    public CecilCallArguments Local(MatchedLocal local)
    {
        if (local is null)
            throw new ArgumentNullException(nameof(local));
        if (!ReferenceEquals(local.Method, _target))
            throw new ArgumentException("The captured local belongs to a different method.", nameof(local));
        return Local(local.Variable);
    }

    public CecilCallArguments Null(TypeReference? nominalType = null)
    {
        var type = nominalType is null ? _target.Module.TypeSystem.Object : Import(nominalType);
        _sources.Add(new CecilArgumentSource(type, isNull: true,
            static _ => new[] { Instruction.Create(OpCodes.Ldnull) }));
        return this;
    }

    public CecilCallArguments Null(CilTypeSpec nominalType)
    {
        if (nominalType is null)
            throw new ArgumentNullException(nameof(nominalType));
        return Null(nominalType.Resolve(_target.Module));
    }

    public CecilCallArguments Constant(bool value)
        => AddConstant(_target.Module.TypeSystem.Boolean, () => Instruction.Create(OpCodes.Ldc_I4, value ? 1 : 0));
    public CecilCallArguments Constant(byte value)
        => AddConstant(_target.Module.TypeSystem.Byte, () => Instruction.Create(OpCodes.Ldc_I4, value));
    public CecilCallArguments Constant(sbyte value)
        => AddConstant(_target.Module.TypeSystem.SByte, () => Instruction.Create(OpCodes.Ldc_I4, value));
    public CecilCallArguments Constant(short value)
        => AddConstant(_target.Module.TypeSystem.Int16, () => Instruction.Create(OpCodes.Ldc_I4, value));
    public CecilCallArguments Constant(ushort value)
        => AddConstant(_target.Module.TypeSystem.UInt16, () => Instruction.Create(OpCodes.Ldc_I4, value));
    public CecilCallArguments Constant(int value)
        => AddConstant(_target.Module.TypeSystem.Int32, () => Instruction.Create(OpCodes.Ldc_I4, value));
    public CecilCallArguments Constant(uint value)
        => AddConstant(_target.Module.TypeSystem.UInt32, () => Instruction.Create(OpCodes.Ldc_I4, unchecked((int)value)));
    public CecilCallArguments Constant(long value)
        => AddConstant(_target.Module.TypeSystem.Int64, () => Instruction.Create(OpCodes.Ldc_I8, value));
    public CecilCallArguments Constant(ulong value)
        => AddConstant(_target.Module.TypeSystem.UInt64, () => Instruction.Create(OpCodes.Ldc_I8, unchecked((long)value)));
    public CecilCallArguments Constant(float value)
        => AddConstant(_target.Module.TypeSystem.Single, () => Instruction.Create(OpCodes.Ldc_R4, value));
    public CecilCallArguments Constant(double value)
        => AddConstant(_target.Module.TypeSystem.Double, () => Instruction.Create(OpCodes.Ldc_R8, value));
    public CecilCallArguments Constant(char value)
        => AddConstant(_target.Module.TypeSystem.Char, () => Instruction.Create(OpCodes.Ldc_I4, value));
    public CecilCallArguments Constant(string value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        return AddConstant(_target.Module.TypeSystem.String, () => Instruction.Create(OpCodes.Ldstr, value));
    }

    /// <summary>
    /// 为 enum/小整数等显式声明 nominal parameter type；value 仍按 int32 压栈。
    /// </summary>
    public CecilCallArguments ConstantI4(int value, TypeReference nominalType)
    {
        if (nominalType is null)
            throw new ArgumentNullException(nameof(nominalType));
        return AddConstant(Import(nominalType), () => Instruction.Create(OpCodes.Ldc_I4, value));
    }

    public CecilCallArguments ConstantI4(int value, CilTypeSpec nominalType)
    {
        if (nominalType is null)
            throw new ArgumentNullException(nameof(nominalType));
        return ConstantI4(value, nominalType.Resolve(_target.Module));
    }

    private CecilCallArguments AddConstant(TypeReference type, Func<Instruction> factory)
    {
        _sources.Add(new CecilArgumentSource(type, isNull: false,
            _ => new[] { factory() }));
        return this;
    }

    private TypeReference Import(TypeReference type)
        => ReferenceEquals(type.Module, _target.Module) ? type : _target.Module.ImportReference(type);
}

internal enum CecilInsertPosition
{
    Before,
    After,
}

internal sealed class CecilEmissionSite
{
    public CecilEmissionSite(MethodDefinition method, Instruction anchor, CecilInsertPosition position)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
        Position = position;
        RequireAnchor();
    }

    public MethodDefinition Method { get; }
    public Instruction Anchor { get; }
    public CecilInsertPosition Position { get; }

    public void Emit(IReadOnlyList<Instruction> instructions, int additionalStackSlots = 0)
    {
        if (instructions is null)
            throw new ArgumentNullException(nameof(instructions));
        if (additionalStackSlots < 0)
            throw new ArgumentOutOfRangeException(nameof(additionalStackSlots));
        if (instructions.Count == 0)
            return;
        RequireAnchor();

        // Inserting bytes may push a previously valid short branch outside its sbyte range.
        // Expanding first keeps both offline Cecil writing and net48-era runtimes deterministic.
        var newMaxStack = checked(Method.Body.MaxStackSize + additionalStackSlots);
        CecilBranchNormalizer.ExpandShortBranches(Method.Body);
        var processor = Method.Body.GetILProcessor();
        if (Position == CecilInsertPosition.After)
        {
            var current = Anchor;
            foreach (var instruction in instructions)
            {
                processor.InsertAfter(current, instruction);
                current = instruction;
            }
        }
        else
        {
            foreach (var instruction in instructions)
                processor.InsertBefore(Anchor, instruction);
            CecilBranchRetargeter.RetargetIncoming(Method.Body, Anchor, instructions[0]);
        }

        Method.Body.MaxStackSize = newMaxStack;
    }

    private void RequireAnchor()
    {
        if (!Method.HasBody || !Method.Body.Instructions.Contains(Anchor))
            throw new InvalidOperationException("The insertion anchor no longer belongs to the target method. Re-run the matcher after modifying IL.");
    }
}

internal sealed class CecilArgumentSource
{
    private readonly Func<ModuleDefinition, IReadOnlyList<Instruction>> _emitter;

    public CecilArgumentSource(TypeReference valueType, bool isNull,
        Func<ModuleDefinition, IReadOnlyList<Instruction>> emitter)
    {
        ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));
        IsNull = isNull;
        _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
    }

    public TypeReference ValueType { get; }
    public bool IsNull { get; }
    public IReadOnlyList<Instruction> Emit(ModuleDefinition module) => _emitter(module);
}

internal sealed class CecilPreparedCall
{
    private readonly MethodDefinition _target;
    private readonly IReadOnlyList<CecilArgumentSource> _sources;

    public CecilPreparedCall(MethodDefinition target, MethodReference method,
        IReadOnlyList<CecilArgumentSource> sources)
    {
        _target = target;
        Method = method;
        _sources = sources;
    }

    public MethodReference Method { get; }
    public int SourceCount => _sources.Count;

    public IReadOnlyList<Instruction> CreateInstructions()
    {
        var instructions = new List<Instruction>();
        foreach (var source in _sources)
            instructions.AddRange(source.Emit(_target.Module));
        instructions.Add(Instruction.Create(OpCodes.Call, Method));
        return instructions;
    }
}

internal sealed class CecilBuiltCall
{
    public CecilBuiltCall(MethodReference method, IReadOnlyList<Instruction> instructions, int sourceCount)
    {
        Method = method;
        Instructions = instructions;
        SourceCount = sourceCount;
    }

    public MethodReference Method { get; }
    public IReadOnlyList<Instruction> Instructions { get; }
    public int SourceCount { get; }
}

internal static class CecilCallBuilder
{
    public static void RequireReturn(MethodReference callback, bool requireVoid, string operation)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        var isVoid = callback.ReturnType.MetadataType == MetadataType.Void;
        if (requireVoid != isVoid)
        {
            var requirement = requireVoid ? "Void" : "a non-Void value";
            throw new ArgumentException($"{operation} requires a callback returning {requirement}.", nameof(callback));
        }
    }

    public static CecilBuiltCall Build(MethodDefinition target, MethodReference callback,
        Action<CecilCallArguments>? configure, TypeReference? implicitValueType,
        bool implicitValueIsNull, string operation)
    {
        var prepared = Prepare(target, callback, configure, implicitValueType,
            implicitValueIsNull, operation);
        return new CecilBuiltCall(prepared.Method, prepared.CreateInstructions(), prepared.SourceCount);
    }

    public static CecilPreparedCall Prepare(MethodDefinition target, MethodReference callback,
        Action<CecilCallArguments>? configure, TypeReference? implicitValueType,
        bool implicitValueIsNull, string operation)
    {
        if (target is null)
            throw new ArgumentNullException(nameof(target));
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        if (!target.HasBody)
            throw new ArgumentException("The target method has no IL body.", nameof(target));
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

        var arguments = new CecilCallArguments(target);
        configure?.Invoke(arguments);

        var implicitCount = implicitValueType is null ? 0 : 1;
        var suppliedCount = implicitCount + arguments.Sources.Count;
        if (callback.Parameters.Count != suppliedCount)
        {
            throw new ArgumentException(
                $"{operation} callback expects {callback.Parameters.Count} parameters, but the site supplies {suppliedCount}.",
                nameof(callback));
        }

        var parameterOffset = 0;
        if (implicitValueType is not null)
        {
            CecilTransformTypeRules.RequirePassable(implicitValueType,
                callback.Parameters[0].ParameterType, implicitValueIsNull,
                "matched value", "callback parameter 0");
            parameterOffset = 1;
        }

        for (var i = 0; i < arguments.Sources.Count; i++)
        {
            var source = arguments.Sources[i];
            CecilTransformTypeRules.RequirePassable(source.ValueType,
                callback.Parameters[i + parameterOffset].ParameterType, source.IsNull,
                $"argument source {i}", $"callback parameter {i + parameterOffset}");
        }

        // Import only after all validation. A failed transform therefore does not add an
        // AssemblyRef/MemberRef to the target module, which matters for .NET Framework binding.
        var imported = ReferenceEquals(callback.Module, target.Module)
            ? callback
            : target.Module.ImportReference(callback);
        return new CecilPreparedCall(target, imported, arguments.Sources.ToArray());
    }
}

internal static class CecilTransformTypeRules
{
    public static void RequirePassable(TypeReference actual, TypeReference expected,
        bool actualIsNull, string actualName, string expectedName)
    {
        if (CanPass(actual, expected, actualIsNull))
            return;

        throw new ArgumentException(
            $"{actualName} '{actual.FullName}' is not compatible with {expectedName} '{expected.FullName}'.");
    }

    private static bool CanPass(TypeReference actual, TypeReference expected, bool actualIsNull)
    {
        if (actualIsNull)
        {
            if (expected is ByReferenceType or PointerType or FunctionPointerType)
                return false;
            if (expected is GenericParameter generic)
                return !generic.HasNotNullableValueTypeConstraint;
            return !expected.IsValueType;
        }

        try
        {
            if (actual.IsSameWith(expected))
                return true;
        }
        catch
        {
            return false;
        }

        // Verification-stack categories are intentionally not enough here: Boolean, Int32,
        // enum and small integers may all be I4 on the stack but are not interchangeable API types.
        if (IsAddressLike(actual) || IsAddressLike(expected))
            return false;
        if (actual.IsValueType || expected.IsValueType)
            return false;

        try
        {
            return actual.IsILStackAssignableTo(expected);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAddressLike(TypeReference type)
        => type is ByReferenceType or PointerType or FunctionPointerType;
}

internal static class CecilBranchNormalizer
{
    public static void ExpandShortBranches(Mono.Cecil.Cil.MethodBody body)
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

internal static class CecilBranchRetargeter
{
    public static void RetargetIncoming(Mono.Cecil.Cil.MethodBody body,
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
}

internal static class CecilConditionTransformer
{
    public static void Transform(MatchedCondition condition, MethodDefinition method,
        MethodReference callback, Action<CecilCallArguments>? additionalArguments)
    {
        if (condition is null)
            throw new ArgumentNullException(nameof(condition));
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        if (!ReferenceEquals(condition.Method, method))
            throw new ArgumentException("The captured condition does not belong to the target method.", nameof(condition));
        if (!condition.CanRewrite)
        {
            throw new InvalidOperationException(condition.RewriteFailureReason
                ?? "The captured condition cannot be safely rewritten.");
        }

        if (callback.ReturnType.MetadataType != MetadataType.Boolean)
        {
            throw new ArgumentException(
                "A condition transform callback must return System.Boolean.", nameof(callback));
        }
        var prepared = CecilCallBuilder.Prepare(method, callback, additionalArguments,
            method.Module.TypeSystem.Boolean, implicitValueIsNull: false,
            operation: "Condition Transform");

        var fragment = condition.Fragment;
        var trueTarget = fragment.TrueContinuation.Leader;
        var falseTarget = fragment.FalseContinuation.Leader;
        var exitGroups = fragment.TrueExits.Select(static edge => new ConditionExitInfo(edge, true))
            .Concat(fragment.FalseExits.Select(static edge => new ConditionExitInfo(edge, false)))
            .GroupBy(static exit => exit.Edge.From)
            .Select(static group => new ConditionExitGroup(group.Key, group.ToArray()))
            .ToArray();

        if (exitGroups.Length == 0)
            throw new InvalidOperationException("The captured condition has no exit edges.");

        foreach (var group in exitGroups)
        {
            if (group.FallExitCount > 1 || group.BranchExitCount > 1)
            {
                throw new NotSupportedException(
                    $"Condition block IL_{group.Source.Leader.Offset:X4} has an unsupported exit shape.");
            }

            EnsureAnchor(method, group.Source.Terminator);
            EnsureSameExceptionRegion(method.Body, group.Source.Terminator, trueTarget);
            EnsureSameExceptionRegion(method.Body, group.Source.Terminator, falseTarget);
        }

        // 每个 source block 使用独立 bridge。相比共享 bridge 会多几个静态 call site，
        // 但每次执行条件仍只走一个 callback，且不要求跨 EH region 汇合。
        var newMaxStack = checked(method.Body.MaxStackSize + 1 + prepared.SourceCount);
        CecilBranchNormalizer.ExpandShortBranches(method.Body);
        foreach (var group in exitGroups)
        {
            var emitted = new List<Instruction>();

            if (group.FallExit is null && group.BranchExit is not null)
            {
                if (group.AllFallThrough is null)
                    throw new InvalidOperationException("The source condition has no fall-through edge.");
                EnsureSameExceptionRegion(method.Body, group.Source.Terminator,
                    group.AllFallThrough.To.Leader);
                emitted.Add(Instruction.Create(OpCodes.Br, group.AllFallThrough.To.Leader));
            }

            if (group.FallExit is not null)
                emitted.AddRange(CreateBridge(prepared, group.FallExit.Value, trueTarget, falseTarget));

            Instruction? branchBridge = null;
            if (group.BranchExit is not null)
            {
                var bridge = CreateBridge(prepared, group.BranchExit.Value, trueTarget, falseTarget);
                branchBridge = bridge[0];
                emitted.AddRange(bridge);
            }

            InsertAfter(method.Body.GetILProcessor(), group.Source.Terminator, emitted);
            if (branchBridge is not null)
                RetargetTakenBranch(group.Source.Terminator, branchBridge);
        }

        method.Body.MaxStackSize = newMaxStack;
    }

    private static IReadOnlyList<Instruction> CreateBridge(CecilPreparedCall call,
        bool originalValue, Instruction trueTarget, Instruction falseTarget)
    {
        var result = new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldc_I4, originalValue ? 1 : 0),
        };
        result.AddRange(call.CreateInstructions());
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

    private static void EnsureSameExceptionRegion(Mono.Cecil.Cil.MethodBody body,
        Instruction source, Instruction target)
    {
        if (GetRegionSignature(body, source) == GetRegionSignature(body, target))
            return;

        throw new NotSupportedException(
            $"Condition rewriting would branch from IL_{source.Offset:X4} to IL_{target.Offset:X4} across an exception-region boundary.");
    }

    private static string GetRegionSignature(Mono.Cecil.Cil.MethodBody body, Instruction instruction)
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

    private sealed class ConditionExitInfo
    {
        public ConditionExitInfo(ControlFlowEdge edge, bool value)
        {
            Edge = edge;
            Value = value;
        }

        public ControlFlowEdge Edge { get; }
        public bool Value { get; }
    }

    private sealed class ConditionExitGroup
    {
        public ConditionExitGroup(BasicBlock source, IReadOnlyList<ConditionExitInfo> exits)
        {
            Source = source;
            FallExitCount = exits.Count(static exit => exit.Edge.IsFallThrough);
            BranchExitCount = exits.Count(static exit => !exit.Edge.IsFallThrough);
            FallExit = exits.SingleOrDefault(static exit => exit.Edge.IsFallThrough);
            BranchExit = exits.SingleOrDefault(static exit => !exit.Edge.IsFallThrough);
            AllFallThrough = source.Successors.SingleOrDefault(static edge => edge.IsFallThrough);
        }

        public BasicBlock Source { get; }
        public int FallExitCount { get; }
        public int BranchExitCount { get; }
        public ConditionExitInfo? FallExit { get; }
        public ConditionExitInfo? BranchExit { get; }
        public ControlFlowEdge? AllFallThrough { get; }
    }
}
