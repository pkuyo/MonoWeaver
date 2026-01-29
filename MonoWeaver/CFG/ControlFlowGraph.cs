using Mono.Cecil.Cil;

namespace MonoWeaver.CFG;

public partial class ControlFlowGraph
{
    public sealed class BasicBlock(int id, Instruction start, Instruction end)
    {
        public Instruction Start = start;
        public Instruction End = end;

        public readonly int Id = id;

        public StackStateId? EntryEvalStack = null;
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
    private EvalStackStatePool _stackPool = new EvalStackStatePool();

    private BasicBlock[] _blocks;
    
    public ControlFlowGraph(BasicBlock[] blocks)
    {
        _blocks = blocks;
    }
}