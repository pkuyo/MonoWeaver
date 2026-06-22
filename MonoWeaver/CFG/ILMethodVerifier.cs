using Mono.Cecil;
using Mono.Cecil.Cil;
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
    AccessTest = 1 << 6,
    Full = Instructions | LocalInit | StackTypes | ByrefEscape | AccessTest,
    Light = StackBalance | Instructions
}

public enum EHRelation
{
    None,
    Same,
    Assciated,
    Enclosing,
    Disjoint
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
        public readonly RegionKind Kind;

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

public partial class ILMethodVerifier
{
    private bool BuildExceptionBlock(ExceptionHandler eh, Dictionary<Instruction, int> instDic, int methodEndIndex, out EHandler? block)
    {
        block = null;
        if (eh.TryStart is null) return false;
        if (eh.HandlerStart is null) return false;
        if (eh is { HandlerType: ExceptionHandlerType.Filter, FilterStart: null })
            return false;

        int filterStart = -1;
        int tryEnd = methodEndIndex; //如果为null代表指向函数尾
        int handlerEnd = methodEndIndex; //如果为null代表指向函数尾
        if (!instDic.TryGetValue(eh.TryStart, out var tryStart)) return false;
        if (eh.TryEnd != null && !instDic.TryGetValue(eh.TryEnd, out tryEnd)) return false;
        if (!instDic.TryGetValue(eh.HandlerStart, out var handlerStart)) return false;
        if (eh.HandlerEnd != null && !instDic.TryGetValue(eh.HandlerEnd, out handlerEnd)) return false;
        if (eh.FilterStart is not null && !instDic.TryGetValue(eh.FilterStart, out filterStart)) return false;

        if (tryStart >= tryEnd || tryEnd > methodEndIndex || handlerStart >= handlerEnd || handlerEnd > methodEndIndex)
            return false;

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

public partial class ILMethodVerifier
{
    internal readonly MethodDefinition _method;

    private Dictionary<Instruction, int> _instDictionary = null!;

    private readonly EvalStackNode _root = new(StackType.Invalid);
    
    private readonly Dictionary<(StackType type, EvalStackNode prev), EvalStackNode> _nodeIntern = new();
    private readonly Dictionary<Instruction, BasicBlock> _blockMap = new();

    internal List<BasicBlock> _blocks = null!;
    internal List<BasicBlock> _entryblocks = null!;

    internal List<EHandler> _exceptionHandlers = null!;

    private List<EHFrame> _regionFrames = null!;

    private bool _needInitAnalysis;

    private VerifyOptions _verifyOptions;

    private AbortStrategy _abortVerificationStrategy = AbortStrategy.NoAbort;

    private bool _initThis = false;

    private bool _modifiesThis = false;

    public bool VerifyStackType => _verifyOptions.HasFlag(VerifyOptions.StackTypes);

    public bool VerifyStackBalance => _verifyOptions.HasFlag(VerifyOptions.StackBalance);

    public bool VerifyInstructions => _verifyOptions.HasFlag(VerifyOptions.Instructions);

    public bool VerifyLocalInit => _verifyOptions.HasFlag(VerifyOptions.LocalInit) && _needInitAnalysis;

    public MethodDefinition Method => _method;

    public bool CecilFault { get; private set; } = false;

    public ILMethodVerifier(MethodDefinition method, VerifyOptions verifyOptions = VerifyOptions.Light)
    {
        if (!method.IsIL)
        {
            throw new ArgumentException("Method must be IL method", method.FullName);
        }

        _verifyOptions = verifyOptions;
        _method = method;
        try
        {
            _needInitAnalysis = !_method.Body.InitLocals;
        }
        catch (Exception e)
        {
            CecilFault = true;
            ReportDiagnostic(CFGDiagnostic.CecilLoadFailed(e));
            return;
        }
        _initThis = !_method.HasThis || !_method.IsSpecialName || _method.Name != ".ctor";
        _modifiesThis = false;
        VerifyMethod();
    }

    //public ILMethodAnalyzer ReAnalyze(VerifyOptions verifyOptions = VerifyOptions.Light)
    //{
    //    _verifyOptions = verifyOptions;
    //    CecilFault = false;
    //    _instDictionary.Clear();
    //    _exceptionHandlers.Clear();
    //    _regionFrames.Clear();
    //    _blockMap.Clear();
    //    _nodeIntern.Clear();
    //    _blocks.Clear();
    //    Diagnostics.Clear();
    //    _reportedDiagnostics.Clear();
    //    _entryblocks.Clear();
    //    _currentErrorCount = 0;
    //    _abortVerificationStrategy = AbortStrategy.NoAbort;
    //    try
    //    {
    //        _needInitAnalysis = !_method.Body.InitLocals;
    //    }
    //    catch (Exception e)
    //    {
    //        CecilFault = true;
    //        ReportDiagnostic(CFGDiagnostic.CecilLoadFailed(e));
    //        return this;
    //    }
    //    _initThis = !_method.HasThis || !_method.IsSpecialName || _method.Name != ".ctor";
    //    _modifiesThis = false;
    //    VerifyMethod();
    //    return this;
    //}


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

        var graph = ILBasicBlockGraphBuilder.Build(_method);
        _blocks = graph.Blocks;
        _entryblocks = graph.EntryBlocks;
        _blockMap.Clear();
        foreach (var block in graph.Blocks)
        {
            var region = _regionFrames[instEhFrames[block.StartIndex]].Region;
            block.Region = region;
            block.Kind = region.Kind;
            _blockMap.Add(block.Leader, block);
        }

        ThrowIfNeedAbort(AbortStrategy.AbortNextStep);

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
        _entryblocks = new List<BasicBlock>();

        var totalMethodRegion = EHandler.CreateMethodRegion(_method.Body.Instructions.Count + 1); //占位末尾为length+1 length为真实末尾
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

    private EHRelation GetEHRelation(EHandler.Region from, EHandler.Region to)
    {
        if (from.Start == to.Start && from.End == to.End) return EHRelation.Same;
        if (from.Start >= to.Start && from.End <= to.End) return EHRelation.Enclosing;
        if (to.Start >= from.Start && to.End <= from.End) return EHRelation.Assciated;
        return EHRelation.Disjoint;
    }

    private void ReportInvalidBranchAcrossEhRegion(Instruction inst,
        EHandler.Region currentRegion,
        EHandler.Region targetRegion,
        bool includeBranchIntoTry)
    {
        var emitted = new HashSet<CFGExceptionType>();
        var relation = GetEHRelation(currentRegion, targetRegion);

        if (IsBranchOutOfCurrentRegion(currentRegion, relation))
        {
            Report(GetBranchOutExceptionType(currentRegion));
        }

        switch (targetRegion.Kind)
        {
            case RegionKind.Filter:
                Report(CFGExceptionType.BranchIntoFilter);
                break;

            case RegionKind.Handler:
                Report(CFGExceptionType.BranchIntoHandler);
                break;

            case RegionKind.Try when includeBranchIntoTry && !IsBranchOutOfCurrentRegion(currentRegion, relation):
                Report(CFGExceptionType.BranchIntoTry);
                break;
        }

        if (emitted.Count == 0)
        {
            Report(targetRegion.Kind == RegionKind.Try
                ? CFGExceptionType.BranchIntoTry
                : CFGExceptionType.InvalidBrTarget);
        }

        void Report(CFGExceptionType exceptionType)
        {
            if (emitted.Add(exceptionType))
            {
                ReportDiagnostic(CFGDiagnostic.BranchRegionInvalid(exceptionType, inst, currentRegion, targetRegion));
            }
        }
    }

    private static bool IsBranchOutOfCurrentRegion(EHandler.Region currentRegion, EHRelation relation)
        => currentRegion.Kind != RegionKind.Normal && relation is not EHRelation.Same and not EHRelation.Assciated;

    private static CFGExceptionType GetBranchOutExceptionType(EHandler.Region currentRegion)
    {
        return currentRegion.Kind switch
        {
            RegionKind.Try => CFGExceptionType.BranchOutOfTry,
            RegionKind.Filter => CFGExceptionType.BranchOutOfFilter,
            RegionKind.Handler => currentRegion.Clause.ExceptionHandler.HandlerType switch
            {
                ExceptionHandlerType.Finally => CFGExceptionType.BranchOutOfFinally,
                ExceptionHandlerType.Fault => CFGExceptionType.BranchOutOfFault,
                _ => CFGExceptionType.BranchOutOfHandler,
            },
            _ => CFGExceptionType.InvalidBrTarget,
        };
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
            if (BuildExceptionBlock(eh, _instDictionary, _method.Body.Instructions.Count, out var hb))
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
        instEhFrames = new int[_method.Body.Instructions.Count + 1];
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
                                Code.Initblk or Code.Cpblk or Code.Unaligned))
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
                            if (inst.OpCode.Code is not Code.Ldelema && 
                                (inst.OpCode.Code is not Code.Call || !VerifyMethodOperand(inst, out var method) ||
                                !method.DeclaringType.IsArray || method.Name != "Address")) //针对MDArray
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


            // 验证调用指令的可解析性；branch target 的区域规则在 operand 校验后统一处理。
            switch (inst.OpCode.OperandType)
            {
                case OperandType.InlineMethod:
                    {
                        if (VerifyMethodOperand(inst, out var mf) && ResolveWithDiagnostic(mf) is MethodDefinition md)
                        {
                            if (!_method.DeclaringType.CanAccess(mf, md))
                            {
                                ReportDiagnostic(CFGDiagnostic.MethodAccessViolation(inst, _method.DeclaringType, mf));
                            }
                        }
                        break;
                    }
                case OperandType.InlineField:
                    {
                        if (VerifyFieldOperand(inst, out var field) && ResolveWithDiagnostic(field) is FieldDefinition fd)
                        {
                            if (!_method.DeclaringType.CanAccess(field, fd))
                            {
                                ReportDiagnostic(CFGDiagnostic.FieldAccessViolation(inst, _method.DeclaringType, field));
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
                        if (VerifyTypeOperand(inst, out var type) && ResolveWithDiagnostic(type) is TypeDefinition)
                        {
                            if (!_method.DeclaringType.CanAccess(type))
                            {
                                ReportDiagnostic(CFGDiagnostic.TypeAccessViolation(inst, _method.DeclaringType, type));
                            }
                        }
                        break;
                    }
                case OperandType.InlineVar:
                case OperandType.ShortInlineVar:
                    {
                        TryGetVariableIndex(inst, out _);
                        break;
                        
                    }
                case OperandType.InlineArg:
                case OperandType.ShortInlineArg:
                    {
                        if(TryGetParameterType(inst, out _, out var flag) && 
                            flag.HasFlag(StackTypeFlags.ThisPtr) &&
                            inst.OpCode.Code is Code.Ldarga or Code.Ldarga_S or Code.Starg or Code.Starg_S)
                        {
                            _modifiesThis = true;
                        }
                        break;
                    }
                case OperandType.InlineBrTarget:
                case OperandType.ShortInlineBrTarget:
                    {
                        VerifyInstructionOperand(inst, out _);
                        break;
                    }
                case OperandType.InlineSwitch:
                    {
                        VerifyInstructionArrayOperand(inst, out _);
                        break;
                    }
                case OperandType.InlineI:
                    {
                        VerifyInt32Operand(inst, out _);
                        break;
                    }
                case OperandType.ShortInlineI:
                    {
                        if (inst.OpCode.Code is Code.Ldc_I4_S)
                            VerifySByteOperand(inst, out _);
                        else
                            VerifyByteOperand(inst, out _);
                        break;
                    }
                case OperandType.InlineI8:
                    {
                        VerifyInt64Operand(inst, out _);
                        break;
                    }
                case OperandType.InlineR:
                    {
                        VerifyDoubleOperand(inst, out _);
                        break;
                    }
                case OperandType.ShortInlineR:
                    {
                        VerifySingleOperand(inst, out _);
                        break;
                    }
                case OperandType.InlineString:
                    {
                        VerifyStringOperand(inst, out _);
                        break;
                    }
                case OperandType.InlineSig:
                    {
                        VerifyCallSiteOperand(inst, out _);
                        break;
                    }
                case OperandType.InlineNone:
                    {
                        VerifyNoOperand(inst);
                        break;
                    }
                case OperandType.InlinePhi:
                    {
                        ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InvalidOpCode, inst,
                            DiagnosticSeverity.Error, "InlinePhi operand type is not valid in CIL."));
                        break;
                    }
                default:
                    //其他非法情况会在Mono.Cecil处报错
                    break;
            }

            VerifyBranchTargets(inst, i, instEhFrames);

            VerifyGenericConstraints(inst);

            // 前缀与特殊指令约束
            switch (inst.OpCode.Code)
            {
                case Code.Tail or Code.Constrained or Code.Volatile or Code.Unaligned or Code.Readonly:
                    {
                        if (prefix != null)
                            pPrefix = prefix;
                        prefix = inst.OpCode.Code;

                        if (inst.OpCode.Code == Code.Unaligned)
                        {
                            if (inst.Operand is not byte b || b > 4 || b == 3)
                            {
                                ReportDiagnostic(CFGDiagnostic.InvalidOperand(typeof(byte), inst.Operand?.GetType()
                                    ?? typeof(void), inst));
                            }
                        }

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
                            _regionFrames[instEhFrames[i]].Region.Clause.ExceptionHandler.HandlerType is
                                not ExceptionHandlerType.Finally and not ExceptionHandlerType.Fault)
                        {
                            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InvalidInstruction,
                                inst, DiagnosticSeverity.Error, "Invalid 'endfinally' outside finally region."));
                        }
                        break;
                    }
                case Code.Stfld: //静态/实例fld判别在前面
                    {
                        if (inst.Operand is FieldReference fieldRef && fieldRef.Resolve() is FieldDefinition fd)
                        {
                            if (fd.Attributes.HasFlag(FieldAttributes.Static))
                            {
                                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InconsistentFieldAccess, inst,
                                    DiagnosticSeverity.Error, "Field static attribute does not match the access opcode."));
                            }
                            if (fd.IsInitOnly && (fd.DeclaringType != _method.DeclaringType || (!CecilHelper.IsInitSetter(_method) && _method.Name != ".ctor") || !_method.IsSpecialName))
                            {
                                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InitOnlyFieldAccess,
                                    inst, DiagnosticSeverity.Error, "Cannot modify initonly field out of .ctor and init set_property."));
                            }
                        }
                        break;
                    }
                case Code.Stsfld: //静态/实例fld判别在前面
                case Code.Ldsflda:
                    {
                        if (inst.Operand is FieldReference fieldRef && fieldRef.Resolve() is FieldDefinition fd )
                        {
                            if (!fd.Attributes.HasFlag(FieldAttributes.Static))
                            {
                                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InconsistentFieldAccess, inst,
                                    DiagnosticSeverity.Error, "Field static attribute does not match the access opcode."));
                            }
                            if (fd.IsInitOnly && (fd.DeclaringType != _method.DeclaringType || _method.Name != ".cctor" || !_method.IsSpecialName))
                            {
                                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InitOnlyFieldAccess,
                                    inst, DiagnosticSeverity.Error, "Cannot modify initonly static field out of .cctor."));
                            }
                        }

                        
                        break;
                    }
                case Code.Newobj:
                    {
                        if (inst.Operand is MethodReference mf && mf.Name != ".ctor")
                        {
                            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.CtorExcepted, inst,
                                    DiagnosticSeverity.Error, "Newobj opcode expect constructor."));
                        }
                        break;
                    }
                case Code.Ldvirtftn:
                    {
                        if (inst.Operand is MethodReference mf && mf.Resolve() is { } md && md.IsStatic)
                        {
                            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.LdvirtftnOnStatic, inst,
                                    DiagnosticSeverity.Error, "Ldvirtftn opcode expect instance-method"));
                        }
                        break;
                    }
            }
        }

        if (prefix != null)
        {
            ReportDiagnostic(CFGDiagnostic.PrefixInvalid(CFGExceptionType.InvalidOpCode, 
                _method.Body.Instructions.Last(), prefix.Value));
        }
        ThrowIfNeedAbort(AbortStrategy.AbortNextStep);
    }

    private void VerifyBranchTargets(Instruction inst, int instructionIndex, int[] instEhFrames)
    {
        if (inst.OpCode.FlowControl is not (FlowControl.Branch or FlowControl.Cond_Branch))
            return;

        if (!CecilHelper.TryResolveOperandTargets(inst.Operand, out var targets, out _))
            return;

        foreach (var targetInst in targets)
        {
            if (targetInst.Previous is not null && targetInst.Previous.OpCode.OpCodeType == OpCodeType.Prefix)
            {
                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InvalidBrTarget, inst));
            }

            if (!_instDictionary.TryGetValue(targetInst, out var targetIndex))
            {
                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InvalidBrTarget, inst));
                continue;
            }

            if (inst.OpCode.Code is Code.Leave or Code.Leave_S)
            {
                VerifyLeaveTarget(inst, instructionIndex, targetIndex, instEhFrames);
            }
            else
            {
                VerifyBranchAcrossExceptionRegion(inst, instructionIndex, targetIndex, instEhFrames);
            }
        }
    }

    private void VerifyLeaveTarget(Instruction inst, int instructionIndex, int targetIndex, int[] instEhFrames)
    {
        var currentRegion = _regionFrames[instEhFrames[instructionIndex]].Region;
        var targetRegion = _regionFrames[instEhFrames[targetIndex]].Region;
        var relation = GetEHRelation(currentRegion, targetRegion);

        if (currentRegion.Kind == RegionKind.Normal)
        {
            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.LeaveTargetInvalid, inst));
            return;
        }

        if (currentRegion.Kind is RegionKind.Handler &&
            currentRegion.Clause.ExceptionHandler.HandlerType is ExceptionHandlerType.Fault or ExceptionHandlerType.Finally &&
            relation != EHRelation.Same)
        {
            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.LeaveTargetInvalid, inst));
            return;
        }

        if (currentRegion.Kind is RegionKind.Filter &&
            currentRegion.Clause.ExceptionHandler.HandlerType is ExceptionHandlerType.Filter or ExceptionHandlerType.Fault or ExceptionHandlerType.Finally &&
            relation != EHRelation.Same)
        {
            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.LeaveTargetInvalid, inst));
            return;
        }

        if (currentRegion.Clause == targetRegion.Clause && targetRegion.Kind == RegionKind.Try)
        {
            // Handler/Try 的 leave 允许跳回同一 EH clause 的 try 区域。
            relation = EHRelation.Same;
        }

        switch (relation)
        {
            case EHRelation.Same:
                break;
            case EHRelation.Assciated:
                if (targetRegion.Kind is not RegionKind.Try and not RegionKind.Normal || targetRegion.Start != targetIndex)
                    ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.LeaveTargetInvalid, inst));
                break;
            case EHRelation.Enclosing:
                if (targetRegion.Kind is not RegionKind.Try and not RegionKind.Normal &&
                    targetRegion != currentRegion.ParentRegion)
                {
                    ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.LeaveTargetInvalid, inst));
                }
                break;
            case EHRelation.Disjoint:
                var tmp2Region = currentRegion;
                for (; tmp2Region.ParentRegion != null; tmp2Region = tmp2Region.ParentRegion)
                {
                    var tmpRegion = targetRegion;
                    for (; tmpRegion.ParentRegion != tmp2Region.ParentRegion && tmpRegion.ParentRegion != null;
                         tmpRegion = tmpRegion.ParentRegion)
                    {
                        if (tmpRegion.ParentRegion.Start != tmpRegion.Start)
                            break;
                    }

                    if (tmpRegion.ParentRegion == tmp2Region.ParentRegion)
                        break;
                }

                if (tmp2Region.ParentRegion == null)
                {
                    ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.LeaveTargetInvalid, inst));
                }
                else if (targetRegion.Kind is not RegionKind.Try || targetRegion.Start != targetIndex)
                {
                    ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.LeaveTargetInvalid, inst));
                }
                break;
        }
    }

    private void VerifyBranchAcrossExceptionRegion(Instruction inst, int instructionIndex, int targetIndex,
        int[] instEhFrames)
    {
        var currentRegion = _regionFrames[instEhFrames[instructionIndex]].Region;
        var targetRegion = _regionFrames[instEhFrames[targetIndex]].Region;
        if (targetRegion == currentRegion)
            return;

        if (targetRegion.Kind != RegionKind.Try)
        {
            ReportInvalidBranchAcrossEhRegion(inst, currentRegion, targetRegion, includeBranchIntoTry: false);
            return;
        }

        var relation = GetEHRelation(currentRegion, targetRegion);
        if (relation is not EHRelation.Assciated and not EHRelation.Same)
        {
            ReportInvalidBranchAcrossEhRegion(inst, currentRegion, targetRegion, includeBranchIntoTry: false);
            return;
        }

        var isFirstInstruction = true;
        var region = targetRegion;
        for (; region != currentRegion && region != null; region = region.ParentRegion)
        {
            if (region.Start != targetIndex)
                isFirstInstruction = false;
        }

        if (!isFirstInstruction)
        {
            ReportInvalidBranchAcrossEhRegion(inst, currentRegion, targetRegion, includeBranchIntoTry: true);
        }

        if (region == null)
        {
            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.UnExpected, inst));
        }
    }

    private void VerifyGenericConstraints(Instruction inst)
    {
        switch (inst.OpCode.Code)
        {
            case Code.Call:
            case Code.Callvirt:
            case Code.Newobj:
                if (inst.Operand is MethodReference calledMethod)
                    VerifyMethodConstraints(inst, calledMethod, "called method");
                break;

            case Code.Ldftn:
            case Code.Ldvirtftn:
                if (inst.Operand is MethodReference functionPointerMethod)
                    VerifyMethodConstraints(inst, functionPointerMethod, "function pointer method");
                break;

            case Code.Box:
                if (inst.Operand is TypeReference boxType)
                    VerifyTypeConstraints(inst, boxType, null, "box type operand");
                break;

            case Code.Stfld:
            case Code.Stsfld:
                if (inst.Operand is FieldReference field)
                    VerifyTypeConstraints(inst, field.DeclaringType, field, "field parent type");
                break;
        }
    }

    private void VerifyMethodConstraints(Instruction inst, MethodReference method, string target)
    {
        if (method.CheckConstraints())
            return;

        ReportDiagnostic(CFGDiagnostic.TypeConstraintViolation(inst, method.DeclaringType, method, target));
    }

    private void VerifyTypeConstraints(Instruction inst, TypeReference type, MemberReference? member, string target)
    {
        if (type.CheckConstraints())
            return;

        ReportDiagnostic(CFGDiagnostic.TypeConstraintViolation(inst, type, member, target));
    }

    private bool TryResolveBranchTargets(Instruction inst, out Instruction[] targets)
    {
        if (CecilHelper.TryResolveOperandTargets(inst.Operand, out targets, out var error))
            return true;

        ReportDiagnostic(CFGDiagnostic.InvalidOperand(error.Expected, error.Current, inst,
            message: error.Message));
        return false;
    }

    private void LightControlFlowPass()
    {
        HashSet<BasicBlock> usedBlocks = new();
        foreach (var entry in _entryblocks)
        {
            LightAnalyzeBlocksControlFlow(entry, usedBlocks);
        }
        ThrowIfNeedAbort(AbortStrategy.AbortNextBlock);
     
    }

    private void LightAnalyzeBlocksControlFlow(BasicBlock entryBlock, HashSet<BasicBlock> usedBlocks)
    {

        if (entryBlock.Kind is RegionKind.Filter ||
            entryBlock.Kind is RegionKind.Handler) 
            //对于filter和handler插入异常对象
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
                    if (inst.OpCode.Code == Code.Localloc)
                    {
                        if (stackHeight > 1)
                        {
                            ReportDiagnostic(CFGDiagnostic.InstructionUnverifiable(CFGExceptionType.Unverifiable_LocallocStackNotEmpty, inst,
                                    DiagnosticSeverity.Error), AbortStrategy.NoAbort);
                        }
                        else
                        {
                            ReportDiagnostic(CFGDiagnostic.InstructionUnverifiable(CFGExceptionType.Unverifiable, inst), AbortStrategy.NoAbort);
                        }
                    }
                    var pop = -1;
                    try
                    {
                        pop = inst.PopCount(_method);
                        if (inst.OpCode.Code is Code.Ret)
                        {
                            pop = _method.ReturnCount();
                        }
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
                        if (inst.Next is null)
                        {
                            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InvalidMethodFallThrough, inst));
                            break;
                        }
                        if (_blockMap.TryGetValue(inst.Next, out var fallThrougthBlock))
                        {
                            if (block.Kind is RegionKind.Handler or RegionKind.Filter && fallThrougthBlock != block)
                            {
                                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InvalidFallThrough, inst));
                                endBlock = true;
                                break;
                            }
                            switch (fallThrougthBlock.Kind)
                            {
                                case RegionKind.Handler or RegionKind.Filter when block.Region != fallThrougthBlock.Region:
                                    ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InvalidFallThrough, inst));
                                    endBlock = true;
                                    break;
                                case RegionKind.Try:
                                    AnalyzeCF_AddNextBlock(block, fallThrougthBlock, bfsBlocks, stackHeight, initLocals, usedBlocks);
                                    endBlock = true;
                                    break;
                            }
                        }
                        break;
                    case FlowControl.Branch:
                        if (!TryResolveBranchTargets(inst, out var branchTargets))
                            break;

                        foreach (var target in branchTargets)
                        {
                            if (!_blockMap.TryGetValue(target, out var targetBlock))
                                continue;

                            if (target.Previous != null && 
                                target.Previous.OpCode.FlowControl == FlowControl.Branch &&
                                _instDictionary[target] < i)
                            {
                                if (stackHeight != 0)
                                {
                                    ReportDiagnostic(CFGDiagnostic.IncompatibleMerge(CFGExceptionType.InvalidBackwardBranch,
                                            block, targetBlock, DiagnosticSeverity.Warning, "Backward branch without predecessor instructions must be 0 stack height")); //这个CLR并不强制执行
                                }
                            }
                            AnalyzeCF_AddNextBlock(block, targetBlock, bfsBlocks, stackHeight, initLocals, usedBlocks);
                        }
                        endBlock = true;
                        break;
                    case FlowControl.Cond_Branch:
                        if (!TryResolveBranchTargets(inst, out var condBranchTargets))
                            break;

                        foreach (var target in condBranchTargets)
                        {
                            if (!_blockMap.TryGetValue(target, out var targetBlock))
                                continue;

                            if (target.Previous != null &&
                                  target.Previous.OpCode.FlowControl == FlowControl.Branch &&
                                  _instDictionary[target] < i)
                            {
                                if (stackHeight != 0)
                                {
                                    ReportDiagnostic(CFGDiagnostic.IncompatibleMerge(CFGExceptionType.InvalidBackwardBranch,
                                            block, targetBlock, DiagnosticSeverity.Warning, "Backward branch without predecessor instructions must be 0 stack height")); //这个CLR并不强制执行
                                }
                            }
                            AnalyzeCF_AddNextBlock(block, targetBlock, bfsBlocks, stackHeight, initLocals, usedBlocks);
                        }
                        AnalyzeCF_AddNextBlock(block, _blockMap[inst.Next], bfsBlocks, stackHeight, initLocals, usedBlocks);
                        endBlock = true;
                        break;
                    case FlowControl.Throw:
                        endBlock = true;
                        ValidateExitStackHeight(inst, stackHeight, 1);
                        break;
                    case FlowControl.Return:
                        endBlock = true;
                        if (inst.OpCode.Code != Code.Endfinally)
                        {
                            ValidateExitStackHeight(inst, stackHeight, 0);
                        }
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
        
        foreach (var block in _entryblocks)
        {
            if (usedBlocks.Contains(block)) continue;
            AnalyzeBlocksControlFlow(block, usedBlocks, buffer, funcBuffer);
        }
    }

    private void AnalyzeBlocksControlFlow(BasicBlock entryBlock, 
        HashSet<BasicBlock> usedBlocks,  StackType[] buffer, StackType[] funcBuffer)
    {
        var localStack = new ListStack<StackType>(_method.Body.MaxStackSize);
        var entryStackNode = _root;
        var initLocals = VerifyLocalInit ? new BitArray(_method.Body.Variables.Count) : null;
        entryBlock.initLocals = VerifyLocalInit ? new BitArray(_method.Body.Variables.Count) : null;
        
        if (entryBlock.Kind is RegionKind.Filter) //对于filter和handler插入异常对象
        {
            AnalyzeCF_EvalStackPush(localStack, ref entryStackNode, StackType.Create(_method.Module.TypeSystem.Object),
                entryBlock.Leader);
        }
        else if (entryBlock.Kind is RegionKind.Handler)
        {
            AnalyzeCF_EvalStackPush(localStack, ref entryStackNode, entryBlock.Region.Clause.ExceptionHandler.CatchType ?? _method.Module.TypeSystem.Object,
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
            Code[] prefixBuffer = new Code[2];
            for (var inst = leader; inst != null; inst = inst.Next)
            {
                var ts = _method.Module.TypeSystem;
                var popBehaviour = inst.OpCode.StackBehaviourPop;
                if(popBehaviour == StackBehaviour.PopAll)
                {
                    localStack.Clear();
                    node = _root;
                }
                var pop = popBehaviour.PopCount() switch
                {
                    -1 => 0,
                    0xFF => 0,
                    var tmpPop => tmpPop
                };
                
                for (int j = 0; j< pop; j++)
                {
                    buffer[j] = AnalyzeCF_EvalStackPop(localStack, ref node, inst);
                }

                switch (inst.OpCode.StackBehaviourPop)
                {
                    case StackBehaviour.Pop0:
                        retType = VerifyPop0(inst, buffer[0], prefixBuffer);
                        break;

                    case StackBehaviour.Pop1:
                        retType = VerifyPop1(inst, buffer[0], prefixBuffer);
                        break;

                    case StackBehaviour.Popi:
                        retType = VerifyPopi(inst, buffer[0], prefixBuffer, localStack.Count + node.Depth + pop);
                        break;

                    case StackBehaviour.Popref:
                        retType = VerifyPopref(inst, buffer[0], prefixBuffer);
                        break;

                    case StackBehaviour.Popi_popi:
                    case StackBehaviour.Popi_popi8:
                    case StackBehaviour.Popi_popr4:
                    case StackBehaviour.Popi_popr8:
                        VerifyPopi_Pop1(inst, buffer);
                        break;
                    case StackBehaviour.Popref_popi:
                        retType = VerifyPopref_popi(inst, buffer, prefixBuffer);
                        break;

                    case StackBehaviour.Popi_pop1:
                        retType = VerifyPopi_pop1(inst, buffer, prefixBuffer);
                        break;

                    case StackBehaviour.Popref_pop1:
                        retType = VerifyPopref_pop1(inst, buffer, prefixBuffer);
                        break;

                    case StackBehaviour.Pop1_pop1:
                        retType = VerifyPop1_pop1(inst, buffer, prefixBuffer);
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
                        VerifyPop3(inst, buffer, prefixBuffer);
                        break;

                    case StackBehaviour.Varpop:
                        if (inst.OpCode.Code is Code.Ret)
                        {
                            var returnType = _method.ReturnType;
                            if (_method.IsRuntimeAsync() && returnType.Resolve() is { } td)
                            {
                                var sig = CecilTypeSystem.TypeSig.Create(td);
                                if (sig == CecilTypeSystem.TypeSig.SystemThreading.Task ||
                                    sig == CecilTypeSystem.TypeSig.SystemThreading.ValueTask)
                                {
                                    returnType = _method.Module.TypeSystem.Void;
                                }
                                else if (sig == CecilTypeSystem.TypeSig.SystemThreading.TaskT ||
                                    sig == CecilTypeSystem.TypeSig.SystemThreading.ValueTaskT)
                                {
                                    if (returnType is GenericInstanceType gType)
                                        returnType = gType.GenericArguments[0];
                                }
                                else
                                {
                                    ReportStackTypeMismatch("any Task/ValueTask types", returnType, inst);
                                }
                            }
                            if (!returnType.IsVoid())
                            {
                                var type = AnalyzeCF_EvalStackPop(localStack, ref node, inst);
                                if (!type.StackValueEqualsTo(returnType))
                                {
                                    ReportStackTypeMismatch(returnType, type, inst);
                                }
                                else
                                {
                                    if (returnType.IsByReference && !type.Flags.HasFlag(StackTypeFlags.PermanentHome))
                                    {
                                        ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.ReturnTempPtr, inst));
                                    }
                                }
                            }
                            break;

                        }
                        retType = VerifyVarPop(inst, ref funcBuffer, out var len, out var hasThis);
                        StackType lastTwo = StackType.Invalid;
                        for (int j = len - 1; j > 0; j--)
                        {
                            var type = AnalyzeCF_EvalStackPop(localStack, ref node, inst);
                            VerifyType(type, funcBuffer[j], inst);
                            if (j == 1)
                            {
                                lastTwo = type;
                            }
                        }
                        if (len != 0)
                        {
                            var type = AnalyzeCF_EvalStackPop(localStack, ref node, inst);

                            if (inst.Operand is MethodReference mf && mf.Resolve() is { } md)
                            {
                                if (!_initThis
                                    && type.Flags.HasFlag(StackTypeFlags.ThisPtr)
                                    && (md.DeclaringType.IsSameWith(_method.DeclaringType.BaseType) || md.DeclaringType.IsSameWith(_method.DeclaringType))
                                    && md.IsSpecialName
                                    && hasThis
                                    && md.Name == ".ctor")
                                {
                                    _initThis = true;
                                }

                                if (md.IsVirtual && (!type.Flags.HasFlag(StackTypeFlags.ThisPtr) || _modifiesThis) && inst.OpCode.Code == Code.Call)
                                {
                                    ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.Unverifiable_ThisMismatched, inst, DiagnosticSeverity.Warning,
                                        "The 'this' parameter to the call must be the calling method's 'this' parameter."));
                                }

                                if (hasThis)
                                {
                                    VerifyFamilyInstanceMethodAccess(inst, type, md);
                                }

                                if (inst.OpCode.Code == Code.Newobj && md.DeclaringType.IsAnyDelegate() && lastTwo.Type is FunctionPointerType fnptr)
                                {
                                    var invoke = md.DeclaringType.Methods.FirstOrDefault(m =>
                                        m.Name == "Invoke" &&
                                        !m.IsStatic);
                                    if (invoke != null)
                                    {
                                        VerifyDelegateCtorSig(fnptr, invoke, type, inst,
                                            mf.DeclaringType as GenericInstanceType);
                                    }
                                }
                            }

                            if (hasThis && type.Flags.HasFlag(StackTypeFlags.ReadOnly)
                                && type.IsByRef && funcBuffer[0].IsByRef) //针对Readonly & 的this调用特殊处理
                            {
                                VerifyType(type.RefToValue(), funcBuffer[0].RefToValue(), inst);
                            }
                            else
                            {
                                VerifyType(type, funcBuffer[0], inst);
                            }
                            
                        }
                        break;

                    case StackBehaviour.PopAll:
                        break;
                    default:
                        ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.UnExpected, inst));
                        break;
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
                    case StackBehaviour.Pushi:
                    case StackBehaviour.Pushi8:
                    case StackBehaviour.Pushr4:
                    case StackBehaviour.Pushr8:
                    case StackBehaviour.Pushref:
                        AnalyzeCF_EvalStackPush(localStack, ref node, retType, inst);
                        break;
                    default:
                        ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.UnExpected, inst));
                        break;
                }
                if (VerifyLocalInit)
                {
                    AnalyzeCF_CalcInitLocal(inst, initLocals!);
                }

                bool endBlock = false;
                switch (inst.OpCode.FlowControl)
                {
                    case FlowControl.Next:
                        if (inst.Next is null)
                        {
                            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InvalidMethodFallThrough, inst));
                            break;
                        }
                        if (_blockMap.TryGetValue(inst.Next, out var fallThrougthBlock))
                        {
                            if (block.Kind is RegionKind.Handler or RegionKind.Filter && fallThrougthBlock != block)
                            {
                                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InvalidFallThrough, inst));
                                endBlock = true;
                                break;
                            }
                            switch (fallThrougthBlock.Kind)
                            {
                                case RegionKind.Handler or RegionKind.Filter when block.Region != fallThrougthBlock.Region:
                                    ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.InvalidFallThrough, inst));
                                    endBlock = true;
                                    break;
                                case RegionKind.Try:
                                    AnalyzeCF_AddNextBlock(block, fallThrougthBlock, bfsBlocks, node, initLocals, usedBlocks);
                                    endBlock = true;
                                    break;
                            }
                        }
                        break;
                    case FlowControl.Branch:
                        AnalyzeCF_AppendStack(localStack, ref node);
                        if (!TryResolveBranchTargets(inst, out var branchTargets))
                            break;

                        foreach (var target in branchTargets)
                        {
                            if (!_blockMap.TryGetValue(target, out var targetBlock))
                                continue;

                            if (target.Previous != null &&
                                   target.Previous.OpCode.FlowControl == FlowControl.Branch &&
                                   _instDictionary[target] < i)
                            {
                                if (node.Depth != 0)
                                {
                                    ReportDiagnostic(CFGDiagnostic.IncompatibleMerge(CFGExceptionType.InvalidBackwardBranch,
                                            block, targetBlock, DiagnosticSeverity.Warning, "Backward branch without predecessor instructions must be 0 stack height"));
                                }
                            }
                            AnalyzeCF_AddNextBlock(block, targetBlock, bfsBlocks, node, initLocals, usedBlocks);
                        }
                        endBlock = true;
                        break;
                    case FlowControl.Cond_Branch:
                        AnalyzeCF_AppendStack(localStack, ref node);
                        if (!TryResolveBranchTargets(inst, out var condBranchTargets))
                            break;

                        foreach (var target in condBranchTargets)
                        {
                            if (!_blockMap.TryGetValue(target, out var targetBlock))
                                continue;

                            if (target.Previous != null &&
                                target.Previous.OpCode.FlowControl == FlowControl.Branch &&
                                _instDictionary[target] < i)
                            {
                                if (node.Depth != 0)
                                {
                                    ReportDiagnostic(CFGDiagnostic.IncompatibleMerge(CFGExceptionType.InvalidBackwardBranch,
                                            block, targetBlock, DiagnosticSeverity.Warning, "Backward branch without predecessor instructions must be 0 stack height"));
                                }
                            }
                            AnalyzeCF_AddNextBlock(block, targetBlock, bfsBlocks, node, initLocals, usedBlocks);
                        }
                        var next = _blockMap[inst.Next];
                        AnalyzeCF_AddNextBlock(block, next, bfsBlocks, node, initLocals, usedBlocks);
                        endBlock = true;    
                        break;
                    case FlowControl.Throw:
                        endBlock = true;
                        ValidateExitStackHeight(inst, localStack.Count + node.Depth, 1);
      
                        break;
                    case FlowControl.Return:
                        endBlock = true;
                        if (inst.OpCode.Code != Code.Endfinally)
                        {
                            ValidateExitStackHeight(inst, localStack.Count + node.Depth, 0);
                            if (!_initThis)
                            {
                                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.UninitThisReturn, inst));
                            }
                        }
                        break;
                }
                if (endBlock)
                    break;
                prefixBuffer[1] = prefixBuffer[0];
                prefixBuffer[0] = inst.OpCode.OpCodeType == OpCodeType.Prefix ? inst.OpCode.Code : default;
                ThrowIfNeedAbort(AbortStrategy.AbortNextStep);
            }
            ThrowIfNeedAbort(AbortStrategy.AbortNextBlock);
        }
    }

    private void ValidateExitStackHeight(Instruction inst, int stackHeight, int expectHeight)
    {
        if (stackHeight == expectHeight)
            return;

        ReportDiagnostic(CFGDiagnostic.InvalidExitStackHeight(inst, expectHeight, stackHeight), AbortStrategy.NoAbort);
    }

    //校验local是否被初始化，进入该函数需确保VerifyInitLocal为true
    private void AnalyzeCF_CalcInitLocal(Instruction inst, BitArray array)
    {
        if (CecilInstructionHelpers.IsStoreLocal(inst.OpCode.Code) && TryGetVariableIndex(inst, out var stlocIndex))
        {
            array[stlocIndex] = true;
        }

        if ((CecilInstructionHelpers.IsLoadLocal(inst.OpCode.Code) || CecilInstructionHelpers.IsLoadLocalAddress(inst.OpCode.Code)) &&
            TryGetVariableIndex(inst, out var ldlocIndex) &&
            !array[ldlocIndex])
        {
            //Ldlocda进行最保守估计，不追踪
            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.UninitializedLocal, inst,
                CecilInstructionHelpers.IsLoadLocal(inst.OpCode.Code) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning));
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
                    from, to,
                    expectedDepth: to.EntryStackDepth,
                    currentDepth: depth));
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
            var expectedEntryNode = lastNode!;
            var currentEntryNode = curNode;
            if (curNode.Depth != lastNode!.Depth) //启用检测堆栈类型则EntryNode一定不为null
            {
                //堆栈合并不平衡错误
                ReportDiagnostic(CFGDiagnostic.IncompatibleMerge(CFGExceptionType.IncompatibleMergeDepth, 
                    from, to,
                    expectedDepth: expectedEntryNode.Depth,
                    currentDepth: currentEntryNode.Depth,
                    expectedStack: SnapshotEvalStack(expectedEntryNode),
                    currentStack: SnapshotEvalStack(currentEntryNode)));
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
                            from, to,
                            expectedDepth: expectedEntryNode.Depth,
                            currentDepth: currentEntryNode.Depth,
                            expectedStack: SnapshotEvalStack(expectedEntryNode),
                            currentStack: SnapshotEvalStack(currentEntryNode)));
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
            }
           
        }
        else
        {
            if (VerifyLocalInit)
            {
                to.initLocals = new BitArray(currentInitLocals!);
            }
            bfsBlocks.Add((to, currentNode));
        }
    }

    private static IReadOnlyList<string> SnapshotEvalStack(EvalStackNode? node)
    {
        if (node == null || node.Depth == 0)
            return [];

        var stack = new List<string>(node.Depth);
        for (var current = node; current is { Depth: > 0 }; current = current.Parent)
        {
            stack.Add(current.Type.ToString());
        }

        stack.Reverse();
        return stack;
    }

    private StackType AnalyzeCF_EvalStackPop(ListStack<StackType> localStack,
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
        var result = node.Type;
        node = node.Parent;
        return result;
    }

    private void AnalyzeCF_EvalStackPush(ListStack<StackType> localStack,
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

    private void AnalyzeCF_AppendStack(ListStack<StackType> localStack, 
        ref EvalStackNode node)
    {
        foreach (var child in localStack.BottomToTop())
        {
            var prev = node;
            node = node.AppendChild(child);
            _nodeIntern.Add((child, prev), node);
        }
        localStack.Clear();
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
