using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using MonoWeaver.Utils;
using System;
using System.Collections.Generic;


namespace MonoWeaver.CFG;


[Flags]
public enum VerifyOptions
{
    ExceptionHandler,
    StackType,
    LocalInit,
}



//TODO: throw new Exception();相关后续需要调整 改为记录错误信息并给据故障等级判断是否中止分析

/// <summary>
/// EH段
/// </summary>
/// <param name="eh"></param>
public sealed class ExceptionBlock(ExceptionHandler eh)
{
    public class Region
    {
        public int Start;
        public int End;
        public ExceptionBlock Clause = null!;
        public Region? ParentRegion;
        public RegionKind Kind;

        public Region(RegionKind kind, int start = -1, int end = -1)
        {
            Start = start;
            End = end;
            Kind = kind;
        }

    }


    public Region ProtectedRegion = null!;
    public Region? FilterRegion;
    public Region HandlerRegion = null!;

    public ExceptionHandler ExceptionHandler = eh;
    public int Id;

    public void SetClause()
    {
        ProtectedRegion.Clause = this;
        HandlerRegion.Clause = this;
        if (FilterRegion is not null)
            FilterRegion.Clause = this;
    }
}

public enum RegionKind
{
    Normal = 0,
    Try,
    Handler,
    Filter
}


public partial class ILCfg
{
    /// <summary>
    /// CFG基本块
    /// </summary>
    public sealed class BasicBlock(Instruction start, ExceptionBlock.Region region) : IEquatable<BasicBlock>
    {
        public Instruction Leader  = start;
        public EvalStackNode EntryNode = null!;
        public List<ControlFlowEdge> Edges = new();

        public RegionKind Kind = region.Kind;
        public ExceptionBlock.Region Region = region;
        public ulong[]? initLocals = null; //如果不需要initlocal分析则为null

        public bool Equals(BasicBlock other)
        {
            return Leader.Equals(other.Leader);
        }
    }

    /// <summary>
    /// CFG边
    /// </summary>
    public sealed class ControlFlowEdge(BasicBlock from, BasicBlock to)
    {
        public BasicBlock From = from;
        public BasicBlock To = to;

        //public EvalStackTransfer? StackTransfer;
    }
}
public partial class ILCfg
{
    private bool BuildExceptionBlock(ExceptionHandler eh, Dictionary<Instruction, int> instDic, out ExceptionBlock? block)
    {
        block = null;
        if (eh.TryStart is null) return false;
        if (eh.TryEnd is null) return false;
        if (eh.HandlerStart is null) return false;
        if (eh.HandlerEnd is null) return false;
        if (eh is { HandlerType: ExceptionHandlerType.Filter, FilterStart: null })
            return false;

        int filterStart = -1;

        if (!instDic.TryGetValue(eh.TryStart, out var tryStart)) return false;
        if (!instDic.TryGetValue(eh.TryEnd, out var tryEnd)) return false;
        if (!instDic.TryGetValue(eh.HandlerStart, out var handlerStart)) return false;
        if (!instDic.TryGetValue(eh.HandlerEnd, out var handlerEnd)) return false;
        if (eh.FilterStart is not null && !instDic.TryGetValue(eh.FilterStart, out filterStart)) return false;

        if(tryStart >= tryEnd || tryEnd >= handlerStart || handlerStart >= handlerEnd)
            return false; //不符合约束

        if (eh.HandlerType == ExceptionHandlerType.Filter)
        {
            if (tryEnd >= filterStart || filterStart >= handlerStart)
                return false; //不符合约束
        }



        block = new ExceptionBlock(eh)
        {
            ProtectedRegion = new ExceptionBlock.Region(RegionKind.Try, tryStart, tryEnd),
            HandlerRegion = new ExceptionBlock.Region(RegionKind.Handler, handlerStart, handlerEnd),
            FilterRegion = eh.HandlerType == ExceptionHandlerType.Filter
                ? new ExceptionBlock.Region(RegionKind.Filter, filterStart, handlerStart)
                : null
        };
        return true;
    }

}

public partial class ILCfg
{

    private readonly MethodDefinition _method;

    private readonly EvalStackNode _root = new();

    private readonly Dictionary<(StackTypeRef type, EvalStackNode prev), EvalStackNode> _nodeIntern = new();
    private readonly Dictionary<Instruction, BasicBlock> _blockMap = new();
    
    private List<BasicBlock> _blocks = null!;
    private Dictionary<int, (RegionKind kind, TypeReference? type)> _regionFrames = null!;
    
    private List<ExceptionBlock> _exceptionBlocks = null!;
    private List<ExceptionBlock.Region> _ehRegions = null!;
    
    private readonly bool _needInitAnalysis;


    public ILCfg(MethodDefinition method)
    {
        if (!method.IsIL)
        {
            throw new ArgumentException("Method must be IL method", method.FullName);
        }
        _method = method;
        _method.Body.SimplifyMacros();
        _needInitAnalysis = !_method.Body.InitLocals;
        BuildGraph();
    }



    private void BuildGraph()
    {
        FirstPass();
        ControlFlowPass();
    }


    /// <summary>
    /// 处理基本块划分和异常处理边界检查，同时进行指令合法性检查（跳转目标合法性、前缀合法性、调用指令可解析性等）
    /// </summary>
    private void FirstPass()
    {
        _blocks = new List<BasicBlock>();

        _exceptionBlocks = new List<ExceptionBlock>(_method.Body.ExceptionHandlers.Count);
        _ehRegions = new List<ExceptionBlock.Region>(_method.Body.ExceptionHandlers.Count);
        
        var instDictionary = new Dictionary<Instruction, int>(_method.Body.Instructions.Count);
        var instEhRegions = new int[_method.Body.Instructions.Count];

        for(int i = 0; i < _method.Body.Instructions.Count; i++)
        {
            instDictionary.Add(_method.Body.Instructions[i], i);
        }


        //初始化EH并检查合法性
        foreach (var eh in _method.Body.ExceptionHandlers) //由于未Apply的Instruction的Offset为0 不能直接用Offset来判断异常边界
        {
            if (BuildExceptionBlock(eh, instDictionary, out var hb))
            {
                hb!.Id = _exceptionBlocks.Count;
                hb!.SetClause();
                _exceptionBlocks.Add(hb!);
                _ehRegions.Add(hb!.ProtectedRegion);
                _ehRegions.Add(hb!.HandlerRegion);
                if(hb!.FilterRegion is not null)
                    _ehRegions.Add(hb!.FilterRegion);
            }
        }

        _ehRegions.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start)
                                         : b.End.CompareTo(a.End));

        var stack = new Stack<ExceptionBlock.Region>();
        var regionFrameList = new List<(int index, RegionKind kind, TypeReference? type)>();
        if (_ehRegions.Count == 0)
        {
            regionFrameList.Add((0, RegionKind.Normal, null));
        }
        else
        {
            for (int i = 0; i < _ehRegions.Count; i++)
            {
                var r = _ehRegions[i];
                if (i == 0 && r.Start != 0) //如果不是开头为protected block
                {
                    regionFrameList.Add((0, RegionKind.Normal, null));
                }

                while (stack.Count > 0 && r.Start >= stack.Peek().End) //不相交
                {
                    var start = stack.Peek().End;
                    stack.Pop();
                    if (stack.Count > 0)
                    {
                        var top = stack.Peek();
                        regionFrameList.Add((start, top.Kind,
                            top.Kind is RegionKind.Handler ? top.Clause.ExceptionHandler?.CatchType : null));
                    }
                    else
                    {
                        regionFrameList.Add((start, RegionKind.Normal, null));
                    }
                }

                if (stack.Count > 0)
                {
                    var top = stack.Peek();

                    // 交错的
                    if (r.End > top.End) throw new Exception();

                    // 重复区域仅能为Try块
                    if (r.Start == top.Start && r.End == top.End &&
                        !(r.Kind == RegionKind.Try && top.Kind == RegionKind.Try))
                        throw new Exception();

                    // filter块内不能嵌套
                    if (top.Kind == RegionKind.Filter && top.Clause != r.Clause)
                        throw new Exception();

                    r.ParentRegion = (r.Start == top.Start && r.End == top.End) ? null : top;
                }
                else r.ParentRegion = null;

                stack.Push(r);
                regionFrameList.Add((r.Start, r.Kind,
                    r.Kind is RegionKind.Handler ? r.Clause.ExceptionHandler?.CatchType : null));
            }
        }

        _regionFrames = new(regionFrameList.Count);
        foreach (var rf in regionFrameList)
            _regionFrames.Add(rf.index, (rf.kind, rf.type));
        
        int frameIndex = 0;
        for (int i = 0; i < instEhRegions.Length; i++)
        {
            if(frameIndex < regionFrameList.Count - 1 && i >= regionFrameList[frameIndex + 1].index)
                frameIndex++;
            instEhRegions[i] = frameIndex;
        }

        foreach (var hb in _exceptionBlocks)
        {
            if (hb.HandlerRegion.ParentRegion != hb.ProtectedRegion.ParentRegion ||
                (hb.FilterRegion != null && hb.FilterRegion.ParentRegion == hb.ProtectedRegion.ParentRegion))
                throw new Exception(); //不满足嵌套均在一个区间
            if (hb.HandlerRegion.ParentRegion != null && hb.HandlerRegion.ParentRegion.Clause.Id > hb.Id)
                throw new Exception(); //不满足父区域必须在前
        }
        
        //指令合法性检查
        Code? pPrefix = null;
        Code? prefix = null;
        int noCheck = 0;
        
        if (_method.Body.Instructions.Count > 0)
            AddBasicBlock(_method.Body.Instructions[0], _ehRegions[instEhRegions[0]]);
        
        for (int i = 0; i < _method.Body.Instructions.Count; i++)
        {
            var inst = _method.Body.Instructions[i];
            if (inst is null) throw new Exception();
            /*
            if (inst.OpCode.FlowControl is FlowControl.Phi)
                AddBasicBlock(inst);
            */
            if (inst.OpCode.FlowControl is FlowControl.Branch
                     or FlowControl.Cond_Branch)
            {
                foreach (var targetInst in CecilHelper.OperandToTargets(inst.Operand))
                {
                    if (!instDictionary.TryGetValue(targetInst, out var index))
                        throw new Exception(); //无效目标位置
                    if (instEhRegions[index] != instEhRegions[i])
                        throw new Exception(); //跨异常边界跳转
                    AddBasicBlock(targetInst, _ehRegions[instEhRegions[index]]);
                }
            }

            //终止指令的下一条指令也是起始
            if (inst.OpCode.FlowControl is FlowControl.Branch
                or FlowControl.Cond_Branch
                or FlowControl.Return
                or FlowControl.Throw)
            {
                if (inst.Next != null)
                    AddBasicBlock(inst.Next, _ehRegions[instEhRegions[i+1]]);
            }

            // JMP特殊处理
            if (inst.OpCode.Code == Code.Jmp && inst.Next != null)
                AddBasicBlock(inst.Next, _ehRegions[instEhRegions[i+1]]);

            //处理前缀合法
            if (prefix != null) 
            {
                if (inst.OpCode.Code is Code.Constrained or Code.Volatile)
                {
                    if (prefix.Value == inst.OpCode.Code)
                        throw new Exception(); //连续前缀
                    if (prefix.Value is not Code.Constrained and not Code.Volatile)
                        throw new Exception(); //非法前缀
                }
                else 
                {
                    switch (prefix.Value)
                    {
                        case Code.Tail:
                            if (!(inst.OpCode.Code is Code.Call or Code.Callvirt or Code.Calli))
                                throw new Exception();
                            if(inst.Next is null || inst.Next.OpCode.FlowControl != FlowControl.Return)
                                throw new Exception();
                            break;
                        case Code.Constrained:
                            if (inst.OpCode.Code is not Code.Callvirt)
                                throw new Exception();
                            break;
                        case Code.Volatile when pPrefix != Code.Unaligned: 
                            if (!(inst.OpCode.Code is Code.Ldind_I1 or Code.Ldind_I2 or Code.Ldind_I4 or Code.Ldind_I8 or Code.Ldind_I or
                                Code.Ldind_R4 or Code.Ldind_R8 or Code.Ldind_U1 or Code.Ldind_U2 or Code.Ldind_U4 or Code.Ldind_Ref or
                                Code.Stind_I1 or Code.Stind_I2 or Code.Stind_I4 or Code.Stind_I8 or Code.Stind_I or
                                Code.Stind_R4 or Code.Stind_R8 or Code.Stind_Ref or
                                Code.Ldfld or Code.Ldsfld or Code.Ldobj or Code.Stfld or Code.Stsfld or Code.Stobj or
                                Code.Initblk or Code.Cpblk))
                                throw new Exception();
                            break;
                        case Code.Unaligned:
                        case Code.Volatile: //包含双前缀
                            if (!(inst.OpCode.Code is Code.Ldind_I1 or Code.Ldind_I2 or Code.Ldind_I4 or Code.Ldind_I8 or Code.Ldind_I or
                                Code.Ldind_R4 or Code.Ldind_R8 or Code.Ldind_U1 or Code.Ldind_U2 or Code.Ldind_U4 or Code.Ldind_Ref or
                                Code.Stind_I1 or Code.Stind_I2 or Code.Stind_I4 or Code.Stind_I8 or Code.Stind_I or
                                Code.Stind_R4 or Code.Stind_R8 or Code.Stind_Ref or
                                Code.Ldfld or Code.Ldobj or Code.Stfld or Code.Stobj or
                                Code.Initblk or Code.Cpblk))
                                throw new Exception();
                            break;
                        case Code.Readonly:
                            if (inst.OpCode.Code is not Code.Ldelema)
                                throw new Exception();
                            break;
                        case Code.No:
                            if(noCheck == 0)
                                 throw new Exception();
                            if((noCheck & 1) != 0)
                            {
                                if(inst.OpCode.Code is Code.Castclass or Code.Unbox or Code.Ldelema or Code.Stelem_Any or 
                                    Code.Stelem_I1 or Code.Stelem_I2 or Code.Stelem_I4 or Code.Stelem_I8 or Code.Stelem_I or
                                    Code.Stelem_R4 or Code.Stelem_R8 or Code.Stelem_Ref)
                                {
                                    break;
                                }
                            }
                            if((noCheck & 2) != 0)
                            {
                                if (inst.OpCode.Code is
                                    Code.Ldelem_I1 or Code.Ldelem_I2 or Code.Ldelem_I4 or Code.Ldelem_I8 or Code.Ldelem_I or Code.Ldelem_Any or
                                    Code.Ldelem_R4 or Code.Ldelem_R8 or Code.Ldelem_U1 or Code.Ldelem_U2 or Code.Ldelem_U4 or Code.Ldelem_Ref or
                                    Code.Stelem_I1 or Code.Stelem_I2 or Code.Stelem_I4 or Code.Stelem_I8 or Code.Stelem_I or
                                    Code.Stelem_R4 or Code.Stelem_R8 or Code.Stelem_Any or Code.Stelem_Ref)
                                {
                                    break;
                                }
                            }
                            if((noCheck & 4) != 0)
                            {
                                if (inst.OpCode.Code is Code.Ldfld or Code.Callvirt or Code.Ldvirtftn or
                                    Code.Ldelem_I1 or Code.Ldelem_I2 or Code.Ldelem_I4 or Code.Ldelem_I8 or Code.Ldelem_I or Code.Ldelem_Any or
                                    Code.Ldelem_R4 or Code.Ldelem_R8 or Code.Ldelem_U1 or Code.Ldelem_U2 or Code.Ldelem_U4 or Code.Ldelem_Ref or
                                    Code.Stelem_I1 or Code.Stelem_I2 or Code.Stelem_I4 or Code.Stelem_I8 or Code.Stelem_I or
                                    Code.Stelem_R4 or Code.Stelem_R8 or Code.Stelem_Any or Code.Stelem_Ref)
                                {
                                    break;
                                }
                            }   
                            throw new Exception();

                    }
                    prefix = null;
                    pPrefix = null;
                    noCheck = 0;
                }
            }

            // 前缀与特殊指令约束
            switch (inst.OpCode.Code)
            {
                case Code.Tail or Code.Constrained or Code.Volatile or Code.Unaligned or Code.Readonly:
                    {
                        if (prefix != null)
                            pPrefix = prefix;
                        prefix = inst.OpCode.Code;
                        break;
                    }
                case Code.No:
                     {
                        prefix = inst.OpCode.Code;
                        if (inst.Operand is not byte b)
                            throw new Exception();
                        noCheck = b;
                        break;
                    }
                case Code.Ret:
                    {
                        if (_ehRegions[instEhRegions[i]].Kind is not RegionKind.Normal)
                            throw new Exception(); //异常边界内不能有返回
                        break;
                    }
                case Code.Rethrow:
                    {
                        if (_ehRegions[instEhRegions[i]].Kind is not RegionKind.Handler ||
                            _ehRegions[instEhRegions[i]].Clause.ExceptionHandler.HandlerType is not ExceptionHandlerType.Catch)
                            throw new Exception(); //不可rethrow
                        break;
                    }
                case Code.Endfilter:
                    {
                        if (_ehRegions[instEhRegions[i]].Kind is not RegionKind.Handler ||
                            _ehRegions[instEhRegions[i]].Clause.ExceptionHandler.HandlerType is not ExceptionHandlerType.Filter)
                            throw new Exception(); 
                        break;
                    }
                case Code.Endfinally:
                    {
                        if (_ehRegions[instEhRegions[i]].Kind is not RegionKind.Handler ||
                            _ehRegions[instEhRegions[i]].Clause.ExceptionHandler.HandlerType is not ExceptionHandlerType.Finally)
                            throw new Exception(); 
                        break;
                    }
                case Code.Leave:
                    {
                        if (_ehRegions[instEhRegions[i]].Kind is RegionKind.Normal)
                            throw new Exception(); //不可leave
                        break;
                    }
            }

            // 验证调用指令的可解析性
            switch (inst.OpCode.OperandType)
            {
                case OperandType.InlineMethod:
                    {
                        if (inst.Operand is not MethodReference mf)
                            throw new InvalidInstructionException(typeof(MethodReference), inst.Operand?.GetType(), inst);

                        if (mf.Resolve() == null)
                            throw new Exception();
                        break;
                    }
                case OperandType.InlineField:
                    {
                        
                        if (inst.Operand is not FieldReference field)
                            throw new InvalidInstructionException(typeof(FieldReference), inst.Operand?.GetType(), inst);
                        if (field.Resolve() is not { } fd)
                            throw new Exception();
                        if (fd.Attributes.HasFlag(FieldAttributes.Static) != (inst.OpCode.Code is Code.Ldsflda or Code.Ldsfld or Code.Stsfld))
                            throw new Exception();
                        break;
                    }
                case OperandType.InlineTok:
                    {
                        if (inst.Operand is not MemberReference member)
                            throw new InvalidInstructionException(typeof(MemberReference), inst.Operand?.GetType(), inst);
                        /*
                        if (member.Resolve() is not { } fd)
                            throw new Exception();
                        */
                        break;
                    }
                case OperandType.InlineType:
                    {
                        if (inst.Operand is not TypeReference type)
                            throw new InvalidInstructionException(typeof(TypeReference), inst.Operand?.GetType(), inst);
                        while (type is TypeSpecification)
                        {
                            type = (type as TypeSpecification)!.ElementType;
                        }
                        if (type.Resolve() is not { } fd)
                            throw new Exception();
                        break;
                    }
                case OperandType.InlineVar:
                    {
                        if (inst.Operand is not VariableReference re)
                            throw new InvalidInstructionException(typeof(VariableReference), inst.Operand?.GetType(), inst);
                        if(re.Index < 0 || re.Index > _method.Body.Variables.Count)
                            throw new Exception(); //参数越界
                        break;
                    }
                case OperandType.InlineArg:
                {
                    if (inst.Operand is not ParameterReference re)
                        throw new InvalidInstructionException(typeof(ParameterReference), inst.Operand?.GetType(), inst);
                    if(re.Index < 0 || re.Index > _method.Parameters.Count)
                        throw new Exception(); //参数越界
                    break;
                }
                default:
                    //其他非法情况会在Mono.Cecil处报错
                    break;
            }

        }
    }
    
    private void AddBasicBlock(Instruction leader, ExceptionBlock.Region region)
    {
        var block = new BasicBlock(leader, region);
        _blocks.Add(block);
        _blockMap.Add(leader, block);
    }

    private void ControlFlowPass()
    {
        var buffer = new StackTypeRef[4];
        var funcBuffer = new StackTypeRef?[8];
        var usedBlocks = new HashSet<BasicBlock>(_blocks.Count);
        
        foreach (var block in _blocks)
        {
            if (usedBlocks.Contains(block))
                continue;
            AnalyzeBlocksControlFlow(block, usedBlocks, buffer, funcBuffer);
        }
    }

    private void AnalyzeBlocksControlFlow(BasicBlock entryBlock, 
        HashSet<BasicBlock> usedBlocks,  StackTypeRef[] buffer, StackTypeRef?[] funcBuffer)
    {
        var localStack = new Stack<StackTypeRef>(_method.Body.MaxStackSize);
        var entryStackNode = _root;
        
        if(entryBlock.Kind is RegionKind.Filter)
            AnalyzeCF_EvalStackPush(localStack, ref entryStackNode, StackTypeRef.Create(_method.Module.TypeSystem.Object));
        else if (entryBlock.Kind is RegionKind.Handler)
            AnalyzeCF_EvalStackPush(localStack, ref entryStackNode, entryBlock.Region.Clause.ExceptionHandler.CatchType);
        
        List<(BasicBlock block, EvalStackNode node)> bfsBlocks =
            [(entryBlock, entryStackNode)];


        for(int i = 0; i < bfsBlocks.Count; i++)
        {
            var (block, node) = bfsBlocks[i];
            usedBlocks.Add(block);
            block.EntryNode = node;
            
            if (_needInitAnalysis)
            {
                //TODO:   
            }
            
            var leader = block.Leader;
            StackTypeRef? retType = null;
            for (var inst = leader; inst != null; inst = inst.Next)
            {
                var ts = _method.Module.TypeSystem;
                if(inst.OpCode.StackBehaviourPop == StackBehaviour.PopAll)
                {
                    localStack.Clear();
                    node = _root;
                }
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


                for (int j = 0; j< pop; j++)
                {
                    buffer[j] = AnalyzeCF_EvalStackPop(localStack, ref node);
                }

                switch (inst.OpCode.StackBehaviourPop)
                {
                    case StackBehaviour.Pop0:
                        break;

                    case StackBehaviour.Pop1:
                        retType = VerifyPop1(inst, buffer, _needInitAnalysis);
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
                        for(int j = len - 1; j >=0; j--)
                        {
                            var type = AnalyzeCF_EvalStackPop(localStack, ref node);
                            VerifyType(type, funcBuffer[j], inst);
                        }
                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }

                if (retType is null && inst.OpCode.StackBehaviourPush is not StackBehaviour.Push0 and not StackBehaviour.Varpush)
                {
                    throw new Exception();
                }
           
                if (inst.OpCode.StackBehaviourPush is StackBehaviour.Push1_push1)
                {
                    AnalyzeCF_EvalStackPush(localStack, ref node, retType!);
                    AnalyzeCF_EvalStackPush(localStack, ref node, retType!);
                }
                else
                {
                    AnalyzeCF_EvalStackPush(localStack, ref node, retType!);
                }
                bool endBlock = false;
                switch (inst.OpCode.FlowControl)
                {
                    case FlowControl.Next:
                        if (_regionFrames.TryGetValue(i, out var tuple))
                        {
                            switch (tuple.kind)
                            {
                                case RegionKind.Handler:
                                case RegionKind.Filter:
                                    throw new Exception();  //不允许fall-through
                                case RegionKind.Try:
                                    AnalyzeCF_AppendStack(localStack, ref node);
                                    var tryEntry = _blockMap[inst.Next];
                                    block.Edges.Add(new ControlFlowEdge(block, tryEntry));
                                    if (_needInitAnalysis)
                                    {
                                        //TODO:
                                    }
                                    endBlock = true;
                                    break;
                            }
                        }
                        break;
                    case FlowControl.Branch:
                        AnalyzeCF_AppendStack(localStack, ref node);
                        foreach (var target in CecilHelper.OperandToTargets(inst.Operand))
                        {
                            var targetBlock = _blockMap[target];
                            bfsBlocks.Add((targetBlock, node));
                            block.Edges.Add(new ControlFlowEdge(block, targetBlock));
                        }
                        endBlock = true;
                        break;
                    case FlowControl.Cond_Branch:
                        AnalyzeCF_AppendStack(localStack, ref node);
                        foreach (var target in CecilHelper.OperandToTargets(inst.Operand))
                        {
                            var targetBlock = _blockMap[target];
                            bfsBlocks.Add((targetBlock, node));
                            block.Edges.Add(new ControlFlowEdge(block, targetBlock));
                        }
                        var next = _blockMap[inst.Next];
                        bfsBlocks.Add((next, node));
                        block.Edges.Add(new ControlFlowEdge(block, next));
                        endBlock = true;    
                        break;
                }
                if (endBlock)
                    break;
            }
        }
    }

    /// <summary>
    /// 判断是否已经处理过，处理过则进行合并判别，如果合并后有路径变更则重新计算cf，否则不添加
    /// </summary>
    private void AnalyzeCF_AddNextBlock(BasicBlock block,  
        List<(BasicBlock block, EvalStackNode node)> bfsBlocks,
        EvalStackNode currentNode,
        HashSet<BasicBlock> usedBlocks)
    {
        if (usedBlocks.Contains(block))
        {
            var lastNode = block.EntryNode;
            var curNode = currentNode;
            if (curNode.Depth != lastNode.Depth)
                throw new Exception(); //合流堆栈不平衡
            List<StackTypeRef> nodes = new List<StackTypeRef>(lastNode.Depth);
            bool noChanged = true;
            while (curNode != _root)
            {
                if (curNode.Type.CanConvertTo(lastNode.Type))
                {
                    nodes.Add(curNode.Type);
                  
                }
                else
                {
                    var merged = curNode.Type.Intersect(lastNode.Type);
                    noChanged = false;
                    nodes.Add(merged ?? throw new Exception() /*合流类型推断失败*/);
                }
                curNode  = curNode.Parent!;
                lastNode = lastNode.Parent!;
            }
            var newNode = curNode;
            
            if (!noChanged) //存在堆栈变更
            {
                newNode = _root;
                for (int i = nodes.Count - 1; i >= 0; i--)
                {
                    if (_nodeIntern.TryGetValue((nodes[i], newNode), out var child))
                        newNode = child;
                    else
                        newNode = newNode.AppendChild(nodes[i]);
                }
            }
            
            //TODO：合并initlocals

            if (!noChanged)
            {
                block.EntryNode = newNode;
                bfsBlocks.Add((block, newNode));
            }
           
        }
        else
        {
            bfsBlocks.Add((block, currentNode));
        }
    }

    private StackTypeRef AnalyzeCF_EvalStackPop(Stack<StackTypeRef> localStack,
        ref EvalStackNode node)
    {
        if (localStack.Count != 0)
            return localStack.Pop();
        if (node.Parent?.Type is null)
            throw new Exception();
        node = node.Parent;
        return node.Type;
    }

    private void AnalyzeCF_EvalStackPush(Stack<StackTypeRef> localStack,
        ref EvalStackNode node, StackTypeRef type)
    {
        if (_nodeIntern.TryGetValue((type, node), out var child))
        {
            node = child;
            return;
        }
        localStack.Push(type);
    }

    private void AnalyzeCF_AppendStack(Stack<StackTypeRef> localStack, 
        ref EvalStackNode node)
    {
        while (localStack.Count != 0)
            node = node.AppendChild(localStack.Pop());
    }
    
}