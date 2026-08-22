using System;
using System.Collections.Generic;
using Mono.Cecil.Cil;

namespace MonoWeaver.Patterns;

/// <summary>诊断条目的类别。</summary>
public enum MatchDiagnosticKind
{
    /// <summary>方法体中存在 pattern model 无法表达的 IL；覆盖这些指令的表达式不可匹配。</summary>
    UnsupportedInstruction,

    /// <summary>编译器临时变量穿透被拒绝：来源不唯一、被取地址、或存入值无法重建。</summary>
    AmbiguousLocal,

    /// <summary>LocalDefinedBy 约束未满足。</summary>
    LocalConstraintFailed,
}

/// <summary>
/// 一条匹配诊断。它不是错误：只在 match 结果不符合预期时用来解释"为什么没匹配上"。
/// </summary>
public sealed class MatchDiagnostic
{
    internal MatchDiagnostic(MatchDiagnosticKind kind, Instruction? instruction, string message)
    {
        Kind = kind;
        Instruction = instruction;
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    public MatchDiagnosticKind Kind { get; }

    /// <summary>相关指令；模型级诊断指向不可表达的指令，匹配级诊断指向失败位置。可能为 null。</summary>
    public Instruction? Instruction { get; }

    public string Message { get; }

    public override string ToString()
        => Instruction is null
            ? $"[{Kind}] {Message}"
            : $"[{Kind}] IL_{Instruction.Offset:X4}: {Message}";
}

/// <summary>去重、封顶的诊断收集器。同一 (kind, instruction, message) 只记录一次。</summary>
internal sealed class MatchDiagnosticCollector
{
    private const int MaxDiagnostics = 128;

    private readonly List<MatchDiagnostic> _diagnostics = new();
    private readonly HashSet<(MatchDiagnosticKind kind, Instruction? instruction, string message)> _seen = new();

    public IReadOnlyList<MatchDiagnostic> Diagnostics => _diagnostics;

    public void Report(MatchDiagnosticKind kind, Instruction? instruction, string message)
    {
        if (_diagnostics.Count >= MaxDiagnostics)
            return;
        if (!_seen.Add((kind, instruction, message)))
            return;
        _diagnostics.Add(new MatchDiagnostic(kind, instruction, message));
    }

    public void ReportAll(IReadOnlyList<MatchDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
            Report(diagnostic.Kind, diagnostic.Instruction, diagnostic.Message);
    }
}
