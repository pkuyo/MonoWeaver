using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using MonoWeaver.Utils;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MonoWeaver.CFG;

//public class EvalStackTransfer
//{
//    public List<TypeReference> Pushed = new();
//    public List<TypeReference> Popped = new();
//}

public enum BasicBlockType : byte
{
    Invalid = 0,
    Normal = 1,
    Try = 2,
    Catch = 3,
    Finally = 4,
    Fault = 5,
}

public class EvalStackNode
{
    public EvalStackNode(int depth = 0, StackTypeRef? type = null)
    {
        Type = type ?? StackTypeRef.Invalid;
        Depth = depth;
    }

    public StackTypeRef Type;

    public EvalStackNode? Parent;

    public List<EvalStackNode> Children = new List<EvalStackNode>();

    public int Depth = 0;

    public void Disconnect()
    {
        Parent?.RemoveChild(this);
        Parent = null;
    }

    public void RemoveChild(EvalStackNode node)
    {
        Children.Remove(node);
    }

    public void AppendChild(EvalStackNode node)
    {
        node.Parent = this;
    }

    public EvalStackNode AppendChild(StackTypeRef type)
    {
        var node = new EvalStackNode(Depth + 1, type)
        {
            Parent = this
        };
        Children.Add(node);
        return node;
    }
}



public partial class ControlFlowGraph
{

    public sealed class ExceptionBlock(ExceptionHandler eh)
    {
        public int TryStart;
        public int TryEnd;
        public int FilterStart;
        public int HandlerStart;
        public int HandlerEnd;

        public ExceptionHandler ExceptionHandler = eh;
    }
    public sealed class BasicBlock(Instruction start, TypeReference? exceptionType = null) : IEquatable<BasicBlock>
    {
        public Instruction Leader  = start;
        public List<ControlFlowEdge> Edges = new();

        public TypeReference? ExceptionType = exceptionType;

        public bool Equals(BasicBlock other)
        {
            return Leader.Equals(other.Leader);
        }
    }

    public sealed class ControlFlowEdge(BasicBlock from, BasicBlock to)
    {
        public BasicBlock From = from;
        public BasicBlock To = to;

        //public EvalStackTransfer? StackTransfer;
    }
}

public partial class ControlFlowGraph
{

    private readonly MethodDefinition _method;

    private EvalStackNode _root = new();

    Dictionary<(StackTypeRef type, EvalStackNode prev), EvalStackNode> _nodeIntern = new();

    private List<BasicBlock> _blocks = null!;

    private Dictionary<Instruction, BasicBlock> _blockMap = new();

    private List<ExceptionBlock> _eBlocks = null!;
    

    public ControlFlowGraph(MethodDefinition method)
    {
        if (method.IsIL)
        {
            throw new ArgumentException("Method must be IL method", method.FullName);
        }
        _method = method;
        _method.Body.SimplifyMacros();
        BuildGraph();
    }



    private void BuildGraph()
    {
        InitBasicBlock();
        BuildEdgeAndVerify();
    }

    private void InitBasicBlock()
    {
        _blocks = new List<BasicBlock>();
        if (_method.Body.Instructions.Count > 0)
            AddBasicBlock(_method.Body.Instructions[0]);

        _eBlocks = new List<ExceptionBlock>(_method.Body.ExceptionHandlers.Count);

        foreach (var eh in _method.Body.ExceptionHandlers) //由于未Apply的Instruction的Offset为0 不能直接用Offset来判断异常边界
        {
            _eBlocks.Add(BuildExceptionBlock(eh));
        }

        foreach (var inst in _method.Body.Instructions)
        {
            if (inst is null) continue;
            if (inst.OpCode.FlowControl is FlowControl.Phi)
                AddBasicBlock(inst);
            else if (inst.OpCode.FlowControl is FlowControl.Branch
                     or FlowControl.Cond_Branch)
            {
                foreach(var i in CecilHelper.OperandToTargets(inst.Operand))
                    AddBasicBlock(i);
            }

            //终止指令的下一条指令也是起始
            if (inst.OpCode.FlowControl is FlowControl.Branch
                or FlowControl.Cond_Branch
                or FlowControl.Return
                or FlowControl.Throw)
            {
                if (inst.Next != null)
                    AddBasicBlock(inst.Next);
            }

            // JMP特殊处理
            if (inst.OpCode.Code == Code.Jmp && inst.Next != null)
                AddBasicBlock(inst.Next);
        }


  
    }

    private void BuildEdgeAndVerify()
    {
        var module = _method.Module;
        var buffer = new StackTypeRef[4];
        var funcBuffer = new StackTypeRef?[8];
        var usedBlocks = new HashSet<BasicBlock>(_blocks.Count);
        
        foreach (var block in _blocks)
        {
            if (usedBlocks.Contains(block))
                continue;
            AnalyzeBlocksWorkflow(block, usedBlocks, buffer, funcBuffer);
        }
    }

    private ExceptionBlock BuildExceptionBlock(ExceptionHandler eh)
    {
        if (_method is null) throw new ArgumentNullException(nameof(_method));
        if (eh is null) throw new ArgumentNullException(nameof(eh));
        if (!_method.HasBody) throw new ArgumentException("Method has no body.", nameof(_method));

        var ins = _method.Body.Instructions;
        if (ins is null) throw new ArgumentException("Method body has no instructions.", nameof(_method));

        if (eh.TryStart is null) throw new ArgumentException("Invalid EH: TryStart is null.", nameof(eh));
        if (eh.TryEnd is null) throw new ArgumentException("Invalid EH: TryEnd is null.", nameof(eh));
        if (eh.HandlerStart is null) throw new ArgumentException("Invalid EH: HandlerStart is null.", nameof(eh));
        if (eh.HandlerEnd is null) throw new ArgumentException("Invalid EH: HandlerEnd is null.", nameof(eh));
        if (eh.HandlerType == ExceptionHandlerType.Filter && eh.FilterStart is null)
            throw new ArgumentException("Invalid EH: FilterStart is null for Filter handler.", nameof(eh));

        int? tryStart = null, tryEnd = null, handlerStart = null, handlerEnd = null, filterStart = null;

        for (int i = 0; i < ins.Count; i++)
        {
            var cur = ins[i];

            if (cur == eh.TryStart) tryStart = i;
            if (cur == eh.TryEnd) tryEnd = i;
            if (cur == eh.HandlerStart) handlerStart = i;
            if (cur == eh.HandlerEnd) handlerEnd = i;

            if (eh.FilterStart != null && cur == eh.FilterStart)
                filterStart = i;

   
            bool needFilter = eh.HandlerType == ExceptionHandlerType.Filter;
            if (tryStart.HasValue && tryEnd.HasValue && handlerStart.HasValue && handlerEnd.HasValue
                && (!needFilter || filterStart.HasValue))
                break;
        }

  
        if (!tryStart.HasValue) throw new ArgumentException("Invalid EH: TryStart is not in this method's instruction list.", nameof(eh));
        if (!tryEnd.HasValue) throw new ArgumentException("Invalid EH: TryEnd is not in this method's instruction list.", nameof(eh));
        if (!handlerStart.HasValue) throw new ArgumentException("Invalid EH: HandlerStart is not in this method's instruction list.", nameof(eh));
        if (!handlerEnd.HasValue) throw new ArgumentException("Invalid EH: HandlerEnd is not in this method's instruction list.", nameof(eh));

        if (eh.HandlerType == ExceptionHandlerType.Filter && !filterStart.HasValue)
            throw new ArgumentException("Invalid EH: FilterStart is not in this method's instruction list.", nameof(eh));

        var block = new ExceptionBlock(eh)
        {
            TryStart = tryStart.Value,
            TryEnd = tryEnd.Value,
            HandlerStart = handlerStart.Value,
            HandlerEnd = handlerEnd.Value,
            FilterStart = (eh.FilterStart is null) ? -1 : filterStart.GetValueOrDefault(-1)
        };
        return block;
    }

    private void AnalyzeBlocksWorkflow(BasicBlock entryBlock, 
        HashSet<BasicBlock> usedBlocks,  StackTypeRef[] buffer, StackTypeRef?[] funcBuffer)
    {
        var localStack = new Stack<StackTypeRef>(_method.Body.MaxStackSize);
        List<(BasicBlock block, EvalStackNode node)> bfsBlocks = [(entryBlock, _root)];
        for(int i = 0; i < bfsBlocks.Count; i++)
        {
            var (block, node) = bfsBlocks[i];
            usedBlocks.Add(block);
            var leader = block.Leader;
            StackTypeRef? retType = null;
            for (var inst = leader; inst != null; inst = inst.Next)
            {
                var ts = _method.Module.TypeSystem;
                if(inst.OpCode.StackBehaviourPop == StackBehaviour.PopAll)
                {
                    localStack.Clear();
                    node = _root;
                }
                var pop = inst.OpCode.StackBehaviourPop switch
                {
                    StackBehaviour.Pop0 => 0,
                    StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
                    StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or
                        StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or
                        StackBehaviour.Popref_popi or StackBehaviour.Popi_pop1 or
                        StackBehaviour.Popref_pop1 or StackBehaviour.Pop1_pop1 => 2,
                    StackBehaviour.Popref_popi_popi or
                        StackBehaviour.Popref_popi_popi8 or
                        StackBehaviour.Popref_popi_popr4 or
                        StackBehaviour.Popref_popi_popr8 or
                        StackBehaviour.Popref_popi_popref => 3,
                    StackBehaviour.Varpop => 0,
                    _ => throw new ArgumentOutOfRangeException()
                };


                for (int j = 0; j< pop; j++)
                {
                    buffer[j] = EvalStackPop(localStack, ref node, inst);
                }

                switch (inst.OpCode.StackBehaviourPop)
                {
                    case StackBehaviour.Pop0:
                        break;

                    case StackBehaviour.Pop1:
                        retType = VerifyPop1(inst, buffer);
                        break;

                    case StackBehaviour.Popi:
                        VerifyType(buffer[0], ts.Int32, inst);
                        break;

                    case StackBehaviour.Popref:
                        retType = VerifyPopref(inst, buffer);
                        break;

                    case StackBehaviour.Popi_popi:
                        VerifyType(buffer[0], ts.Int32, inst);
                        VerifyType(buffer[1], ts.Int32, inst);
                        break;

                    case StackBehaviour.Popi_popi8:
                        VerifyType(buffer[0], ts.Int32, inst);
                        VerifyType(buffer[1], ts.Int64, inst);
                        break;

                    case StackBehaviour.Popi_popr4:
                        VerifyType(buffer[0], ts.Int32, inst);
                        VerifyType(buffer[1], ts.Single, inst);
                        break;

                    case StackBehaviour.Popi_popr8:
                        VerifyType(buffer[0], ts.Int32, inst);
                        VerifyType(buffer[1], ts.Double, inst);
                        break;

                    case StackBehaviour.Popref_popi:
                        retType = VerifyPopref_popi(inst, buffer);
                        break;

                    case StackBehaviour.Popi_pop1:
                        retType = VerifyPopi_pop1(inst, buffer);
                        break;

                    case StackBehaviour.Popref_pop1:
                        retType = VerifyPopref_pop1(inst, buffer);
                        break;

                    case StackBehaviour.Pop1_pop1:
                        retType = VerifyPop1_pop1(inst, buffer);
                        break;

                    case StackBehaviour.Popi_popi_popi:
                        VerifyType(buffer[0], ts.Int32, inst);
                        VerifyType(buffer[1], ts.Int32, inst);
                        VerifyType(buffer[0], ts.Int32, inst);
                        break;

                    case StackBehaviour.Popref_popi_popi:
                    case StackBehaviour.Popref_popi_popi8:
                    case StackBehaviour.Popref_popi_popr4:
                    case StackBehaviour.Popref_popi_popr8:
                    case StackBehaviour.Popref_popi_popref:
                        retType = VerifyPop3(inst, buffer);
                        break;

                    case StackBehaviour.Varpop:
                        retType = FillVarPop(inst, ref funcBuffer, out var len);
                        for(int j = len - 1; j >=0; j--)
                        {
                            var type = EvalStackPop(localStack, ref node, inst);
                            VerifyType(type, funcBuffer[j], inst);
                        }
                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }

                if (retType is null && inst.OpCode.StackBehaviourPush is not StackBehaviour.Push0 or StackBehaviour.Varpush)
                {
                    throw new Exception(); //TODO: 完善故障信息错误报告
                }
           
                if (inst.OpCode.StackBehaviourPush is StackBehaviour.Push1_push1)
                {
                    EvalStackPush(localStack, ref node, retType!);
                    EvalStackPush(localStack, ref node, retType!);
                }
                else
                {
                    EvalStackPush(localStack, ref node, retType!);
                }
                bool endBlock = false;
                switch (inst.OpCode.FlowControl)
                {
                    case FlowControl.Next:
                        break;
                    case FlowControl.Branch:
                        Append(localStack, ref node, inst);
                        foreach (var target in CecilHelper.OperandToTargets(inst.Operand))
                        {
                            var targetBlock = _blockMap[target];
                            bfsBlocks.Add((targetBlock, node));
                            block.Edges.Add(new ControlFlowEdge(block, targetBlock));
                        }
                        endBlock = true;
                        break;
                    case FlowControl.Cond_Branch:
                        foreach (var target in CecilHelper.OperandToTargets(inst.Operand))
                        {
                            var targetBlock = _blockMap[target];
                            bfsBlocks.Add((targetBlock, node));
                            block.Edges.Add(new ControlFlowEdge(block, targetBlock));
                        }
                        var next = _blockMap[inst.Next];
                        bfsBlocks.Add((next, node));
                        block.Edges.Add(new ControlFlowEdge(block, next));
                        endBlock = true;    
                        break;
                }
                if (endBlock)
                    break;
            }
        }
    }

    private StackTypeRef EvalStackPop(Stack<StackTypeRef> localStack, ref EvalStackNode node, Instruction instruction)
    {
        if (localStack.Count != 0)
            return localStack.Pop();
        if (node.Parent?.Type is null)
            throw new CFGException(CFGExceptionType.StackUnderflow, instruction);
        node = node.Parent;
        return node.Type;
    }

    private void EvalStackPush(Stack<StackTypeRef> localStack, ref EvalStackNode node, StackTypeRef type)
    {
        if (_nodeIntern.TryGetValue((type, node), out var child))
            return;
        localStack.Push(type);
    }

    private void Append(Stack<StackTypeRef> localStack, ref EvalStackNode node,
        Instruction instruction)
    {
        while (localStack.Count != 0)
            node = node.AppendChild(localStack.Pop());
    }


    private BasicBlock AddBasicBlock(Instruction leader, TypeReference? catchType)
    {
        var block = new BasicBlock(leader, catchType);
        _blocks.Add(block);
        _blockMap.Add(leader, block);
        return block;
    }


    private BasicBlock AddBasicBlock(Instruction leader)
    {
        var block = new BasicBlock(leader);
        _blocks.Add(block);
        _blockMap.Add(leader, block);
        return block;
    }

}

public partial class ControlFlowGraph
{

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackTypeRef VerifyType(StackTypeRef type1, StackTypeRef? type2, Instruction inst)
    {
        throw new NotImplementedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackTypeRef VerifyValueType(StackTypeRef type1, Instruction inst)
    {
        throw new NotImplementedException();

    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackTypeRef VerifyNum(StackTypeRef type1, Instruction inst)
    {
        throw new NotImplementedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackTypeRef VerifyInt(StackTypeRef type1, Instruction inst)
    {
        throw new NotImplementedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackTypeRef VerifyByRef(StackTypeRef type1, Instruction inst)
    {
        throw new NotImplementedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackTypeRef VerifyFloat(StackTypeRef type1, Instruction inst)
    {
        throw new NotImplementedException();
    }

    private StackTypeRef? VerifyPop1(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {
            case Code.Box:
                return VerifyValueType(stacks[0], inst);
            case Code.Ckfinite:
                return VerifyType(stacks[0], StackTypeRef.F, inst);
            case Code.Conv_I1:
            case Code.Conv_I2:
            case Code.Conv_I4:
            case Code.Conv_I8:
            case Code.Conv_I:
            case Code.Conv_U1:
            case Code.Conv_U2:
            case Code.Conv_U4:
            case Code.Conv_U8:
            case Code.Conv_U:
            case Code.Conv_R4:
            case Code.Conv_R8:
            case Code.Conv_R_Un:
            case Code.Conv_Ovf_I1:
            case Code.Conv_Ovf_I2:
            case Code.Conv_Ovf_I4:
            case Code.Conv_Ovf_I8:
            case Code.Conv_Ovf_I:
            case Code.Conv_Ovf_U1:
            case Code.Conv_Ovf_U2:
            case Code.Conv_Ovf_U4:
            case Code.Conv_Ovf_U8:
            case Code.Conv_Ovf_U:
            case Code.Conv_Ovf_I1_Un:
            case Code.Conv_Ovf_I2_Un:
            case Code.Conv_Ovf_I4_Un:
            case Code.Conv_Ovf_I8_Un:
            case Code.Conv_Ovf_I_Un:
            case Code.Conv_Ovf_U1_Un:
            case Code.Conv_Ovf_U2_Un:
            case Code.Conv_Ovf_U4_Un:
            case Code.Conv_Ovf_U8_Un:
            case Code.Conv_Ovf_U_Un:
                return VerifyNum(stacks[0], inst);
            case Code.Dup:
                return stacks[0];
            case Code.Neg:
                return VerifyNum(stacks[0], inst);
            case Code.Not:
                return VerifyInt(stacks[0], inst);
            case Code.Pop:
                return null;
            case Code.Refanytype:
                return VerifyType(stacks[0], module.TypeSystem.TypedReference, inst);
            case Code.Refanyval:
                return VerifyByRef(stacks[0], inst);
            case Code.Starg:
                {
                    if (inst.Operand is not ushort index)
                        throw new InvalidInstructionException(typeof(ushort), inst.Operand?.GetType(), inst);
                    if (index >= _method.Parameters.Count + (_method.HasThis ? 1 : 0))
                        throw new OperandOutOfRangeException(inst,
                            $"Method parameters count: {_method.Parameters.Count + (_method.HasThis ? 1 : 0)}");
                    stacks[0] = (_method.HasThis && index == 0) ? _method.DeclaringType
                    : _method.Parameters[index - (_method.HasThis ? 1 : 0)].ParameterType;
                    return null;
                }
            case Code.Stloc:
                {
                    if (inst.Operand is not ushort index)
                        throw new InvalidInstructionException(typeof(ushort), inst.Operand?.GetType(), inst);
                    if (index >= _method.Body.Variables.Count)
                        throw new OperandOutOfRangeException(inst,
                            $"Method local variables count: {_method.Body.Variables.Count}");
                    stacks[0] = _method.Body.Variables[index].VariableType;
                    return null;
                }
            case Code.Stsfld:
                {
                    if (inst.Operand is not FieldReference field)
                        throw new InvalidInstructionException(typeof(FieldReference), inst.Operand?.GetType(), inst);
                    stacks[0] = field.FieldType;
                    return null;
                }
            default:
                throw new ArgumentOutOfRangeException(); //TODO:
        }
    }

    private StackTypeRef? VerifyPop1_pop1(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }

    private StackTypeRef? VerifyPopi_pop1(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }

    private StackTypeRef? VerifyPopref_pop1(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }
    private StackTypeRef? VerifyPop3(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }

    private StackTypeRef? VerifyPopref(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }

    private StackTypeRef? VerifyPopref_popi(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }


    private StackTypeRef? FillVarPop(Instruction inst, ref StackTypeRef?[] args, out int len)
    {
        if (inst.Operand is not IMethodSignature sig)
        {
            len = 0;
            return null;
        }
        var paramLen = sig.Parameters.Count + (sig.HasThis ? 1 : 0);
        len = paramLen;
        if(args.Length < paramLen)
        {
            Array.Resize(ref args, paramLen);
        }
        int i = 0;
        if (sig.HasThis)
        {
            if (sig is MethodReference methodRef)
                args[i++] = methodRef.DeclaringType;
            else
                args[i++] = null;
        }
        foreach(var p in sig.Parameters)
        {
            args[i++] = p.ParameterType;
        }
        return (sig.ReturnType.Namespace == "System" && sig.ReturnType.Name == "Void") 
            ? null : StackTypeRef.Create(sig.ReturnType);
    }

}