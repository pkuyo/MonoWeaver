using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using MonoMod.Cil;
using MonoWeaver.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace MonoWeaver.CFG;


public partial class ControlFlowGraph
{
    public sealed class BasicBlock(int id, Instruction start) : IComparable<BasicBlock>
    {
        public Instruction Leader  = start;
        public ControlFlowEdge[]? edges = null;

        public readonly int Id = id;

        public int CompareTo(BasicBlock other)
        {
           return Id.CompareTo(other.Id);
        }
    }

    public sealed class ControlFlowEdge
    {
        public BasicBlock From;
        public BasicBlock To;

        public EvalStackTransfer? StackTransfer;
    }
}

public partial class ControlFlowGraph
{

    private readonly MethodDefinition _method;
    private readonly bool _verify;

    private EvalStackNode _stackPool;

    private List<BasicBlock> _blocks = null!;

    private List<ControlFlowEdge> _edges = null!;

    private Dictionary<Instruction, BasicBlock> _blockMap = new();

    private Dictionary<int, StackTypeRef> _valueId = new(); //为了可以删除

    private int _currentId = 1;

    

    public ControlFlowGraph(MethodDefinition method, bool verify = true)
    {
        if (!method.HasBody)
        {
            throw new ArgumentException("Method must have body", nameof(method));
        }
        _method = method;
        _verify = verify;
        _method.Body.SimplifyMacros();
        BuildGraph();
    }



    private void BuildGraph()
    {
        BuildBasicBlock();
        BuildEdge();
        VerifyStack();
    }

    private void BuildBasicBlock()
    {
        HashSet<Instruction> leaders = new HashSet<Instruction>(); //添加Block起始
        _blocks = new List<BasicBlock>();
        if (_method.Body.Instructions.Count > 0)
            leaders.Add(_method.Body.Instructions[0]);

        foreach (var inst in _method.Body.Instructions)
        {
            if (inst is null) continue;
            if (inst.OpCode.FlowControl is FlowControl.Phi)
                leaders.Add(inst);
            else if (inst.OpCode.FlowControl is FlowControl.Branch
                     or FlowControl.Cond_Branch)
            {
                switch (inst.Operand)
                {
                    case ILLabel lab:   //MonoMod Runtime Detour
                        leaders.Add(lab.Target);
                        break;

                    case Instruction target:
                        leaders.Add(target);
                        break;

                    case Instruction[] targets:           // switch in Cecil
                        foreach (var t in targets) leaders.Add(t);
                        break;

                    case ILLabel[] labelTargets:
                        foreach (var l in labelTargets) leaders.Add(l.Target);
                        break;
                }
            }

            //终止指令的下一条指令也是起始
            if (inst.OpCode.FlowControl is FlowControl.Branch
                or FlowControl.Cond_Branch
                or FlowControl.Return
                or FlowControl.Throw)
            {
                if (inst.Next != null)
                    leaders.Add(inst.Next);
            }

            // JMP特殊处理
            if (inst.OpCode.Code == Code.Jmp && inst.Next != null)
                leaders.Add(inst.Next);
        }


        foreach (var eh in _method.Body.ExceptionHandlers)
        {
            if (eh.TryStart != null) leaders.Add(eh.TryStart);
            if (eh.TryEnd != null) leaders.Add(eh.TryEnd);
            if (eh.HandlerStart != null) leaders.Add(eh.HandlerStart);
            if (eh.HandlerEnd != null) leaders.Add(eh.HandlerEnd);
            if (eh.FilterStart != null) leaders.Add(eh.FilterStart);
        }

        foreach (var leader in leaders)
        {
            AddBasicBlock(leader);
        }
    }

    private void BuildEdge()
    {
        var root = new EvalStackNode();
        var node = root;
        var module = _method.Module;
        StackTypeRef[] buffer = new StackTypeRef[4];
        StackTypeRef?[] funcBuffer = new StackTypeRef?[8];

        foreach (var block in _blocks)
        {
            var leader = block.Leader;
            var localStack = new Stack<StackTypeRef>();
            StackTypeRef? retType = null;
            for (var inst = leader; inst != null; inst = inst.Next)
            {
                var ts = module.TypeSystem;
                var popAll = inst.OpCode.StackBehaviourPop == StackBehaviour.PopAll;
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

                var push = inst.OpCode.StackBehaviourPush switch
                {
                    StackBehaviour.Push0 => 0,
                    StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or 
                    StackBehaviour.Pushref or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 => 1,
                    StackBehaviour.Push1_push1 => 2,
                    StackBehaviour.Varpush => 0,
                    _ => throw new ArgumentOutOfRangeException()
                };

                for (int i = 0; i< pop; i++)
                {
                    buffer[i] = Pop(localStack, ref node, inst);
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
                        for(int i = len - 1; i >=0; i--)
                        {
                            var type = Pop(localStack, ref node, inst);
                            VerifyType(type, funcBuffer[i], inst);
                        }
                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }

                switch (inst.OpCode.StackBehaviourPush)
                {
                    //TODO: WIP
                }
            }

            static StackTypeRef Pop(Stack<StackTypeRef> localStack, ref EvalStackNode node, Instruction instruction)
            {
                if (localStack.Count != 0)
                    return localStack.Pop();
                if(node.Parent?.Type is null)
                    throw new CFGException(CFGExceptionType.StackUnderflow, instruction);
                node = node.Parent;
                return node.Type;
            }

            static void Append(Stack<StackTypeRef> localStack, ref EvalStackNode node,
                Instruction instruction, int maxDepth)
            {
                while (localStack.Count != 0)
                    node = node.AppendChild(localStack.Pop());
                if(node.Depth >= maxDepth)
                    throw new CFGException(CFGExceptionType.StackOverflow, instruction);
            }
        }
    }

    

    private void VerifyStack()
    {

    }

    private BasicBlock AddBasicBlock(Instruction leader)
    {
        var block = new BasicBlock(_currentId++, leader);
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