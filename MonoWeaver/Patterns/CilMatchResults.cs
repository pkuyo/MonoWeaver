using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace MonoWeaver.Patterns;

/// <summary>调用方要求唯一匹配，但结果为空或存在歧义时抛出。</summary>
public sealed class CilPatternMatchException : Exception
{
    public CilPatternMatchException(string message) : base(message) { }
}

/// <summary>某一种强类型 match 的结果集合。</summary>
public sealed class CilMatchSet<TMatch> : IReadOnlyList<TMatch>
{
    private readonly IReadOnlyList<TMatch> _matches;
    private readonly Func<TMatch, Instruction> _location;

    internal CilMatchSet(MethodDefinition method, ExpressionPattern pattern,
        IReadOnlyList<TMatch> matches, Func<TMatch, Instruction> location)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        _matches = matches ?? throw new ArgumentNullException(nameof(matches));
        _location = location ?? throw new ArgumentNullException(nameof(location));
    }

    public MethodDefinition Method { get; }
    public ExpressionPattern Pattern { get; }
    public int Count => _matches.Count;
    public TMatch this[int index] => _matches[index];

    /// <summary>返回唯一结果；0 个或多个候选都视为不安全。</summary>
    public TMatch Single()
    {
        if (_matches.Count == 1)
            return _matches[0];

        var details = _matches.Count == 0
            ? "No matching expression was found."
            : $"{_matches.Count} matching expressions were found at: " +
              string.Join(", ", _matches.Select(match => $"IL_{_location(match).Offset:X4}"));
        throw new CilPatternMatchException(details +
            " Add surrounding expression context, a Mark, or a local-definition constraint.");
    }

    public IEnumerator<TMatch> GetEnumerator() => _matches.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>一个命名 capture。根 match 不需要再转换成 capture 才能改写。</summary>
public abstract class MatchCapture
{
    private protected MatchCapture(MethodDefinition method, string? name)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Name = name;
    }

    public MethodDefinition Method { get; }
    public string? Name { get; }
}

/// <summary>命名 capture 的只读集合，并提供按语义类型读取的入口。</summary>
public sealed class MatchCaptureCollection : IReadOnlyDictionary<string, MatchCapture>
{
    private readonly IReadOnlyDictionary<string, MatchCapture> _captures;

    internal MatchCaptureCollection(IReadOnlyDictionary<string, MatchCapture> captures)
        => _captures = captures ?? throw new ArgumentNullException(nameof(captures));

    public MatchCapture this[string key] => _captures[key];
    public IEnumerable<string> Keys => _captures.Keys;
    public IEnumerable<MatchCapture> Values => _captures.Values;
    public int Count => _captures.Count;
    public bool ContainsKey(string key) => _captures.ContainsKey(key);
    public bool TryGetValue(string key, out MatchCapture value) => _captures.TryGetValue(key, out value!);
    public IEnumerator<KeyValuePair<string, MatchCapture>> GetEnumerator() => _captures.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public ValueCapture Value(string name) => Require<ValueCapture>(name);
    public EffectCapture Effect(string name) => Require<EffectCapture>(name);
    public ConditionCapture Condition(string name) => Require<ConditionCapture>(name);
    public ArgumentCapture Argument(string name) => Require<ArgumentCapture>(name);
    public LocalCapture Local(string name) => Require<LocalCapture>(name);

    private TCapture Require<TCapture>(string name) where TCapture : MatchCapture
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A capture name is required.", nameof(name));
        if (!_captures.TryGetValue(name, out var capture))
            throw new KeyNotFoundException($"The pattern did not produce a capture named '{name}'.");
        if (capture is not TCapture typed)
        {
            throw new InvalidOperationException(
                $"Capture '{name}' is {capture.GetType().Name}, not {typeof(TCapture).Name}.");
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
    private protected ValueTarget(MethodDefinition method, string? name, TypeReference valueType,
        Instruction definitionFirstInstruction, Instruction definitionInstruction,
        Instruction occurrenceInstruction, Instruction? consumerInstruction)
        : base(method, name)
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
}

/// <summary>value pattern 的根匹配；它本身就是唯一的根 value 改写入口。</summary>
public class ValueMatch : ValueTarget
{
    internal ValueMatch(MethodDefinition method, ValuePattern pattern,
        TypeReference valueType, Instruction definitionFirstInstruction,
        Instruction definitionInstruction, Instruction occurrenceInstruction,
        Instruction? consumerInstruction, IReadOnlyDictionary<string, MatchCapture> captures)
        : base(method, name: null, valueType, definitionFirstInstruction, definitionInstruction,
            occurrenceInstruction, consumerInstruction)
    {
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        Captures = new MatchCaptureCollection(captures);
    }

    public ValuePattern Pattern { get; }
    public MatchCaptureCollection Captures { get; }
}

/// <summary>由 <see cref="ValuePattern{T}"/> 得到的强类型根匹配。</summary>
public sealed class ValueMatch<T> : ValueMatch
{
    internal ValueMatch(MethodDefinition method, ValuePattern<T> pattern,
        TypeReference valueType, Instruction definitionFirstInstruction,
        Instruction definitionInstruction, Instruction occurrenceInstruction,
        Instruction? consumerInstruction, IReadOnlyDictionary<string, MatchCapture> captures)
        : base(method, pattern, valueType, definitionFirstInstruction, definitionInstruction,
            occurrenceInstruction, consumerInstruction, captures) { }

    public new ValuePattern<T> Pattern => (ValuePattern<T>)base.Pattern;
}

/// <summary>一个命名 value capture。</summary>
public class ValueCapture : ValueTarget
{
    internal ValueCapture(MethodDefinition method, string name, TypeReference valueType,
        Instruction definitionFirstInstruction, Instruction definitionInstruction,
        Instruction occurrenceInstruction, Instruction? consumerInstruction)
        : base(method, name, valueType, definitionFirstInstruction, definitionInstruction,
            occurrenceInstruction, consumerInstruction) { }
}

/// <summary>捕获到的显式 argument 或 this value。</summary>
public sealed class ArgumentCapture : ValueCapture
{
    internal ArgumentCapture(MethodDefinition method, string name, TypeReference valueType,
        Instruction definitionFirstInstruction, Instruction definitionInstruction,
        Instruction occurrenceInstruction, Instruction? consumerInstruction,
        bool isThis, int parameterIndex, ParameterDefinition? parameter)
        : base(method, name, valueType, definitionFirstInstruction, definitionInstruction,
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
    internal LocalCapture(MethodDefinition method, string name, VariableDefinition variable,
        Instruction definitionFirstInstruction, Instruction definitionInstruction,
        Instruction occurrenceInstruction, Instruction? consumerInstruction)
        : base(method, name, variable?.VariableType ?? throw new ArgumentNullException(nameof(variable)),
            definitionFirstInstruction, definitionInstruction, occurrenceInstruction, consumerInstruction)
    {
        Variable = variable;
    }

    public VariableDefinition Variable { get; }
}

/// <summary>一个完整的无结果 effect，可直接 Before/After/Replace。</summary>
public abstract class EffectTarget : MatchCapture
{
    private protected EffectTarget(MethodDefinition method, string? name,
        Instruction firstInstruction, Instruction lastInstruction)
        : base(method, name)
    {
        FirstInstruction = firstInstruction ?? throw new ArgumentNullException(nameof(firstInstruction));
        LastInstruction = lastInstruction ?? throw new ArgumentNullException(nameof(lastInstruction));
    }

    public Instruction FirstInstruction { get; }
    public Instruction LastInstruction { get; }
}

/// <summary>effect pattern 的根匹配。</summary>
public sealed class EffectMatch : EffectTarget
{
    internal EffectMatch(MethodDefinition method, EffectPattern pattern,
        Instruction firstInstruction, Instruction lastInstruction,
        IReadOnlyDictionary<string, MatchCapture> captures)
        : base(method, name: null, firstInstruction, lastInstruction)
    {
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        Captures = new MatchCaptureCollection(captures);
    }

    public EffectPattern Pattern { get; }
    public MatchCaptureCollection Captures { get; }
}

/// <summary>命名的 void/effect capture。</summary>
public sealed class EffectCapture : EffectTarget
{
    internal EffectCapture(MethodDefinition method, string name,
        Instruction firstInstruction, Instruction lastInstruction)
        : base(method, name, firstInstruction, lastInstruction) { }
}

/// <summary>一个短路 condition decision；内部可能包含多个 branch，但公开为单一语义目标。</summary>
public abstract class ConditionTarget : MatchCapture
{
    private protected ConditionTarget(MethodDefinition method, string? name,
        ConditionFragment fragment)
        : base(method, name)
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
}

/// <summary>condition pattern 的根匹配。</summary>
public sealed class ConditionMatch : ConditionTarget
{
    internal ConditionMatch(MethodDefinition method, ConditionPattern pattern,
        ConditionFragment fragment, IReadOnlyDictionary<string, MatchCapture> captures)
        : base(method, name: null, fragment)
    {
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        Captures = new MatchCaptureCollection(captures);
    }

    public ConditionPattern Pattern { get; }
    public MatchCaptureCollection Captures { get; }
}

/// <summary>由 Mark 捕获的 condition 子片段。</summary>
public sealed class ConditionCapture : ConditionTarget
{
    internal ConditionCapture(MethodDefinition method, string name, ConditionFragment fragment)
        : base(method, name, fragment) { }
}
