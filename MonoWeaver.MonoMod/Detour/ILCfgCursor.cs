using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoWeaver.CFG;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MonoWeaver.Detour
{
    public partial class ILCfgCursor
    {
        public enum AutoBranchKind { Auto, Br, Leave }

        public enum BlockPosition { FirstInstruction, LastInstruction }

        public delegate void CursorModifyHandler(ILCfgCursor cursor);

        public void EmitIfTrue(CursorModifyHandler cond, CursorModifyHandler handler, AutoBranchKind kind = AutoBranchKind.Auto)
        {
            throw new NotImplementedException();
        }

        public void EmitIfTrue(CursorModifyHandler cond, CursorModifyHandler prevHandler, Instruction target, AutoBranchKind kind = AutoBranchKind.Auto)
        {
            throw new NotImplementedException();
        }

        public void EmitIfElse(CursorModifyHandler cond, CursorModifyHandler trueHandler, CursorModifyHandler falseHandler, AutoBranchKind kind = AutoBranchKind.Auto)
        {
            throw new NotImplementedException();
        }

        public void EmitRet(CursorModifyHandler handler)
        {
            throw new NotImplementedException();
        }

        public void EmitExceptionHandler(Instruction start, Instruction end, CursorModifyHandler handler, ExceptionHandlerType type)
        {
            throw new NotImplementedException();
        }

        public bool TryGotoNextAtStackDepth(int depth, MoveType moveType = MoveType.Before)
        {
            throw new NotImplementedException();
        }

        public bool TryGotoBlock(ILCFGraph.BasicBlock block, BlockPosition pos = BlockPosition.FirstInstruction)
        {
            throw new NotImplementedException();
        }

        public void GotoNextAtStackDepth(int depth, MoveType moveType = MoveType.Before)
        {
            throw new NotImplementedException();
        }

        public void GotoBlock(ILCFGraph.BasicBlock block, BlockPosition pos = BlockPosition.FirstInstruction)
        {
            throw new NotImplementedException();
        }

        public void GotoAfterBaseCall()
        {
            throw new NotImplementedException();
        }

        public void GotoBeforeAllRets(bool captureRet = true)
        {
            throw new NotImplementedException();
        }


        public bool TryWrapAllCall(
            Func<Instruction, bool> callPred,
            CursorModifyHandler beforeCall,
            CursorModifyHandler afterCall
        )
        {
            throw new NotImplementedException();
        }


        public IEnumerable<ILCFGraph.BasicBlock> PredBlocks()
        {
            throw new NotImplementedException();
        }
        public IEnumerable<ILCFGraph.BasicBlock> SuccBlocks()
        {
            throw new NotImplementedException();
        }

        public ILCfgCursor Clone()
        {
            throw new NotImplementedException(); 
        }


        public ILCFGraph.BasicBlock CurrentBlock { get; }


    }

    public partial class ILCfgCursor
    {
        //TODO: 内包含一个ILCursor，包装ILCursor所有函数

        private ILCursor InnerCursor { get; set; }

    }
}
