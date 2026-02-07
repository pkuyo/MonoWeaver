using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using MonoWeaver.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace MonoWeaver.CFG;



//TODO: throw new Exception();相关后续需要调整 改为记录错误信息并给据故障等级判断是否中止分析

/// <summary>
/// EH段
/// </summary>
/// <param name="eh"></param>
public sealed class HandlerBlock(ExceptionHandler eh)
{
    public class Region
    {
        public int Start;
        public int End;
        public HandlerBlock Clause = null!;
        public Region? ParentRegion;
        public RegionKind Kind;

        public Region(RegionKind kind, int start = -1, int end = -1)
        {
            Start = start;
            End = end;
            Kind = kind;
        }

    }

    public enum RegionKind
    {
        Normal = 0,
        Try,
        Handler,
        Filter
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


/// <summary>
/// 评估栈DAG图节点
/// </summary>
public class EvalStackNode
{
    public EvalStackNode(int depth = 0, StackTypeRef? type = null)
    {
        Type = type ?? StackTypeRef.Invalid;
        Depth = depth;
    }

    public StackTypeRef Type;

    public EvalStackNode? Parent;

    public List<EvalStackNode> Children = new ();

    public int Depth;

    public void Disconnect()
    {
        Parent?.RemoveChild(this);
        Parent = null;
    }

    public void RemoveChild(EvalStackNode node)
    {
        Children.Remove(node);
    }

    public void AppendChild(EvalStackNode node)
    {
        node.Parent = this;
    }

    public EvalStackNode AppendChild(StackTypeRef type)
    {
        var node = new EvalStackNode(Depth + 1, type)
        {
            Parent = this
        };
        Children.Add(node);
        return node;
    }
}



public partial class ControlFlowGraph
{
    /// <summary>
    /// CFG基本块
    /// </summary>
    /// <param name="start"></param>
    /// <param name="exceptionType"></param>
    public sealed class BasicBlock(Instruction start, TypeReference? exceptionType = null) : IEquatable<BasicBlock>
    {
        public Instruction Leader  = start;
        public List<ControlFlowEdge> Edges = new();

        public TypeReference? ExceptionType = exceptionType;

        public bool Equals(BasicBlock other)
        {
            return Leader.Equals(other.Leader);
        }
    }

    /// <summary>
    /// CFG边
    /// </summary>
    /// <param name="from"></param>
    /// <param name="to"></param>
    public sealed class ControlFlowEdge(BasicBlock from, BasicBlock to)
    {
        public BasicBlock From = from;
        public BasicBlock To = to;

        //public EvalStackTransfer? StackTransfer;
    }
}
public partial class ControlFlowGraph
{


    private bool BuildHandlerBlock(ExceptionHandler eh, Dictionary<Instruction, int> instDic, out HandlerBlock? block)
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



        block = new HandlerBlock(eh)
        {
            ProtectedRegion = new HandlerBlock.Region(HandlerBlock.RegionKind.Try, tryStart, tryEnd),
            HandlerRegion = new HandlerBlock.Region(HandlerBlock.RegionKind.Handler, handlerStart, handlerEnd),
            FilterRegion = eh.HandlerType == ExceptionHandlerType.Filter
                ? new HandlerBlock.Region(HandlerBlock.RegionKind.Filter, filterStart, handlerStart)
                : null
        };
        return true;
    }

}

public partial class ControlFlowGraph
{

    private readonly MethodDefinition _method;

    private EvalStackNode _root = new();

    Dictionary<(StackTypeRef type, EvalStackNode prev), EvalStackNode> _nodeIntern = new();

    private List<BasicBlock> _blocks = null!;

    private Dictionary<Instruction, BasicBlock> _blockMap = new();
    

    public ControlFlowGraph(MethodDefinition method)
    {
        if (!method.IsIL)
        {
            throw new ArgumentException("Method must be IL method", method.FullName);
        }
        _method = method;
        _method.Body.SimplifyMacros();
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
        if (_method.Body.Instructions.Count > 0)
            AddBasicBlock(_method.Body.Instructions[0]);

        var hBlocks = new List<HandlerBlock>(_method.Body.ExceptionHandlers.Count);
        var regions = new List<HandlerBlock.Region>(_method.Body.ExceptionHandlers.Count);
        var instDictionary = new Dictionary<Instruction, int>(_method.Body.Instructions.Count);
        var instEhRegions = new int[_method.Body.Instructions.Count];

        for(int i = 0; i < _method.Body.Instructions.Count; i++)
        {
            instDictionary.Add(_method.Body.Instructions[i], i);
        }


        //初始化EH并检查合法性
        foreach (var eh in _method.Body.ExceptionHandlers) //由于未Apply的Instruction的Offset为0 不能直接用Offset来判断异常边界
        {
            if (BuildHandlerBlock(eh, instDictionary, out var hb))
            {
                hb!.Id = hBlocks.Count;
                hb!.SetClause();
                hBlocks.Add(hb!);
                regions.Add(hb!.ProtectedRegion);
                regions.Add(hb!.HandlerRegion);
                if(hb!.FilterRegion is not null)
                    regions.Add(hb!.FilterRegion);
            }
        }

        regions.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start)
                                         : b.End.CompareTo(a.End));

        var stack = new Stack<HandlerBlock.Region>();
        var regionFrame = new List<(int index, HandlerBlock.RegionKind kind, TypeReference? type)>(); //TODO:删除异常EH相关Region
        for (int i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            if(i == 0 && r.Start != 0)
            {
                regionFrame.Add((0, HandlerBlock.RegionKind.Normal, null));
            }

            while (stack.Count > 0 && r.Start >= stack.Peek().End) //不相交
            {
                var start = stack.Peek().End;
                stack.Pop();
                if(stack.Count > 0)
                {
                    var top = stack.Peek();
                    regionFrame.Add((start, top.Kind,
                        top.Kind is HandlerBlock.RegionKind.Handler ? top.Clause.ExceptionHandler?.CatchType : null));
                }
                else
                {
                    regionFrame.Add((start, HandlerBlock.RegionKind.Normal, null));
                }
            }

            if (stack.Count > 0)
            {
                var top = stack.Peek();

                // 交错的
                if (r.End > top.End) throw new Exception();

                // 重复区域仅能为Try块
                if (r.Start == top.Start && r.End == top.End &&
                    !(r.Kind == HandlerBlock.RegionKind.Try && top.Kind == HandlerBlock.RegionKind.Try))
                    throw new Exception();

                // filter块内不能嵌套
                if (top.Kind == HandlerBlock.RegionKind.Filter && top.Clause != r.Clause)
                    throw new Exception();

                r.ParentRegion = (r.Start == top.Start && r.End == top.End) ? null : top;
            }
            else r.ParentRegion = null;
            stack.Push(r);
            regionFrame.Add((r.Start, r.Kind, 
                r.Kind is HandlerBlock.RegionKind.Handler ? r.Clause.ExceptionHandler?.CatchType : null));
        }
        int frameIndex = 0;

        for (int i = 0; i < instEhRegions.Length; i++)
        {
            if(frameIndex < regionFrame.Count - 1 && i >= regionFrame[frameIndex + 1].index)
                frameIndex++;
            instEhRegions[i] = frameIndex;
        }

        foreach (var hb in hBlocks)
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

        for (int i = 0; i < _method.Body.Instructions.Count; i++)
        {
            var inst = _method.Body.Instructions[i];
            if (inst is null) throw new Exception();
            /*
            if (inst.OpCode.FlowControl is FlowControl.Phi)
                AddBasicBlock(inst);
            */
            else if (inst.OpCode.FlowControl is FlowControl.Branch
                     or FlowControl.Cond_Branch)
            {
                foreach (var targetInst in CecilHelper.OperandToTargets(inst.Operand))
                {
                    if (!instDictionary.TryGetValue(targetInst, out var index))
                        throw new Exception(); //无效目标位置
                    if (instEhRegions[index] != instEhRegions[i])
                        throw new Exception(); //跨异常边界跳转
                    AddBasicBlock(targetInst);
                }
            }

            //终止指令的下一条指令也是起始
            if (inst.OpCode.FlowControl is FlowControl.Branch
                or FlowControl.Cond_Branch
                or FlowControl.Return
                or FlowControl.Throw)
            {
                if (inst.Next != null)
                    AddBasicBlock(inst.Next);
            }

            // JMP特殊处理
            if (inst.OpCode.Code == Code.Jmp && inst.Next != null)
                AddBasicBlock(inst.Next);


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
                        case Code.Volatile:
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

            // 验证调用指令的可解析性，合法性由mono cecil处理
            switch (inst.OpCode.Code)
            {
                case Code.Call or Code.Callvirt or Code.Jmp or Code.Ldftn or Code.Newobj or Code.Ldvirtftn:
                    {
                        if (inst.Operand is not MethodReference mf)
                            throw new InvalidInstructionException(typeof(MethodReference), inst.Operand?.GetType(), inst);

                        if (mf.Resolve() == null)
                            throw new Exception();
                        break;
                    }
                case Code.Ldfld or Code.Ldflda or Code.Stfld:
                    {
                        if (inst.Operand is not FieldReference field)
                            throw new InvalidInstructionException(typeof(FieldReference), inst.Operand?.GetType(), inst);
                        if (field.Resolve() is not { } fd)
                            throw new Exception();
                        if (fd.Attributes.HasFlag(FieldAttributes.Static))
                            throw new Exception();
                        break;
                    }
                case Code.Ldsflda or Code.Ldsfld or Code.Stsfld:
                    {
                        if (inst.Operand is not FieldReference field)
                            throw new InvalidInstructionException(typeof(FieldReference), inst.Operand?.GetType(), inst);
                        if (field.Resolve() is not { } fd)
                            throw new Exception();
                        if (!fd.Attributes.HasFlag(FieldAttributes.Static))
                            throw new Exception();
                        break;
                    }
                case Code.Ldtoken:
                    {
                        if (inst.Operand is not MemberReference member)
                            throw new InvalidInstructionException(typeof(MemberReference), inst.Operand?.GetType(), inst);
                        /*
                        if (member.Resolve() is not { } fd)
                            throw new Exception();
                        */
                        break;
                    }
                case Code.Isinst or Code.Newarr or Code.Ldobj or Code.Stobj or Code.Unbox or Code.Unbox_Any or Code.Castclass
                        or Code.Initobj or Code.Cpobj or Code.Sizeof or Code.Ldelema or Code.Ldelem_Any or Code.Stelem_Any 
                        or Code.Mkrefany or Code.Refanyval:
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
                case Code.Tail or Code.Constrained or Code.Volatile or Code.Unaligned or Code.Readonly:
                    {
                        if (prefix != null)
                        {
                            pPrefix = prefix;
                        }
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
                        if (regions[instEhRegions[i]].Kind is not HandlerBlock.RegionKind.Normal)
                            throw new Exception(); //异常边界内不能有返回
                        break;
                    }
                case Code.Rethrow:
                    {
                        if (regions[instEhRegions[i]].Kind is not HandlerBlock.RegionKind.Handler ||
                            regions[instEhRegions[i]].Clause.ExceptionHandler.HandlerType is not ExceptionHandlerType.Catch)
                            throw new Exception(); //不可rethrow
                        break;
                    }
                case Code.Endfilter:
                    {
                        if (regions[instEhRegions[i]].Kind is not HandlerBlock.RegionKind.Handler ||
                            regions[instEhRegions[i]].Clause.ExceptionHandler.HandlerType is not ExceptionHandlerType.Filter)
                            throw new Exception(); 
                        break;
                    }
                case Code.Endfinally:
                    {
                        if (regions[instEhRegions[i]].Kind is not HandlerBlock.RegionKind.Handler ||
                            regions[instEhRegions[i]].Clause.ExceptionHandler.HandlerType is not ExceptionHandlerType.Finally)
                            throw new Exception(); 
                        break;
                    }
                case Code.Leave:
                    {
                        if (regions[instEhRegions[i]].Kind is HandlerBlock.RegionKind.Normal)
                            throw new Exception(); //不可leave
                        break;
                    }
            }

        }
    }

    private void ControlFlowPass()
    {
        var module = _method.Module;
        var buffer = new StackTypeRef[4];
        var funcBuffer = new StackTypeRef?[8];
        var usedBlocks = new HashSet<BasicBlock>(_blocks.Count);
        
        foreach (var block in _blocks)
        {
            if (usedBlocks.Contains(block))
                continue;
            AnalyzeBlocksWorkflow(block, usedBlocks, buffer, funcBuffer);
        }
    }

    private void AnalyzeBlocksWorkflow(BasicBlock entryBlock, 
        HashSet<BasicBlock> usedBlocks,  StackTypeRef[] buffer, StackTypeRef?[] funcBuffer)
    {
        var localStack = new Stack<StackTypeRef>(_method.Body.MaxStackSize);
        List<(BasicBlock block, EvalStackNode node)> bfsBlocks = [(entryBlock, _root)];
        for(int i = 0; i < bfsBlocks.Count; i++)
        {
            var (block, node) = bfsBlocks[i];
            usedBlocks.Add(block);
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
                    buffer[j] = EvalStackPop(localStack, ref node, inst);
                }

                switch (inst.OpCode.StackBehaviourPop)
                {
                    case StackBehaviour.Pop0:
                        break;

                    case StackBehaviour.Pop1:
                        retType = VerifyPop1(inst, buffer);
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
                            var type = EvalStackPop(localStack, ref node, inst);
                            VerifyType(type, funcBuffer[j], inst);
                        }
                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }

                if (retType is null && inst.OpCode.StackBehaviourPush is not StackBehaviour.Push0 and not StackBehaviour.Varpush)
                {
                    throw new Exception(); //TODO: 完善故障信息错误报告
                }
           
                if (inst.OpCode.StackBehaviourPush is StackBehaviour.Push1_push1)
                {
                    EvalStackPush(localStack, ref node, retType!);
                    EvalStackPush(localStack, ref node, retType!);
                }
                else
                {
                    EvalStackPush(localStack, ref node, retType!);
                }
                bool endBlock = false;
                switch (inst.OpCode.FlowControl)
                {
                    case FlowControl.Next:
                        break;
                    case FlowControl.Branch:
                        Append(localStack, ref node, inst);
                        foreach (var target in CecilHelper.OperandToTargets(inst.Operand))
                        {
                            var targetBlock = _blockMap[target];
                            bfsBlocks.Add((targetBlock, node));
                            block.Edges.Add(new ControlFlowEdge(block, targetBlock));
                        }
                        endBlock = true;
                        break;
                    case FlowControl.Cond_Branch:
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

    private StackTypeRef EvalStackPop(Stack<StackTypeRef> localStack, ref EvalStackNode node, Instruction instruction)
    {
        if (localStack.Count != 0)
            return localStack.Pop();
        if (node.Parent?.Type is null)
            throw new CFGException(CFGExceptionType.StackUnderflow, instruction);
        node = node.Parent;
        return node.Type;
    }

    private void EvalStackPush(Stack<StackTypeRef> localStack, ref EvalStackNode node, StackTypeRef type)
    {
        if (_nodeIntern.TryGetValue((type, node), out var child))
            return;
        localStack.Push(type);
    }

    private void Append(Stack<StackTypeRef> localStack, ref EvalStackNode node,
        Instruction instruction)
    {
        while (localStack.Count != 0)
            node = node.AppendChild(localStack.Pop());
    }


    private BasicBlock AddBasicBlock(Instruction leader, TypeReference? catchType)
    {
        var block = new BasicBlock(leader, catchType);
        _blocks.Add(block);
        _blockMap.Add(leader, block);
        return block;
    }


    private BasicBlock AddBasicBlock(Instruction leader)
    {
        var block = new BasicBlock(leader);
        _blocks.Add(block);
        _blockMap.Add(leader, block);
        return block;
    }

}

public partial class ControlFlowGraph
{

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackTypeRef VerifyType(StackTypeRef type1, StackTypeRef? type2, Instruction inst)
    {
        throw new NotImplementedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackTypeRef VerifyValueType(StackTypeRef type1, Instruction inst)
    {
        throw new NotImplementedException();

    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackTypeRef VerifyNum(StackTypeRef type1, Instruction inst)
    {
        throw new NotImplementedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackTypeRef VerifyInt(StackTypeRef type1, Instruction inst)
    {
        throw new NotImplementedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackTypeRef VerifyByRef(StackTypeRef type1, Instruction inst)
    {
        throw new NotImplementedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackTypeRef VerifyFloat(StackTypeRef type1, Instruction inst)
    {
        throw new NotImplementedException();
    }

    private StackTypeRef? VerifyPop1(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {
            case Code.Box:
                return VerifyValueType(stacks[0], inst);
            case Code.Ckfinite:
                return VerifyType(stacks[0], StackTypeRef.F, inst);
            case Code.Conv_I1:
            case Code.Conv_I2:
            case Code.Conv_I4:
            case Code.Conv_I8:
            case Code.Conv_I:
            case Code.Conv_U1:
            case Code.Conv_U2:
            case Code.Conv_U4:
            case Code.Conv_U8:
            case Code.Conv_U:
            case Code.Conv_R4:
            case Code.Conv_R8:
            case Code.Conv_R_Un:
            case Code.Conv_Ovf_I1:
            case Code.Conv_Ovf_I2:
            case Code.Conv_Ovf_I4:
            case Code.Conv_Ovf_I8:
            case Code.Conv_Ovf_I:
            case Code.Conv_Ovf_U1:
            case Code.Conv_Ovf_U2:
            case Code.Conv_Ovf_U4:
            case Code.Conv_Ovf_U8:
            case Code.Conv_Ovf_U:
            case Code.Conv_Ovf_I1_Un:
            case Code.Conv_Ovf_I2_Un:
            case Code.Conv_Ovf_I4_Un:
            case Code.Conv_Ovf_I8_Un:
            case Code.Conv_Ovf_I_Un:
            case Code.Conv_Ovf_U1_Un:
            case Code.Conv_Ovf_U2_Un:
            case Code.Conv_Ovf_U4_Un:
            case Code.Conv_Ovf_U8_Un:
            case Code.Conv_Ovf_U_Un:
                return VerifyNum(stacks[0], inst);
            case Code.Dup:
                return stacks[0];
            case Code.Neg:
                return VerifyNum(stacks[0], inst);
            case Code.Not:
                return VerifyInt(stacks[0], inst);
            case Code.Pop:
                return null;
            case Code.Refanytype:
                return VerifyType(stacks[0], module.TypeSystem.TypedReference, inst);
            case Code.Refanyval:
                return VerifyByRef(stacks[0], inst);
            case Code.Starg:
                {
                    if (inst.Operand is not ushort index)
                        throw new InvalidInstructionException(typeof(ushort), inst.Operand?.GetType(), inst);
                    if (index >= _method.Parameters.Count + (_method.HasThis ? 1 : 0))
                        throw new OperandOutOfRangeException(inst,
                            $"Method parameters count: {_method.Parameters.Count + (_method.HasThis ? 1 : 0)}");
                    stacks[0] = (_method.HasThis && index == 0) ? _method.DeclaringType
                    : _method.Parameters[index - (_method.HasThis ? 1 : 0)].ParameterType;
                    return null;
                }
            case Code.Stloc:
                {
                    if (inst.Operand is not ushort index)
                        throw new InvalidInstructionException(typeof(ushort), inst.Operand?.GetType(), inst);
                    if (index >= _method.Body.Variables.Count)
                        throw new OperandOutOfRangeException(inst,
                            $"Method local variables count: {_method.Body.Variables.Count}");
                    stacks[0] = _method.Body.Variables[index].VariableType;
                    return null;
                }
            case Code.Stsfld:
                {
                    if (inst.Operand is not FieldReference field)
                        throw new InvalidInstructionException(typeof(FieldReference), inst.Operand?.GetType(), inst);
                    stacks[0] = field.FieldType;
                    return null;
                }
            default:
                throw new ArgumentOutOfRangeException(); //TODO:
        }
    }

    private StackTypeRef? VerifyPop1_pop1(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }

    private StackTypeRef? VerifyPopi_pop1(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }

    private StackTypeRef? VerifyPopref_pop1(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }
    private StackTypeRef? VerifyPop3(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }

    private StackTypeRef? VerifyPopref(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }

    private StackTypeRef? VerifyPopref_popi(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }


    private StackTypeRef? FillVarPop(Instruction inst, ref StackTypeRef?[] args, out int len)
    {
        if (inst.Operand is not IMethodSignature sig)
        {
            len = 0;
            return null;
        }
        var paramLen = sig.Parameters.Count + (sig.HasThis ? 1 : 0);
        len = paramLen;
        if(args.Length < paramLen)
        {
            Array.Resize(ref args, paramLen);
        }
        int i = 0;
        if (sig.HasThis)
        {
            if (sig is MethodReference methodRef)
                args[i++] = methodRef.DeclaringType;
            else
                args[i++] = null;
        }
        foreach(var p in sig.Parameters)
        {
            args[i++] = p.ParameterType;
        }
        return (sig.ReturnType.Namespace == "System" && sig.ReturnType.Name == "Void") 
            ? null : StackTypeRef.Create(sig.ReturnType);
    }

}