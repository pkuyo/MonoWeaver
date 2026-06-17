using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace MonoWeaver.CFG;

public enum CFGExceptionType
{
    None = 0, // 无异常。
    
    //Mono cecil相关错误
    InstructionNull, // 指令对象为空。
    ExceptionHandlerInvalid, // 异常处理表项无效。
    InvalidOpCode, // 操作码或前缀组合无效。
    InvalidOperand, // 指令操作数类型无效。
    CtorExcepted, // 期望构造函数操作数。
    
    //EH段相关（try catch）
    EhRegionOverlap, // EH 区间发生交错重叠。
    EhRegionNonTryDuplication, // 非 try EH 区间重复。
    EhNestedInFilter, // filter 区间中出现嵌套。
    TryAndHandlerNotInSameEnclosingRegion, // try 与 handler 不在同一外层区域。
    InvalidEhTableOrdering, // EH 表顺序不合法。
    
    //CFG相关
    InvalidInstruction, // 指令语义不合法。
    TypeMismatch, // 类型不匹配。
    InconsistentFieldAccess, // 字段访问方式不一致。
    StackUnderflow, // 求值栈下溢。
    StackOverflow, // 求值栈超过 maxstack。
    InvalidExitStackHeight, // 控制流出口栈高度错误。
    InvalidFallThrough, // 非法顺序落入下一块。
    UninitializedLocal, // 读取未初始化局部变量。
    IncompatibleMergeTypes, // 控制流合并点栈类型不兼容。
    InvalidBackwardBranch, // 后向分支栈状态不合法。
    IncompatibleMergeDepth, // 控制流合并点栈深度不一致。
    InvalidBrTarget, // 分支目标无效。
    BrTargetCrossEhRegion, // 分支跨越 EH 区域边界。
    LeaveTargetInvalid, // leave 目标无效。
    OutOfRange, // 索引或目标超出范围。
    
    ResolveFailed, // 元数据引用解析失败。
    UnExpected, // 未预期的验证状态。

    //不可验证
    Unverifiable, // IL 不可验证但可执行。
    Unverifiable_LocallocStackNotEmpty, // localloc 时求值栈非空。

    FieldAccess, // 字段访问规则错误。
    MethodAccess // 方法访问规则错误。
}

public enum DiagnosticSeverity
{
    None = 0,
    Warning,
    Error,
    Fatal,
}

public enum TypeMismatchKind
{
    Type,
    StackType,
    MethodReturnType,
    MethodParameterType,
    MethodParameterCount,
}

public enum AbortStrategy : byte
{
    NoAbort = 0,
    AbortNextBlock = 1,
    AbortNextStep = 2,
    AbortImminently = 0xFF
}


public interface ICFGContext { }

public sealed record InstContext(Instruction Instruction) : ICFGContext;
public sealed record TypeMismatchContext(
    Instruction Instruction,
    TypeMismatchKind Kind,
    string Expect,
    string Current,
    TypeReference? ExpectType = null,
    TypeReference? CurrentType = null,
    int? ParameterIndex = null) : ICFGContext;

public sealed record InvalidOperandContext(Instruction Instruction, Type Expect, Type Current) : ICFGContext;
public sealed record OperandOutOfRangeContext(Instruction Instruction) : ICFGContext;
public sealed record ExitStackHeightContext(Instruction Instruction, int Expect, int Current) : ICFGContext;
public sealed record NullInstContext(int Index) : ICFGContext;
public sealed record HandlerContext(ExceptionHandler Handler) : ICFGContext;

public sealed record MergeBlockContext(ILMethodAnalyzer.BasicBlock from, ILMethodAnalyzer.BasicBlock to) : ICFGContext;
public sealed record ResolveContext(MemberReference reference) : ICFGContext;
public sealed record EhRegionContext(EHandler.Region Region1, EHandler.Region Region2) : ICFGContext;

public sealed record CFGDiagnostic(
    CFGExceptionType Type,
    DiagnosticSeverity Severity,
    string Message,
    ICFGContext? Context = null
)
{
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(Severity).Append("] ").Append(Type).Append(": ").Append(Message);

        switch (Context)
        {
            case InstContext ic:
                sb.Append(" @ IL_").Append(ic.Instruction.Offset.ToString("X4"))
                  .Append(" ").Append(ic.Instruction.OpCode);
                break;

            case TypeMismatchContext tm:
                sb.Append(" @ IL_").Append(tm.Instruction.Offset.ToString("X4"))
                  .Append(" kind=").Append(tm.Kind)
                  .Append(" expect=").Append(tm.Expect)
                  .Append(" current=").Append(tm.Current);
                if (tm.ParameterIndex is { } parameterIndex)
                    sb.Append(" parameter=").Append(parameterIndex);
                break;

            case InvalidOperandContext io:
                sb.Append(" @ IL_").Append(io.Instruction.Offset.ToString("X4"))
                  .Append(" expect=").Append(io.Expect.FullName)
                  .Append(" current=").Append(io.Current.FullName);
                break;

            case OperandOutOfRangeContext oc:
                sb.Append(" @ IL_").Append(oc.Instruction.Offset.ToString("X4"));
                break;

            case ExitStackHeightContext esh:
                sb.Append(" @ IL_").Append(esh.Instruction.Offset.ToString("X4"))
                  .Append(" expect=").Append(esh.Expect)
                  .Append(" current=").Append(esh.Current);
                break;

            case NullInstContext ni:
                sb.Append(" index=").Append(ni.Index);
                break;

            case HandlerContext hc:
                sb.Append(" handler=").Append(hc.Handler?.HandlerType.ToString());
                break;

            case MergeBlockContext bc:
                sb.Append(" from=").Append(bc.from).Append(" to=").Append(bc.to);
                break;

            case ResolveContext rc:
                sb.Append(" type=").Append(rc.reference.FullName);
                break;

            case EhRegionContext eh:
                sb.Append(" region1=").Append(eh.Region1)
                  .Append(" region2=").Append(eh.Region2);
                break;
        }

        return sb.ToString();
    }
    
    public static CFGDiagnostic TypeMismatch(TypeReference expect, TypeReference? current, Instruction inst,
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => new(CFGExceptionType.TypeMismatch, severity,
            message ?? $"Type mismatch: expect {expect.FullName}, got {current?.FullName ?? "<null>"}",
            new TypeMismatchContext(inst, TypeMismatchKind.Type, expect.FullName,
                current?.FullName ?? "<null>", expect, current));

    public static CFGDiagnostic StackTypeMismatch(string expect, string current, Instruction inst,
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => new(CFGExceptionType.TypeMismatch, severity,
            message ?? $"Stack type mismatch: expect {expect}, got {current}",
            new TypeMismatchContext(inst, TypeMismatchKind.StackType, expect, current));

    public static CFGDiagnostic MethodSignatureMismatch(TypeMismatchKind kind, MethodDefinition current, MethodDefinition expect,
        Instruction inst, int? parameterIndex = null, DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
    {
        switch (kind)
        {
            case TypeMismatchKind.MethodReturnType:
                return MethodSignatureMismatch(kind, expect.ReturnType, current.ReturnType, inst,
                    parameterIndex, severity, message);

            case TypeMismatchKind.MethodParameterType:
                if (parameterIndex is not { } index)
                    throw new ArgumentNullException(nameof(parameterIndex));
                return MethodSignatureMismatch(kind, expect.Parameters[index].ParameterType,
                    current.Parameters[index].ParameterType, inst, parameterIndex, severity, message);

            case TypeMismatchKind.MethodParameterCount:
                return MethodSignatureMismatch(kind, expect.Parameters.Count.ToString(),
                    current.Parameters.Count.ToString(), inst, parameterIndex, severity, message);

            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    public static CFGDiagnostic MethodSignatureMismatch(TypeMismatchKind kind, TypeReference expect, TypeReference current,
        Instruction inst, int? parameterIndex = null, DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => MethodSignatureMismatch(kind, expect.FullName, current.FullName, inst, parameterIndex, severity, message,
            expect, current);

    public static CFGDiagnostic MethodSignatureMismatch(TypeMismatchKind kind, string expect, string current,
        Instruction inst, int? parameterIndex = null, DiagnosticSeverity severity = DiagnosticSeverity.Error,
        string? message = null, TypeReference? expectType = null, TypeReference? currentType = null)
    {
        return new CFGDiagnostic(CFGExceptionType.TypeMismatch, severity,
            message ?? FormatMethodSignatureMismatchMessage(kind, expect, current, parameterIndex),
            new TypeMismatchContext(inst, kind, expect, current, expectType, currentType, parameterIndex));
    }

    private static string FormatMethodSignatureMismatchMessage(TypeMismatchKind kind, string expect, string current, int? parameterIndex)
        => kind switch
        {
            TypeMismatchKind.MethodReturnType =>
                $"Method signature return type mismatch: expect {expect}, got {current}",
            TypeMismatchKind.MethodParameterType =>
                $"Method signature parameter[{parameterIndex}] type mismatch: expect {expect}, got {current}",
            TypeMismatchKind.MethodParameterCount =>
                $"Method signature parameter count mismatch: expect {expect}, got {current}",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    
    public static CFGDiagnostic IncompatibleMerge(CFGExceptionType exceptionType, ILMethodAnalyzer.BasicBlock from, ILMethodAnalyzer.BasicBlock to,
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => new(exceptionType, severity,
            message ?? $"Incompatible basic block merge",
            new MergeBlockContext(from, to));
    
    public static CFGDiagnostic InvalidOperand(Type expect, Type current, Instruction inst,
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => new(CFGExceptionType.InvalidOperand, severity,
            message ?? $"Invalid Operand: expect {expect.FullName}, got {current.FullName ?? "<null>"}",
            new InvalidOperandContext(inst, expect, current));
    
    public static CFGDiagnostic InstructionInvalid(CFGExceptionType exceptionType, Instruction inst,
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => new(exceptionType, severity,
            message ?? "Instruction is invalid",
            new InstContext(inst));

    public static CFGDiagnostic InvalidExitStackHeight(Instruction inst, int expect, int current,
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => new(CFGExceptionType.InvalidExitStackHeight, severity,
            message ?? $"Invalid exit stack height: expect {expect}, got {current}",
            new ExitStackHeightContext(inst, expect, current));

    public static CFGDiagnostic InstructionUnverifiable(CFGExceptionType exceptionType, Instruction inst,
    DiagnosticSeverity severity = DiagnosticSeverity.Warning, string? message = null)
    => new(exceptionType, severity,
        message ?? "Instruction is unverifiable",
        new InstContext(inst));

    public static CFGDiagnostic PrefixInvalid(CFGExceptionType exceptionType, Instruction inst, Code prefix,
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => new(exceptionType, severity,
            message ?? $"Invalid prefix opcode: {prefix}, target opcode: {inst.OpCode.Code}",
            new InstContext(inst));



    public static CFGDiagnostic NullInstruction(int index,
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => new(CFGExceptionType.InstructionNull, severity,
            message ?? "Instruction is null",
            new NullInstContext(index));
    
    public static CFGDiagnostic EhRegionInvalid(CFGExceptionType type, EHandler.Region region1, EHandler.Region region2, 
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => new(type, severity,
            message ?? "EHRegion is Invalid",
            new EhRegionContext(region1, region2));
    
    public static CFGDiagnostic EhHandlerInvalid(ExceptionHandler handler, 
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => new(CFGExceptionType.ExceptionHandlerInvalid, severity,
            message ?? "ExceptionHandler is Invalid",
            new HandlerContext(handler));
    
    public static CFGDiagnostic ResolveFailed(MemberReference memberReference, 
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => new(CFGExceptionType.ResolveFailed, severity,
            message ?? "MemberReference cannot be resolved to MemberDefinition",
            new ResolveContext(memberReference));
}


public partial class ILMethodAnalyzer
{
    public sealed class CfgVerifyException(ILMethodAnalyzer methodAnalyzer) : Exception($"Verify Method:{methodAnalyzer._method.FullName}, " +
                                                                  $"Fatal:{methodAnalyzer.Diagnostics.Count(i => i.Severity == DiagnosticSeverity.Fatal)}, " +
                                                                  $"Error:{methodAnalyzer.Diagnostics.Count(i => i.Severity == DiagnosticSeverity.Error)}, " +
                                                                  $"Waring:{methodAnalyzer.Diagnostics.Count(i => i.Severity == DiagnosticSeverity.Warning)}")
    {
        public List<CFGDiagnostic> Diagnostics { get; } = methodAnalyzer.Diagnostics;

        public MethodDefinition Method { get; } = methodAnalyzer._method;

    }

    public List<CFGDiagnostic> Diagnostics { get; } = new();

    public int MaxErrorCount { get; init; } = 10;
    
    private int _currentErrorCount;
    
    private void ReportDiagnostic(CFGDiagnostic diag, AbortStrategy abortStrategy = AbortStrategy.AbortNextStep)
    {
        Diagnostics.Add(diag);
        _currentErrorCount += diag.Severity is DiagnosticSeverity.Error ? 1 : 0;
        if (_currentErrorCount > MaxErrorCount || 
            diag.Severity is DiagnosticSeverity.Fatal ||
            abortStrategy == AbortStrategy.AbortImminently)
        {
            throw new CfgVerifyException(this);
        }

        if ((int)abortStrategy > (int)_abortVerificationStrategy && diag.Severity != DiagnosticSeverity.Warning)
        {
            _abortVerificationStrategy = abortStrategy;
        }
    }

    public ILMethodAnalyzer ThrowIfHasErrors()
    {
        if (Diagnostics.Count > 0)
        {
            throw new CfgVerifyException(this);
        }
        return this;
    }
    
    private void ThrowIfNeedAbort(AbortStrategy abortStrategy)
    {
        if ((int)abortStrategy <= (int)_abortVerificationStrategy)
        {
            throw new CfgVerifyException(this);
        }
    }
}
