using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;

namespace MonoWeaver.Patterns;

/// <summary>调用方要求唯一匹配，但结果为空或存在歧义时抛出。</summary>
public sealed class CilPatternMatchException : Exception
{
    public CilPatternMatchException(string message) : base(message) { }
}

/// <summary>某一种强类型 match 的结果集合。</summary>
public sealed class CilMatchSet<TMatch> : IReadOnlyList<TMatch>
    where TMatch : MatchCapture
{
    private const int MaxDiagnosticsInExceptionMessage = 5;

    private readonly IReadOnlyList<TMatch> _matches;
    private readonly Func<TMatch, Instruction> _location;

    internal CilMatchSet(MethodDefinition method, ExpressionPattern pattern,
        IReadOnlyList<TMatch> matches, Func<TMatch, Instruction> location,
        IReadOnlyList<MatchDiagnostic>? diagnostics = null)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        _matches = matches ?? throw new ArgumentNullException(nameof(matches));
        _location = location ?? throw new ArgumentNullException(nameof(location));
        Diagnostics = diagnostics ?? Array.Empty<MatchDiagnostic>();
    }

    public MethodDefinition Method { get; }
    public ExpressionPattern Pattern { get; }
    public int Count => _matches.Count;
    public TMatch this[int index] => _matches[index];

    /// <summary>
    /// 本次匹配收集到的诊断：方法中不可表达的 IL、被拒绝的临时变量穿透、local 定义约束失败原因。
    /// 诊断的存在不代表匹配失败，只在结果不符合预期时用于解释原因。
    /// </summary>
    public IReadOnlyList<MatchDiagnostic> Diagnostics { get; }

    /// <summary>返回唯一结果；0 个或多个候选都视为不安全。</summary>
    public TMatch Single()
    {
        if (_matches.Count == 1)
            return _matches[0];

        var details = _matches.Count == 0
            ? "No matching expression was found."
            : $"{_matches.Count} matching expressions were found at: " +
              string.Join(", ", _matches.Select(match => $"IL_{_location(match).Offset:X4}"));
        var message = details +
            " Add surrounding expression context, an embedded fragment, or a local definition constraint.";
        if (_matches.Count == 0 && Diagnostics.Count != 0)
            message += Environment.NewLine + FormatDiagnostics(MaxDiagnosticsInExceptionMessage);
        throw new CilPatternMatchException(message);
    }

    /// <summary>把诊断格式化成适合日志输出的多行报告。</summary>
    public string ExplainFailure()
    {
        if (Diagnostics.Count == 0)
        {
            return $"No diagnostics were recorded for {Method.FullName}. " +
                   "The method model understood every instruction; check the pattern shape, constants, and overloads.";
        }

        return $"Match diagnostics for {Method.FullName} ({Count} match(es)):" +
               Environment.NewLine + FormatDiagnostics(Diagnostics.Count);
    }

    private string FormatDiagnostics(int limit)
    {
        var lines = Diagnostics.Take(limit).Select(diagnostic => "  " + diagnostic);
        var text = "Possible reasons:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
        if (Diagnostics.Count > limit)
            text += Environment.NewLine + $"  ... and {Diagnostics.Count - limit} more (see CilMatchSet.Diagnostics).";
        return text;
    }

    /// <summary>只保留起点在 <paramref name="anchor"/> 之后（IL 顺序）的匹配。</summary>
    public CilMatchSet<TMatch> After(Instruction anchor)
        => Filter(match => IndexOf(match.RangeStart) > RequireIndex(anchor));

    /// <summary>只保留起点在 <paramref name="anchor"/> 整段之后的匹配；锚点可以是之前的匹配或捕获。</summary>
    public CilMatchSet<TMatch> After(MatchCapture anchor)
        => After(RequireSameMethod(anchor).RangeEnd);

    /// <summary>只保留终点在 <paramref name="anchor"/> 之前（IL 顺序）的匹配。</summary>
    public CilMatchSet<TMatch> Before(Instruction anchor)
        => Filter(match => IndexOf(match.RangeEnd) < RequireIndex(anchor));

    /// <summary>只保留终点在 <paramref name="anchor"/> 整段之前的匹配；锚点可以是之前的匹配或捕获。</summary>
    public CilMatchSet<TMatch> Before(MatchCapture anchor)
        => Before(RequireSameMethod(anchor).RangeStart);

    /// <summary>只保留完全落在两条指令之间（不含）的匹配。</summary>
    public CilMatchSet<TMatch> Between(Instruction after, Instruction before)
        => After(after).Before(before);

    /// <summary>只保留完全落在两段匹配之间的匹配。</summary>
    public CilMatchSet<TMatch> Between(MatchCapture after, MatchCapture before)
        => After(after).Before(before);

    private CilMatchSet<TMatch> Filter(Func<TMatch, bool> keep)
        => new(Method, Pattern, _matches.Where(keep).ToArray(), _location, Diagnostics);

    //位置按 IL 顺序比较，与 ILCursor 一致；不考虑控制流先后。
    private int IndexOf(Instruction instruction)
        => Method.Body.Instructions.IndexOf(instruction);

    private int RequireIndex(Instruction anchor)
    {
        if (anchor is null)
            throw new ArgumentNullException(nameof(anchor));
        var index = IndexOf(anchor);
        if (index < 0)
        {
            throw new InvalidOperationException(
                $"The anchor instruction '{anchor}' is not in the body of {Method.FullName}. " +
                "If the method was rewritten since the anchor was obtained, match again.");
        }
        return index;
    }

    private MatchCapture RequireSameMethod(MatchCapture anchor)
    {
        if (anchor is null)
            throw new ArgumentNullException(nameof(anchor));
        if (!ReferenceEquals(anchor.Method, Method))
            throw new InvalidOperationException($"The anchor belongs to {anchor.Method.FullName}, not {Method.FullName}.");
        return anchor;
    }

    public IEnumerator<TMatch> GetEnumerator() => _matches.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>一次匹配得到的目标（根 match 或某个 pattern 对象的捕获）。根 match 不需要再转换成 capture 才能改写。</summary>
public abstract class MatchCapture
{
    private protected MatchCapture(MethodDefinition method, ExpressionPattern? source)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Source = source;
    }

    public MethodDefinition Method { get; }

    /// <summary>以哪个 pattern 对象的身份捕获；根 match 为 null。</summary>
    public ExpressionPattern? Source { get; }
    internal ILBasicBlockGraph? Graph { get; set; }

    /// <summary>此匹配覆盖的 IL 段起点，供按位置筛选（After/Before）使用。</summary>
    internal abstract Instruction RangeStart { get; }

    /// <summary>此匹配覆盖的 IL 段终点，供按位置筛选（After/Before）使用。</summary>
    internal abstract Instruction RangeEnd { get; }
}

/// <summary>capture 的内部存储，以 pattern 对象为 key（引用相等）。</summary>
internal sealed class MatchCaptureCollection
{
    private readonly IReadOnlyDictionary<ExpressionPattern, MatchCapture> _captures;

    internal MatchCaptureCollection(IReadOnlyDictionary<ExpressionPattern, MatchCapture> captures)
        => _captures = captures ?? throw new ArgumentNullException(nameof(captures));

    public int Count => _captures.Count;

    public TCapture Require<TCapture>(ExpressionPattern pattern) where TCapture : MatchCapture
    {
        if (pattern is null)
            throw new ArgumentNullException(nameof(pattern));
        if (!_captures.TryGetValue(pattern, out var capture))
            throw new KeyNotFoundException($"The match does not contain a capture for pattern '{pattern}'.");
        if (capture is not TCapture typed)
        {
            throw new InvalidOperationException(
                $"Capture for pattern '{pattern}' is {capture.GetType().Name}, not {typeof(TCapture).Name}.");
        }
        return typed;
    }
}

/// <summary>
/// 一个 value occurrence。公开 API 只暴露“当前 occurrence”的求值范围和结果位置；
/// 原始 definition/producer 仅供内部重写器使用，避免调用方选错点位。
/// </summary>
public abstract class ValueTarget : MatchCapture
{
    private protected ValueTarget(MethodDefinition method, ExpressionPattern? source, TypeReference valueType,
        Instruction definitionFirstInstruction, Instruction definitionInstruction,
        Instruction occurrenceInstruction, Instruction? consumerInstruction)
        : base(method, source)
    {
        ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));
        DefinitionFirstInstruction = definitionFirstInstruction ??
                                     throw new ArgumentNullException(nameof(definitionFirstInstruction));
        DefinitionInstruction = definitionInstruction ?? throw new ArgumentNullException(nameof(definitionInstruction));
        ResultInstruction = occurrenceInstruction ?? throw new ArgumentNullException(nameof(occurrenceInstruction));
        ConsumerInstruction = consumerInstruction;
        FirstInstruction = ReferenceEquals(DefinitionInstruction, ResultInstruction)
            ? DefinitionFirstInstruction
            : ResultInstruction;
    }

    public TypeReference ValueType { get; }
    public Instruction FirstInstruction { get; }
    public Instruction ResultInstruction { get; }

    public Instruction DefinitionFirstInstruction { get; }
    public Instruction DefinitionInstruction { get; }
    public Instruction? ConsumerInstruction { get; }

    /// <summary>
    /// 该 occurrence 是否经由取地址指令（ldarga/ldloca/ldelema）到达。
    /// 此时栈上是 managed pointer 而非值本身，占位改写（After/Transform/Observe/Replace）不安全。
    /// </summary>
    public bool IsAddressBacked { get; internal init; }

    internal override Instruction RangeStart => FirstInstruction;
    internal override Instruction RangeEnd => ConsumerInstruction ?? ResultInstruction;
}

/// <summary>value pattern 的根匹配；它本身就是唯一的根 value 改写入口。</summary>
public class ValueMatch : ValueTarget
{
    internal ValueMatch(MethodDefinition method, ValuePattern pattern,
        TypeReference valueType, Instruction definitionFirstInstruction,
        Instruction definitionInstruction, Instruction occurrenceInstruction,
        Instruction? consumerInstruction, IReadOnlyDictionary<ExpressionPattern, MatchCapture> captures)
        : base(method, source: null, valueType, definitionFirstInstruction, definitionInstruction,
            occurrenceInstruction, consumerInstruction)
    {
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        Captures = new MatchCaptureCollection(captures);
    }

    public ValuePattern Pattern { get; }
    internal MatchCaptureCollection Captures { get; }

    public LocalCapture this[CilLocal local] => Captures.Require<LocalCapture>(local);
    public ArgumentCapture this[CilArg argument] => Captures.Require<ArgumentCapture>(argument);
    public ArgumentCapture this[CilThis thisArg] => Captures.Require<ArgumentCapture>(thisArg);
    public ValueCapture this[ValuePattern fragment] => Captures.Require<ValueCapture>(fragment);
    public ConditionCapture this[ConditionPattern fragment] => Captures.Require<ConditionCapture>(fragment);

    /// <summary>按 lambda 参数名取回参数捕获（普通类型的 lambda 参数、<c>CilArg&lt;T&gt;</c>/<c>CilThis&lt;T&gt;</c> 参数）。</summary>
    public ArgumentCapture Arg(string parameterName)
        => Captures.Require<ArgumentCapture>(Pattern.Parameter(parameterName));

    /// <summary>取回 <c>__this</c> 参数（或 <c>CilThis&lt;T&gt;</c> 类型的 lambda 参数）的捕获。</summary>
    public ArgumentCapture This()
        => Captures.Require<ArgumentCapture>(LambdaParameterLookup.This(Pattern));

    /// <summary>按 lambda 参数名取回 <c>CilLocal&lt;T&gt;</c> 类型参数的捕获。</summary>
    public LocalCapture Local(string parameterName)
        => Captures.Require<LocalCapture>(Pattern.Parameter(parameterName));
}

/// <summary>由 <see cref="ValuePattern{T}"/> 得到的强类型根匹配。</summary>
public sealed class ValueMatch<T> : ValueMatch
{
    internal ValueMatch(MethodDefinition method, ValuePattern<T> pattern,
        TypeReference valueType, Instruction definitionFirstInstruction,
        Instruction definitionInstruction, Instruction occurrenceInstruction,
        Instruction? consumerInstruction, IReadOnlyDictionary<ExpressionPattern, MatchCapture> captures)
        : base(method, pattern, valueType, definitionFirstInstruction, definitionInstruction,
            occurrenceInstruction, consumerInstruction, captures) { }

    public new ValuePattern<T> Pattern => (ValuePattern<T>)base.Pattern;
}

/// <summary>一个命名 value capture。</summary>
public class ValueCapture : ValueTarget
{
    internal ValueCapture(MethodDefinition method, ExpressionPattern source, TypeReference valueType,
        Instruction definitionFirstInstruction, Instruction definitionInstruction,
        Instruction occurrenceInstruction, Instruction? consumerInstruction)
        : base(method, source, valueType, definitionFirstInstruction, definitionInstruction,
            occurrenceInstruction, consumerInstruction) { }
}

/// <summary>捕获到的显式 argument 或 this value。</summary>
public sealed class ArgumentCapture : ValueCapture
{
    internal ArgumentCapture(MethodDefinition method, ExpressionPattern source, TypeReference valueType,
        Instruction definitionFirstInstruction, Instruction definitionInstruction,
        Instruction occurrenceInstruction, Instruction? consumerInstruction,
        bool isThis, int parameterIndex, ParameterDefinition? parameter)
        : base(method, source, valueType, definitionFirstInstruction, definitionInstruction,
            occurrenceInstruction, consumerInstruction)
    {
        IsThis = isThis;
        ParameterIndex = parameterIndex;
        Parameter = parameter;
    }

    public bool IsThis { get; }
    public int ParameterIndex { get; }
    public ParameterDefinition? Parameter { get; }
}

/// <summary>捕获到的 local load。</summary>
public sealed class LocalCapture : ValueCapture
{
    internal LocalCapture(MethodDefinition method, ExpressionPattern source, VariableDefinition variable,
        Instruction definitionFirstInstruction, Instruction definitionInstruction,
        Instruction occurrenceInstruction, Instruction? consumerInstruction)
        : base(method, source, variable?.VariableType ?? throw new ArgumentNullException(nameof(variable)),
            definitionFirstInstruction, definitionInstruction, occurrenceInstruction, consumerInstruction)
    {
        Variable = variable;
    }

    public VariableDefinition Variable { get; }
}

/// <summary>一个完整的无结果 effect，可直接 Before/After/Replace。</summary>
public abstract class EffectTarget : MatchCapture
{
    private protected EffectTarget(MethodDefinition method, ExpressionPattern? source,
        Instruction firstInstruction, Instruction lastInstruction)
        : base(method, source)
    {
        FirstInstruction = firstInstruction ?? throw new ArgumentNullException(nameof(firstInstruction));
        LastInstruction = lastInstruction ?? throw new ArgumentNullException(nameof(lastInstruction));
    }

    public Instruction FirstInstruction { get; }
    public Instruction LastInstruction { get; }

    internal override Instruction RangeStart => FirstInstruction;
    internal override Instruction RangeEnd => LastInstruction;
}

/// <summary>effect pattern 的根匹配。</summary>
public sealed class EffectMatch : EffectTarget
{
    internal EffectMatch(MethodDefinition method, EffectPattern pattern,
        Instruction firstInstruction, Instruction lastInstruction,
        IReadOnlyDictionary<ExpressionPattern, MatchCapture> captures)
        : base(method, source: null, firstInstruction, lastInstruction)
    {
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        Captures = new MatchCaptureCollection(captures);
    }

    public EffectPattern Pattern { get; }
    internal MatchCaptureCollection Captures { get; }

    public LocalCapture this[CilLocal local] => Captures.Require<LocalCapture>(local);
    public ArgumentCapture this[CilArg argument] => Captures.Require<ArgumentCapture>(argument);
    public ArgumentCapture this[CilThis thisArg] => Captures.Require<ArgumentCapture>(thisArg);
    public ValueCapture this[ValuePattern fragment] => Captures.Require<ValueCapture>(fragment);
    public ConditionCapture this[ConditionPattern fragment] => Captures.Require<ConditionCapture>(fragment);

    /// <summary>按 lambda 参数名取回参数捕获（普通类型的 lambda 参数、<c>CilArg&lt;T&gt;</c>/<c>CilThis&lt;T&gt;</c> 参数）。</summary>
    public ArgumentCapture Arg(string parameterName)
        => Captures.Require<ArgumentCapture>(Pattern.Parameter(parameterName));

    /// <summary>取回 <c>__this</c> 参数（或 <c>CilThis&lt;T&gt;</c> 类型的 lambda 参数）的捕获。</summary>
    public ArgumentCapture This()
        => Captures.Require<ArgumentCapture>(LambdaParameterLookup.This(Pattern));

    /// <summary>按 lambda 参数名取回 <c>CilLocal&lt;T&gt;</c> 类型参数的捕获。</summary>
    public LocalCapture Local(string parameterName)
        => Captures.Require<LocalCapture>(Pattern.Parameter(parameterName));
}

/// <summary>一个短路 condition decision；内部可能包含多个 branch，但公开为单一语义目标。</summary>
public abstract class ConditionTarget : MatchCapture
{
    private protected ConditionTarget(MethodDefinition method, ExpressionPattern? source,
        ConditionFragment fragment)
        : base(method, source)
    {
        Fragment = fragment ?? throw new ArgumentNullException(nameof(fragment));
        FirstInstruction = fragment.Entry.Leader;
        LastInstruction = fragment.Blocks
            .Select(static block => block.Terminator)
            .OrderBy(instruction => method.Body.Instructions.IndexOf(instruction))
            .Last();
        CanRewrite = fragment.CanRewrite;
        RewriteFailureReason = fragment.RewriteFailureReason;
    }

    internal ConditionFragment Fragment { get; }
    public Instruction FirstInstruction { get; }
    public Instruction LastInstruction { get; }
    public bool CanRewrite { get; }
    public string? RewriteFailureReason { get; }

    internal override Instruction RangeStart => FirstInstruction;
    internal override Instruction RangeEnd => LastInstruction;
}

/// <summary>condition pattern 的根匹配。</summary>
public sealed class ConditionMatch : ConditionTarget
{
    internal ConditionMatch(MethodDefinition method, ConditionPattern pattern,
        ConditionFragment fragment, IReadOnlyDictionary<ExpressionPattern, MatchCapture> captures)
        : base(method, source: null, fragment)
    {
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        Captures = new MatchCaptureCollection(captures);
    }

    public ConditionPattern Pattern { get; }
    internal MatchCaptureCollection Captures { get; }

    public LocalCapture this[CilLocal local] => Captures.Require<LocalCapture>(local);
    public ArgumentCapture this[CilArg argument] => Captures.Require<ArgumentCapture>(argument);
    public ArgumentCapture this[CilThis thisArg] => Captures.Require<ArgumentCapture>(thisArg);
    public ValueCapture this[ValuePattern fragment] => Captures.Require<ValueCapture>(fragment);
    public ConditionCapture this[ConditionPattern fragment] => Captures.Require<ConditionCapture>(fragment);

    /// <summary>按 lambda 参数名取回参数捕获（普通类型的 lambda 参数、<c>CilArg&lt;T&gt;</c>/<c>CilThis&lt;T&gt;</c> 参数）。</summary>
    public ArgumentCapture Arg(string parameterName)
        => Captures.Require<ArgumentCapture>(Pattern.Parameter(parameterName));

    /// <summary>取回 <c>__this</c> 参数（或 <c>CilThis&lt;T&gt;</c> 类型的 lambda 参数）的捕获。</summary>
    public ArgumentCapture This()
        => Captures.Require<ArgumentCapture>(LambdaParameterLookup.This(Pattern));

    /// <summary>按 lambda 参数名取回 <c>CilLocal&lt;T&gt;</c> 类型参数的捕获。</summary>
    public LocalCapture Local(string parameterName)
        => Captures.Require<LocalCapture>(Pattern.Parameter(parameterName));
}

/// <summary>由 Mark 捕获的 condition 子片段。</summary>
public sealed class ConditionCapture : ConditionTarget
{
    internal ConditionCapture(MethodDefinition method, ExpressionPattern source, ConditionFragment fragment)
        : base(method, source, fragment) { }
}

internal static class LambdaParameterLookup
{
    public static ExpressionPattern This(ExpressionPattern pattern)
    {
        if (pattern.Parameters.TryGetValue(PatternExpressionParser.ThisParameterName, out var named))
            return named;
        ExpressionPattern? found = null;
        foreach (var leaf in pattern.Parameters.Values)
        {
            if (leaf is not CilThis)
                continue;
            if (found is not null)
                throw new InvalidOperationException($"Pattern '{pattern}' declares more than one this parameter.");
            found = leaf;
        }
        return found ?? throw new KeyNotFoundException(
            $"Pattern '{pattern}' has no this parameter. Declare a lambda parameter named '__this' or of type CilThis<T>.");
    }
}
