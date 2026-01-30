using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using MonoMod.Cil;
using System;
using System.Collections.Generic;

namespace MonoWeaver.CFG;

public partial class ControlFlowGraph
{
    public sealed class BasicBlock(int id, Instruction start) : IComparable<BasicBlock>
    {
        public Instruction Leader  = start;
        public ControlFlowEdge[]? edges = null;

        public readonly int Id = id;

        public StackStateId? EntryEvalStack = null;

        public int CompareTo(BasicBlock other)
        {
           return Id.CompareTo(other.Id);
        }
    }

    public sealed class ControlFlowEdge
    {
        public BasicBlock From;
        public BasicBlock To;
     
        public EvalStackTransfer? EvalStackTransfer;
    }
}

public partial class ControlFlowGraph
{

    private readonly MethodBody _method;

    private EvalStackStatePool _stackPool;

    private List<BasicBlock> _blocks = null!;

    private List<ControlFlowEdge> _edges = null!;

    private Dictionary<Instruction, BasicBlock> _blockMap = new();

    private int _currentId = 1;


    public ControlFlowGraph(List<BasicBlock> blocks, List<ControlFlowEdge> edges, MethodBody method, EvalStackStatePool stackPool)
    {
        _blocks = blocks;
        _edges = edges;
        _method = method;
        _stackPool = stackPool;
        foreach(var block in _blocks)
        {
            _blockMap.Add(block.Leader, block);
        }
    }

    public ControlFlowGraph(MethodBody method, bool verify = true)
    {
        _method = method;
        _stackPool = new EvalStackStatePool();
        _method.SimplifyMacros();
        BuildBasicBlock();
    }


    private void AddBasicBlock(Instruction leader)
    {
        var block = new BasicBlock(_currentId++, leader);
        _blocks.Add(block);
        _blockMap.Add(leader, block);
    }

    public void BuildBasicBlock()
    {
        HashSet<Instruction> leaders = new HashSet<Instruction>(); //添加Block起始

        if (_method.Instructions.Count > 0)
            leaders.Add(_method.Instructions[0]);

        foreach (var inst in _method.Instructions)
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


        foreach (var eh in _method.ExceptionHandlers)
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

            var end = leader;
            var edge = new List<ControlFlowEdge>();
            for (var inst = leader.Next; inst != null; inst = inst.Next)
            {
                end = inst;
            } 
        }
    }

}