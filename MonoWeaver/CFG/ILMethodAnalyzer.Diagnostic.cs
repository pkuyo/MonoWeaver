using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
    TypeConstraintViolation, // 类型实参不满足泛型约束。
    InconsistentFieldAccess, // 字段访问方式不一致。
    InitOnlyFieldAccess, // 只读字段访问不一致,
    StackUnderflow, // 求值栈下溢。
    StackOverflow, // 求值栈超过 maxstack。
    InvalidExitStackHeight, // 控制流出口栈高度错误。
    InvalidFallThrough, // 非法顺序落入下一块。
    InvalidMethodFallThrough, // 非法顺序离开函数。
    UninitializedLocal, // 读取未初始化局部变量。
    IncompatibleMergeTypes, // 控制流合并点栈类型不兼容。
    InvalidBackwardBranch, // 后向分支栈状态不合法。
    IncompatibleMergeDepth, // 控制流合并点栈深度不一致。
    InvalidBrTarget, // 分支目标无效。
    BranchOutOfTry, // 分支离开 try 区域。
    BranchOutOfHandler, // 分支离开 catch/handler 区域。
    BranchOutOfFilter, // 分支离开 filter 区域。
    BranchOutOfFinally, // 分支离开 finally 区域。
    BranchOutOfFault, // 分支离开 fault 区域。
    BranchIntoTry, // 分支进入 try 区域。
    BranchIntoHandler, // 分支进入 handler 区域。
    BranchIntoFilter, // 分支进入 filter 区域。
    LeaveTargetInvalid, // leave 目标无效。
    OutOfRange, // 索引或目标超出范围。
    UninitializedThis, // 读取未初始化 this。
    UninitThisReturn, // 方法返回时 this 未初始化。
    ReturnTempPtr, // 返回临时指针
    ResolveFailed, // 元数据引用解析失败。
    LdvirtftnOnStatic, //对Static函数使用Ldvirtftn

    UnExpected, // 未预期的验证状态。
    CecilLoadFaulted, // Cecil读取失败

    //不可验证
    Unverifiable, // IL 不可验证但可执行。
    Unverifiable_LocallocStackNotEmpty, // localloc 时求值栈非空。
    Unverifiable_ThisMismatched, // 错误的this传参到call调用非virtual函数
    TypeAccess, // 类型访问规则错误。
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

public sealed record TypeConstraintContext(
    Instruction Instruction,
    TypeReference? Type,
    MemberReference? Member,
    string Target) : ICFGContext;

public sealed record AccessContext(
    Instruction Instruction,
    TypeReference LocalType,
    TypeReference? TargetType,
    MemberReference? Member,
    string Target) : ICFGContext;

public sealed record InvalidOperandContext(Instruction Instruction, Type Expect, Type Current) : ICFGContext;
public sealed record OperandOutOfRangeContext(Instruction Instruction) : ICFGContext;
public sealed record ExitStackHeightContext(Instruction Instruction, int Expect, int Current) : ICFGContext;
public sealed record NullInstContext(int Index) : ICFGContext;
public sealed record HandlerContext(ExceptionHandler Handler) : ICFGContext;

public sealed record MergeBlockContext(
    ILMethodAnalyzer.BasicBlock From,
    ILMethodAnalyzer.BasicBlock To,
    int? ExpectedDepth = null,
    int? CurrentDepth = null,
    IReadOnlyList<string>? ExpectedStack = null,
    IReadOnlyList<string>? CurrentStack = null) : ICFGContext;
public sealed record BranchRegionContext(
    Instruction Instruction,
    EHandler.Region From,
    EHandler.Region To) : ICFGContext;
public sealed record ResolveContext(MemberReference reference) : ICFGContext;
public sealed record EhRegionContext(EHandler.Region Region1, EHandler.Region Region2) : ICFGContext;

public sealed record CecilFaultContext(Exception e) : ICFGContext;

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

            case TypeConstraintContext tc:
                sb.Append(" @ IL_").Append(tc.Instruction.Offset.ToString("X4"))
                  .Append(" target=").Append(tc.Target);
                if (tc.Type != null)
                    sb.Append(" type=").Append(tc.Type.FullName);
                if (tc.Member != null)
                    sb.Append(" member=").Append(tc.Member.FullName);
                break;

            case AccessContext ac:
                sb.Append(" @ IL_").Append(ac.Instruction.Offset.ToString("X4"))
                  .Append(" target=").Append(ac.Target)
                  .Append(" local=").Append(ac.LocalType.FullName);
                if (ac.TargetType != null)
                    sb.Append(" type=").Append(ac.TargetType.FullName);
                if (ac.Member != null)
                    sb.Append(" member=").Append(ac.Member.FullName);
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
                sb.Append(" from=").Append(bc.From).Append(" to=").Append(bc.To);
                if (bc.ExpectedDepth is { } expectedDepth)
                    sb.Append(" expectedDepth=").Append(expectedDepth);
                if (bc.CurrentDepth is { } currentDepth)
                    sb.Append(" currentDepth=").Append(currentDepth);
                if (bc.ExpectedStack != null)
                    sb.Append(" expectedStack=").Append(FormatStack(bc.ExpectedStack));
                if (bc.CurrentStack != null)
                    sb.Append(" currentStack=").Append(FormatStack(bc.CurrentStack));
                break;

            case BranchRegionContext br:
                sb.Append(" @ IL_").Append(br.Instruction.Offset.ToString("X4"))
                  .Append(" from=").Append(br.From)
                  .Append(" to=").Append(br.To);
                break;

            case ResolveContext rc:
                sb.Append(" type=").Append(rc.reference.FullName);
                break;

            case EhRegionContext eh:
                sb.Append(" region1=").Append(eh.Region1)
                  .Append(" region2=").Append(eh.Region2);
                break;
            case CecilFaultContext cf:
                sb.Append(" exception:").Append(cf.e);
                break;
        }

        return sb.ToString();
    }
    
    public static CFGDiagnostic TypeMismatch(TypeReference? expect, TypeReference? current, Instruction inst,
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => new(CFGExceptionType.TypeMismatch, severity,
            message ?? $"Type mismatch: expect {expect?.FullName ?? "<null>"}, got {current?.FullName ?? "<null>"}",
            new TypeMismatchContext(inst, TypeMismatchKind.Type, expect?.FullName ?? "<null>",
                current?.FullName ?? "<null>", expect, current));

    public static CFGDiagnostic TypeConstraintViolation(Instruction inst, TypeReference? type,
        MemberReference? member = null, string target = "type",
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => new(CFGExceptionType.TypeConstraintViolation, severity,
            message ?? FormatTypeConstraintViolationMessage(type, member, target),
            new TypeConstraintContext(inst, type, member, target));

    private static string FormatTypeConstraintViolationMessage(TypeReference? type, MemberReference? member, string target)
    {
        var subject = member?.FullName ?? type?.FullName ?? "<unknown>";
        return $"Generic constraint violation on {target}: {subject}";
    }

    public static CFGDiagnostic TypeAccessViolation(Instruction inst, TypeReference localType,
        TypeReference targetType, MemberReference? member = null, string target = "type",
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => AccessViolation(CFGExceptionType.TypeAccess, inst, localType, targetType, member,
            target, severity, message);

    public static CFGDiagnostic FieldAccessViolation(Instruction inst, TypeReference localType,
        FieldReference field, TypeReference? targetType = null, string target = "field",
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => AccessViolation(CFGExceptionType.FieldAccess, inst, localType,
            targetType ?? field.DeclaringType, field, target, severity, message);

    public static CFGDiagnostic MethodAccessViolation(Instruction inst, TypeReference localType,
        MethodReference method, TypeReference? targetType = null, string target = "method",
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => AccessViolation(CFGExceptionType.MethodAccess, inst, localType,
            targetType ?? method.DeclaringType, method, target, severity, message);

    public static CFGDiagnostic AccessViolation(CFGExceptionType exceptionType, Instruction inst,
        TypeReference localType, TypeReference? targetType, MemberReference? member = null,
        string target = "member", DiagnosticSeverity severity = DiagnosticSeverity.Error,
        string? message = null)
        => new(exceptionType, severity,
            message ?? FormatAccessViolationMessage(localType, targetType, member, target),
            new AccessContext(inst, localType, targetType, member, target));

    private static string FormatAccessViolationMessage(TypeReference localType,
        TypeReference? targetType, MemberReference? member, string target)
    {
        var subject = member?.FullName ?? targetType?.FullName ?? "<unknown>";
        return $"Access violation on {target}: {subject} is not accessible from {localType.FullName}";
    }

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
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null,
        int? expectedDepth = null, int? currentDepth = null,
        IReadOnlyList<string>? expectedStack = null, IReadOnlyList<string>? currentStack = null)
        => new(exceptionType, severity,
            message ?? $"Incompatible basic block merge",
            new MergeBlockContext(from, to, expectedDepth, currentDepth, expectedStack, currentStack));

    private static string FormatStack(IReadOnlyList<string> stack)
        => stack.Count == 0 ? "[]" : "[" + string.Join(", ", stack) + "]";

    public static CFGDiagnostic BranchRegionInvalid(CFGExceptionType exceptionType, Instruction inst,
        EHandler.Region from, EHandler.Region to,
        DiagnosticSeverity severity = DiagnosticSeverity.Error, string? message = null)
        => new(exceptionType, severity,
            message ?? FormatBranchRegionInvalidMessage(exceptionType),
            new BranchRegionContext(inst, from, to));

    private static string FormatBranchRegionInvalidMessage(CFGExceptionType exceptionType)
        => exceptionType switch
        {
            CFGExceptionType.BranchOutOfTry => "Branch leaves a try region.",
            CFGExceptionType.BranchOutOfHandler => "Branch leaves an exception handler region.",
            CFGExceptionType.BranchOutOfFilter => "Branch leaves a filter region.",
            CFGExceptionType.BranchOutOfFinally => "Branch leaves a finally region.",
            CFGExceptionType.BranchOutOfFault => "Branch leaves a fault region.",
            CFGExceptionType.BranchIntoTry => "Branch target enters a try region.",
            CFGExceptionType.BranchIntoHandler => "Branch target enters an exception handler region.",
            CFGExceptionType.BranchIntoFilter => "Branch target enters a filter region.",
            _ => "Branch target crosses EH region boundaries.",
        };
    
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

    public static CFGDiagnostic CecilLoadFailed(Exception e)
        => new(CFGExceptionType.CecilLoadFaulted, DiagnosticSeverity.Fatal,
            "Mono Cecil load method body failed",
            new CecilFaultContext(e));
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

    private readonly HashSet<DiagnosticDedupKey> _reportedDiagnostics = new();

    public int MaxErrorCount { get; init; } = 10;
    
    private int _currentErrorCount;
    
    private void ReportDiagnostic(CFGDiagnostic diag, AbortStrategy abortStrategy = AbortStrategy.AbortNextStep)
    {
        if (TryCreateDedupKey(diag, out var key) && !_reportedDiagnostics.Add(key))
        {
            return;
        }

        Diagnostics.Add(diag);
        _currentErrorCount += diag.Severity is DiagnosticSeverity.Error ? 1 : 0;
        if (_currentErrorCount > MaxErrorCount || 
            diag.Severity is DiagnosticSeverity.Fatal ||
            abortStrategy == AbortStrategy.AbortImminently)
        {
            throw new CfgVerifyException(this);
        }
    }

    private static bool TryCreateDedupKey(CFGDiagnostic diag, out DiagnosticDedupKey key)
    {
        var instruction = diag.Context switch
        {
            InstContext ic => ic.Instruction,
            TypeMismatchContext tm => tm.Instruction,
            TypeConstraintContext tc => tc.Instruction,
            AccessContext ac => ac.Instruction,
            InvalidOperandContext io => io.Instruction,
            OperandOutOfRangeContext oc => oc.Instruction,
            ExitStackHeightContext esh => esh.Instruction,
            MergeBlockContext mb => mb.To.Leader,
            BranchRegionContext br => br.Instruction,
            _ => null
        };

        if (instruction == null)
        {
            key = default;
            return false;
        }

        key = new DiagnosticDedupKey(instruction, diag.Type);
        return true;
    }

    private readonly struct DiagnosticDedupKey : IEquatable<DiagnosticDedupKey>
    {
        private readonly Instruction _instruction;
        private readonly CFGExceptionType _type;

        public DiagnosticDedupKey(Instruction instruction, CFGExceptionType type)
        {
            _instruction = instruction;
            _type = type;
        }

        public bool Equals(DiagnosticDedupKey other)
        {
            return ReferenceEquals(_instruction, other._instruction) && _type == other._type;
        }

        public override bool Equals(object? obj)
        {
            return obj is DiagnosticDedupKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (RuntimeHelpers.GetHashCode(_instruction) * 397) ^ (int)_type;
            }
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
