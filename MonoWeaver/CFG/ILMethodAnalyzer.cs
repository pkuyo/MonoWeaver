using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using MonoWeaver.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;


namespace MonoWeaver.CFG;


[Flags]
public enum VerifyOptions
{
    Instructions = 1 << 1,
    LocalInit = 1 << 2,
    StackBalance = 1 << 3,
    StackTypes = 1 << 4 | StackBalance, //TODO:WIP
    ByrefEscape = 1 << 5, //TODO:待实现
    Full = Instructions | LocalInit | StackTypes | ByrefEscape,
    Light = StackBalance | Instructions
}

/// <summary>
/// 符合c#的EH段
/// </summary>
/// <param name="eh"></param>
public sealed class EHandler(ExceptionHandler eh)
{
    public static EHandler CreateMethodRegion(int end)
    {
        var re = new EHandler(new ExceptionHandler((ExceptionHandlerType)8))
        {
            Id = int.MaxValue,
            ProtectedRegion = new Region(RegionKind.Normal, 0, end),
        };
        re.HandlerRegion = re.ProtectedRegion;
        re.SetClause();
        return re;
    }
    public class Region
    {
        public int Start;
        public int End;
        public EHandler Clause = null!;
        public Region? ParentRegion;
        public RegionKind Kind;

        public Region(RegionKind kind, int start = -1, int end = -1)
        {
            Start = start;
            End = end;
            Kind = kind;
        }

            public override string ToString()
            {
                return $"[{Kind} Region: {Start}-{End}]";
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

public sealed record EHFrame(int Start, RegionKind Kind, EHandler.Region Region);



public enum RegionKind
{
    Normal = 0,
    Try,
    Handler,
    Filter
}


public partial class ILMethodAnalyzer
{
    /// <summary>
    /// CFG基本块
    /// </summary>
    public sealed class BasicBlock(Instruction start, EHandler.Region region) : IEquatable<BasicBlock>
    {
        public Instruction Leader  = start;
        public EvalStackNode? EntryNode = null!;
        public List<ControlFlowEdge> Edges = new();
        public int _entryStackDepth = -1;

        public RegionKind Kind = region.Kind;
        public EHandler.Region Region = region;

        public int EntryStackDepth => EntryNode?.Depth ?? _entryStackDepth;

        public BitArray? initLocals = null; //如果不需要initLocals分析则为null

        public bool Equals(BasicBlock other)
        {
            return Leader.Equals(other.Leader);
        }

        public override string ToString()
        {
            return $"{Leader.SafeToString()} [{Kind} Region: {Region.Start}-{Region.End}]";
        }
    }

    /// <summary>
    /// CFG边
    /// </summary>
    public sealed class ControlFlowEdge(BasicBlock from, BasicBlock to)
    {
        public BasicBlock From = from;
        public BasicBlock To = to;
    }
}
public partial class ILMethodAnalyzer
{
    private bool BuildExceptionBlock(ExceptionHandler eh, Dictionary<Instruction, int> instDic, out EHandler? block)
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

        if(tryStart >= tryEnd || tryEnd > handlerStart || handlerStart >= handlerEnd)
            return false; //不符合约束

        if (eh.HandlerType == ExceptionHandlerType.Filter)
        {
            if (tryEnd > filterStart || filterStart >= handlerStart)
                return false; //不符合约束
        }



        block = new EHandler(eh)
        {
            ProtectedRegion = new EHandler.Region(RegionKind.Try, tryStart, tryEnd),
            HandlerRegion = new EHandler.Region(RegionKind.Handler, handlerStart, handlerEnd),
            FilterRegion = eh.HandlerType == ExceptionHandlerType.Filter
                ? new EHandler.Region(RegionKind.Filter, filterStart, handlerStart)
                : null
        };
        return true;
    }

}

public partial class ILMethodAnalyzer
{
    internal readonly MethodDefinition _method;

    private Dictionary<Instruction, int> _instDictionary = null!;

    private readonly EvalStackNode _root = new(StackType.Invalid);
    
    private readonly Dictionary<(StackType type, EvalStackNode prev), EvalStackNode> _nodeIntern = new();
    private readonly Dictionary<Instruction, BasicBlock> _blockMap = new();

    internal List<BasicBlock> _blocks = null!;

    internal List<EHandler> _exceptionHandlers = null!;

    private List<EHFrame> _regionFrames = null!;

    private bool _needInitAnalysis;

    private VerifyOptions _verifyOptions;

    private AbortStrategy _abortVerificationStrategy;


    public bool VerifyStackType => _verifyOptions.HasFlag(VerifyOptions.StackTypes);

    public bool VerifyStackBalance => _verifyOptions.HasFlag(VerifyOptions.StackBalance);

    public bool VerifyInstructions => _verifyOptions.HasFlag(VerifyOptions.Instructions);

    public bool VerifyLocalInit => _verifyOptions.HasFlag(VerifyOptions.LocalInit) && _needInitAnalysis;

    public MethodDefinition Method => _method;


    public ILMethodAnalyzer(MethodDefinition method, VerifyOptions verifyOptions = VerifyOptions.Light)
    {
        if (!method.IsIL)
        {
            throw new ArgumentException("Method must be IL method", method.FullName);
        }

        _verifyOptions = verifyOptions;
        _method = method;
        _method.Body.SimplifyMacros();
        _needInitAnalysis = !_method.Body.InitLocals;
        VerifyMethod();
    }

    public ILMethodAnalyzer ReAnalyze(VerifyOptions verifyOptions = VerifyOptions.Light)
    {
        _verifyOptions = verifyOptions;
        _needInitAnalysis = !_method.Body.InitLocals;
        _instDictionary.Clear();
        _exceptionHandlers.Clear();
        _regionFrames.Clear();
        _blockMap.Clear();
        _nodeIntern.Clear();
        _blocks.Clear();
        _method.Body.SimplifyMacros();
        Diagnostics.Clear();
        _currentErrorCount = 0;
        VerifyMethod();
        return this;
    }

    public ILCFGraph ToGraph()
    {
        return new ILCFGraph(this);
    }


    private void VerifyMethod()
    {
        FirstPass();
        if (VerifyStackBalance)
        {
            if (VerifyStackType)
            {
                ControlFlowPass();
            }
            else
            {
                LightControlFlowPass();
            }
        }
    }




    /// <summary>
    /// 处理基本块划分和异常处理边界检查，同时进行指令合法性检查（跳转目标合法性、前缀合法性、调用指令可解析性等）
    /// </summary>
    private void FirstPass()
    {
        InitializeFirstPassState(out var ehRegions);
        
        BuildExceptionHandlersAndCollectRegions(ehRegions);
        
        BuildAndValidateRegionFrames(ehRegions,out var instEhFrames);

        ValidateExceptionHandlerRegionRelations();

        ScanInstructionsAndAddBasicBlocks(instEhFrames);

        if (VerifyInstructions)
        {
            VerifyAllInstructions(instEhFrames);
        }
    }

    /// <summary>
    /// FirstPass相关和部分字段状态初始化
    /// </summary>
    /// <param name="ehRegions"></param>
    private void InitializeFirstPassState(out List<EHandler.Region> ehRegions)
    {
        _blocks = new List<BasicBlock>();

        var totalMethodRegion = EHandler.CreateMethodRegion(_method.Body.Instructions.Count);
        ehRegions = new List<EHandler.Region>(_method.Body.ExceptionHandlers.Count);
        _exceptionHandlers ??= new List<EHandler>(_method.Body.ExceptionHandlers.Count + 1);
        _exceptionHandlers.Add(totalMethodRegion);
        ehRegions.Add(totalMethodRegion.ProtectedRegion); //添加一个默认的区间，简化后续处理

        _instDictionary ??= new Dictionary<Instruction, int>(_method.Body.Instructions.Count);

        
        for(int i = 0; i < _method.Body.Instructions.Count; i++)
        {
            if (_method.Body.Instructions[i] is null)
            {
                ReportDiagnostic(CFGDiagnostic.NullInstruction(i));
            }
            else
            {
                _instDictionary.Add(_method.Body.Instructions[i], i);
            }
        }
        ThrowIfNeedAbort(AbortStrategy.AbortNextStep);
    }

    /// <summary>
    /// 根据Mono.Cecil的ExceptionHandlers解析EhRegion
    /// </summary>
    /// <param name="ehRegions"></param>
    private void BuildExceptionHandlersAndCollectRegions(List<EHandler.Region> ehRegions)
    {
        var sameProtected = new Dictionary<(int start, int end), EHandler.Region>();
        //初始化EH并检查合法性
        foreach (var eh in _method.Body.ExceptionHandlers) //由于未Apply的Instruction的Offset为0 不能直接用Offset来判断异常边界
        {
            if (BuildExceptionBlock(eh, _instDictionary, out var hb))
            {
                hb!.Id = _exceptionHandlers.Count;
                hb!.SetClause();
                _exceptionHandlers.Add(hb!);
                if(sameProtected.TryGetValue((hb!.ProtectedRegion.Start, hb!.ProtectedRegion.End), out var region))
                {
                    hb.ProtectedRegion = region;
                }
                else
                {
                    sameProtected.Add((hb!.ProtectedRegion.Start, hb!.ProtectedRegion.End), hb!.ProtectedRegion);
                    ehRegions.Add(hb!.ProtectedRegion);
                }
                ehRegions.Add(hb!.HandlerRegion);
                if(hb!.FilterRegion is not null)
                    ehRegions.Add(hb!.FilterRegion);
            }
            else
            {
                ReportDiagnostic(CFGDiagnostic.EhHandlerInvalid(eh)); //eh段有错误
            }
        }


        ehRegions.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start)
            : b.End.CompareTo(a.End));
        
        ThrowIfNeedAbort(AbortStrategy.AbortNextStep);
    }

    /// <summary>
    /// 根据ehRegions校验合法性并构建Region段与instruction的对应关系
    /// </summary>
    /// <param name="ehRegions"></param>
    /// <param name="instEhFrames"></param>
    private void BuildAndValidateRegionFrames(List<EHandler.Region> ehRegions, out int[] instEhFrames)
    {
         var stack = new Stack<EHandler.Region>();
        _regionFrames = [];
        instEhFrames = new int[_method.Body.Instructions.Count];
        int lastFrameStart = int.MinValue;
        
        for (int i = 0; i < ehRegions.Count; i++)
        {
            var r = ehRegions[i];

            while (stack.Count > 0 && r.Start >= stack.Peek().End) //不相交
            {
                var endPos = stack.Peek().End;
                stack.Pop();
                var top = stack.Peek();
                AddFrame(endPos, top);
               
            }

            if (stack.Count > 0)
            {
                var top = stack.Peek();

                // 交错的
                if (r.End > top.End)
                {
                    ReportDiagnostic(CFGDiagnostic.EhRegionInvalid(CFGExceptionType.EhRegionOverlap, r, top));
                }

                if (r.Start == top.Start && r.End == top.End)
                {
                    ReportDiagnostic(CFGDiagnostic.EhRegionInvalid(CFGExceptionType.EhRegionNonTryDuplication, r, top));
                }

                // filter块内不能嵌套
                if (top.Kind == RegionKind.Filter && top.Clause != r.Clause)
                {
                    ReportDiagnostic(CFGDiagnostic.EhRegionInvalid(CFGExceptionType.EhNestedInFilter, r, top));
                }

                r.ParentRegion = top;
            }
            else r.ParentRegion = null;

            stack.Push(r);

            AddFrame(r.Start, r);
        }
        while (stack.Count > 0)
        {
            var endPos = stack.Peek().End;
            stack.Pop();

            if (stack.Count > 0)
            {
                var top = stack.Peek();
                AddFrame(endPos, top);
            }
        }

        void AddFrame(int start, EHandler.Region region)
        {
            if (lastFrameStart == start)
            {
                _regionFrames[_regionFrames.Count - 1] = new EHFrame(start, region.Kind, region);
                return;
            }

            _regionFrames.Add(new EHFrame(start, region.Kind, region));
            lastFrameStart = start;
        }
        
        int frameIndex = 0;
        for (int i = 0; i < instEhFrames.Length; i++)
        {
            if(frameIndex < _regionFrames.Count - 1 && i >= _regionFrames[frameIndex + 1].Start)
                frameIndex++;
            instEhFrames[i] = frameIndex;
        }
        ThrowIfNeedAbort(AbortStrategy.AbortNextStep);
    }

    /// <summary>
    /// 验证嵌套和顺序关系
    /// </summary>
    private void ValidateExceptionHandlerRegionRelations()
    {
        foreach (var hb in _exceptionHandlers)
        {
            if (hb.HandlerRegion.ParentRegion != hb.ProtectedRegion.ParentRegion ||
                (hb.FilterRegion != null && hb.FilterRegion.ParentRegion != hb.ProtectedRegion.ParentRegion))
            {
                //不满足嵌套均在一个区间
                ReportDiagnostic(CFGDiagnostic.EhRegionInvalid(CFGExceptionType.TryAndHandlerNotInSameEnclosingRegion, 
                    hb.ProtectedRegion, hb.HandlerRegion)); 
            }

            if (hb.HandlerRegion.ParentRegion != null && hb.HandlerRegion.ParentRegion.Clause.Id < hb.Id)
            {
                //不满足子区域必须在前
                ReportDiagnostic(CFGDiagnostic.EhRegionInvalid(CFGExceptionType.InvalidEhTableOrdering, 
                    hb.ProtectedRegion, hb.HandlerRegion)); 
            }
        }
        
        ThrowIfNeedAbort(AbortStrategy.AbortNextStep);
    }

    /// <summary>
    /// 遍历instruction并添加basicBlock
    /// </summary>
    /// <param name="instEhFrames"></param>
    private void ScanInstructionsAndAddBasicBlocks(int[] instEhFrames)
    {
        foreach (var ehFlame in _regionFrames)
        {
            AddBasicBlock(_method.Body.Instructions[ehFlame.Start], ehFlame.Region);
        }
        
        for (int i = 0; i < _method.Body.Instructions.Count; i++)
        {
            var inst = _method.Body.Instructions[i];
            /*
            if (inst.OpCode.FlowControl is FlowControl.Phi)
                AddBasicBlock(inst);
            */
            if (inst.OpCode.FlowControl is FlowControl.Branch
                or FlowControl.Cond_Branch)
            {
                foreach (var targetInst in CecilHelper.OperandToTargets(inst.Operand))
                {
                    if (!_instDictionary.TryGetValue(targetInst, out var index))
                    {
                        //无效目标位置
                        ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InvalidBrTarget, inst));
                    }

                    if ((instEhFrames[index] == instEhFrames[i]) == (inst.OpCode.Code == Code.Leave))
                    {
                        //跨异常边界跳转
                        ReportDiagnostic(CFGDiagnostic.InstructionInvalid(
                            inst.OpCode.Code == Code.Leave
                                ? CFGExceptionType.LeaveTargetSameEhRegion
                                : CFGExceptionType.BrTargetCrossEhRegion, inst));
                    }

                    AddBasicBlock(targetInst, _regionFrames[instEhFrames[index]].Region);
                }
            }

            //终止指令的下一条指令也是起始
            if (inst.OpCode.FlowControl is FlowControl.Branch
                or FlowControl.Cond_Branch
                or FlowControl.Return
                or FlowControl.Throw)
            {
                if (inst.Next != null)
                {
                    AddBasicBlock(inst.Next, _regionFrames[instEhFrames[i + 1]].Region);
                }
            }

            // JMP特殊处理
            if (inst.OpCode.Code is Code.Jmp && inst.Next != null)
            {
                AddBasicBlock(inst.Next, _regionFrames[instEhFrames[i + 1]].Region);
            }

        }
        ThrowIfNeedAbort(AbortStrategy.AbortNextStep);
    }

    /// <summary>
    /// 验证全部指令的单指令（或前缀）的合法性
    /// </summary>
    /// <param name="instEhFrames"></param>
    private void VerifyAllInstructions(int[] instEhFrames)
    {
        Code? pPrefix = null;
        Code? prefix = null;
        int noCheck = 0;
        for (int i = 0; i < _method.Body.Instructions.Count; i++)
        {
            var inst = _method.Body.Instructions[i];
            //处理前缀合法
            if (prefix != null)
            {
                if (inst.OpCode.Code is Code.Constrained or Code.Volatile)
                {


                    if (prefix.Value == inst.OpCode.Code ||
                        (prefix.Value is not Code.Constrained and not Code.Volatile))
                    {
                        //非法前缀
                        ReportDiagnostic(CFGDiagnostic.PrefixInvalid(CFGExceptionType.InvalidOpCode, 
                            inst, prefix.Value));
                    }
                }
                else
                {
                    switch (prefix.Value)
                    {
                        case Code.Tail:
                            if (inst.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Calli))
                            {
                                ReportDiagnostic(CFGDiagnostic.PrefixInvalid(CFGExceptionType.InvalidOpCode, 
                                    inst, prefix.Value));
                            }

                            if (inst.Next is null || inst.Next.OpCode.FlowControl != FlowControl.Return)
                            {
                                ReportDiagnostic(CFGDiagnostic.PrefixInvalid(CFGExceptionType.InvalidOpCode, 
                                    inst, prefix.Value));
                            }
                            break;
                        case Code.Constrained:
                            if (inst.OpCode.Code is not Code.Callvirt)
                            {
                                ReportDiagnostic(CFGDiagnostic.PrefixInvalid(CFGExceptionType.InvalidOpCode, 
                                    inst, prefix.Value));
                            }
                            break;
                        case Code.Volatile when pPrefix != Code.Unaligned:
                            if (!(inst.OpCode.Code is Code.Ldind_I1 or Code.Ldind_I2 or Code.Ldind_I4 or Code.Ldind_I8 or Code.Ldind_I or
                                Code.Ldind_R4 or Code.Ldind_R8 or Code.Ldind_U1 or Code.Ldind_U2 or Code.Ldind_U4 or Code.Ldind_Ref or
                                Code.Stind_I1 or Code.Stind_I2 or Code.Stind_I4 or Code.Stind_I8 or Code.Stind_I or
                                Code.Stind_R4 or Code.Stind_R8 or Code.Stind_Ref or
                                Code.Ldfld or Code.Ldsfld or Code.Ldobj or Code.Stfld or Code.Stsfld or Code.Stobj or
                                Code.Initblk or Code.Cpblk))
                            {
                                ReportDiagnostic(CFGDiagnostic.PrefixInvalid(CFGExceptionType.InvalidOpCode, 
                                    inst, prefix.Value));
                            }
                            break;
                        case Code.Unaligned:
                        case Code.Volatile: //包含双前缀
                            if (!(inst.OpCode.Code is Code.Ldind_I1 or Code.Ldind_I2 or Code.Ldind_I4 or Code.Ldind_I8 or Code.Ldind_I or
                                Code.Ldind_R4 or Code.Ldind_R8 or Code.Ldind_U1 or Code.Ldind_U2 or Code.Ldind_U4 or Code.Ldind_Ref or
                                Code.Stind_I1 or Code.Stind_I2 or Code.Stind_I4 or Code.Stind_I8 or Code.Stind_I or
                                Code.Stind_R4 or Code.Stind_R8 or Code.Stind_Ref or
                                Code.Ldfld or Code.Ldobj or Code.Stfld or Code.Stobj or
                                Code.Initblk or Code.Cpblk))
                            {
                                ReportDiagnostic(CFGDiagnostic.PrefixInvalid(CFGExceptionType.InvalidOpCode, 
                                    inst, prefix.Value));
                            }
                            break;
                        case Code.Readonly:
                            if (inst.OpCode.Code is not Code.Ldelema)
                            {
                                ReportDiagnostic(CFGDiagnostic.PrefixInvalid(CFGExceptionType.InvalidOpCode, 
                                    inst, prefix.Value));
                            }
                            break;
                        case Code.No:
                            if ((noCheck & 1) != 0)
                            {
                                if (inst.OpCode.Code is Code.Castclass or Code.Unbox or Code.Ldelema or Code.Stelem_Any or
                                    Code.Stelem_I1 or Code.Stelem_I2 or Code.Stelem_I4 or Code.Stelem_I8 or Code.Stelem_I or
                                    Code.Stelem_R4 or Code.Stelem_R8 or Code.Stelem_Ref)
                                {
                                    break;
                                }
                            }
                            if ((noCheck & 2) != 0)
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
                            if ((noCheck & 4) != 0)
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
                            ReportDiagnostic(CFGDiagnostic.PrefixInvalid(CFGExceptionType.InvalidOpCode, 
                                inst, prefix.Value));
                            break;
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
                        {
                            ReportDiagnostic(CFGDiagnostic.InvalidOperand(typeof(byte), inst.Operand?.GetType() ?? typeof(void), inst));
                        }
                        else
                        {
                            noCheck = b;
                        }
                        break;
                    }
                case Code.Ret:
                    {
                        if (_regionFrames[instEhFrames[i]].Kind is not RegionKind.Normal)
                        {
                            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InvalidInstruction, 
                                inst, DiagnosticSeverity.Error, "Invalid 'ret' inside EH block."));
                        }
                        break;
                    }
                case Code.Rethrow:
                    {
                        if (_regionFrames[instEhFrames[i]].Kind is not RegionKind.Handler ||
                            _regionFrames[instEhFrames[i]].Region.Clause.ExceptionHandler.HandlerType is not ExceptionHandlerType
                                .Catch)
                        {
                            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InvalidInstruction, 
                                inst, DiagnosticSeverity.Error, "Invalid 'rethrow' outside EH region."));
                            //不可rethrow
                        }

                        break;
                    }
                case Code.Endfilter:
                    {
                        if (_regionFrames[instEhFrames[i]].Kind is not RegionKind.Filter ||
                            _regionFrames[instEhFrames[i]].Region.Clause.ExceptionHandler.HandlerType is not ExceptionHandlerType
                                .Filter)
                        {
                            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InvalidInstruction, 
                                inst, DiagnosticSeverity.Error, "Invalid 'endfilter' outside filter region."));
                        }
                        break;
                    }
                case Code.Endfinally:
                    {
                        if (_regionFrames[instEhFrames[i]].Kind is not RegionKind.Handler ||
                            _regionFrames[instEhFrames[i]].Region.Clause.ExceptionHandler.HandlerType is not ExceptionHandlerType
                                .Finally)
                        {
                            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InvalidInstruction, 
                                inst, DiagnosticSeverity.Error, "Invalid 'endfilter' outside finally region."));
                        }
                        break;
                    }
                case Code.Leave:
                    {
                        if (_regionFrames[instEhFrames[i]].Kind is not RegionKind.Try &&
                            _regionFrames[instEhFrames[i]].Kind is not RegionKind.Handler)
                        {
                            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InvalidInstruction, 
                                inst, DiagnosticSeverity.Error, "Invalid 'leave' outside try/catch region."));
                        }
                        break;
                    }
            }

            // 验证调用指令的可解析性 对于inlineBr在前面AddBasicBlock处理了
            switch (inst.OpCode.OperandType)
            {
                case OperandType.InlineMethod:
                    {
                        if (VerifyMethodOperand(inst, out var mf))
                        {
                            ResolveWithDiagnostic(mf);
                        }
                        break;
                    }
                case OperandType.InlineField:
                    {
                        if (VerifyFieldOperand(inst, out var field) && ResolveWithDiagnostic(field) is FieldDefinition fd)
                        {
                            if (fd.Attributes.HasFlag(FieldAttributes.Static) !=
                                (inst.OpCode.Code is Code.Ldsflda or Code.Ldsfld or Code.Stsfld))
                            {
                                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InconsistentFieldAccess, inst,
                                    DiagnosticSeverity.Error, "Field static attribute does not match the access opcode."));
                            }
                        }
                        break;
                    }
                case OperandType.InlineTok:
                    {
                        if (VerifyMemberOperand(inst, out var member))
                        {
                            ResolveWithDiagnostic(member);
                        }
                        break;
                    }
                case OperandType.InlineType:
                    {
                        if (VerifyTypeOperand(inst, out var type))
                        {
                            ResolveWithDiagnostic(type);
                        }
                        break;
                    }
                case OperandType.InlineVar:
                    {
                        if(VerifyVarOperand(inst, out var re))
                        {
                            if (re.Index < 0 || re.Index >= _method.Body.Variables.Count)
                            {
                                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.OutOfRange, inst));
                            }
                        } 
                        break;
                        
                    }
                case OperandType.InlineArg:
                    {
                        if (VerifyParameterOperand(inst, out var re))
                        {
                            if (re.Index < 0 || re.Index >= _method.Parameters.Count)
                            {
                                if(re.Index != -1 || !_method.HasThis)
                                    ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.OutOfRange, inst));
                            }
                        }
                        break;
                    }
                default:
                    //其他非法情况会在Mono.Cecil处报错
                    break;
            }

        
        }
        ThrowIfNeedAbort(AbortStrategy.AbortNextStep);
    }
    
    private void AddBasicBlock(Instruction leader, EHandler.Region region)
    {
        if (_blockMap.ContainsKey(leader)) return;  
        var block = new BasicBlock(leader, region);
        _blocks.Add(block);
        _blockMap.Add(leader, block);
    }

    private void LightControlFlowPass()
    {
        var usedBlocks = new HashSet<BasicBlock>();

        foreach (var block in _blocks)
        {
            if (usedBlocks.Contains(block))  continue;
            LightAnalyzeBlocksControlFlow(block, usedBlocks);
            ThrowIfNeedAbort(AbortStrategy.AbortNextBlock);
        }
    }

    private void LightAnalyzeBlocksControlFlow(BasicBlock entryBlock, HashSet<BasicBlock> usedBlocks)
    {

        if (entryBlock.Kind is RegionKind.Filter ||
            (entryBlock.Kind is RegionKind.Handler && entryBlock.Region.Clause.ExceptionHandler.CatchType is not null)) //对于filter和handler插入异常对象
        {
            entryBlock._entryStackDepth = 1;
        }
        else
        {
            entryBlock._entryStackDepth = 0;
        }
        List<BasicBlock> bfsBlocks = [entryBlock];
        
        var initLocals = VerifyLocalInit ? new BitArray(_method.Body.Variables.Count) : null;
        entryBlock.initLocals = VerifyLocalInit ? new BitArray(_method.Body.Variables.Count) : null;
        
        for (int i = 0; i < bfsBlocks.Count; i++)
        {
            var block = bfsBlocks[i];
            var stackHeight = block.EntryStackDepth;
            usedBlocks.Add(block);
            
            initLocals?.SetAll(true);
            initLocals?.And(block.initLocals!);
            
            var leader = block.Leader;
            for (var inst = leader; inst != null; inst = inst.Next)
            {
                if (inst.OpCode.StackBehaviourPop == StackBehaviour.PopAll)
                {
                    stackHeight = 0;
                }
                else
                {
                    var pop = -1;
                    try
                    {
                        pop = inst.PopCount(_method);
                    }
                    catch
                    {
                        ReportDiagnostic(CFGDiagnostic.InvalidOperand(typeof(IMethodSignature), inst.Operand?.GetType() ?? typeof(void), inst),
                            AbortStrategy.AbortImminently);
                    }
                    stackHeight -= pop;
                    if (stackHeight < 0)
                    {
                        ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.StackUnderflow, inst,
                            DiagnosticSeverity.Error), AbortStrategy.NoAbort);
                    }
                }

                var push = -1;
                try
                {
                    push = inst.PushCount();
                }
                catch
                {
                    ReportDiagnostic(CFGDiagnostic.InvalidOperand(typeof(IMethodSignature), inst.Operand?.GetType() ?? typeof(void), inst),
                        AbortStrategy.AbortImminently);
                }
                stackHeight += push;
                if (stackHeight > _method.Body.MaxStackSize)
                {
                    ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.StackOverflow, inst),
                        AbortStrategy.NoAbort);
                }
                if (VerifyLocalInit)
                {
                    AnalyzeCF_CalcInitLocal(inst, block.initLocals!);
                }

                bool endBlock = false;
                switch (inst.OpCode.FlowControl)
                {
                    case FlowControl.Next:
                        if (_blockMap.TryGetValue(inst.Next, out var ehBlock))
                        {
                            switch (ehBlock.Kind)
                            {
                                case RegionKind.Handler or RegionKind.Filter when block.Region != ehBlock.Region:
                                    ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InvalidFallThrough, inst));
                                    endBlock = true;
                                    break;
                                case RegionKind.Try:
                                    AnalyzeCF_AddNextBlock(block, ehBlock, bfsBlocks, stackHeight, initLocals, usedBlocks);
                                    endBlock = true;
                                    break;
                            }
                        }
                        break;
                    case FlowControl.Branch:
                        foreach (var target in CecilHelper.OperandToTargets(inst.Operand))
                        {
                            AnalyzeCF_AddNextBlock(block, _blockMap[target], bfsBlocks, stackHeight, initLocals, usedBlocks);
                        }
                        endBlock = true;
                        break;
                    case FlowControl.Cond_Branch:
                        foreach (var target in CecilHelper.OperandToTargets(inst.Operand))
                        {
                            AnalyzeCF_AddNextBlock(block, _blockMap[target], bfsBlocks, stackHeight, initLocals, usedBlocks);
                        }
                        AnalyzeCF_AddNextBlock(block, _blockMap[inst.Next], bfsBlocks, stackHeight, initLocals, usedBlocks);
                        endBlock = true;
                        break;
                    case FlowControl.Throw:
                        endBlock = true;
                        break;
                    case FlowControl.Return when inst.OpCode.Code is Code.Endfilter:
                        AnalyzeCF_AddNextBlock(block, _blockMap[inst.Next], bfsBlocks, stackHeight, initLocals, usedBlocks);
                        endBlock = true;
                        break;
                  
                }

                if (endBlock)
                {
                    break;
                }
                ThrowIfNeedAbort(AbortStrategy.AbortNextStep);
            }
            ThrowIfNeedAbort(AbortStrategy.AbortNextBlock);
        }
    }

    private void ControlFlowPass()
    {
        var buffer = new StackType[4];
        var funcBuffer = new StackType[8];
        var usedBlocks = new HashSet<BasicBlock>();
        
        foreach (var block in _blocks)
        {
            if (usedBlocks.Contains(block)) continue;
            AnalyzeBlocksControlFlow(block, usedBlocks, buffer, funcBuffer);
        }
    }

    private void AnalyzeBlocksControlFlow(BasicBlock entryBlock, 
        HashSet<BasicBlock> usedBlocks,  StackType[] buffer, StackType[] funcBuffer)
    {
        var localStack = new Stack<StackType>(_method.Body.MaxStackSize);
        var entryStackNode = _root;
        var initLocals = VerifyLocalInit ? new BitArray(_method.Body.Variables.Count) : null;
        entryBlock.initLocals = VerifyLocalInit ? new BitArray(_method.Body.Variables.Count) : null;
        
        if (entryBlock.Kind is RegionKind.Filter) //对于filter和handler插入异常对象
        {
            AnalyzeCF_EvalStackPush(localStack, ref entryStackNode, StackType.Create(_method.Module.TypeSystem.Object),
                entryBlock.Leader);
        }
        else if (entryBlock.Kind is RegionKind.Handler && entryBlock.Region.Clause.ExceptionHandler.CatchType is not null)
        {
            AnalyzeCF_EvalStackPush(localStack, ref entryStackNode, entryBlock.Region.Clause.ExceptionHandler.CatchType,
                entryBlock.Leader);
        }
        
        List<(BasicBlock block, EvalStackNode node)> bfsBlocks = [(entryBlock, entryStackNode)];


        for(int i = 0; i < bfsBlocks.Count; i++)
        {
            var (block, node) = bfsBlocks[i];
            usedBlocks.Add(block);
            block.EntryNode = node;
            
            initLocals?.SetAll(true);
            initLocals?.And(block.initLocals!);
            
            var leader = block.Leader;
            StackType retType = StackType.Invalid;
            for (var inst = leader; inst != null; inst = inst.Next)
            {
                var ts = _method.Module.TypeSystem;
                if(inst.OpCode.StackBehaviourPop == StackBehaviour.PopAll)
                {
                    localStack.Clear();
                    node = _root;
                }
                var pop = inst.OpCode.StackBehaviourPop.PopCount() switch
                {
                    -1 => 0,
                    var tmpPop => tmpPop
                };
                
                for (int j = 0; j< pop; j++)
                {
                    buffer[j] = AnalyzeCF_EvalStackPop(localStack, ref node, inst);
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
                        VerifyType(buffer[2], ts.Int32, inst);
                        break;

                    case StackBehaviour.Popref_popi_popi:
                    case StackBehaviour.Popref_popi_popi8:
                    case StackBehaviour.Popref_popi_popr4:
                    case StackBehaviour.Popref_popi_popr8:
                    case StackBehaviour.Popref_popi_popref:
                        VerifyPop3(inst, buffer);
                        break;

                    case StackBehaviour.Varpop:
                        retType = VerifyVarPop(inst, ref funcBuffer, out var len);
                        for(int j = len - 1; j >=0; j--)
                        {
                            var type = AnalyzeCF_EvalStackPop(localStack, ref node, inst);
                            VerifyType(type, funcBuffer[j], inst);
                        }
                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }

                switch (inst.OpCode.StackBehaviourPush)
                {
                    case StackBehaviour.Push0:
                        break;
                    case StackBehaviour.Push1_push1:
                        AnalyzeCF_EvalStackPush(localStack, ref node, retType, inst);
                        AnalyzeCF_EvalStackPush(localStack, ref node, retType, inst);
                        break;
                    case StackBehaviour.Varpush:
                        if (retType != StackType.Invalid)
                        {
                            AnalyzeCF_EvalStackPush(localStack, ref node, retType, inst);
                        }
                        break;
                    case StackBehaviour.Push1:
                        retType = VerifyPush1(inst, retType);
                        AnalyzeCF_EvalStackPush(localStack, ref node, retType, inst);
                        break;
                    case StackBehaviour.Pushi:
                        AnalyzeCF_EvalStackPush(localStack, ref node, StackType.I4, inst);
                        break;
                    case StackBehaviour.Pushi8:
                        AnalyzeCF_EvalStackPush(localStack, ref node, StackType.I8, inst);
                        break;
                    case StackBehaviour.Pushr4:
                        AnalyzeCF_EvalStackPush(localStack, ref node, StackType.F, inst);
                        break;
                    case StackBehaviour.Pushr8:
                        AnalyzeCF_EvalStackPush(localStack, ref node, StackType.F, inst);
                        break;
                    case StackBehaviour.Pushref:
                        retType = VerifyPushref(inst, retType);
                        AnalyzeCF_EvalStackPush(localStack, ref node, retType, inst);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
                if (VerifyLocalInit)
                {
                    AnalyzeCF_CalcInitLocal(inst, initLocals!);
                }

                bool endBlock = false;
                switch (inst.OpCode.FlowControl)
                {
                    case FlowControl.Next:
                        if (_blockMap.TryGetValue(inst.Next, out var ehBlock))
                        {
                            switch (ehBlock.Kind)
                            {
                                case RegionKind.Handler:
                                case RegionKind.Filter:
                                    ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InvalidFallThrough, inst));
                                    endBlock = true;
                                    break;
                                case RegionKind.Try:
                                    AnalyzeCF_AppendStack(localStack, ref node);
                                    AnalyzeCF_AddNextBlock(block, ehBlock, bfsBlocks, node, initLocals, usedBlocks);
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
                            AnalyzeCF_AddNextBlock(block, targetBlock, bfsBlocks, node, initLocals, usedBlocks);
                            block.Edges.Add(new ControlFlowEdge(block, targetBlock));
                        }
                        endBlock = true;
                        break;
                    case FlowControl.Cond_Branch:
                        AnalyzeCF_AppendStack(localStack, ref node);
                        foreach (var target in CecilHelper.OperandToTargets(inst.Operand))
                        {
                            var targetBlock = _blockMap[target];
                            AnalyzeCF_AddNextBlock(block, targetBlock, bfsBlocks, node, initLocals, usedBlocks);
                            block.Edges.Add(new ControlFlowEdge(block, targetBlock));
                        }
                        var next = _blockMap[inst.Next];
                        AnalyzeCF_AddNextBlock(block, next, bfsBlocks, node, initLocals, usedBlocks);
                        block.Edges.Add(new ControlFlowEdge(block, next));
                        endBlock = true;    
                        break;
                    case FlowControl.Throw:
                        endBlock = true;
                        break;
                }
                if (endBlock)
                    break;
                ThrowIfNeedAbort(AbortStrategy.AbortNextStep);
            }
            ThrowIfNeedAbort(AbortStrategy.AbortNextBlock);
        }
    }

    //校验local是否被初始化，进入该函数需确保VerifyInitLocal为true
    private void AnalyzeCF_CalcInitLocal(Instruction inst, BitArray array)
    {
        if (inst.OpCode.Code is Code.Stloc)
        {
            array[((VariableDefinition)(inst.Operand)).Index] = true;
        }

        if (inst.OpCode.Code is Code.Ldloc or Code.Ldloca && !array[((VariableDefinition)(inst.Operand)).Index])
        {
            //Ldlocda进行最保守估计，不追踪
            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.UninitializedLocal, inst,
                inst.OpCode.Code is Code.Ldloc ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning));
        }
    }

    /// <summary>
    /// 判断是否已经处理过，处理过则进行合并判别，如果合并后有路径变更则重新计算cf，否则不添加
    /// </summary>
    private void AnalyzeCF_AddNextBlock(BasicBlock from, BasicBlock to,
        List<BasicBlock> bfsBlocks,
        int depth,
        BitArray? currentInitLocals,
        HashSet<BasicBlock> usedBlocks)
    {
        var a = _instDictionary[to.Leader];
        depth = depth + to.Region.Kind switch
        {
            RegionKind.Filter or RegionKind.Handler 
            when to.Region.Start == _instDictionary[to.Leader] => 1,
            _ => 0,
        };
        if (usedBlocks.Contains(to))
        {
            to.initLocals?.And(currentInitLocals!);
            if (depth != to.EntryStackDepth)
            {
                //堆栈合并不平衡错误
                ReportDiagnostic(CFGDiagnostic.IncompatibleMerge(CFGExceptionType.IncompatibleMergeDepth,
                    from, to));
            }
        }
        else
        {
            if (VerifyLocalInit)
            {
                to.initLocals = new BitArray(currentInitLocals!);
            }
            to._entryStackDepth = depth;
            bfsBlocks.Add(to);
            usedBlocks.Add(to);
            from.Edges.Add(new ControlFlowEdge(from, to));
        }
        //Console.WriteLine($"Add edge from {from.Leader}-{_instDictionary[from.Leader]}:[{from.EntryStackDepth}] to {to.Leader}-{_instDictionary[to.Leader]}:[{to.EntryStackDepth}]");
    }
    /// <summary>
    /// 判断是否已经处理过，处理过则进行合并判别，如果合并后有路径变更则重新计算cf，否则不添加
    /// </summary>
    private void AnalyzeCF_AddNextBlock(BasicBlock from, BasicBlock to,
        List<(BasicBlock block, EvalStackNode node)> bfsBlocks,
        EvalStackNode currentNode,
        BitArray? currentInitLocals,
        HashSet<BasicBlock> usedBlocks)
    {
        if (usedBlocks.Contains(to))
        {
            to.initLocals?.And(currentInitLocals!);
            var lastNode = to.EntryNode;
            var curNode = currentNode;
            if (curNode.Depth != lastNode!.Depth) //启用检测堆栈类型则EntryNode一定不为null
            {
                //堆栈合并不平衡错误
                ReportDiagnostic(CFGDiagnostic.IncompatibleMerge(CFGExceptionType.IncompatibleMergeDepth, 
                    from, to));
                return;
            }
            List<StackType> nodes = new List<StackType>(lastNode.Depth);
            bool noChanged = true;
            while (curNode != _root)
            {
                if (curNode.Type.StackValueEqualsTo(lastNode.Type))
                {
                    nodes.Add(curNode.Type);
                  
                }
                else
                {
                    var merged = curNode.Type.Intersect(lastNode.Type);
                    if (merged == StackType.Invalid)
                    {
                        ReportDiagnostic(CFGDiagnostic.IncompatibleMerge(CFGExceptionType.IncompatibleMergeTypes, 
                            from, to));
                        return;
                    }
                    noChanged = false;
                    nodes.Add(merged);
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
            
            

            if (!noChanged)
            {
                to.EntryNode = newNode;
                bfsBlocks.Add((to, newNode));
                from.Edges.Add(new ControlFlowEdge(from, to));
            }
           
        }
        else
        {
            if (VerifyLocalInit)
            {
                to.initLocals = new BitArray(currentInitLocals!);
            }
            bfsBlocks.Add((to, currentNode));
            from.Edges.Add(new ControlFlowEdge(from, to));
        }
    }

    private StackType AnalyzeCF_EvalStackPop(Stack<StackType> localStack,
        ref EvalStackNode node, Instruction inst)
    {
        if (localStack.Count != 0)
            return localStack.Pop();

        if (node.Parent?.Type is null)
        {
            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.StackUnderflow, inst,
                DiagnosticSeverity.Fatal));
            return StackType.Invalid;
        }
        node = node.Parent;
        return node.Type;
    }

    private void AnalyzeCF_EvalStackPush(Stack<StackType> localStack,
        ref EvalStackNode node, StackType type, Instruction inst)
    {
        if (localStack.Count + node.Depth + 1 > _method.Body.MaxStackSize)
        {
            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.StackOverflow, inst),
                AbortStrategy.NoAbort);
        }
        if (_nodeIntern.TryGetValue((type, node), out var child))
        {
            node = child;
            return;
        }
        localStack.Push(type);
    }

    private void AnalyzeCF_AppendStack(Stack<StackType> localStack, 
        ref EvalStackNode node)
    {
        while (localStack.Count != 0)
        {
            var type = localStack.Pop();
            var prev = node;
            node = node.AppendChild(type);
            _nodeIntern.Add((type, prev), node);
        }
    }

    private EHFrame EhBlockByInstruction(int instIndex)
    {
        for (int i = 1; i < _regionFrames.Count; i++)
        {
            if (_regionFrames[i].Start > instIndex)
            {
                return _regionFrames[i - 1];
            }
        }

        return _regionFrames[_regionFrames.Count - 1];
    }
}
