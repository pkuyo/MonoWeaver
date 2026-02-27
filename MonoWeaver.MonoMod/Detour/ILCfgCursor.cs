using System;
using System.Collections.Generic;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoWeaver.CFG;
using MethodBody = Mono.Cecil.Cil.MethodBody;

namespace MonoWeaver.MonoMod.Detour
{
    public partial class ILCfgCursor
    {
        public enum AutoBranchKind { Auto, Br, Leave }

        public enum BlockPosition { FirstInstruction, LastInstruction }

        public delegate void CursorModifyHandler(ILCfgCursor cursor);
        
        public ILCfgCursor(ILCfgContext context)
        {
            _innerCursor = new ILCursor(context.InnerContext);
            _context = context;
        }
        internal ILCfgCursor(ILCfgContext context, ILCursor cursor)
        {
            _context = context;
            _innerCursor = cursor;
        }

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

        /*
        public void EmitExceptionHandler(Instruction start, Instruction end, CursorModifyHandler handler, ExceptionHandlerType type)
        {
            throw new NotImplementedException();
        }
        */

        public bool TryGotoNextAtStackDepth(int depth, MoveType moveType = MoveType.Before)
        {
            throw new NotImplementedException();
        }

        public bool TryGotoBlock(ILCFGraph.Block block, BlockPosition pos = BlockPosition.FirstInstruction)
        {
            throw new NotImplementedException();
        }

        public void GotoNextAtStackDepth(int depth, MoveType moveType = MoveType.Before)
        {
            throw new NotImplementedException();
        }

        public void GotoBlock(ILCFGraph.Block block, BlockPosition pos = BlockPosition.FirstInstruction)
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


        public IEnumerable<ILCFGraph.Block> PredBlocks()
        {
            throw new NotImplementedException();
        }
        public IEnumerable<ILCFGraph.Block> SuccBlocks()
        {
            throw new NotImplementedException();
        }
        
        public ILCFGraph.Block CurrentBlock => Graph.BlockByInstruction(Next);


    }

    public partial class ILCfgCursor
    {
        //内包含一个ILCursor，包装ILCursor所有函数
        private readonly ILCursor _innerCursor;

        private readonly ILCfgContext _context;
        public bool IsBefore(Instruction instr)
        {
            return _innerCursor.IsBefore(instr);
        }

        public bool IsAfter(Instruction instr)
        {
           return _innerCursor.IsAfter(instr);
        }

        public ILCfgCursor Goto(Instruction insn, MoveType moveType = MoveType.Before, bool setTarget = false)
        {
            _innerCursor.Goto(insn, moveType, setTarget);
            return this;
        }

        public ILCfgCursor MoveAfterLabels()
        {
            _innerCursor.MoveAfterLabels();
            return this;
        }

        public ILCfgCursor MoveBeforeLabels()
        {
            _innerCursor.MoveBeforeLabels();
            return this;
        }

        public ILCfgCursor Goto(int index, MoveType moveType = MoveType.Before, bool setTarget = false)
        {
            _innerCursor.Goto(index, moveType, setTarget);
            return this;
        }

        public ILCfgCursor GotoLabel(ILLabel label, MoveType moveType = MoveType.AfterLabel, bool setTarget = false)
        {
            _innerCursor.GotoLabel(label, moveType, setTarget);
            return this;
        }

        public ILCfgCursor GotoNext(MoveType moveType = MoveType.Before, params Func<Instruction, bool>[] predicates)
        {
            _innerCursor.GotoNext(moveType, predicates);
            return this;
        }

        public bool TryGotoNext(MoveType moveType = MoveType.Before, params Func<Instruction, bool>[] predicates)
        {
            return _innerCursor.TryGotoNext(moveType, predicates);
        }

        public ILCfgCursor GotoPrev(MoveType moveType = MoveType.Before, params Func<Instruction, bool>[] predicates)
        {
            _innerCursor.GotoPrev(moveType, predicates);
            return this;
        }

        public bool TryGotoPrev(MoveType moveType = MoveType.Before, params Func<Instruction, bool>[] predicates)
        {
            return _innerCursor.TryGotoPrev(moveType, predicates);
        }

        public ILCfgCursor GotoNext(params Func<Instruction, bool>[] predicates)
        {
            _innerCursor.GotoNext(predicates);
            return this;
        }

        public bool TryGotoNext(params Func<Instruction, bool>[] predicates)
        {
            return _innerCursor.TryGotoNext(predicates);
        }

        public ILCfgCursor GotoPrev(params Func<Instruction, bool>[] predicates)
        {
             _innerCursor.GotoPrev(predicates);
            return this;
        }

        public bool TryGotoPrev(params Func<Instruction, bool>[] predicates)
        {
            return _innerCursor.TryGotoPrev(predicates);
        }

        public void FindNext(out ILCfgCursor[] cursors, params Func<Instruction, bool>[] predicates)
        {
            _innerCursor.FindNext(out var cs, predicates);
            cursors = new ILCfgCursor[cs.Length];
            for (int i = 0; i < cs.Length; i++) cursors[i] = new ILCfgCursor(Context, cs[i]);
        }

        public bool TryFindNext(out ILCfgCursor[] cursors, params Func<Instruction, bool>[] predicates)
        {
            var re = _innerCursor.TryFindNext(out var cs, predicates);
            cursors = new ILCfgCursor[cs.Length];
            for (int i = 0; i < cs.Length; i++) cursors[i] = new ILCfgCursor(Context, cs[i]);
            return re;
        }

        public void FindPrev(out ILCfgCursor[] cursors, params Func<Instruction, bool>[] predicates)
        {
            _innerCursor.FindPrev(out var cs, predicates);
            cursors = new ILCfgCursor[cs.Length];
            for (int i = 0; i < cs.Length; i++) cursors[i] = new ILCfgCursor(Context, cs[i]);
        }

        public bool TryFindPrev(out ILCfgCursor[] cursors, params Func<Instruction, bool>[] predicates)
        {
            var re = _innerCursor.TryFindPrev(out var cs, predicates);
            cursors = new ILCfgCursor[cs.Length];
            for (int i = 0; i < cs.Length; i++) cursors[i] = new ILCfgCursor(Context, cs[i]);
            return re;
        }

        public void MarkLabel(ILLabel label)
        {
            _innerCursor.MarkLabel(label);
        }

        public ILLabel MarkLabel()
        {
            return _innerCursor.MarkLabel();
        }

        public ILLabel DefineLabel()
        {
            return _innerCursor.DefineLabel();
        }

        public ILCfgCursor Remove()
        {
            Graph.Remove(_innerCursor.Next);
            _innerCursor.Remove();
            return this;
        }

        public ILCfgCursor RemoveRange(int num)
        {
            //TODO: 笨方式
            var inst = _innerCursor.Next;
            for(int i = 0; i < num && inst != null; i++, inst = inst.Next)
                Graph.Remove(inst);
                
            _innerCursor.RemoveRange(num);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, ParameterDefinition parameter)
        {
            _innerCursor.Emit(opcode, parameter);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, VariableDefinition variable)
        {
            _innerCursor.Emit(opcode, variable);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, Instruction[] targets)
        {
            _innerCursor.Emit(opcode, targets);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, Instruction target)
        {
            _innerCursor.Emit(opcode, target);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, double value)
        {
            _innerCursor.Emit(opcode, value);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, float value)
        {
            _innerCursor.Emit(opcode, value);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, long value)
        {
            _innerCursor.Emit(opcode, value);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, sbyte value)
        {
            _innerCursor.Emit(opcode, value);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, byte value)
        {
            _innerCursor.Emit(opcode, value);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, string value)
        {
            _innerCursor.Emit(opcode, value);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, FieldReference field)
        {
            _innerCursor.Emit(opcode, field);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, CallSite site)
        {
            _innerCursor.Emit(opcode, site);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, TypeReference type)
        {
            _innerCursor.Emit(opcode, type);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode)
        {
            _innerCursor.Emit(opcode);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, int value)
        {
            _innerCursor.Emit(opcode, value);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, MethodReference method)
        {
            _innerCursor.Emit(opcode, method);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, FieldInfo field)
        {
            _innerCursor.Emit(opcode, field);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, MethodBase method)
        {
            _innerCursor.Emit(opcode, method);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, Type type)
        {
            _innerCursor.Emit(opcode, type);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit(OpCode opcode, object operand)
        {
            _innerCursor.Emit(opcode, operand);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public ILCfgCursor Emit<T>(OpCode opcode, string memberName)
        {
            _innerCursor.Emit<T>(opcode, memberName);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            return this;
        }

        public int AddReference<T>(T t)
        {
            return _innerCursor.AddReference(t);
        }

        public void EmitGetReference<T>(int id)
        {
            _innerCursor.EmitGetReference<T>(id);
            Graph.Emit(_innerCursor.Prev.Previous.Previous, _innerCursor.Prev.Previous);
            Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
        }

        public int EmitReference<T>(T t)
        {
            return _innerCursor.EmitReference(t);
        }

        public int EmitDelegate<T>(T cb) where T : Delegate
        {
            var re = _innerCursor.EmitDelegate(cb);
            if (re == -1)
            {
                Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            }
            else
            {
                Graph.Emit(_innerCursor.Prev.Previous.Previous.Previous, _innerCursor.Prev.Previous.Previous);
                Graph.Emit(_innerCursor.Prev.Previous.Previous, _innerCursor.Prev.Previous);
                Graph.Emit(_innerCursor.Prev.Previous, _innerCursor.Prev);
            }
            return re;
        }
        
        public ILCfgCursor Clone()
        {
            return new ILCfgCursor(_context, _innerCursor.Clone());
        }

        public Instruction Next
        {
            get => _innerCursor.Next;
            set => _innerCursor.Next = value;
        }

        public Instruction Prev
        {
            get => _innerCursor.Prev;
            set => _innerCursor.Prev = value;
        }

        public Instruction Previous
        {
            get => _innerCursor.Previous;
            set => _innerCursor.Previous = value;
        }

        public SearchTarget SearchTarget
        {
            get => _innerCursor.SearchTarget;
            set => _innerCursor.SearchTarget = value;
        }

        public IEnumerable<ILLabel> IncomingLabels => _innerCursor.IncomingLabels;

        public MethodDefinition Method => _innerCursor.Method;

        public MethodBody Body => _innerCursor.Body;

        public ModuleDefinition Module => _innerCursor.Module;
        
        public ILCfgContext Context => _context;

        public ILCFGraph Graph => _context.Graph;
    }
}
