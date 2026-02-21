using System;
using System.Collections.Generic;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace MonoWeaver.CFG;

public enum CFGExceptionType
{
    None = 0,
    
    //Mono cecil相关错误
    InstructionNull,
    ExceptionHandlerInvalid,
    InvalidOpCode,
    InvalidOperand,
    
    EhRegionOverlap,
    EhRegionNonTryDuplication,
    EhNestedInFilter,
    TryAndHandlerNotInSameEnclosingRegion,
    InvalidEhTableOrdering,
    
    //CFG相关
    InvalidInstruction,
    TypeMismatch,
    InconsistentFieldAccess,
    StackUnderflow,
    StackOverflow,
    InvalidFallThrough,
    UninitializedLocal,
    IncompatibleMergeTypes,
    IncompatibleMergeDepth,
    OutOfRange,
    
    ResolveFailed,
    UnExpected,
}

public enum DiagnosticSeverity
{
    None = 0,
    Warning,
    Error,
    Fatal,
    Internal,
}


public interface ICFGContext { }

public sealed record InstContext(Instruction Instruction) : ICFGContext;
public sealed record TypeMismatchContext(Instruction Instruction, TypeReference Expect, TypeReference? Current) : ICFGContext;

public sealed record InvalidOperandContext(Instruction Instruction, Type Expect, Type Current) : ICFGContext;
public sealed record OperandOutOfRangeContext(Instruction Instruction) : ICFGContext;
public sealed record NullInstContext(int Index) : ICFGContext;
public sealed record HandlerContext(ExceptionHandler Handler) : ICFGContext;

public sealed record MergeBlockContext(ILCfg.BasicBlock from, ILCfg.BasicBlock to) : ICFGContext;
public sealed record ResolveContext(MemberReference reference) : ICFGContext;
public sealed record EhRegionContext(ExceptionBlock.Region Region1, ExceptionBlock.Region Region2) : ICFGContext;

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
                  .Append(" expect=").Append(tm.Expect.FullName)
                  .Append(" current=").Append(tm.Current?.FullName ?? "<null>");
                break;

            case OperandOutOfRangeContext oc:
                sb.Append(" @ IL_").Append(oc.Instruction.Offset.ToString("X4"));
                break;

            case NullInstContext ni:
                sb.Append(" index=").Append(ni.Index);
                break;

            case HandlerContext hc:
                sb.Append(" handler=").Append(hc.Handler?.HandlerType.ToString());
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
            new TypeMismatchContext(inst, expect, current));
    
    public static CFGDiagnostic IncompatibleMerge(ILCfg.BasicBlock from, ILCfg.BasicBlock to,
        DiagnosticSeverity severity = DiagnosticSeverity.Fatal, string? message = null)
        => new(CFGExceptionType.IncompatibleMergeTypes, severity,
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
    
    public static CFGDiagnostic PrefixInvalid(CFGExceptionType exceptionType, Instruction inst, Code prefix,
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => new(exceptionType, severity,
            message ?? $"Invalid prefix opcode: {prefix}, target opcode: {inst.OpCode.Code}",
            new InstContext(inst));



    public static CFGDiagnostic NullInstruction(int index,
        DiagnosticSeverity severity = DiagnosticSeverity.Fatal, string? message = null)
        => new(CFGExceptionType.InstructionNull, severity,
            message ?? "Instruction is null",
            new NullInstContext(index));
    
    public static CFGDiagnostic EhRegionInvalid(CFGExceptionType type, ExceptionBlock.Region region1, ExceptionBlock.Region region2, 
        DiagnosticSeverity severity = DiagnosticSeverity.Fatal, string? message = null)
        => new(type, severity,
            message ?? "EHRegion is Invalid",
            new EhRegionContext(region1, region2));
    
    public static CFGDiagnostic EhHandlerInvalid(ExceptionHandler handler, 
        DiagnosticSeverity severity = DiagnosticSeverity.Fatal, string? message = null)
        => new(CFGExceptionType.ExceptionHandlerInvalid, severity,
            message ?? "ExceptionHandler is Invalid",
            new HandlerContext(handler));
    
    public static CFGDiagnostic ResolveFailed(MemberReference memberReference, 
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => new(CFGExceptionType.ExceptionHandlerInvalid, severity,
            message ?? "MemberReference cannot be resolved to MemberDefinition",
            new ResolveContext(memberReference));
}


public partial class ILCfg
{
    private readonly List<CFGDiagnostic> _diagnostics = new();

    private int _maxErrorCount = 10;
    private void ReportDiagnostic(CFGDiagnostic diag)
    {
        _diagnostics.Add(diag);
    }
}