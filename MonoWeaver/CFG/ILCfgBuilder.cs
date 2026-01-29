using System;
using System.Collections.Generic;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using MonoMod.Cil;

namespace MonoWeaver.CFG
{
    // ReSharper disable once InconsistentNaming
    public partial class ILCfgBuilder
    {
        public record Edge(Instruction From, Instruction To);

        public record Block(Instruction Start, Instruction End, Edge[] Edges);
    }

    // ReSharper disable once InconsistentNaming
    public partial class ILCfgBuilder : IDisposable
    {
        private readonly MethodBody _method;

        private readonly List<Block> _blocks = new List<Block>();

        public ILCfgBuilder(MethodBody method)
        {
            _method = method;
            _method.SimplifyMacros();
            BuildBlock();
        }

        public void BuildBlock()
        {
            HashSet<Instruction> leaders = new HashSet<Instruction>(); //添加Block起始

            if (_method.Instructions.Count > 0)
                leaders.Add(_method.Instructions[0]);

            foreach (var inst in _method.Instructions)
            {
                if(inst is null) continue;
                if(inst.OpCode.FlowControl is FlowControl.Phi)
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
                if (eh.HandlerStart != null) leaders.Add(eh.HandlerEnd);
                if (eh.HandlerEnd != null) leaders.Add(eh.HandlerEnd);
                if (eh.FilterStart != null) leaders.Add(eh.FilterStart);
            }

            foreach (var leader in leaders)
            {
                var end = leader;
                var edge = new List<Edge>();
                for (var inst = leader.Next; inst != null; inst = inst.Next)
                {
                    end = inst;
                }
                _blocks.Add(new Block(leader, end, edge.ToArray()));
            }
        }

        public void Verify()
        {

        }

        public void Dispose()
        {
            _method.OptimizeMacros();
        }
    }
}
