using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Utils;

namespace MonoWeaver.Patterns;


/// <summary>
/// pattern matcher 使用的小型 symbolic model。
/// 它不尝试 decompile 整个 method，只保留 pattern candidate 需要的 expression dependency。
/// </summary>
internal sealed class MethodModel
{
    private readonly Dictionary<Instruction, int> _instructionIndices;
    private readonly Dictionary<Instruction, BasicBlock> _blockByInstruction;
    private readonly Dictionary<Instruction, TargetExpressionNode> _instructionResults = new();
    private readonly Dictionary<Instruction, TargetExpressionNode> _localStoreValues = new();
    private readonly Dictionary<BasicBlock, TargetExpressionNode> _conditionExpressions = new();
    private readonly List<TargetExpressionNode> _valueCandidates = new();
    private readonly List<TargetEffect> _effectCandidates = new();
    private readonly HashSet<TargetExpressionNode> _duplicatedNodes = new();
    private readonly MatchDiagnosticCollector _modelDiagnostics = new();
    private readonly List<TargetExpressionNode> _stack = new();
    private readonly int _variableCount;
    private LocalDefinitionIndex? _localDefinitions;

    private static readonly object[] BoxedSmallInts = CreateBoxedSmallInts();

    private static object[] CreateBoxedSmallInts()
    {
        var result = new object[10];
        for (var i = 0; i < result.Length; i++)
            result[i] = i - 1;
        return result;
    }

    private MethodModel(ILBasicBlockGraph graph)
    {
        Graph = graph;
        Method = graph.Method;
        Instructions = graph.Instructions;
        _instructionIndices = graph.InstructionIndices;
        Blocks = graph.Blocks;
        EntryBlocks = graph.EntryBlocks;
        _blockByInstruction = graph.BlockByInstruction;
        _variableCount = Method.HasBody ? Method.Body.Variables.Count : 0;
    }

    public ILBasicBlockGraph Graph { get; }
    public MethodDefinition Method { get; }
    public Instruction[] Instructions { get; }
    public IReadOnlyList<BasicBlock> Blocks { get; }
    public IReadOnlyList<BasicBlock> EntryBlocks { get; }
    public IReadOnlyList<TargetExpressionNode> ValueCandidates => _valueCandidates;
    public IReadOnlyList<TargetEffect> EffectCandidates => _effectCandidates;

    public LocalDefinitionIndex LocalDefinitions => _localDefinitions ??= LocalDefinitionIndex.Create(this);

    /// <summary>模型构建期发现的不可表达 IL；与具体 pattern 无关，对该方法的所有匹配生效。</summary>
    public IReadOnlyList<MatchDiagnostic> ModelDiagnostics => _modelDiagnostics.Diagnostics;

    public static MethodModel Create(MethodDefinition method)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (!method.HasBody)
            throw new ArgumentException("The method must have a CIL body.", nameof(method));
        if (method.Body.Instructions.Count == 0)
            throw new ArgumentException("The method body is empty.", nameof(method));

        return Create(ILBasicBlockGraphBuilder.Build(method));
    }

    public static MethodModel Create(ILBasicBlockGraph graph)
    {
        if (graph is null)
            throw new ArgumentNullException(nameof(graph));
        var model = new MethodModel(graph);
        model.BuildExpressions();
        return model;
    }

    public bool IsStale
        => Graph.IsStale || (Method.HasBody && Method.Body.Variables.Count != _variableCount);

    public int IndexOf(Instruction instruction)
        => _instructionIndices.TryGetValue(instruction, out var index) ? index : -1;

    public BasicBlock BlockOf(Instruction instruction) => _blockByInstruction[instruction];

    public bool TryGetInstructionResult(Instruction instruction, out TargetExpressionNode result)
        => _instructionResults.TryGetValue(instruction, out result!);

    public bool TryGetStoredValue(Instruction storeInstruction, out TargetExpressionNode value)
        => _localStoreValues.TryGetValue(storeInstruction, out value!);

    public bool TryGetConditionExpression(BasicBlock block, out TargetExpressionNode expression)
        => _conditionExpressions.TryGetValue(block, out expression!);

    public BasicBlock ResolveTransparentTarget(BasicBlock block, bool enabled)
    {
        if (!enabled)
            return block;

        var seen = new HashSet<BasicBlock>();
        while (seen.Add(block) && IsTransparentForwarder(block, out var next))
            block = next;
        return block;
    }

    private bool IsTransparentForwarder(BasicBlock block, out BasicBlock next)
    {
        next = null!;
        if (block.Successors.Count != 1 ||
            block.Successors[0].Kind != ControlFlowEdgeKind.Unconditional ||
            Instructions[block.EndIndex].OpCode.Code is not Code.Br and not Code.Br_S) //跳转只允许 br/br_s
            return false;

        for (var i = block.StartIndex; i < block.EndIndex; i++)
        {
            if (Instructions[i].OpCode.Code != Code.Nop)
                return false;
        }

        next = block.Successors[0].To;
        return true;
    }

    private void BuildExpressions()
    {
        var entryDepth = ComputeEntryDepths();
        foreach (var block in Blocks)
        {
            _stack.Clear();
            var depth = entryDepth.TryGetValue(block, out var knownDepth) ? knownDepth : 0;
            for (var i = 0; i < depth; i++)
                _stack.Add(new TargetUnknownNode(null, block.Leader, "incoming stack slot"));

            for (var index = block.StartIndex; index <= block.EndIndex; index++)
                SimulateInstruction(Instructions[index], block);
        }
    }

    private Dictionary<BasicBlock, int> ComputeEntryDepths()
    {
        var result = new Dictionary<BasicBlock, int>();
        var queue = new Queue<BasicBlock>();

        Seed(Blocks[0], 0);
        foreach (var handler in Method.Body.ExceptionHandlers)
        {
            if (handler.FilterStart is not null)
                Seed(BlockOf(handler.FilterStart), 1);
            if (handler.HandlerStart is not null)
            {
                var depth = handler.HandlerType is ExceptionHandlerType.Catch or ExceptionHandlerType.Filter ? 1 : 0;
                Seed(BlockOf(handler.HandlerStart), depth);
            }
        }

        void Seed(BasicBlock block, int depth)
        {
            if (result.TryGetValue(block, out var current) && current == depth)
                return;
            result[block] = depth;
            queue.Enqueue(block);
        }

        while (queue.Count != 0)
        {
            var block = queue.Dequeue();
            var depth = result[block];
            for (var i = block.StartIndex; i <= block.EndIndex; i++)
            {
                var instruction = Instructions[i];
                if (instruction.OpCode.StackBehaviourPop == StackBehaviour.PopAll)
                    depth = 0;
                else
                    depth = Math.Max(0, depth - SafePopCount(instruction));
                depth += SafePushCount(instruction);
            }

            foreach (var edge in block.Successors)
            {
                if (!result.TryGetValue(edge.To, out var old))
                {
                    result[edge.To] = depth;
                    queue.Enqueue(edge.To);
                }
                else if (old != depth)
                {
                    //防止异常 IL 代码造成程序异常
                    var merged = Math.Min(old, depth);
                    if (merged != old)
                    {
                        result[edge.To] = merged;
                        queue.Enqueue(edge.To);
                    }
                }
            }
        }

        return result;
    }

    private int SafePopCount(Instruction instruction)
    {
        try { return instruction.PopCount(Method); }
        catch { return 0; }
    }

    private static int SafePushCount(Instruction instruction)
    {
        try { return instruction.PushCount(); }
        catch { return 0; }
    }

    private TargetExpressionNode Pop(Instruction instruction)
    {
        if (_stack.Count == 0)
        {
            _modelDiagnostics.Report(MatchDiagnosticKind.UnsupportedInstruction, instruction,
                "Stack underflow while building the pattern model; expressions feeding this instruction cannot be matched.");
            return new TargetUnknownNode(null, instruction, "stack underflow while building pattern model");
        }
        var value = _stack[_stack.Count - 1];
        _stack.RemoveAt(_stack.Count - 1);
        return value;
    }

    private void Push(Instruction instruction, TargetExpressionNode node, bool candidate = true)
    {
        _stack.Add(node);
        _instructionResults[instruction] = node;
        if (candidate)
            _valueCandidates.Add(node); //任何一次压栈均可做 value 候选
    }

    private void PushInt(Instruction instruction, int value)
        => Push(instruction, new TargetConstantNode(
            value >= -1 && value <= 8 ? BoxedSmallInts[value + 1] : value,
            Method.Module.TypeSystem.Int32, instruction));

    private void PushUnknown(Instruction instruction, int count)
    {
        var code = instruction.OpCode.Code;
        _modelDiagnostics.Report(MatchDiagnosticKind.UnsupportedInstruction, instruction,
            $"'{code}' has an operand the pattern model cannot resolve; expressions covering it cannot be matched.");
        for (var i = 0; i < count; i++)
            Push(instruction, new TargetUnknownNode(null, instruction, "unsupported " + code));
    }

    private void PushBinary(Instruction instruction, ExpressionType operation, TypeReference? resultType = null)
    {
        var right = Pop(instruction);
        var left = Pop(instruction);
        Push(instruction, new TargetBinaryNode(operation, left, right, resultType ?? left.ResultType, instruction));
    }

    private void PushUnary(Instruction instruction, ExpressionType operation)
    {
        var operand = Pop(instruction);
        Push(instruction, new TargetUnaryNode(operation, operand, operand.ResultType, instruction));
    }

    private void SetBranchComparison(Instruction instruction, BasicBlock block, ExpressionType operation)
    {
        var right = Pop(instruction);
        var left = Pop(instruction);
        _conditionExpressions[block] = new TargetBinaryNode(operation, left, right,
            Method.Module.TypeSystem.Boolean, instruction);
    }

    private void SimulateInstruction(Instruction instruction, BasicBlock block)
    {
        var code = instruction.OpCode.Code;

        if (CecilInstructionHelpers.TryGetArgument(Method, instruction, out var isThis, out var parameterIndex, out var parameter)
            && code is Code.Ldarg or Code.Ldarg_S or Code.Ldarg_0 or Code.Ldarg_1 or Code.Ldarg_2 or Code.Ldarg_3)
        {
            var type = isThis
                ? Method.DeclaringType
                : parameter?.ParameterType ?? Method.Module.TypeSystem.Object;
            Push(instruction, new TargetArgumentNode(type, instruction, isThis, parameterIndex, parameter,
                CreateArgumentStackType(type, isThis, parameter)));
            return;
        }

        if (CecilInstructionHelpers.IsLoadArgumentAddress(code)
            && CecilInstructionHelpers.TryGetArgument(Method, instruction, out var addressIsThis,
                out var addressParameterIndex, out var addressParameter))
        {
            var type = addressIsThis
                ? Method.DeclaringType
                : addressParameter?.ParameterType ?? Method.Module.TypeSystem.Object;
            //地址本身不能作为 value 候选被改写，只作为 struct 接收者 / ref 实参的载体
            Push(instruction, new TargetAddressNode(new TargetArgumentNode(type, instruction, addressIsThis,
                addressParameterIndex, addressParameter,
                CreateArgumentStackType(type, addressIsThis, addressParameter)), instruction), candidate: false);
            return;
        }

        if (CecilInstructionHelpers.IsLoadLocal(instruction)
            && CecilInstructionHelpers.TryGetLocal(Method, instruction, out _, out var loadedLocal)
            && loadedLocal is not null)
        {
            Push(instruction, new TargetLocalReadNode(loadedLocal, instruction));
            return;
        }

        if (CecilInstructionHelpers.IsLoadLocalAddress(instruction)
            && CecilInstructionHelpers.TryGetLocal(Method, instruction, out _, out var addressedLocal)
            && addressedLocal is not null)
        {
            Push(instruction, new TargetAddressNode(new TargetLocalReadNode(addressedLocal, instruction), instruction),
                candidate: false);
            return;
        }

        if (CecilInstructionHelpers.IsStoreLocal(instruction)
            && CecilInstructionHelpers.TryGetLocal(Method, instruction, out _, out var storedLocal)
            && storedLocal is not null)
        {
            _localStoreValues[instruction] = Pop(instruction);
            return;
        }

        switch (code)
        {
            case Code.Nop:
            case Code.Break:
                return;

            case Code.Ldnull:
                Push(instruction, new TargetConstantNode(null, Method.Module.TypeSystem.Object, instruction, StackType.Null));
                return;
            case Code.Ldstr:
                Push(instruction, new TargetConstantNode(instruction.Operand, Method.Module.TypeSystem.String, instruction));
                return;
            case Code.Ldc_I4_M1: PushInt(instruction, -1); return;
            case Code.Ldc_I4_0: PushInt(instruction, 0); return;
            case Code.Ldc_I4_1: PushInt(instruction, 1); return;
            case Code.Ldc_I4_2: PushInt(instruction, 2); return;
            case Code.Ldc_I4_3: PushInt(instruction, 3); return;
            case Code.Ldc_I4_4: PushInt(instruction, 4); return;
            case Code.Ldc_I4_5: PushInt(instruction, 5); return;
            case Code.Ldc_I4_6: PushInt(instruction, 6); return;
            case Code.Ldc_I4_7: PushInt(instruction, 7); return;
            case Code.Ldc_I4_8: PushInt(instruction, 8); return;
            case Code.Ldc_I4:
            case Code.Ldc_I4_S:
                PushInt(instruction, Convert.ToInt32(instruction.Operand)); return;
            case Code.Ldc_I8:
                Push(instruction, new TargetConstantNode(instruction.Operand, Method.Module.TypeSystem.Int64, instruction)); return;
            case Code.Ldc_R4:
                Push(instruction, new TargetConstantNode(instruction.Operand, Method.Module.TypeSystem.Single, instruction)); return;
            case Code.Ldc_R8:
                Push(instruction, new TargetConstantNode(instruction.Operand, Method.Module.TypeSystem.Double, instruction)); return;

            case Code.Dup:
            {
                var value = Pop(instruction);
                _stack.Add(value);
                _stack.Add(value);
                _instructionResults[instruction] = value;
                _duplicatedNodes.Add(value); //该值有多个消费者，覆盖它的区间不能整体删除
                return;
            }
            case Code.Pop:
                _effectCandidates.Add(new TargetEffect(Pop(instruction), instruction)); //类似于 _ = xxxxx();
                return;

            case Code.Ldfld:
            {
                var instance = Pop(instruction);
                if (instruction.Operand is FieldReference field)
                    Push(instruction, new TargetFieldNode(field, instance, instruction));
                else
                    PushUnknown(instruction, 1);
                return;
            }
            case Code.Ldsfld:
                if (instruction.Operand is FieldReference staticField)
                    Push(instruction, new TargetFieldNode(staticField, null, instruction));
                else
                    PushUnknown(instruction, 1);
                return;

            case Code.Ldflda:
            {
                _modelDiagnostics.Report(MatchDiagnosticKind.UnsupportedInstruction, instruction,
                    $"'{code}' takes a field address; expressions covering it cannot be matched.");
                var instance = Pop(instruction);
                var resultType = instruction.Operand is FieldReference field
                    ? new ByReferenceType(field.FieldType)
                    : null;
                Push(instruction, new TargetOperationNode(code, new[] { instance }, resultType, instruction));
                return;
            }
            case Code.Ldsflda:
            {
                _modelDiagnostics.Report(MatchDiagnosticKind.UnsupportedInstruction, instruction,
                    $"'{code}' takes a field address; expressions covering it cannot be matched.");
                var resultType = instruction.Operand is FieldReference field
                    ? new ByReferenceType(field.FieldType)
                    : null;
                Push(instruction, new TargetOperationNode(code, Array.Empty<TargetExpressionNode>(), resultType, instruction));
                return;
            }

            case Code.Newarr:
            {
                var length = Pop(instruction);
                if (instruction.Operand is TypeReference elementType)
                    Push(instruction, new TargetNewArrayNode(elementType, new[] { length }, instruction));
                else
                    PushUnknown(instruction, 1);
                return;
            }

            case Code.Ldlen:
            {
                var array = Pop(instruction);
                Push(instruction, new TargetArrayLengthNode(array, Method.Module.TypeSystem.Int32, instruction));
                return;
            }

            case Code.Ldelema:
            {
                var index = Pop(instruction);
                var array = Pop(instruction);
                Push(instruction, new TargetAddressNode(new TargetArrayElementNode(array, index,
                    ResolveArrayElementType(array, instruction), instruction), instruction), candidate: false);
                return;
            }

            case Code.Ldelem_Any:
            case Code.Ldelem_I:
            case Code.Ldelem_I1:
            case Code.Ldelem_I2:
            case Code.Ldelem_I4:
            case Code.Ldelem_I8:
            case Code.Ldelem_R4:
            case Code.Ldelem_R8:
            case Code.Ldelem_Ref:
            case Code.Ldelem_U1:
            case Code.Ldelem_U2:
            case Code.Ldelem_U4:
            {
                var index = Pop(instruction);
                var array = Pop(instruction);
                Push(instruction, new TargetArrayElementNode(array, index,
                    ResolveArrayElementType(array, instruction), instruction));
                return;
            }

            case Code.Stelem_Any:
            case Code.Stelem_I:
            case Code.Stelem_I1:
            case Code.Stelem_I2:
            case Code.Stelem_I4:
            case Code.Stelem_I8:
            case Code.Stelem_R4:
            case Code.Stelem_R8:
            case Code.Stelem_Ref:
            {
                var value = Pop(instruction);
                var index = Pop(instruction);
                var array = Pop(instruction);
                //同字段写入：操作数被 dup 复用时删除区间会破坏其他消费者的栈值
                if (!_duplicatedNodes.Contains(value) && !_duplicatedNodes.Contains(index)
                    && !_duplicatedNodes.Contains(array))
                {
                    _effectCandidates.Add(new TargetEffect(new TargetArrayStoreNode(array, index, value,
                        ResolveArrayElementType(array, instruction), instruction), instruction));//类似于 value[x] = y;
                }
                else
                {
                    ReportReusedStoreResult(instruction, code);
                }
                return;
            }

            case Code.Call:
            case Code.Callvirt:
            case Code.Newobj:
            case Code.Calli:
                SimulateCall(instruction);
                return;

            case Code.Add:
                PushBinary(instruction, ExpressionType.Add); return;
            case Code.Add_Ovf:
            case Code.Add_Ovf_Un:
                PushBinary(instruction, ExpressionType.AddChecked); return;
            case Code.Sub:
                PushBinary(instruction, ExpressionType.Subtract); return;
            case Code.Sub_Ovf:
            case Code.Sub_Ovf_Un:
                PushBinary(instruction, ExpressionType.SubtractChecked); return;
            case Code.Mul:
                PushBinary(instruction, ExpressionType.Multiply); return;
            case Code.Mul_Ovf:
            case Code.Mul_Ovf_Un:
                PushBinary(instruction, ExpressionType.MultiplyChecked); return;
            case Code.Div:
            case Code.Div_Un:
                PushBinary(instruction, ExpressionType.Divide); return;
            case Code.Rem:
            case Code.Rem_Un:
                PushBinary(instruction, ExpressionType.Modulo); return;
            case Code.And:
                PushBinary(instruction, ExpressionType.And); return;
            case Code.Or:
                PushBinary(instruction, ExpressionType.Or); return;
            case Code.Xor:
                PushBinary(instruction, ExpressionType.ExclusiveOr); return;
            case Code.Shl:
                PushBinary(instruction, ExpressionType.LeftShift); return;
            case Code.Shr:
            case Code.Shr_Un:
                PushBinary(instruction, ExpressionType.RightShift); return;
            case Code.Ceq:
                PushBinary(instruction, ExpressionType.Equal, Method.Module.TypeSystem.Boolean); return;
            case Code.Cgt:
            case Code.Cgt_Un:
                PushBinary(instruction, ExpressionType.GreaterThan, Method.Module.TypeSystem.Boolean); return;
            case Code.Clt:
            case Code.Clt_Un:
                PushBinary(instruction, ExpressionType.LessThan, Method.Module.TypeSystem.Boolean); return;

            case Code.Neg:
                PushUnary(instruction, ExpressionType.Negate); return;
            case Code.Not:
                PushUnary(instruction, ExpressionType.Not); return;

            case Code.Castclass:
            case Code.Isinst:
            case Code.Box:
            case Code.Unbox_Any:
            case Code.Conv_I:
            case Code.Conv_I1:
            case Code.Conv_I2:
            case Code.Conv_I4:
            case Code.Conv_I8:
            case Code.Conv_U:
            case Code.Conv_U1:
            case Code.Conv_U2:
            case Code.Conv_U4:
            case Code.Conv_U8:
            case Code.Conv_R4:
            case Code.Conv_R8:
            case Code.Conv_R_Un:
            case Code.Conv_Ovf_I:
            case Code.Conv_Ovf_I_Un:
            case Code.Conv_Ovf_I1:
            case Code.Conv_Ovf_I1_Un:
            case Code.Conv_Ovf_I2:
            case Code.Conv_Ovf_I2_Un:
            case Code.Conv_Ovf_I4:
            case Code.Conv_Ovf_I4_Un:
            case Code.Conv_Ovf_I8:
            case Code.Conv_Ovf_I8_Un:
            case Code.Conv_Ovf_U:
            case Code.Conv_Ovf_U_Un:
            case Code.Conv_Ovf_U1:
            case Code.Conv_Ovf_U1_Un:
            case Code.Conv_Ovf_U2:
            case Code.Conv_Ovf_U2_Un:
            case Code.Conv_Ovf_U4:
            case Code.Conv_Ovf_U4_Un:
            case Code.Conv_Ovf_U8:
            case Code.Conv_Ovf_U8_Un:
            {
                var operand = Pop(instruction);
                var targetType = instruction.Operand as TypeReference ?? InferConversionType(code);
                StackType? resultStackType = null;
                var operation = IsOverflowConversion(code)
                    ? ExpressionType.ConvertChecked
                    : code == Code.Isinst ? ExpressionType.TypeAs : ExpressionType.Convert;
                if (code == Code.Box)
                {
                    resultStackType = CreateBoxStackType(targetType);
                    targetType = Method.Module.TypeSystem.Object;
                }
                Push(instruction, new TargetUnaryNode(operation, operand, targetType, instruction, resultStackType));
                return;
            }

            case Code.Brtrue:
            case Code.Brtrue_S:
            case Code.Brfalse:
            case Code.Brfalse_S:
                _conditionExpressions[block] = Pop(instruction);
                return;

            case Code.Beq:
            case Code.Beq_S:
                SetBranchComparison(instruction, block, ExpressionType.Equal); return;
            case Code.Bne_Un:
            case Code.Bne_Un_S:
                SetBranchComparison(instruction, block, ExpressionType.NotEqual); return;
            case Code.Bgt:
            case Code.Bgt_S:
            case Code.Bgt_Un:
            case Code.Bgt_Un_S:
                SetBranchComparison(instruction, block, ExpressionType.GreaterThan); return;
            case Code.Bge:
            case Code.Bge_S:
            case Code.Bge_Un:
            case Code.Bge_Un_S:
                SetBranchComparison(instruction, block, ExpressionType.GreaterThanOrEqual); return;
            case Code.Blt:
            case Code.Blt_S:
            case Code.Blt_Un:
            case Code.Blt_Un_S:
                SetBranchComparison(instruction, block, ExpressionType.LessThan); return;
            case Code.Ble:
            case Code.Ble_S:
            case Code.Ble_Un:
            case Code.Ble_Un_S:
                SetBranchComparison(instruction, block, ExpressionType.LessThanOrEqual); return;

            case Code.Ret:
                if (!Method.ReturnType.MetadataType.Equals(MetadataType.Void))
                    Pop(instruction);
                return;
            case Code.Throw:
                Pop(instruction);
                return;
            case Code.Stfld:
            {
                var value = Pop(instruction);
                var instance = Pop(instruction);
                //赋值结果被 dup 复用（如 return obj.F = x;）时，删除该区间会拿掉其他消费者依赖的值，
                //因此不作为独立 effect 提供。
                if (instruction.Operand is FieldReference field)
                {
                    if (!_duplicatedNodes.Contains(value) && !_duplicatedNodes.Contains(instance))
                    {
                        _effectCandidates.Add(new TargetEffect(
                            new TargetFieldStoreNode(field, instance, value, instruction), instruction)); //obj.F = x;
                    }
                    else
                    {
                        ReportReusedStoreResult(instruction, code);
                    }
                }
                return;
            }
            case Code.Stsfld:
            {
                var value = Pop(instruction);
                if (instruction.Operand is FieldReference field)
                {
                    if (!_duplicatedNodes.Contains(value))
                    {
                        _effectCandidates.Add(new TargetEffect(
                            new TargetFieldStoreNode(field, null, value, instruction), instruction)); //Type.F = x;
                    }
                    else
                    {
                        ReportReusedStoreResult(instruction, code);
                    }
                }
                return;
            }
        }

        //保守 fallback。它保持 stack shape 可用，但不会假装理解当前 pattern DSL 无法表达的 operation。
        SimulateUnknown(instruction, reportUnsupported: true);
    }

    /// <summary>
    /// 按 opcode 的栈行为压/弹占位节点。leave/endfinally 这类 PopAll 指令只是清空求值栈，
    /// </summary>
    private void SimulateUnknown(Instruction instruction, bool reportUnsupported)
    {
        var code = instruction.OpCode.Code;
        var popCount = SafePopCount(instruction);
        if (popCount == 0xFF || instruction.OpCode.StackBehaviourPop == StackBehaviour.PopAll)
        {
            _stack.Clear();
            return;
        }

        var inputs = popCount == 0 ? Array.Empty<TargetExpressionNode>() : new TargetExpressionNode[popCount];
        for (var i = popCount - 1; i >= 0; i--)
            inputs[i] = Pop(instruction);
        var pushCount = SafePushCount(instruction);
        if (reportUnsupported && (popCount > 0 || pushCount > 0))
        {
            _modelDiagnostics.Report(MatchDiagnosticKind.UnsupportedInstruction, instruction,
                $"'{code}' is not supported by the pattern model; expressions covering it cannot be matched.");
        }
        for (var i = 0; i < pushCount; i++)
            Push(instruction, new TargetOperationNode(code, inputs, null, instruction));
    }

    //赋值结果被后续代码使用（如 return obj.F = x;）时该写入不作为可删除 effect 提供，
    //这里记录原因，便于匹配为空时解释。
    private void ReportReusedStoreResult(Instruction instruction, Code code)
        => _modelDiagnostics.Report(MatchDiagnosticKind.UnsupportedInstruction, instruction,
            $"The result of this '{code}' is reused by later code, so the store is not offered as a removable effect.");

    private static StackType CreateArgumentStackType(TypeReference type, bool isThis,
        ParameterDefinition? parameter)
    {
        var flags = isThis
            ? StackTypeFlags.ThisPtr
            : parameter?.IsIn == true ? StackTypeFlags.ReadOnly : StackTypeFlags.None;

        return isThis && type.IsValueType
            ? StackType.CreateByRef(type, flags)
            : StackType.Create(type, flags);
    }

    private static StackType CreateBoxStackType(TypeReference? type)
    {
        if (type is null)
            return StackType.Invalid;

        try
        {
            return StackType.CreateBoxed(type);
        }
        catch
        {
            return StackType.Invalid;
        }
    }

    private void SimulateCall(Instruction instruction)
    {
        if (instruction.Operand is not MethodReference method)
        {
            _modelDiagnostics.Report(MatchDiagnosticKind.UnsupportedInstruction, instruction,
                $"'{instruction.OpCode.Code}' does not call a direct method reference; expressions covering it cannot be matched.");
            SimulateUnknown(instruction, reportUnsupported: false);
            return;
        }

        var arguments = new TargetExpressionNode[method.Parameters.Count];
        for (var i = arguments.Length - 1; i >= 0; i--)
            arguments[i] = Pop(instruction);

        TargetExpressionNode? instance = null;
        if (instruction.OpCode.Code != Code.Newobj && method.HasThis)
            instance = Pop(instruction);

        TypeReference? resultType = instruction.OpCode.Code == Code.Newobj
            ? method.DeclaringType
            : method.ReturnType.MetadataType == MetadataType.Void
                ? null
                : CecilHelper.InflateDeclaringGenerics(method.ReturnType, method.DeclaringType);

        var node = new TargetCallNode(method, instance, arguments, resultType, instruction);
        if (resultType is null)
            _effectCandidates.Add(new TargetEffect(node, instruction)); //void call 也算 effect
        else
            Push(instruction, node);
    }

    private static bool IsOverflowConversion(Code code)
        => code is Code.Conv_Ovf_I or Code.Conv_Ovf_I_Un
            or Code.Conv_Ovf_I1 or Code.Conv_Ovf_I1_Un
            or Code.Conv_Ovf_I2 or Code.Conv_Ovf_I2_Un
            or Code.Conv_Ovf_I4 or Code.Conv_Ovf_I4_Un
            or Code.Conv_Ovf_I8 or Code.Conv_Ovf_I8_Un
            or Code.Conv_Ovf_U or Code.Conv_Ovf_U_Un
            or Code.Conv_Ovf_U1 or Code.Conv_Ovf_U1_Un
            or Code.Conv_Ovf_U2 or Code.Conv_Ovf_U2_Un
            or Code.Conv_Ovf_U4 or Code.Conv_Ovf_U4_Un
            or Code.Conv_Ovf_U8 or Code.Conv_Ovf_U8_Un;

    private TypeReference? ResolveArrayElementType(TargetExpressionNode array, Instruction instruction)
    {
        if (instruction.Operand is TypeReference operandType)
            return operandType;

        if (array.ResultType is ArrayType arrayType)
            return arrayType.ElementType;

        var ts = Method.Module.TypeSystem;
        return instruction.OpCode.Code switch
        {
            Code.Ldelem_I1 => ts.SByte,
            Code.Ldelem_U1 => ts.Byte,
            Code.Ldelem_I2 => ts.Int16,
            Code.Ldelem_U2 => ts.UInt16,
            Code.Ldelem_I4 => ts.Int32,
            Code.Ldelem_U4 => ts.UInt32,
            Code.Ldelem_I8 => ts.Int64,
            Code.Ldelem_I => ts.IntPtr,
            Code.Ldelem_R4 => ts.Single,
            Code.Ldelem_R8 => ts.Double,
            Code.Ldelem_Ref => ts.Object,
            Code.Stelem_I1 => ts.SByte,
            Code.Stelem_I2 => ts.Int16,
            Code.Stelem_I4 => ts.Int32,
            Code.Stelem_I8 => ts.Int64,
            Code.Stelem_I => ts.IntPtr,
            Code.Stelem_R4 => ts.Single,
            Code.Stelem_R8 => ts.Double,
            Code.Stelem_Ref => ts.Object,
            _ => null
        };
    }

    private TypeReference? InferConversionType(Code code)
    {
        var ts = Method.Module.TypeSystem;
        return code switch
        {
            Code.Conv_I1 or Code.Conv_Ovf_I1 or Code.Conv_Ovf_I1_Un => ts.SByte,
            Code.Conv_U1 or Code.Conv_Ovf_U1 or Code.Conv_Ovf_U1_Un => ts.Byte,
            Code.Conv_I2 or Code.Conv_Ovf_I2 or Code.Conv_Ovf_I2_Un => ts.Int16,
            Code.Conv_U2 or Code.Conv_Ovf_U2 or Code.Conv_Ovf_U2_Un => ts.UInt16,
            Code.Conv_I4 or Code.Conv_Ovf_I4 or Code.Conv_Ovf_I4_Un => ts.Int32,
            Code.Conv_U4 or Code.Conv_Ovf_U4 or Code.Conv_Ovf_U4_Un => ts.UInt32,
            Code.Conv_I8 or Code.Conv_Ovf_I8 or Code.Conv_Ovf_I8_Un => ts.Int64,
            Code.Conv_U8 or Code.Conv_Ovf_U8 or Code.Conv_Ovf_U8_Un => ts.UInt64,
            Code.Conv_R4 => ts.Single,
            Code.Conv_R8 or Code.Conv_R_Un => ts.Double,
            Code.Conv_I or Code.Conv_Ovf_I or Code.Conv_Ovf_I_Un => ts.IntPtr,
            Code.Conv_U or Code.Conv_Ovf_U or Code.Conv_Ovf_U_Un => ts.UIntPtr,
            _ => null
        };
    }
}

