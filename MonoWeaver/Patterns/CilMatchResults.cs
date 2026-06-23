using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace MonoWeaver.Patterns;

/// <summary>当调用方要求 unique match，但结果为空或有歧义时抛出。</summary>
public sealed class CilPatternMatchException : Exception
{
    public CilPatternMatchException(string message) : base(message) { }
}

/// <summary>一组 match，并提供会拒绝 ambiguous insertion point 的 helper。</summary>
public sealed class CilMatchSet : IReadOnlyList<CilMatch>
{
    private readonly IReadOnlyList<CilMatch> _matches;

    internal CilMatchSet(MethodDefinition method, CilExpressionPattern pattern, IReadOnlyList<CilMatch> matches)
    {
        Method = method;
        Pattern = pattern;
        _matches = matches;
    }

    public MethodDefinition Method { get; }
    public CilExpressionPattern Pattern { get; }
    public int Count => _matches.Count;
    public CilMatch this[int index] => _matches[index];

    /// <summary>
    /// 返回唯一 match。0 个或多个 candidate 都会抛出异常，因为对 IL hook 来说，
    /// 静默选择第一个 candidate 是不安全的。
    /// </summary>
    public CilMatch Single()
    {
        if (_matches.Count == 1)
            return _matches[0];

        var details = _matches.Count == 0
            ? "No matching expression was found."
            : $"{_matches.Count} matching expressions were found at: " +
              string.Join(", ", _matches.Select(static m => $"IL_{m.FirstInstruction.Offset:X4}"));
        throw new CilPatternMatchException(details + " Add surrounding expression context, a Mark, or a local-definition constraint.");
    }

    public IEnumerator<CilMatch> GetEnumerator() => _matches.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>一个精确 match，以及该 pattern 产生的所有 capture。</summary>
public sealed class CilMatch
{
    private readonly IReadOnlyDictionary<string, MatchCapture> _captures;

    internal CilMatch(MethodDefinition method, CilExpressionPattern pattern, Instruction firstInstruction,
        Instruction lastInstruction, MatchCapture root, IReadOnlyDictionary<string, MatchCapture> captures)
    {
        Method = method;
        Pattern = pattern;
        FirstInstruction = firstInstruction;
        LastInstruction = lastInstruction;
        Root = root;
        _captures = captures;
    }

    public MethodDefinition Method { get; }
    public CilExpressionPattern Pattern { get; }
    public Instruction FirstInstruction { get; }
    public Instruction LastInstruction { get; }
    public MatchCapture Root { get; }
    public IReadOnlyDictionary<string, MatchCapture> Captures => _captures;

    /// <summary>把 root 作为 value/effect match 返回。</summary>
    public MatchedValue Value()
        => Root as MatchedValue
           ?? throw new InvalidOperationException($"The root capture is {Root.GetType().Name}, not {nameof(MatchedValue)}.");

    /// <summary>把 root 作为 Boolean condition match 返回。</summary>
    public MatchedCondition Condition()
        => Root as MatchedCondition
           ?? throw new InvalidOperationException($"The root capture is {Root.GetType().Name}, not {nameof(MatchedCondition)}.");

    public MatchedValue Value(string captureName) => Require<MatchedValue>(captureName);
    public MatchedCondition Condition(string captureName) => Require<MatchedCondition>(captureName);
    public MatchedArgument Argument(string captureName) => Require<MatchedArgument>(captureName);
    public MatchedLocal Local(string captureName) => Require<MatchedLocal>(captureName);

    private T Require<T>(string name) where T : MatchCapture
    {
        if (!_captures.TryGetValue(name, out var capture))
            throw new KeyNotFoundException($"The pattern did not produce a capture named '{name}'.");
        if (capture is not T typed)
            throw new InvalidOperationException($"Capture '{name}' is {capture.GetType().Name}, not {typeof(T).Name}.");
        return typed;
    }
}

/// <summary>public pattern capture 的 base type。</summary>
public abstract class MatchCapture
{
    protected MatchCapture(MethodDefinition method, string? name, TypeReference? valueType)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Name = name;
        ValueType = valueType;
    }

    /// <summary>此 capture 所属的目标方法；纯 Cecil transform 不需要额外上下文。</summary>
    public MethodDefinition Method { get; }
    public string? Name { get; }
    public TypeReference? ValueType { get; }
}

/// <summary>
/// 一个 matched value occurrence。<see cref="AfterUseInstruction"/> 标识此 occurrence 使用的
/// 具体 load/producer，而 <see cref="ProducerInstruction"/> 标识其原始 definition。
/// </summary>
public class MatchedValue : MatchCapture
{
    internal MatchedValue(MethodDefinition method, string? name, TypeReference? valueType, Instruction firstInstruction,
        Instruction producerInstruction, Instruction afterUseInstruction, Instruction? consumerInstruction)
        : base(method, name, valueType)
    {
        FirstInstruction = firstInstruction;
        ProducerInstruction = producerInstruction;
        AfterUseInstruction = afterUseInstruction;
        ConsumerInstruction = consumerInstruction;
    }

    public Instruction FirstInstruction { get; }
    public Instruction ProducerInstruction { get; }
    public Instruction AfterUseInstruction { get; }
    public Instruction? ConsumerInstruction { get; }
}

/// <summary>捕获到的显式 argument 或 <c>this</c> value。</summary>
public sealed class MatchedArgument : MatchedValue
{
    internal MatchedArgument(MethodDefinition method, string? name, TypeReference valueType, Instruction firstInstruction,
        Instruction producerInstruction, Instruction afterUseInstruction, Instruction? consumerInstruction,
        bool isThis, int parameterIndex, ParameterDefinition? parameter)
        : base(method, name, valueType, firstInstruction, producerInstruction, afterUseInstruction, consumerInstruction)
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
public sealed class MatchedLocal : MatchedValue
{
    internal MatchedLocal(MethodDefinition method, string? name, VariableDefinition variable, Instruction firstInstruction,
        Instruction producerInstruction, Instruction afterUseInstruction, Instruction? consumerInstruction)
        : base(method, name, variable.VariableType, firstInstruction, producerInstruction, afterUseInstruction, consumerInstruction)
    {
        Variable = variable;
    }

    public VariableDefinition Variable { get; }
}

/// <summary>来自 matched short-circuit condition fragment 的一个 true/false exit edge。</summary>
public sealed class MatchedConditionExit
{
    internal MatchedConditionExit(Instruction terminator, Instruction target, bool value, bool isFallThrough)
    {
        Terminator = terminator;
        Target = target;
        Value = value;
        IsFallThrough = isFallThrough;
    }

    public Instruction Terminator { get; }
    public Instruction Target { get; }
    public bool Value { get; }
    public bool IsFallThrough { get; }
}

/// <summary>
/// 一个 matched Boolean decision。它可能对应多个 conditional branch，而不是一个 materialized Boolean value。
/// </summary>
public sealed class MatchedCondition : MatchCapture
{
    internal MatchedCondition(MethodDefinition method, string? name, ConditionFragment fragment, TypeReference booleanType)
        : base(method, name, booleanType)
    {
        Fragment = fragment;
        EntryInstruction = fragment.Entry.Leader;
        TrueContinuation = fragment.TrueContinuation.Leader;
        FalseContinuation = fragment.FalseContinuation.Leader;
        TrueExits = fragment.TrueExits.Select(static e => new MatchedConditionExit(
            e.Terminator, e.To.Leader, true, e.IsFallThrough)).ToArray();
        FalseExits = fragment.FalseExits.Select(static e => new MatchedConditionExit(
            e.Terminator, e.To.Leader, false, e.IsFallThrough)).ToArray();
        CanRewrite = fragment.CanRewrite;
        RewriteFailureReason = fragment.RewriteFailureReason;
    }

    internal ConditionFragment Fragment { get; }

    public Instruction EntryInstruction { get; }
    public Instruction TrueContinuation { get; }
    public Instruction FalseContinuation { get; }
    public IReadOnlyList<MatchedConditionExit> TrueExits { get; }
    public IReadOnlyList<MatchedConditionExit> FalseExits { get; }
    public bool CanRewrite { get; }
    public string? RewriteFailureReason { get; }
}
