using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;

namespace MonoWeaver.Test;

public static class CecilMutator
{
    public static void Apply(MethodDefinition m, CFGExceptionType type)
    {
        switch (type)
        {
            case CFGExceptionType.InvalidOpCode: Mut_InvalidOpCode(m); break;

            case CFGExceptionType.EhRegionOverlap: Mut_EhRegionOverlap(m); break;
            case CFGExceptionType.EhRegionNonTryDuplication: Mut_EhRegionNonTryDuplication(m); break;
            case CFGExceptionType.EhNestedInFilter: Mut_EhNestedInFilter(m); break;
            case CFGExceptionType.TryAndHandlerNotInSameEnclosingRegion: Mut_TryAndHandlerNotInSameEnclosingRegion(m); break;
            case CFGExceptionType.InvalidEhTableOrdering: Mut_InvalidEhTableOrdering(m); break;

            case CFGExceptionType.InvalidInstruction: Mut_InvalidInstruction(m); break;
            case CFGExceptionType.TypeMismatch: Mut_TypeMismatch(m); break;
            case CFGExceptionType.InconsistentFieldAccess: Mut_InconsistentFieldAccess(m); break;
            case CFGExceptionType.StackUnderflow: Mut_StackUnderflow(m); break;
            case CFGExceptionType.StackOverflow: Mut_StackOverflow(m); break;
            case CFGExceptionType.InvalidFallThrough: Mut_InvalidFallThrough(m); break;
            case CFGExceptionType.UninitializedLocal: Mut_UninitializedLocal(m); break;
            case CFGExceptionType.IncompatibleMergeTypes: Mut_IncompatibleMergeTypes(m); break;
            case CFGExceptionType.IncompatibleMergeDepth: Mut_IncompatibleMergeDepth(m); break;
            case CFGExceptionType.InvalidBrTarget: Mut_InvalidBrTarget(m); break;
            case CFGExceptionType.BrTargetCrossEhRegion: Mut_BrTargetCrossEhRegion(m); break;
            case CFGExceptionType.OutOfRange: Mut_ArguementOutOfRange(m); break;

            case CFGExceptionType.None:
            default:
                throw new NotSupportedException($"Unsupported: {type}");
        }
    }


    private static void ResetBody(MethodDefinition m, int maxStack = 8, bool initLocals = true)
    {
        m.Body = new MethodBody(m)
        {
            MaxStackSize = maxStack,
            InitLocals = initLocals
        };
    }


    private static (ILProcessor il, Instruction ret) BuildValidRet(MethodDefinition m)
    {
        ResetBody(m);
        var il = m.Body.GetILProcessor();
        var ret = Instruction.Create(OpCodes.Ret);
        il.Append(ret);
        return (il, ret);
    }


    private static void Mut_InvalidOpCode(MethodDefinition m)
    {
        var (il, ret) = BuildValidRet(m);
        // 插入 volatile. 前缀，但后面接 ret（不兼容） => InvalidOpCode
        il.InsertBefore(ret, Instruction.Create(OpCodes.Volatile));
    }


    private static void Mut_InvalidInstruction(MethodDefinition m)
    {
        var (il, ret) = BuildValidRet(m);
        // rethrow 只能在 catch handler 内
        il.InsertBefore(ret, Instruction.Create(OpCodes.Rethrow));
    }


    private static void Mut_StackUnderflow(MethodDefinition m)
    {
        var (il, ret) = BuildValidRet(m);
        il.InsertBefore(ret, Instruction.Create(OpCodes.Pop));
    }


    private static void Mut_StackOverflow(MethodDefinition m)
    {
        ResetBody(m, maxStack: 2);
        var il = m.Body.GetILProcessor();

        var i1 = Instruction.Create(OpCodes.Ldc_I4_0);
        var i2 = Instruction.Create(OpCodes.Ldc_I4_1);
        var pop1 = Instruction.Create(OpCodes.Pop);
        var pop2 = Instruction.Create(OpCodes.Pop);
        var ret = Instruction.Create(OpCodes.Ret);

        il.Append(i1);
        il.Append(i2);
        il.Append(pop1);
        il.Append(pop2);
        il.Append(ret);

 
        m.Body.MaxStackSize = 1;
    }


    private static void Mut_TypeMismatch(MethodDefinition m)
    {
        ResetBody(m);
        var module = m.Module;
        m.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Object)); // local0: object

        var il = m.Body.GetILProcessor();


        var ldnull = Instruction.Create(OpCodes.Ldnull);
        var stloc = Instruction.Create(OpCodes.Stloc_0);
        var ret = Instruction.Create(OpCodes.Ret);

        il.Append(ldnull);
        il.Append(stloc);
        il.Append(ret);
        
        il.Replace(ldnull, Instruction.Create(OpCodes.Ldc_I4_0));
    }


    private static void Mut_InconsistentFieldAccess(MethodDefinition m)
    {
        ResetBody(m);
        var il = m.Body.GetILProcessor();

        var fieldOwner = m.Module.Types.Single(t => t.FullName.EndsWith("FieldOwner"));
        var instField = fieldOwner.Fields.Single(f => f.Name == nameof(FieldOwner.InstanceField));
        var staticField = fieldOwner.Fields.Single(f => f.Name == nameof(FieldOwner.StaticField));


        var ldsfld = Instruction.Create(OpCodes.Ldsfld, staticField);
        var pop = Instruction.Create(OpCodes.Pop);
        var ret = Instruction.Create(OpCodes.Ret);

        il.Append(ldsfld);
        il.Append(pop);
        il.Append(ret);
        
        ldsfld.Operand = instField;
    }


    private static void Mut_UninitializedLocal(MethodDefinition m)
    {
        ResetBody(m, initLocals: true);
        var module = m.Module;
        m.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32)); // local0: int

        var il = m.Body.GetILProcessor();

        // ldc.i4.0; stloc.0; ldloc.0; pop; ret
        var ldc = Instruction.Create(OpCodes.Ldc_I4_0);
        var stloc = Instruction.Create(OpCodes.Stloc_0);
        var ldloc = Instruction.Create(OpCodes.Ldloc_0);
        var pop = Instruction.Create(OpCodes.Pop);
        var ret = Instruction.Create(OpCodes.Ret);

        il.Append(ldc);
        il.Append(stloc);
        il.Append(ldloc);
        il.Append(pop);
        il.Append(ret);
        
        il.Remove(ldc);
        il.Remove(stloc);
        m.Body.InitLocals = false;
    }


    private static void Mut_IncompatibleMergeTypes(MethodDefinition m)
    {
        ResetBody(m);
        var il = m.Body.GetILProcessor();

        // 合法基线：两个分支都 push object (ldnull)，merge 后 pop ret
        var labelTrue = Instruction.Create(OpCodes.Nop);
        var merge = Instruction.Create(OpCodes.Nop);

        var ldarg0 = Instruction.Create(OpCodes.Ldarg_0);         // bool cond (此 stub 要有一个 bool 参数)
        var brtrue = Instruction.Create(OpCodes.Brtrue_S, labelTrue);

        var ldnullFalse = Instruction.Create(OpCodes.Ldnull);
        var brMerge1 = Instruction.Create(OpCodes.Br_S, merge);

        var ldnullTrue = Instruction.Create(OpCodes.Ldnull);      // 等下替换成 ldc.i4.0
        var brMerge2 = Instruction.Create(OpCodes.Br_S, merge);

        var pop = Instruction.Create(OpCodes.Pop);
        var ret = Instruction.Create(OpCodes.Ret);

        il.Append(ldarg0);
        il.Append(brtrue);
        il.Append(ldnullFalse);
        il.Append(brMerge1);

        il.Append(labelTrue);
        il.Append(ldnullTrue);
        il.Append(brMerge2);

        il.Append(merge);
        il.Append(pop);
        il.Append(ret);

        // 修改 true 分支 push 的类型：object -> int32 => merge 类型不兼容
        il.Replace(ldnullTrue, Instruction.Create(OpCodes.Ldc_I4_0));
    }
    
    private static void Mut_IncompatibleMergeDepth(MethodDefinition m)
    {
        ResetBody(m);
        var il = m.Body.GetILProcessor();

        var labelTrue = Instruction.Create(OpCodes.Nop);
        var merge = Instruction.Create(OpCodes.Nop);

        var ldarg0 = Instruction.Create(OpCodes.Ldarg_0);
        var brtrue = Instruction.Create(OpCodes.Brtrue_S, labelTrue);

        var ldnullFalse = Instruction.Create(OpCodes.Ldnull);
        var brMerge1 = Instruction.Create(OpCodes.Br_S, merge);

        var ldnullTrue = Instruction.Create(OpCodes.Ldnull);
        var brMerge2 = Instruction.Create(OpCodes.Br_S, merge);

        var pop = Instruction.Create(OpCodes.Pop);
        var ret = Instruction.Create(OpCodes.Ret);

        il.Append(ldarg0);
        il.Append(brtrue);
        il.Append(ldnullFalse);
        il.Append(brMerge1);

        il.Append(labelTrue);
        il.Append(ldnullTrue);
        il.Append(brMerge2);

        il.Append(merge);
        il.Append(pop);
        il.Append(ret);

        il.Replace(ldnullTrue, Instruction.Create(OpCodes.Nop));
    }
    
    private static void Mut_InvalidBrTarget(MethodDefinition m)
    {
        ResetBody(m);
        var il = m.Body.GetILProcessor();

        var ret = Instruction.Create(OpCodes.Ret);
        var br = Instruction.Create(OpCodes.Br_S, ret);
        il.Append(br);
        il.Append(ret);
        
        var dangling = Instruction.Create(OpCodes.Nop);
        br.Operand = dangling;
    }


    private static void Mut_ArguementOutOfRange(MethodDefinition m)
    {
        ResetBody(m);
        var il = m.Body.GetILProcessor();
        var module = m.Module;

        var bogus = new ParameterDefinition("bogus", ParameterAttributes.None, module.TypeSystem.Int32);


        il.Append(Instruction.Create(OpCodes.Ldarg, bogus));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ret));
    }
    
    private static void Mut_BrTargetCrossEhRegion(MethodDefinition m)
    {
        ResetBody(m);
        var il = m.Body.GetILProcessor();
        var module = m.Module;

        var end = Instruction.Create(OpCodes.Ret);
        var afterHandler = Instruction.Create(OpCodes.Nop);

        // tryStart:
        var t0 = Instruction.Create(OpCodes.Nop);
        var leave = Instruction.Create(OpCodes.Leave_S, end);

        // handlerStart:
        var h0 = Instruction.Create(OpCodes.Pop);
        var h1 = Instruction.Create(OpCodes.Leave_S, end);

        il.Append(t0);
        il.Append(leave);
        il.Append(h0);
        il.Append(h1);
        il.Append(afterHandler);
        il.Append(end);

        var eh = new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = module.ImportReference(typeof(Exception)),
            TryStart = t0,
            TryEnd = h0,
            HandlerStart = h0,
            HandlerEnd = afterHandler 
        };
        m.Body.ExceptionHandlers.Add(eh);
        
        il.Replace(leave, Instruction.Create(OpCodes.Br_S, end));
    }


    private static void Mut_InvalidFallThrough(MethodDefinition m)
    {
        ResetBody(m);
        var il = m.Body.GetILProcessor();
        var module = m.Module;

        var end = Instruction.Create(OpCodes.Ret);
        var afterHandler = Instruction.Create(OpCodes.Nop);

        var t0 = Instruction.Create(OpCodes.Nop);
        
        var leave = Instruction.Create(OpCodes.Leave_S, end);

        var h0 = Instruction.Create(OpCodes.Pop);
        var h1 = Instruction.Create(OpCodes.Leave_S, end);

        il.Append(t0);
        il.Append(leave);
        il.Append(h0);
        il.Append(h1);
        il.Append(afterHandler);
        il.Append(end);

        var eh = new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = module.ImportReference(typeof(Exception)),
            TryStart = t0,
            TryEnd = h0,
            HandlerStart = h0,
            HandlerEnd = afterHandler
        };
        m.Body.ExceptionHandlers.Add(eh);
        
        il.Replace(leave, Instruction.Create(OpCodes.Ldnull));
    }


    private static (Instruction i0, Instruction i2, Instruction i4, Instruction i6, Instruction endRet,
                    ExceptionHandler eh1, ExceptionHandler eh2)
        BuildValid_TwoDisjointTryCatch(MethodDefinition m)
    {
        ResetBody(m);
        var il = m.Body.GetILProcessor();
        var module = m.Module;

        // i0 nop
        // i1 leave end
        // i2 nop
        // i3 leave end
        // i4 pop; i5 leave end   (handler1)
        // i6 pop; i7 leave end   (handler2)
        // i8 ret

        var end = Instruction.Create(OpCodes.Ret);

        var i0 = Instruction.Create(OpCodes.Nop);
        var i1 = Instruction.Create(OpCodes.Leave_S, end);

        var i2 = Instruction.Create(OpCodes.Nop);
        var i3 = Instruction.Create(OpCodes.Leave_S, end);

        var i4 = Instruction.Create(OpCodes.Pop);
        var i5 = Instruction.Create(OpCodes.Leave_S, end);

        var i6 = Instruction.Create(OpCodes.Pop);
        var i7 = Instruction.Create(OpCodes.Leave_S, end);

        il.Append(i0);
        il.Append(i1);
        il.Append(i2);
        il.Append(i3);
        il.Append(i4);
        il.Append(i5);
        il.Append(i6);
        il.Append(i7);
        il.Append(end);

        var eh1 = new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = module.ImportReference(typeof(Exception)),
            TryStart = i0,
            TryEnd = i2,
            HandlerStart = i4,
            HandlerEnd = i6
        };

        var eh2 = new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = module.ImportReference(typeof(Exception)),
            TryStart = i2,
            TryEnd = i4,
            HandlerStart = i6,
            HandlerEnd = end
        };

        m.Body.ExceptionHandlers.Add(eh1);
        m.Body.ExceptionHandlers.Add(eh2);

        return (i0, i2, i4, i6, end, eh1, eh2);
    }


    private static void Mut_EhRegionOverlap(MethodDefinition m)
    {
        var (i0, i2, i4, _, _, _, eh2) = BuildValid_TwoDisjointTryCatch(m);
        
        eh2.TryStart = i0;
        eh2.TryEnd = i4;
    }
    
    private static void Mut_EhRegionNonTryDuplication(MethodDefinition m)
    {
        var (_, _, _, _, _, eh1, eh2) = BuildValid_TwoDisjointTryCatch(m);
        
        eh2.HandlerStart = eh1.HandlerStart;
        eh2.HandlerEnd = eh1.HandlerEnd;
    }


    private static void Mut_EhNestedInFilter(MethodDefinition m)
    {
        ResetBody(m);
        var il = m.Body.GetILProcessor();
        var module = m.Module;

        var end = Instruction.Create(OpCodes.Ret);
        var afterFilterHandler = Instruction.Create(OpCodes.Nop);
        var afterCatch2Handler = Instruction.Create(OpCodes.Nop);

        // try1
        var t0 = Instruction.Create(OpCodes.Nop);
        var t1 = Instruction.Create(OpCodes.Leave_S, end);

        // filter block (FilterStart..HandlerStart)
        var f0 = Instruction.Create(OpCodes.Pop);        // pop exception
        var f1 = Instruction.Create(OpCodes.Ldc_I4_0);   // false
        var f2 = Instruction.Create(OpCodes.Endfilter);

        // filter handler
        var h0 = Instruction.Create(OpCodes.Pop);
        var h1 = Instruction.Create(OpCodes.Leave_S, end);

        // try2
        var t2 = Instruction.Create(OpCodes.Nop);
        var t3 = Instruction.Create(OpCodes.Leave_S, end);

        // catch2 handler
        var h2 = Instruction.Create(OpCodes.Pop);
        var h3 = Instruction.Create(OpCodes.Leave_S, end);

        il.Append(t0);
        il.Append(t1);
        il.Append(f0);
        il.Append(f1);
        il.Append(f2);
        il.Append(h0);
        il.Append(h1);
        il.Append(afterFilterHandler);

        il.Append(t2);
        il.Append(t3);
        il.Append(h2);
        il.Append(h3);
        il.Append(afterCatch2Handler);

        il.Append(end);

        var ehFilter = new ExceptionHandler(ExceptionHandlerType.Filter)
        {
            TryStart = t0,
            TryEnd = f0,              // exclusive
            FilterStart = f0,
            HandlerStart = h0,
            HandlerEnd = afterFilterHandler
        };

        var ehCatch2 = new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = module.ImportReference(typeof(Exception)),
            TryStart = t2,
            TryEnd = h2,
            HandlerStart = h2,
            HandlerEnd = afterCatch2Handler
        };

        m.Body.ExceptionHandlers.Add(ehFilter);
        m.Body.ExceptionHandlers.Add(ehCatch2);

        //把 try2 挪到 filter 区间内部
        ehCatch2.TryStart = f1;
        ehCatch2.TryEnd = f2;
    }


    private static void Mut_TryAndHandlerNotInSameEnclosingRegion(MethodDefinition m)
    {
        ResetBody(m);
        var il = m.Body.GetILProcessor();
        var module = m.Module;

        var end = Instruction.Create(OpCodes.Ret);
        var afterOuterHandler = Instruction.Create(OpCodes.Nop);
        var afterInnerHandlerOutside = Instruction.Create(OpCodes.Nop);
        
        var o0 = Instruction.Create(OpCodes.Nop);
        var o1 = Instruction.Create(OpCodes.Nop);
        
        var i0 = Instruction.Create(OpCodes.Nop);
        var i1 = Instruction.Create(OpCodes.Leave_S, end);
        
        var ih0 = Instruction.Create(OpCodes.Pop);
        var ih1 = Instruction.Create(OpCodes.Leave_S, end);
        
        var oh0 = Instruction.Create(OpCodes.Pop);
        var oh1 = Instruction.Create(OpCodes.Leave_S, end);

     
        var ext0 = Instruction.Create(OpCodes.Pop);
        var ext1 = Instruction.Create(OpCodes.Leave_S, end);

        il.Append(o0);
        il.Append(o1);
        il.Append(i0);
        il.Append(i1);
        il.Append(ih0);
        il.Append(ih1);
        il.Append(oh0);
        il.Append(oh1);
        il.Append(afterOuterHandler);
        il.Append(ext0);
        il.Append(ext1);
        il.Append(afterInnerHandlerOutside);
        il.Append(end);

        // outer EH: try [o0, oh0), handler [oh0, afterOuterHandler)
        var outer = new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = module.ImportReference(typeof(Exception)),
            TryStart = o0,
            TryEnd = oh0,
            HandlerStart = oh0,
            HandlerEnd = afterOuterHandler
        };

        // inner EH：try [i0, ih0), handler [ih0, oh0)  (inner handler 在 outer try 内)
        var inner = new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = module.ImportReference(typeof(Exception)),
            TryStart = i0,
            TryEnd = ih0,
            HandlerStart = ih0,
            HandlerEnd = oh0
        };

        m.Body.ExceptionHandlers.Add(outer);
        m.Body.ExceptionHandlers.Add(inner);

        //inner handler -> outer try 外
        inner.HandlerStart = ext0;
        inner.HandlerEnd = afterInnerHandlerOutside;
    }


    private static void Mut_InvalidEhTableOrdering(MethodDefinition m)
    {
        ResetBody(m);
        var il = m.Body.GetILProcessor();
        var module = m.Module;

        var end = Instruction.Create(OpCodes.Ret);
        var afterInnerHandler = Instruction.Create(OpCodes.Nop);
        var afterOuterHandler = Instruction.Create(OpCodes.Nop);

        // outer try
        var o0 = Instruction.Create(OpCodes.Nop);

        // inner try
        var i0 = Instruction.Create(OpCodes.Nop);
        var i1 = Instruction.Create(OpCodes.Leave_S, end);

        // inner handler
        var ih0 = Instruction.Create(OpCodes.Pop);
        var ih1 = Instruction.Create(OpCodes.Leave_S, end);

        // outer handler
        var oh0 = Instruction.Create(OpCodes.Pop);
        var oh1 = Instruction.Create(OpCodes.Leave_S, end);

        il.Append(o0);
        il.Append(i0);
        il.Append(i1);
        il.Append(ih0);
        il.Append(ih1);
        il.Append(afterInnerHandler);
        il.Append(oh0);
        il.Append(oh1);
        il.Append(afterOuterHandler);
        il.Append(end);

        var outer = new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = module.ImportReference(typeof(Exception)),
            TryStart = o0,
            TryEnd = oh0,
            HandlerStart = oh0,
            HandlerEnd = afterOuterHandler
        };

        var inner = new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = module.ImportReference(typeof(Exception)),
            TryStart = i0,
            TryEnd = ih0,
            HandlerStart = ih0,
            HandlerEnd = afterInnerHandler
        };
        m.Body.ExceptionHandlers.Add(outer);
        m.Body.ExceptionHandlers.Add(inner);
    }
}