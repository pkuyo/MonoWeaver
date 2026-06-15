using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MonoWeaver.CFG
{
    //TODO: Add Fucking income Label
    public partial class ILCFGraph
    {
        public sealed class Block(Instruction start, EHBlock.Region region, int depth, MethodBody body, bool stackDepthDirty = true) : IEquatable<Block>, IComparable<Block>
        {
            public Instruction Leader = start;
            public List<Edge> Edges = new();
            public List<Edge> prevEdges = new();

            internal bool _stackDepthDirty = stackDepthDirty;
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

            public void AddEdgeTo(Block to)
            {
                var edge = new Edge(this, to);
                Edges.Add(edge);
                to.prevEdges.Add(edge);
            }

            public void MoveAllEdgesTo(Block to)
            {
                to.Edges = Edges;
                foreach(var edge in Edges)
                {
                    edge.From = to;
                }
                Edges = new List<Edge>();
            }

            public void RemoveAllEdges()
            {
                foreach(var edge in Edges)
                {
                    edge.To.prevEdges.Remove(edge);
                }
                Edges.Clear(); 
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

                private int? _startIndexCache;
                private int? _endIndexCache;


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
                var newBlock = new Block(oldBlock.Leader, regionMap[oldBlock.Region], oldBlock.EntryStackDepth, analyzer._method.Body, false);
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
            return StackDepthAt(block, inst);
        }
        
        public int StackDepthAt(Block block, Instruction inst)
        {
            var i = block.Leader;
            var depth = block.EntryStackDepth;
            while(i != inst)
            {
                depth += i.PushCount() - i.PopCount(_analyzer._method);
                i = i.Next;
            }
            return depth;
        }

        /// <summary>
        /// 在Instruction插入至Instructions后使用
        /// </summary>
        /// <param name="before">待插入指令的前一条指令</param>
        /// <param name="newInst">待插入指令</param>
        public void Emit(Instruction? before, Instruction newInst)
        {
            var normal = newInst.OpCode.FlowControl is FlowControl.Next or FlowControl.Call &&
                         (newInst.Previous is null || newInst.Previous.OpCode.Code != Code.Tail);
            if (before == null)
            {
                if (_blocks.Count == 0)
                {
                    _blocks.Add(new Block(newInst, _regions[0], 0, _analyzer._method.Body, false));
                }
                else if(normal)
                {
                    var curBlock = BlockByInstruction(newInst.Next);
                    curBlock.Leader = newInst;
                    curBlock._stackDepthDirty = true;
                }
                else
                {
                    var newBlock = new Block(newInst, _regions[0], 0, _analyzer._method.Body, false);
                    _blocks.Add(newBlock);
                    ConnectionBlockForInstruction(newInst, newBlock, BlockByInstruction(newInst.Next));
                }
                return;
            }
            var block = BlockByInstruction(before);
            var region = RegionByInstruction(before);
            var index = _blocks.IndexOf(block);
            var nextBlock = index == _blocks.Count - 1 ? null : _blocks[index + 1];


            if (before == nextBlock?.Leader?.Previous) //基本块末尾, 插入指令为后继或新block
            {
                if (normal && ((nextBlock.prevEdges.Count == 0) ||
                    nextBlock.prevEdges.Count == 1 &&
                    nextBlock.prevEdges[0].From == block))
                {
                    nextBlock.Leader = newInst; //后继block
                    nextBlock._stackDepthDirty = true;
                }
                else
                {
                    var newBlock = new Block(newInst, region, StackDepthAt(newInst), _analyzer._method.Body);
                    if (normal) nextBlock._stackDepthDirty = true;
                    ConnectionBlockForInstruction(newInst, newBlock, nextBlock);
                    _blocks.Insert(index + 1, newBlock); //新block
                }
            }
            else
            {
                if (!normal) //基本块拆分
                {
                    var newBlock = new Block(newInst, region, StackDepthAt(newInst), _analyzer._method.Body);
                    block.MoveAllEdgesTo(newBlock);
                    _blocks.Insert(index + 1, newBlock);
                    ConnectionBlockForInstruction(newInst, block, newBlock); //拆分block
                }
                block._stackDepthDirty = true;
            }
        }


        /// <summary>
        /// 在replace前调用
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="newInst"></param>
        /// <exception cref="Exception"></exception>
        public void Replace(Instruction pos, Instruction newInst)
        {
            var block = BlockByInstruction(pos);
            var region = RegionByInstruction(pos);
            var index =  _blocks.IndexOf(block);
            var nextBlock = index == _blocks.Count - 1 ? null : _blocks[index + 1];


            var normal = pos.OpCode.FlowControl is FlowControl.Next or FlowControl.Call && 
                (pos.Previous is null || pos.Previous.OpCode.Code != Code.Tail);

            if (block.Leader == pos) block.Leader = newInst;
            


            if (nextBlock != null && pos == nextBlock.Leader.Previous) //基本块末尾
            {
                if (normal && ((nextBlock.prevEdges.Count == 0) ||
                    nextBlock.prevEdges.Count == 1 &&
                    nextBlock.prevEdges[0].From == block)) //基本块合并
                {
                    nextBlock.MoveAllEdgesTo(block);
                    if (block.Region != nextBlock.Region)
                    {
                        throw new Exception(); //TODO: EH段不一致
                    }
                    _blocks.Remove(nextBlock);
                }
            }
            else if(!normal) //基本块拆分
            {
                var newBlock = new Block(newInst.Next, region, StackDepthAt(pos), _analyzer._method.Body);
                block.MoveAllEdgesTo(newBlock);
                _blocks.Insert(index + 1, newBlock);
                ConnectionBlockForInstruction(newInst, block, newBlock);
            }
            block._stackDepthDirty = true;

            var tmpRegion = region;
            while (tmpRegion?.Start == pos)
            {
                tmpRegion.Start = newInst;
                tmpRegion = tmpRegion.ParentRegion;
            }

            tmpRegion = region;
            while (tmpRegion?.End == pos)
            {
                tmpRegion.End = newInst;
                tmpRegion = tmpRegion.ParentRegion;
            }

            block._stackDepthDirty = pos.PopCount(_method) + pos.PushCount() != newInst.PopCount(_method) + newInst.PushCount();
        }

        /// <summary>
        /// 需在instruction从instructions删除前调用
        /// </summary>
        /// <param name="pos"></param>
        public void Remove(Instruction pos)
        {
            
            var block = BlockByInstruction(pos);
            var moveTo = pos.Next ?? pos.Previous;
            if (moveTo is null)  return;
            var index =  _blocks.IndexOf(block);
            var nextBlock = index == _blocks.Count - 1 ? null : _blocks[index + 1];
            
            var normal = pos.OpCode.FlowControl is FlowControl.Next or FlowControl.Call && 
                         (pos.Previous is null || pos.Previous.OpCode.Code != Code.Tail);
            if (!normal && block.Leader != pos && nextBlock != null)
            {
                nextBlock.MoveAllEdgesTo(block);
            }
            else if (block.Leader == pos) block.Leader = moveTo;

            var region = RegionByInstruction(pos);
            var tmpRegion = region;
            while (tmpRegion?.Start == pos)
            {
                tmpRegion.Start = moveTo;
                tmpRegion = tmpRegion.ParentRegion;
            }

            tmpRegion = region;
            while (tmpRegion?.End == pos)
            {
                tmpRegion.End = moveTo;
                tmpRegion = tmpRegion.ParentRegion;
            }

        }

        public void AddExceptionHandler()
        {
            //TODO:
        }

        public void Update()
        {
            var dirtyBlocks = _blocks.Where(i => i._edgeDirty).ToList();

            foreach(var block in dirtyBlocks)
            {
                block.Edges.Clear();
                var index = _blocks.IndexOf(block);
                var nextBlock = index == _blocks.Count - 1 ? null : _blocks[index + 1];
                var endInst = nextBlock is null ? _analyzer._method.Body.Instructions[_analyzer._method.Body.Instructions.Count - 1] 
                    : nextBlock.Leader.Previous;
                block._edgeDirty = false;
                ConnectionBlockForInstruction(endInst, block, nextBlock);
            }

            dirtyBlocks = _blocks.Where(i => i._stackDepthDirty).ToList();
            var blockSet = new HashSet<Block>();
            for(int i = 0; i < dirtyBlocks.Count; i++)
            {
                var block = dirtyBlocks[i];
                if (blockSet.Contains(block)) continue;
                blockSet.Add(block);
                var index =  _blocks.IndexOf(block);
                var nextBlock = index == _blocks.Count - 1 ? null : _blocks[index + 1];
                var endInst = nextBlock?.Leader ??
                              _analyzer._method.Body.Instructions[_analyzer._method.Body.Instructions.Count - 1];
                block._stackDepthDirty = false;
                var newDepth = StackDepthAt(endInst);
                foreach (var edge in block.Edges)
                {
                    if (edge.To.EntryStackDepth != newDepth) //不考虑合流 合流等待analyzer验证
                    {
                        edge.To.EntryStackDepth = newDepth;
                    }
                    dirtyBlocks.Add(edge.To);
                }
            }
        }

        private void UpdateEHBlocks()
        {
            
        }


        public Block? NextBlock(Block block)
        {
            var index = _blocks.IndexOf(block);
            if(index == -1 || index == _blocks.Count - 1) return null;
            return _blocks[index + 1];
        }

        /// <summary>
        /// 拆分跳转目的地的block,返回跳转到的block
        /// </summary>
        /// <param name="target"></param>
        private Block TargetSplitBlock(Instruction target)
        {
            var block = BlockByInstruction(target);
            if (block.Leader == target) return block;

            var index = _blocks.IndexOf(block);
            var splitBlock = new Block(target, block.Region, StackDepthAt(target), _analyzer._method.Body);
            block.MoveAllEdgesTo(splitBlock);
            block.AddEdgeTo(splitBlock);
            _blocks.Insert(index + 1, splitBlock);

            return splitBlock;
        }

        private void ConnectionBlockForInstruction(Instruction endInst, Block block, Block? nextBlock)
        {
            block.RemoveAllEdges();
            switch (endInst.OpCode.FlowControl)
            {
                case FlowControl.Branch:
                case FlowControl.Call when endInst.OpCode.Code is Code.Jmp:
                    if (!CecilHelper.TryResolveOperandTargets(endInst.Operand, out var branchTargets, out _))
                    {
                        block._edgeDirty = true;
                        break;
                    }

                    foreach (var target in branchTargets)
                    {
                        var targetBlock = TargetSplitBlock(target);
                        block.AddEdgeTo(targetBlock);
                    }
                    break;
                case FlowControl.Cond_Branch:
                    if (!CecilHelper.TryResolveOperandTargets(endInst.Operand, out var condBranchTargets, out _))
                    {
                        block._edgeDirty = true;
                        break;
                    }

                    foreach (var target in condBranchTargets)
                    {
                        var targetBlock = TargetSplitBlock(target);
                        block.AddEdgeTo(targetBlock);
                    }
                    if(nextBlock is not null)  block.AddEdgeTo(nextBlock);
                    break;
                case FlowControl.Throw:
                    break;
                case FlowControl.Return:
                    break;
                default:
                    if (nextBlock is not null) block.AddEdgeTo(nextBlock);
                    break;
            }
        }

        public Block BlockByInstruction(Instruction inst)
        {
            var index = _analyzer._method.Body.Instructions.IndexOf(inst);
            if (index == -1)
                throw new Exception();

            for (int i = 0; i < _blocks.Count; i++)
            {
                if (_blocks[i].Index > index)
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
                if (_regions[i].StartIndex > index)
                    return stack.Peek();
                else if (_regions[i].EndIndex > index)
                    stack.Push(_regions[i]);
                else if (stack.Peek().EndIndex <= index)
                    stack.Pop();
            }
            throw new Exception(); //TODO:
        }
    }
}
