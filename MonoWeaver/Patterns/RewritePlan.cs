using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;
using MonoWeaver.Utils;

namespace MonoWeaver.Cecil;

/// <summary>一次待提交的 CIL 改写。构造 plan 时不会修改目标方法；每个 plan 只能提交一次。</summary>
public sealed class RewritePlan
{
    private sealed record MethodBodySnapshot(Instruction[] Instructions, OpCode[] OpCodes,
        object?[] Operands, VariableDefinition[] Variables,
        ExceptionHandler[] Handlers, int MaxStack);

    private readonly MethodDefinition _method;
    private readonly EmissionSite? _site;
    private readonly CallArguments? _arguments;
    private readonly TypeReference? _returnType;
    private readonly Func<ModuleDefinition, IReadOnlyList<Instruction>>? _callbackEmitter;
    private readonly Action? _customApply;
    private readonly Action? _beforeApply;
    private readonly int _extraStackSlots;
    private readonly bool _emitDupBeforeArguments;
    private bool _applied;

    internal RewritePlan(EmissionSite site, CallArguments arguments, TypeReference returnType,
        Func<ModuleDefinition, IReadOnlyList<Instruction>> callbackEmitter, int extraStackSlots,
        bool emitDupBeforeArguments = false, Action? beforeApply = null)
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
        _beforeApply = beforeApply;
    }

    internal RewritePlan(MethodDefinition method, Action customApply, Action? beforeApply = null,
        CallArguments? arguments = null)
    {
        _method = method ?? throw new ArgumentNullException(nameof(method));
        _customApply = customApply ?? throw new ArgumentNullException(nameof(customApply));
        _beforeApply = beforeApply;
        _arguments = arguments;
    }

    public RewritePlan Apply()
    {
        if (_applied)
            throw new InvalidOperationException("This rewrite plan was already applied.");

        var label = _method.Body.Instructions.FirstOrDefault(instruction =>
            instruction.OpCode.FlowControl is FlowControl.Cond_Branch or FlowControl.Branch
            && instruction.Operand is { } operand
            && CecilHelper.IsMonoModILLabel(operand.GetType()));

        try
        {
            if (label is not null)
                CecilHelper.BranchLabelsToTarget(CecilHelper.GetContext(label));

            _beforeApply?.Invoke();

            if (_customApply is not null)
            {
                _customApply();
                _applied = true;
                return this;
            }

            if (_site is null || _arguments is null || _callbackEmitter is null)
                throw new InvalidOperationException("The rewrite plan is incomplete.");
            if (!_method.HasBody || !_method.Body.Instructions.Contains(_site.Anchor))
                throw new InvalidOperationException("The match is stale. Re-run the matcher after modifying IL.");

            _method.Body.MaxStackSize = checked(_method.Body.MaxStackSize + AdditionalStackSlots);
            BranchModifier.ExpandShortBranches(_method.Body);
            var processor = _method.Body.GetILProcessor();
            var current = _site.Anchor;
            Instruction? firstInserted = null;

            _arguments.EmitStores(_site, processor, ref current, ref firstInserted);

            if (_emitDupBeforeArguments)
                _site.Insert(processor, Instruction.Create(OpCodes.Dup), ref current, ref firstInserted);

            foreach (var argument in _arguments.ArgPlans)
                argument.EmitLoad(_site, processor, ref current, ref firstInserted);

            foreach (var instruction in _callbackEmitter(_site.Method.Module))
                _site.Insert(processor, instruction, ref current, ref firstInserted);

            if (_site.Position == InsertPosition.Before && firstInserted is not null)
                BranchModifier.RetargetIncoming(_method.Body, _site.Anchor, firstInserted);

            _applied = true;
            return this;
        }
        finally
        {
            if (label is not null)
                CecilHelper.BranchTargetsToLabels(CecilHelper.GetContext(label));
        }
    }

    public RewritePlan Apply(VerifyOptions options)
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

    private int AdditionalStackSlots
    {
        get
        {
            var slots = _extraStackSlots + (_arguments?.ArgPlans.Count ?? 0);
            if (_emitDupBeforeArguments)
                slots++;
            if (_site is not null && _returnType is not null && !_returnType.IsVoid()
                && !_emitDupBeforeArguments)
            {
                slots = Math.Max(slots, 1);
            }
            return slots;
        }
    }

    private static MethodBodySnapshot CaptureBody(MethodBody body)
    {
        var instructions = body.Instructions.ToArray();
        return new MethodBodySnapshot(instructions,
            instructions.Select(static instruction => instruction.OpCode).ToArray(),
            instructions.Select(static instruction => instruction.Operand).ToArray(),
            body.Variables.ToArray(),
            body.ExceptionHandlers.Select(CloneHandler).ToArray(),
            body.MaxStackSize);
    }

    private static void RestoreBody(MethodBody body, MethodBodySnapshot snapshot)
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
        => new(handler.HandlerType)
        {
            TryStart = handler.TryStart,
            TryEnd = handler.TryEnd,
            HandlerStart = handler.HandlerStart,
            HandlerEnd = handler.HandlerEnd,
            FilterStart = handler.FilterStart,
            CatchType = handler.CatchType,
        };
}

/// <summary>描述 callback 调用时除隐式 matched value 之外的显式参数。</summary>
public sealed class CallArguments
{
    public static CallArguments ConfigAndValidateCall(MethodDefinition target,
        MethodReference callback, Action<CallArguments>? configure,
        TypeReference? implicitValueType, bool implicitValueIsNull,
        string operation = "Call")
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        if (callback.HasThis)
        {
            throw new ArgumentException(
                $"{operation} accepts only static MethodReference callbacks.", nameof(callback));
        }
        if (callback.Name is ".ctor" or ".cctor")
            throw new ArgumentException($"{operation} cannot call a constructor.", nameof(callback));
        if (callback.CallingConvention == MethodCallingConvention.VarArg)
            throw new NotSupportedException("VarArg callback methods are not supported.");
        if (callback is not GenericInstanceMethod && callback.GenericParameters.Count != 0)
        {
            throw new ArgumentException(
                "Open generic callback methods are not supported. Supply a GenericInstanceMethod.",
                nameof(callback));
        }
        if (callback.ContainsGenericParameter)
        {
            throw new ArgumentException(
                "Callback signatures containing unbound generic parameters are not supported.",
                nameof(callback));
        }

        return ConfigAndValidateCall(target,
            callback.Parameters.Select(static parameter => parameter.ParameterType).ToArray(),
            configure, implicitValueType, implicitValueIsNull, operation);
    }

    internal static CallArguments ConfigAndValidateCall(MethodDefinition target,
        CecilDelegateCall callback, Action<CallArguments>? configure,
        TypeReference? implicitValueType, bool implicitValueIsNull,
        string operation = "Call")
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        return ConfigAndValidateCall(target, callback.ParameterTypes, configure,
            implicitValueType, implicitValueIsNull, operation);
    }

    private static CallArguments ConfigAndValidateCall(MethodDefinition target,
        IReadOnlyList<TypeReference> parameterTypes, Action<CallArguments>? configure,
        TypeReference? implicitValueType, bool implicitValueIsNull, string operation)
    {
        if (target is null)
            throw new ArgumentNullException(nameof(target));
        if (!target.HasBody)
            throw new ArgumentException("The target method has no IL body.", nameof(target));

        var arguments = new CallArguments(target);
        configure?.Invoke(arguments);

        var implicitCount = implicitValueType is null ? 0 : 1;
        var suppliedCount = implicitCount + arguments.ArgPlans.Count;
        if (parameterTypes.Count != suppliedCount)
        {
            throw new ArgumentException(
                $"{operation} callback expects {parameterTypes.Count} parameters, " +
                $"but the site supplies {suppliedCount}.");
        }

        var parameterOffset = 0;
        if (implicitValueType is not null)
        {
            PatternTransformExtensions.RequireAssignable(implicitValueType, parameterTypes[0],
                implicitValueIsNull, "matched value", "callback parameter 0");
            parameterOffset = 1;
        }

        for (var i = 0; i < arguments.ArgPlans.Count; i++)
        {
            var source = arguments.ArgPlans[i];
            PatternTransformExtensions.RequireAssignable(source.ArgType,
                parameterTypes[i + parameterOffset], source.IsNull,
                $"argument source {i}", $"callback parameter {i + parameterOffset}");
        }

        return arguments;
    }

    private readonly MethodDefinition _target;
    private readonly List<IArgumentPlan> _argPlans = new();

    internal CallArguments(MethodDefinition target)
        => _target = target ?? throw new ArgumentNullException(nameof(target));

    internal IReadOnlyList<IArgumentPlan> ArgPlans => _argPlans;

    internal void ResetStores()
    {
        foreach (var plan in _argPlans)
            plan.ResetStore();
    }

    internal void EmitStores(EmissionSite site, ILProcessor processor,
        ref Instruction current, ref Instruction? firstInserted)
    {
        foreach (var plan in _argPlans)
            plan.EmitStore(site, processor, ref current, ref firstInserted);
    }

    internal IReadOnlyList<Instruction> CreateLoadInstructions(ModuleDefinition module)
    {
        var instructions = new List<Instruction>();
        foreach (var plan in _argPlans)
            instructions.AddRange(plan.CreateLoad(module));
        return instructions;
    }

    internal void ValidateForSite(EmissionSite site, string operation)
    {
        if (site is null)
            throw new ArgumentNullException(nameof(site));
        if (!ReferenceEquals(site.Method, _target))
            throw new ArgumentException("The emission site belongs to a different method.", nameof(site));

        var facts = CaptureAvailability.Create(_target);
        foreach (var captured in _argPlans.OfType<CapturedArgumentPlan>())
        {
            facts.RequireAvailable(captured.Value, site.Anchor, site.Position, operation);
        }
    }

    internal void ValidateForReplacement(Instruction first, Instruction last, string operation)
    {
        if (first is null)
            throw new ArgumentNullException(nameof(first));
        if (last is null)
            throw new ArgumentNullException(nameof(last));

        var body = _target.Body;
        var firstIndex = body.Instructions.IndexOf(first);
        var lastIndex = body.Instructions.IndexOf(last);
        if (firstIndex < 0 || lastIndex < firstIndex)
            throw new InvalidOperationException("The replacement range is stale or invalid.");

        var facts = CaptureAvailability.Create(_target);
        foreach (var captured in _argPlans.OfType<CapturedArgumentPlan>())
        {
            var captureIndex = body.Instructions.IndexOf(captured.Value.ResultInstruction);
            if (captureIndex >= firstIndex && captureIndex <= lastIndex)
            {
                throw new NotSupportedException(
                    $"{operation} cannot load capture '{CaptureName(captured.Value)}' because " +
                    "that occurrence is produced by the code being replaced.");
            }

            facts.RequireAvailable(captured.Value, first, InsertPosition.Before, operation);
        }
    }

    internal void ValidateForConditionExits(ConditionTarget condition, string operation)
    {
        if (condition is null)
            throw new ArgumentNullException(nameof(condition));
        if (!ReferenceEquals(condition.Method, _target))
            throw new ArgumentException("The condition belongs to a different method.", nameof(condition));

        var sites = condition.Fragment.TrueExits
            .Concat(condition.Fragment.FalseExits)
            .Select(static edge => edge.From.Terminator)
            .Distinct()
            .ToArray();
        var facts = CaptureAvailability.Create(_target);
        foreach (var captured in _argPlans.OfType<CapturedArgumentPlan>())
        {
            foreach (var site in sites)
                // The temporary store is emitted immediately after the captured producer,
                // therefore it must execute before the exit terminator takes either edge.
                facts.RequireAvailable(captured.Value, site, InsertPosition.Before, operation);
        }
    }

    internal void ValidateForConditionReplacement(ConditionTarget condition, string operation)
    {
        if (condition is null)
            throw new ArgumentNullException(nameof(condition));
        if (!ReferenceEquals(condition.Method, _target))
            throw new ArgumentException("The condition belongs to a different method.", nameof(condition));

        var facts = CaptureAvailability.Create(_target);
        foreach (var captured in _argPlans.OfType<CapturedArgumentPlan>())
        {
            var captureBlock = facts.BlockOf(captured.Value.ResultInstruction);
            if (condition.Fragment.Blocks.Contains(captureBlock))
            {
                throw new NotSupportedException(
                    $"{operation} cannot load capture '{CaptureName(captured.Value)}' because " +
                    "that occurrence belongs to the condition being replaced.");
            }

            facts.RequireAvailable(captured.Value, condition.Fragment.Entry.Leader,
                InsertPosition.Before, operation);
        }
    }

    internal void MaterializeCapturedValues(ILProcessor processor)
    {
        if (processor is null)
            throw new ArgumentNullException(nameof(processor));

        var captured = _argPlans.OfType<CapturedArgumentPlan>().ToArray();
        if (captured.Length == 0)
            return;

        // Each capture is duplicated only once at a time, so one extra slot is sufficient.
        processor.Body.MaxStackSize = checked(processor.Body.MaxStackSize + 1);
        foreach (var plan in captured)
            plan.Materialize(processor);
    }

    private static string CaptureName(ValueTarget value)
        => string.IsNullOrWhiteSpace(value.Name) ? "<root>" : value.Name!;

    public CallArguments This()
    {
        if (!_target.HasThis)
            throw new InvalidOperationException("The target method has no instance argument.");

        TypeReference thisType = _target.DeclaringType
                                 ?? throw new InvalidOperationException("The method declaring type is null.");
        if (thisType.IsValueType)
            thisType = new ByReferenceType(thisType);

        _argPlans.Add(new ArgumentPlan(thisType, isNull: false,
            static _ => new[] { Instruction.Create(OpCodes.Ldarg_0) }));
        return this;
    }

    /// <summary>保存并重载某个已经求值的精确 occurrence。</summary>
    public CallArguments Capture(ValueTarget value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        if (!ReferenceEquals(value.Method, _target))
            throw new ArgumentException("The captured value belongs to a different method.", nameof(value));
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

        _argPlans.Add(new ArgumentPlan(parameter.ParameterType, isNull: false,
            _ => new[] { Instruction.Create(OpCodes.Ldarg, parameter) }));
        return this;
    }

    public CallArguments Arg(ArgumentCapture argument)
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

        _argPlans.Add(new ArgumentPlan(variable.VariableType, isNull: false,
            _ => new[] { Instruction.Create(OpCodes.Ldloc, variable) }));
        return this;
    }

    public CallArguments Local(LocalCapture local)
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
        _argPlans.Add(new ArgumentPlan(type, isNull: true,
            static _ => new[] { Instruction.Create(OpCodes.Ldnull) }));
        return this;
    }

    public CallArguments Null(CilTypeSpec? nominalType = null)
        => Null(nominalType?.Resolve(_target.Module));

    public CallArguments Constant(bool value)
        => AddConstant(_target.Module.TypeSystem.Boolean,
            () => Instruction.Create(OpCodes.Ldc_I4, value ? 1 : 0));
    public CallArguments Constant(byte value)
        => AddConstant(_target.Module.TypeSystem.Byte,
            () => Instruction.Create(OpCodes.Ldc_I4, value));
    public CallArguments Constant(sbyte value)
        => AddConstant(_target.Module.TypeSystem.SByte,
            () => Instruction.Create(OpCodes.Ldc_I4, value));
    public CallArguments Constant(short value)
        => AddConstant(_target.Module.TypeSystem.Int16,
            () => Instruction.Create(OpCodes.Ldc_I4, value));
    public CallArguments Constant(ushort value)
        => AddConstant(_target.Module.TypeSystem.UInt16,
            () => Instruction.Create(OpCodes.Ldc_I4, value));
    public CallArguments Constant(int value)
        => AddConstant(_target.Module.TypeSystem.Int32,
            () => Instruction.Create(OpCodes.Ldc_I4, value));
    public CallArguments Constant(uint value)
        => AddConstant(_target.Module.TypeSystem.UInt32,
            () => Instruction.Create(OpCodes.Ldc_I4, unchecked((int)value)));
    public CallArguments Constant(long value)
        => AddConstant(_target.Module.TypeSystem.Int64,
            () => Instruction.Create(OpCodes.Ldc_I8, value));
    public CallArguments Constant(ulong value)
        => AddConstant(_target.Module.TypeSystem.UInt64,
            () => Instruction.Create(OpCodes.Ldc_I8, unchecked((long)value)));
    public CallArguments Constant(float value)
        => AddConstant(_target.Module.TypeSystem.Single,
            () => Instruction.Create(OpCodes.Ldc_R4, value));
    public CallArguments Constant(double value)
        => AddConstant(_target.Module.TypeSystem.Double,
            () => Instruction.Create(OpCodes.Ldc_R8, value));
    public CallArguments Constant(char value)
        => AddConstant(_target.Module.TypeSystem.Char,
            () => Instruction.Create(OpCodes.Ldc_I4, value));

    public CallArguments Constant(string value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        return AddConstant(_target.Module.TypeSystem.String,
            () => Instruction.Create(OpCodes.Ldstr, value));
    }

    public CallArguments ConstantI4(int value, TypeReference nominalType)
    {
        if (nominalType is null)
            throw new ArgumentNullException(nameof(nominalType));
        return AddConstant(Import(nominalType),
            () => Instruction.Create(OpCodes.Ldc_I4, value));
    }

    public CallArguments ConstantI4(int value, CilTypeSpec nominalType)
    {
        if (nominalType is null)
            throw new ArgumentNullException(nameof(nominalType));
        return ConstantI4(value, nominalType.Resolve(_target.Module));
    }

    private CallArguments AddConstant(TypeReference type, Func<Instruction> factory)
    {
        _argPlans.Add(new ArgumentPlan(type, isNull: false,
            _ => new[] { factory() }));
        return this;
    }

    private TypeReference Import(TypeReference type)
        => ReferenceEquals(type.Module, _target.Module)
            ? type
            : _target.Module.ImportReference(type);
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

internal interface IArgumentPlan
{
    void EmitLoad(EmissionSite site, ILProcessor processor,
        ref Instruction current, ref Instruction? firstInserted);

    void EmitStore(EmissionSite site, ILProcessor processor,
        ref Instruction current, ref Instruction? firstInserted);

    TypeReference ArgType { get; }
    bool IsNull { get; }
    IReadOnlyList<Instruction> CreateLoad(ModuleDefinition module);
    void ResetStore();
}

internal class ArgumentPlan : IArgumentPlan
{
    private readonly Func<ModuleDefinition, IReadOnlyList<Instruction>> _emitter;

    public ArgumentPlan(TypeReference valueType, bool isNull,
        Func<ModuleDefinition, IReadOnlyList<Instruction>> emitter)
    {
        ArgType = valueType ?? throw new ArgumentNullException(nameof(valueType));
        IsNull = isNull;
        _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
    }

    public TypeReference ArgType { get; }
    public bool IsNull { get; }

    public virtual void EmitLoad(EmissionSite site, ILProcessor processor,
        ref Instruction current, ref Instruction? firstInserted)
    {
        foreach (var instruction in _emitter(processor.Body.Method.Module))
            site.Insert(processor, instruction, ref current, ref firstInserted);
    }

    public virtual void EmitStore(EmissionSite site, ILProcessor processor,
        ref Instruction current, ref Instruction? firstInserted) { }

    public IReadOnlyList<Instruction> CreateLoad(ModuleDefinition module) => _emitter(module);
    public virtual void ResetStore() { }
}

internal sealed class CapturedArgumentPlan : IArgumentPlan
{
    private VariableDefinition? _variable;

    public CapturedArgumentPlan(ValueTarget value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        ArgType = value.ValueType;
    }

    public ValueTarget Value { get; }
    public TypeReference ArgType { get; }
    public bool IsNull => false;

    public void EmitLoad(EmissionSite site, ILProcessor processor,
        ref Instruction current, ref Instruction? firstInserted)
    {
        if (_variable is null)
            throw new InvalidOperationException("The captured occurrence was not stored before it was loaded.");
        if (_variable.Index < 0 || _variable.Index >= processor.Body.Variables.Count
            || !ReferenceEquals(processor.Body.Variables[_variable.Index], _variable))
        {
            throw new InvalidOperationException("The temporary local for a captured occurrence is stale.");
        }

        site.Insert(processor, Instruction.Create(OpCodes.Ldloc, _variable),
            ref current, ref firstInserted);
    }

    public void EmitStore(EmissionSite site, ILProcessor processor,
        ref Instruction current, ref Instruction? firstInserted)
    {
        if (_variable is not null && Value.Method.Body.Variables.Contains(_variable))
            return;

        if (!Value.Method.Body.Instructions.Contains(Value.ResultInstruction))
            throw new InvalidOperationException("The captured occurrence is stale.");

        _variable = new VariableDefinition(Value.ValueType);
        Value.Method.Body.Variables.Add(_variable);
        var duplicate = Instruction.Create(OpCodes.Dup);
        var store = Instruction.Create(OpCodes.Stloc, _variable);

        // 当 capture 与 callback 使用同一个 after-site 时，store 必须进入同一 emission 序列，
        // 否则 InsertAfter(anchor, callback) 会跑到 store 前面。
        if (ReferenceEquals(Value.ResultInstruction, site.Anchor)
            && site.Position == InsertPosition.After)
        {
            site.Insert(processor, duplicate, ref current, ref firstInserted);
            site.Insert(processor, store, ref current, ref firstInserted);
            return;
        }

        processor.InsertAfter(Value.ResultInstruction, duplicate);
        processor.InsertAfter(duplicate, store);
    }

    public void Materialize(ILProcessor processor)
    {
        if (processor is null)
            throw new ArgumentNullException(nameof(processor));
        if (_variable is not null && Value.Method.Body.Variables.Contains(_variable))
            return;
        if (!ReferenceEquals(processor.Body.Method, Value.Method))
            throw new ArgumentException("The processor belongs to a different method.", nameof(processor));
        if (!Value.Method.Body.Instructions.Contains(Value.ResultInstruction))
            throw new InvalidOperationException("The captured occurrence is stale.");

        _variable = new VariableDefinition(Value.ValueType);
        Value.Method.Body.Variables.Add(_variable);
        var duplicate = Instruction.Create(OpCodes.Dup);
        var store = Instruction.Create(OpCodes.Stloc, _variable);
        processor.InsertAfter(Value.ResultInstruction, duplicate);
        processor.InsertAfter(duplicate, store);
    }

    public IReadOnlyList<Instruction> CreateLoad(ModuleDefinition module)
    {
        if (_variable is null)
            throw new InvalidOperationException("The captured occurrence was not stored before it was loaded.");
        return new[] { Instruction.Create(OpCodes.Ldloc, _variable) };
    }

    public void ResetStore() => _variable = null;
}

internal sealed class CaptureAvailability
{
    private readonly ILBasicBlockGraph _graph;
    private readonly Dictionary<BasicBlock, HashSet<BasicBlock>> _dominators;

    private CaptureAvailability(ILBasicBlockGraph graph)
    {
        _graph = graph;
        _dominators = ComputeDominators(graph);
    }

    public static CaptureAvailability Create(MethodDefinition method)
        => new(ILBasicBlockGraphBuilder.Build(method));

    public BasicBlock BlockOf(Instruction instruction)
    {
        if (!_graph.BlockByInstruction.TryGetValue(instruction, out var block))
            throw new InvalidOperationException("A captured occurrence is stale.");
        return block;
    }

    public void RequireAvailable(ValueTarget value, Instruction anchor, InsertPosition position,
        string operation)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        if (!_graph.InstructionIndices.TryGetValue(value.ResultInstruction, out var captureIndex))
            throw new InvalidOperationException("A captured occurrence is stale.");
        if (!_graph.InstructionIndices.TryGetValue(anchor, out var anchorIndex))
            throw new InvalidOperationException("The rewrite site is stale.");

        var captureBlock = _graph.BlockByInstruction[value.ResultInstruction];
        var anchorBlock = _graph.BlockByInstruction[anchor];
        var available = ReferenceEquals(captureBlock, anchorBlock)
            ? captureIndex < anchorIndex ||
              (captureIndex == anchorIndex && position == InsertPosition.After)
            : _dominators.TryGetValue(anchorBlock, out var dominators) &&
              dominators.Contains(captureBlock);

        if (!available)
        {
            var name = string.IsNullOrWhiteSpace(value.Name) ? "<root>" : value.Name;
            throw new NotSupportedException(
                $"{operation} cannot load capture '{name}' because it is not produced on every " +
                "control-flow path before the callback. Capture an earlier dominating value, " +
                "or move the rewrite to a later target.");
        }
    }

    private static Dictionary<BasicBlock, HashSet<BasicBlock>> ComputeDominators(
        ILBasicBlockGraph graph)
    {
        var blocks = graph.Blocks.ToArray();
        var all = new HashSet<BasicBlock>(blocks);
        var roots = new HashSet<BasicBlock>(graph.EntryBlocks);
        foreach (var block in blocks.Where(static block => block.Predecessors.Count == 0))
            roots.Add(block);

        // Treat disconnected/unreachable blocks as independent roots. This is conservative:
        // a capture in one unreachable component never becomes available in another by accident.
        var reachable = new HashSet<BasicBlock>();
        var pending = new Stack<BasicBlock>(roots);
        while (pending.Count != 0)
        {
            var block = pending.Pop();
            if (!reachable.Add(block))
                continue;
            foreach (var edge in block.Successors)
                pending.Push(edge.To);
        }
        foreach (var block in blocks)
        {
            if (!reachable.Contains(block))
                roots.Add(block);
        }

        var result = new Dictionary<BasicBlock, HashSet<BasicBlock>>(blocks.Length);
        foreach (var block in blocks)
            result[block] = roots.Contains(block) ? new HashSet<BasicBlock> { block } : new HashSet<BasicBlock>(all);

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in blocks)
            {
                if (roots.Contains(block))
                    continue;

                HashSet<BasicBlock> next;
                if (block.Predecessors.Count == 0)
                {
                    next = new HashSet<BasicBlock> { block };
                }
                else
                {
                    next = new HashSet<BasicBlock>(result[block.Predecessors[0].From]);
                    for (var i = 1; i < block.Predecessors.Count; i++)
                        next.IntersectWith(result[block.Predecessors[i].From]);
                    next.Add(block);
                }

                if (!result[block].SetEquals(next))
                {
                    result[block] = next;
                    changed = true;
                }
            }
        }

        return result;
    }
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
