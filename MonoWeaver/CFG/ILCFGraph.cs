using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

namespace MonoWeaver.CFG
{
    public partial class ILCFGraph
    {
        public sealed class Block(Instruction start, EHBlock.Region region, int depth, MethodBody body) : IEquatable<Block>, IComparable<Block>
        {
            public Instruction Leader = start;
            public List<Edge> Edges = new();
            public List<Edge> prevEdges = new();

            internal bool _stackDepthDirty = false;
            internal bool _edgeDirty = false;


            public RegionKind Kind => Region.Kind;

            public EHBlock.Region Region = region;

            private readonly MethodBody body = body;

            public int EntryStackDepth { get; set; } = depth;

            public int Index => _indexCache ?? body.Instructions.IndexOf(Leader);
          

            public void SetIndexCacheEnable(bool enable)
            {
                if(enable)  _indexCache = body.Instructions.IndexOf(Leader);
                else _indexCache = null;
            }

            internal int? _indexCache;

            public bool Equals(Block other)
            {
                return Leader.Equals(other.Leader);
            }

            public int CompareTo(Block other)
            {
                return Index.CompareTo(other.Index);
            }

            public void AddEdge(Block to)
            {
                var edge = new Edge(this, to);
                Edges.Add(edge);
                to.prevEdges.Add(edge);
            }
        }

        public sealed class Edge(Block from, Block to)
        {
            public Block From = from;
            public Block To = to;
        }

        public sealed class EHBlock
        {
     
            public EHBlock(ExceptionHandler eh, MethodBody body)
            {
                ProtectedRegion = new Region(this, RegionKind.Try, eh.TryStart, eh.TryEnd);
                FilterRegion = eh.FilterStart is null ? null : new Region(this, RegionKind.Filter, eh.FilterStart, eh.HandlerStart);
                HandlerRegion = new Region(this, RegionKind.Handler, eh.HandlerStart, eh.HandlerEnd);
                ExceptionHandler = eh;
                _body = body;
            }

            public class Region
            {
                public Instruction Start;
                public Instruction End;
                public EHBlock Clause = null!;
                public Region? ParentRegion;
                public RegionKind Kind;

                public int StartIndex => _startIndexCache ?? Clause._body.Instructions.IndexOf(Start);

                public int EndIndex => _endIndexCache ?? Clause._body.Instructions.IndexOf(End);


                public void SetIndexCacheEnable(bool enable)
                {
                    if (enable)
                    {
                        _startIndexCache = Clause._body.Instructions.IndexOf(Start);
                        _endIndexCache = Clause._body.Instructions.IndexOf(End);
                    }
                    else
                    {
                        _startIndexCache = null;
                        _endIndexCache = null;
                    }
                }

                public int? _startIndexCache;
                public int? _endIndexCache;


                public Region(EHBlock clause, RegionKind kind, Instruction start, Instruction end)
                {
                    Clause = clause;
                    Start = start;
                    End = end;
                    Kind = kind;
                    SetIndexCacheEnable(true);
                }
            }


            public Region ProtectedRegion;
            public Region? FilterRegion;
            public Region HandlerRegion;

            public ExceptionHandler ExceptionHandler;

            private MethodBody _body;

            public bool Update()
            {
                bool changed = false;
                if (ProtectedRegion.Start != ExceptionHandler.TryStart ||
                     ProtectedRegion.End != ExceptionHandler.TryEnd)
                {
                    ProtectedRegion.Start = ExceptionHandler.TryStart;
                    ProtectedRegion.End = ExceptionHandler.TryEnd;
                    changed = true;
                }
                if (HandlerRegion.Start != ExceptionHandler.HandlerStart ||
                    HandlerRegion.End != ExceptionHandler.HandlerEnd)
                {
                    HandlerRegion.Start = ExceptionHandler.HandlerStart;
                    HandlerRegion.End = ExceptionHandler.HandlerEnd;
                    changed = true;
                }
                if (ExceptionHandler.FilterStart is not null)
                {
                    if (FilterRegion is null)
                    {
                        FilterRegion = new Region(this, RegionKind.Filter, ExceptionHandler.FilterStart, ExceptionHandler.HandlerStart);
                        changed = true;
                    }
                    else if (FilterRegion.Start != ExceptionHandler.FilterStart ||
                        FilterRegion.End != ExceptionHandler.HandlerStart)
                    {
                        FilterRegion.Start = ExceptionHandler.FilterStart;
                        FilterRegion.End = ExceptionHandler.HandlerStart;
                        changed = true;
                    }
                }

                return changed;
            }
        }
    }



    public partial class ILCFGraph
    {

        private List<EHBlock> _ehBlocks;

        private List<Block> _blocks;

        private List<EHBlock.Region> _regions;

        private ILMethodAnalyzer _analyzer;

        private MethodReference _method;

        public ILCFGraph(ILMethodAnalyzer analyzer)
        {
            var handlerMap = new Dictionary<EHandler, EHBlock>();
            var regionMap = new Dictionary<EHandler.Region, EHBlock.Region>();
            var blockMap = new Dictionary<ILMethodAnalyzer.BasicBlock, Block>();

            var ehBlock = new EHBlock(new ExceptionHandler((ExceptionHandlerType)8)
            {
                TryStart = analyzer._method.Body.Instructions[0],
                TryEnd = analyzer._method.Body.Instructions[analyzer._method.Body.Instructions.Count],
                HandlerStart = analyzer._method.Body.Instructions[0],
                HandlerEnd = analyzer._method.Body.Instructions[analyzer._method.Body.Instructions.Count],
            }, analyzer._method.Body);
            _method = analyzer._method;

            _regions = new List<EHBlock.Region>()
            {
                ehBlock.ProtectedRegion
            };

            _analyzer = analyzer;
            foreach (var oldHandler in analyzer._exceptionHandlers)
            {
                var newHandler = new EHBlock(oldHandler.ExceptionHandler, analyzer._method.Body);
                handlerMap[oldHandler] = newHandler;
                regionMap[oldHandler.ProtectedRegion] = newHandler.ProtectedRegion;
                regionMap[oldHandler.HandlerRegion] = newHandler.HandlerRegion;
                _regions.Add(newHandler.ProtectedRegion);
                _regions.Add(newHandler.HandlerRegion);

                if (oldHandler.FilterRegion != null && newHandler.FilterRegion != null)
                {
                    regionMap[oldHandler.FilterRegion] = newHandler.FilterRegion;
                    _regions.Add(newHandler.FilterRegion);
                }
            }
            foreach (var oldRegion in regionMap.Keys)
            {
                if (oldRegion.ParentRegion != null)
                {
                    regionMap[oldRegion].ParentRegion = regionMap[oldRegion.ParentRegion];
                }
            }
            foreach (var oldBlock in analyzer._blocks)
            {
                var newBlock = new Block(oldBlock.Leader, regionMap[oldBlock.Region], oldBlock.EntryStackDepth, analyzer._method.Body);
                blockMap[oldBlock] = newBlock;
            }

            foreach (var oldBlock in analyzer._blocks)
            {
                var newBlock = blockMap[oldBlock];

                foreach (var oldEdge in oldBlock.Edges)
                {
                    var edge = new Edge(blockMap[oldEdge.From], blockMap[oldEdge.To]);
                    newBlock.Edges.Add(edge);
                    blockMap[oldEdge.To].prevEdges.Add(edge);
                }
            }


            _blocks = analyzer._blocks
                .Select(b => {
                blockMap[b].SetIndexCacheEnable(true);
                return blockMap[b];
             }).ToList();
            _blocks.Sort((a, b) => a.Index.CompareTo(b.Index));

            _ehBlocks = analyzer._exceptionHandlers.Select(h => handlerMap[h]).ToList();
            _regions.Sort((a, b) => a.StartIndex != b.StartIndex ? a.StartIndex.CompareTo(b.StartIndex)
                 : b.EndIndex.CompareTo(a.EndIndex));

            foreach (var block in _blocks) block.SetIndexCacheEnable(false);
            foreach (var region in _regions) region.SetIndexCacheEnable(false);
        }

        public int StackDepthAt(Instruction inst)
        {
            var block = BlockByInstruction(inst);
            var i = block.Leader;
            var depth = block.EntryStackDepth;
            while(i != inst)
            {
                depth += inst.PushCount() - inst.PopCount(_analyzer._method);
                i = i.Next;
            }
            return depth;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="before">待插入指令的前一条指令</param>
        /// <param name="newInst">待插入指令</param>
        public void Emit(Instruction before, Instruction newInst)
        {
            var block = BlockByInstruction(before);
            var region = RegionByInstruction(before);
            var index = _blocks.IndexOf(block);
            var nextBlock = index == _blocks.Count - 1 ? null : _blocks[index + 1];

            var normal = newInst.OpCode.FlowControl is FlowControl.Next or FlowControl.Call &&
                (newInst.Previous is null || newInst.Previous.OpCode.Code != Code.Tail);
            if (nextBlock != null && before == nextBlock.Leader.Previous) //基本块末尾, 插入指令为新block
            {
                //TODO
            }
            //TODO
        }


        public void Replace(Instruction pos, Instruction newInst)
        {
            var block = BlockByInstruction(pos);
            var region = RegionByInstruction(pos);
            var index =  _blocks.IndexOf(block);
            var nextBlock = index == _blocks.Count - 1 ? null : _blocks[index + 1];


            var normal = pos.OpCode.FlowControl is FlowControl.Next or FlowControl.Call && 
                (pos.Previous is null || pos.Previous.OpCode.Code != Code.Tail);

            if (block.Leader == pos)
            {
                block.Leader = newInst;
            }

            if(nextBlock != null && pos == nextBlock.Leader.Previous) //基本块末尾
            {
                if(normal) //基本块合并
                {
                    block.Edges = nextBlock.Edges;
                    if(block.Region != nextBlock.Region)
                    {
                        throw new Exception(); //TODO:
                    }
                    block._stackDepthDirty = true;
                    foreach (var edge in block.Edges)
                    {
                        edge.From = block;
                    }
                }
            }
            else if(!normal) //基本块拆分
            {
                var newBlock = new Block(newInst, region, StackDepthAt(pos), _analyzer._method.Body);
                newBlock.Edges = block.Edges;
                _blocks.Insert(index + 1, newBlock);
                foreach (var edge in block.Edges)
                {
                    edge.From = block;
                }

                // 给block构建Edge
                if (newInst.OpCode.FlowControl is FlowControl.Branch
                or FlowControl.Cond_Branch)
                {
                    foreach (var targetInst in CecilHelper.OperandToTargets(newInst.Operand))
                    {    
                        if(targetInst is null)  { block._edgeDirty = true; continue; }
                        block.AddEdge(BlockByInstruction(targetInst));
                    }
                }
                if (newInst.OpCode.FlowControl is FlowControl.Branch
                    or FlowControl.Cond_Branch
                    or FlowControl.Return
                    or FlowControl.Throw && newInst.Next != null)
                {
                    block.AddEdge(BlockByInstruction(newInst.Next));
                }

                block._stackDepthDirty = true;
                newBlock._stackDepthDirty = true;
            }

            var tmpRegion = region;
            while (region?.Start == pos)
            {
                region.Start = newInst;
                region = region.ParentRegion;
            }

            tmpRegion = region;
            while (region?.End == pos)
            {
                region.End = newInst;
                region = region.ParentRegion;
            }

            block._stackDepthDirty = pos.PopCount(_method) + pos.PushCount() != newInst.PopCount(_method) + newInst.PushCount();
        }

        public void Remove(Instruction pos)
        {
            var block = BlockByInstruction(pos);
            if (pos.Next != null)
            {
                Replace(pos, pos.Next);
            }
            else
            {
                Replace(pos, pos.Previous);
            }
            block._stackDepthDirty = true;
        }

        public void Update()
        {
            var dirtyBlock = _blocks.Where(i => i._stackDepthDirty).ToList();
            var blockSet = new HashSet<Block>();
            //TODO:
        }

        public Block BlockByInstruction(Instruction inst)
        {
            var index = _analyzer._method.Body.Instructions.IndexOf(inst);
            if (index == -1)
                throw new Exception();

            for (int i = 0; i < _blocks.Count; i++)
            {
                if (_blocks[i].Index > i)
                    return _blocks[i - 1];
            }
            return _blocks[_blocks.Count - 1];
        }

        public EHBlock.Region RegionByInstruction(Instruction inst)
        {
            var stack = new Stack<EHBlock.Region>();
            var index = _analyzer._method.Body.Instructions.IndexOf(inst);
            if (index == -1)
                throw new Exception();

            for (int i = 0; i < _regions.Count; i++)
            {
                if (_regions[i].StartIndex > i)
                    return stack.Peek();
                else if (_regions[i].EndIndex > i)
                    stack.Push(_regions[i]);
                else if (stack.Peek().EndIndex <= i)
                    stack.Pop();
            }
            throw new Exception(); //TODO:
        }
    }
}
