using Mono.Cecil.Cil;
using MonoWeaver.Utils;
using System;
using System.Collections;
using System.Collections.Generic;

namespace MonoWeaver.CFG
{
    public partial class ILCFGraph
    {
        public sealed class BasicBlock(Instruction start, EHandler.Region region) : IEquatable<BasicBlock>
        {
            public Instruction Leader = start;
            public List<ControlFlowEdge> Edges = new();
            public List<ControlFlowEdge> prevEdges = new();

            public RegionKind Kind = region.Kind;
            public EHandler.Region Region = region;

            public int EntryStackDepth { get; } = -1;

            public bool Equals(BasicBlock other)
            {
                return Leader.Equals(other.Leader);
            }

            public override string ToString()
            {
                return $"{Leader.SafeToString()} [{Kind} Region: {Region.Start}-{Region.End}]";
            }
        }

        public sealed class ControlFlowEdge(BasicBlock from, BasicBlock to)
        {
            public BasicBlock From = from;
            public BasicBlock To = to;
        }
    }

    public partial class ILCFGraph
    {
        public ILCFGraph(ILMethodAnalyzer analyzer)
        {

        }

        public int StackDepthAt(Instruction inst)
        {
            throw new NotImplementedException();
        }

        public void Emit(Instruction pos, Instruction newInst)
        {

        }
        public void Replace(Instruction pos, Instruction newInst)
        {

        }

        public void Remove(Instruction pos)
        {

        }

        public void Update()
        {

        }
    }
}
