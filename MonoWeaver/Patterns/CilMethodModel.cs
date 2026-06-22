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
/// pattern matcher 使用的刻意保持小型的 symbolic model。它不尝试 decompile 整个 method；
/// 只保留 pattern candidate 需要的 expression dependency。
/// </summary>
internal sealed class CilMethodModel
{
    private readonly Dictionary<Instruction, int> _instructionIndices;
    private readonly Dictionary<Instruction, BasicBlock> _blockByInstruction;
    private readonly Dictionary<Instruction, TargetExpressionNode> _instructionResults = new();
    private readonly Dictionary<Instruction, TargetExpressionNode> _localStoreValues = new();
    private readonly Dictionary<BasicBlock, TargetExpressionNode> _conditionExpressions = new();
    private readonly List<TargetExpressionNode> _valueCandidates = new();
    private readonly List<TargetEffect> _effectCandidates = new();

    private CilMethodModel(ILBasicBlockGraph graph)
    {
        Method = graph.Method;
        Instructions = graph.Instructions;
        _instructionIndices = graph.InstructionIndices;
        Blocks = graph.Blocks;
        _blockByInstruction = graph.BlockByInstruction;
    }

    public MethodDefinition Method { get; }
    public Instruction[] Instructions { get; }
    public IReadOnlyList<BasicBlock> Blocks { get; }
    public IReadOnlyList<TargetExpressionNode> ValueCandidates => _valueCandidates;
    public IReadOnlyList<TargetEffect> EffectCandidates => _effectCandidates;
    public LocalDefinitionIndex LocalDefinitions { get; private set; } = null!;

    public static CilMethodModel Create(MethodDefinition method)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (!method.HasBody)
            throw new ArgumentException("The method must have a CIL body.", nameof(method));
        if (method.Body.Instructions.Count == 0)
            throw new ArgumentException("The method body is empty.", nameof(method));

        var graph = ILBasicBlockGraphBuilder.Build(method);
        var model = new CilMethodModel(graph);
        model.BuildExpressions();
        model.LocalDefinitions = LocalDefinitionIndex.Create(model);
        return model;
    }

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
        if (block.Successors.Count != 1 || block.Successors[0].Kind != ControlFlowEdgeKind.Unconditional)
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
            var stack = new List<TargetExpressionNode>();
            var depth = entryDepth.TryGetValue(block, out var knownDepth) ? knownDepth : 0;
            for (var i = 0; i < depth; i++)
                stack.Add(new TargetUnknownNode(null, block.Leader, $"incoming stack slot {i}"));

            for (var index = block.StartIndex; index <= block.EndIndex; index++)
                SimulateInstruction(Instructions[index], stack, block);
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
                    // authoritative diagnostic 由 verifier 负责。matcher 保留较低 depth，
                    // 避免 malformed 或 unverifiable code 导致 simulation 越界。
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

    private void SimulateInstruction(Instruction instruction, List<TargetExpressionNode> stack,
        BasicBlock block)
    {
        TargetExpressionNode Pop()
        {
            if (stack.Count == 0)
                return new TargetUnknownNode(null, instruction, "stack underflow while building pattern model");
            var value = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            return value;
        }

        void Push(TargetExpressionNode node, bool candidate = true)
        {
            stack.Add(node);
            _instructionResults[instruction] = node;
            if (candidate)
                _valueCandidates.Add(node);
        }

        var code = instruction.OpCode.Code;

        if (CecilInstructionHelpers.TryGetArgument(Method, instruction, out var isThis, out var parameterIndex, out var parameter)
            && code is Code.Ldarg or Code.Ldarg_S or Code.Ldarg_0 or Code.Ldarg_1 or Code.Ldarg_2 or Code.Ldarg_3)
        {
            var type = isThis
                ? Method.DeclaringType
                : parameter?.ParameterType ?? Method.Module.TypeSystem.Object;
            Push(new TargetArgumentNode(type, instruction, isThis, parameterIndex, parameter,
                CreateArgumentStackType(type, isThis, parameter)));
            return;
        }

        if (CecilInstructionHelpers.IsLoadLocal(instruction)
            && CecilInstructionHelpers.TryGetLocal(Method, instruction, out _, out var loadedLocal)
            && loadedLocal is not null)
        {
            Push(new TargetLocalReadNode(loadedLocal, instruction));
            return;
        }

        if (CecilInstructionHelpers.IsStoreLocal(instruction)
            && CecilInstructionHelpers.TryGetLocal(Method, instruction, out _, out var storedLocal)
            && storedLocal is not null)
        {
            _localStoreValues[instruction] = Pop();
            return;
        }

        switch (code)
        {
            case Code.Nop:
            case Code.Break:
                return;

            case Code.Ldnull:
                Push(new TargetConstantNode(null, Method.Module.TypeSystem.Object, instruction, StackType.Null));
                return;
            case Code.Ldstr:
                Push(new TargetConstantNode(instruction.Operand, Method.Module.TypeSystem.String, instruction));
                return;
            case Code.Ldc_I4_M1: PushInt(-1); return;
            case Code.Ldc_I4_0: PushInt(0); return;
            case Code.Ldc_I4_1: PushInt(1); return;
            case Code.Ldc_I4_2: PushInt(2); return;
            case Code.Ldc_I4_3: PushInt(3); return;
            case Code.Ldc_I4_4: PushInt(4); return;
            case Code.Ldc_I4_5: PushInt(5); return;
            case Code.Ldc_I4_6: PushInt(6); return;
            case Code.Ldc_I4_7: PushInt(7); return;
            case Code.Ldc_I4_8: PushInt(8); return;
            case Code.Ldc_I4:
            case Code.Ldc_I4_S:
                PushInt(Convert.ToInt32(instruction.Operand)); return;
            case Code.Ldc_I8:
                Push(new TargetConstantNode(instruction.Operand, Method.Module.TypeSystem.Int64, instruction)); return;
            case Code.Ldc_R4:
                Push(new TargetConstantNode(instruction.Operand, Method.Module.TypeSystem.Single, instruction)); return;
            case Code.Ldc_R8:
                Push(new TargetConstantNode(instruction.Operand, Method.Module.TypeSystem.Double, instruction)); return;

            case Code.Dup:
            {
                var value = Pop();
                stack.Add(value);
                stack.Add(value);
                _instructionResults[instruction] = value;
                return;
            }
            case Code.Pop:
                _effectCandidates.Add(new TargetEffect(Pop(), instruction));
                return;

            case Code.Ldfld:
            {
                var instance = Pop();
                if (instruction.Operand is FieldReference field)
                    Push(new TargetFieldNode(field, instance, instruction));
                else
                    PushUnknown(1);
                return;
            }
            case Code.Ldsfld:
                if (instruction.Operand is FieldReference staticField)
                    Push(new TargetFieldNode(staticField, null, instruction));
                else
                    PushUnknown(1);
                return;

            case Code.Ldflda:
            {
                var instance = Pop();
                var resultType = instruction.Operand is FieldReference field
                    ? new ByReferenceType(field.FieldType)
                    : null;
                Push(new TargetOperationNode(code, new[] { instance }, resultType, instruction));
                return;
            }
            case Code.Ldsflda:
            {
                var resultType = instruction.Operand is FieldReference field
                    ? new ByReferenceType(field.FieldType)
                    : null;
                Push(new TargetOperationNode(code, Array.Empty<TargetExpressionNode>(), resultType, instruction));
                return;
            }

            case Code.Newarr:
            {
                var length = Pop();
                if (instruction.Operand is TypeReference elementType)
                    Push(new TargetNewArrayNode(elementType, new[] { length }, instruction));
                else
                    PushUnknown(1);
                return;
            }

            case Code.Ldlen:
            {
                var array = Pop();
                Push(new TargetArrayLengthNode(array, Method.Module.TypeSystem.Int32, instruction));
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
                var index = Pop();
                var array = Pop();
                Push(new TargetArrayElementNode(array, index,
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
                var value = Pop();
                var index = Pop();
                var array = Pop();
                _effectCandidates.Add(new TargetEffect(new TargetArrayStoreNode(array, index, value,
                    ResolveArrayElementType(array, instruction), instruction), instruction));
                return;
            }

            case Code.Call:
            case Code.Callvirt:
            case Code.Newobj:
            case Code.Calli:
                SimulateCall(instruction, stack, Pop, Push);
                return;

            case Code.Add:
                PushBinary(ExpressionType.Add); return;
            case Code.Add_Ovf:
            case Code.Add_Ovf_Un:
                PushBinary(ExpressionType.AddChecked); return;
            case Code.Sub:
                PushBinary(ExpressionType.Subtract); return;
            case Code.Sub_Ovf:
            case Code.Sub_Ovf_Un:
                PushBinary(ExpressionType.SubtractChecked); return;
            case Code.Mul:
                PushBinary(ExpressionType.Multiply); return;
            case Code.Mul_Ovf:
            case Code.Mul_Ovf_Un:
                PushBinary(ExpressionType.MultiplyChecked); return;
            case Code.Div:
            case Code.Div_Un:
                PushBinary(ExpressionType.Divide); return;
            case Code.Rem:
            case Code.Rem_Un:
                PushBinary(ExpressionType.Modulo); return;
            case Code.And:
                PushBinary(ExpressionType.And); return;
            case Code.Or:
                PushBinary(ExpressionType.Or); return;
            case Code.Xor:
                PushBinary(ExpressionType.ExclusiveOr); return;
            case Code.Shl:
                PushBinary(ExpressionType.LeftShift); return;
            case Code.Shr:
            case Code.Shr_Un:
                PushBinary(ExpressionType.RightShift); return;
            case Code.Ceq:
                PushBinary(ExpressionType.Equal, Method.Module.TypeSystem.Boolean); return;
            case Code.Cgt:
            case Code.Cgt_Un:
                PushBinary(ExpressionType.GreaterThan, Method.Module.TypeSystem.Boolean); return;
            case Code.Clt:
            case Code.Clt_Un:
                PushBinary(ExpressionType.LessThan, Method.Module.TypeSystem.Boolean); return;

            case Code.Neg:
                PushUnary(ExpressionType.Negate); return;
            case Code.Not:
                PushUnary(ExpressionType.Not); return;

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
                var operand = Pop();
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
                Push(new TargetUnaryNode(operation, operand, targetType, instruction, resultStackType));
                return;
            }

            case Code.Brtrue:
            case Code.Brtrue_S:
            case Code.Brfalse:
            case Code.Brfalse_S:
                _conditionExpressions[block] = Pop();
                return;

            case Code.Beq:
            case Code.Beq_S:
                SetBranchComparison(ExpressionType.Equal); return;
            case Code.Bne_Un:
            case Code.Bne_Un_S:
                SetBranchComparison(ExpressionType.NotEqual); return;
            case Code.Bgt:
            case Code.Bgt_S:
            case Code.Bgt_Un:
            case Code.Bgt_Un_S:
                SetBranchComparison(ExpressionType.GreaterThan); return;
            case Code.Bge:
            case Code.Bge_S:
            case Code.Bge_Un:
            case Code.Bge_Un_S:
                SetBranchComparison(ExpressionType.GreaterThanOrEqual); return;
            case Code.Blt:
            case Code.Blt_S:
            case Code.Blt_Un:
            case Code.Blt_Un_S:
                SetBranchComparison(ExpressionType.LessThan); return;
            case Code.Ble:
            case Code.Ble_S:
            case Code.Ble_Un:
            case Code.Ble_Un_S:
                SetBranchComparison(ExpressionType.LessThanOrEqual); return;

            case Code.Ret:
                if (!Method.ReturnType.MetadataType.Equals(MetadataType.Void))
                    Pop();
                return;
            case Code.Throw:
                Pop();
                return;
            case Code.Stfld:
                Pop(); // value
                Pop(); // instance
                return;
            case Code.Stsfld:
                Pop();
                return;
        }

        // 保守 fallback。它保持 stack shape 可用，但不会假装理解当前 pattern DSL
        // 无法表达的 operation。
        var inputs = new List<TargetExpressionNode>();
        var popCount = SafePopCount(instruction);
        for (var i = 0; i < popCount; i++)
            inputs.Insert(0, Pop());
        var pushCount = SafePushCount(instruction);
        for (var i = 0; i < pushCount; i++)
            Push(new TargetOperationNode(code, inputs, null, instruction));

        void PushInt(int value)
            => Push(new TargetConstantNode(value, Method.Module.TypeSystem.Int32, instruction));

        void PushUnknown(int count)
        {
            for (var i = 0; i < count; i++)
                Push(new TargetUnknownNode(null, instruction, $"unsupported {code}"));
        }

        void PushBinary(ExpressionType operation, TypeReference? resultType = null)
        {
            var right = Pop();
            var left = Pop();
            Push(new TargetBinaryNode(operation, left, right, resultType ?? left.ResultType, instruction));
        }

        void PushUnary(ExpressionType operation)
        {
            var operand = Pop();
            Push(new TargetUnaryNode(operation, operand, operand.ResultType, instruction));
        }

        void SetBranchComparison(ExpressionType operation)
        {
            var right = Pop();
            var left = Pop();
            _conditionExpressions[block] = new TargetBinaryNode(operation, left, right,
                Method.Module.TypeSystem.Boolean, instruction);
        }
    }

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

    private void SimulateCall(Instruction instruction, List<TargetExpressionNode> stack,
        Func<TargetExpressionNode> pop, Action<TargetExpressionNode, bool> push)
    {
        if (instruction.Operand is not MethodReference method)
        {
            var fallbackInputs = new List<TargetExpressionNode>();
            var popCount = SafePopCount(instruction);
            for (var i = 0; i < popCount; i++)
                fallbackInputs.Insert(0, pop());
            if (SafePushCount(instruction) != 0)
                push(new TargetOperationNode(instruction.OpCode.Code, fallbackInputs, null, instruction), true);
            return;
        }

        var arguments = new TargetExpressionNode[method.Parameters.Count];
        for (var i = arguments.Length - 1; i >= 0; i--)
            arguments[i] = pop();

        TargetExpressionNode? instance = null;
        if (instruction.OpCode.Code != Code.Newobj && method.HasThis)
            instance = pop();

        TypeReference? resultType = instruction.OpCode.Code == Code.Newobj
            ? method.DeclaringType
            : method.ReturnType.MetadataType == MetadataType.Void ? null : method.ReturnType;

        var node = new TargetCallNode(method, instance, arguments, resultType, instruction);
        if (resultType is null)
            _effectCandidates.Add(new TargetEffect(node, instruction));
        else
            push(node, true);
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

