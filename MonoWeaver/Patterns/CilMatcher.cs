using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Utils;

namespace MonoWeaver.Patterns;

/// <summary>
/// 将 <see cref="CilExpressionPattern"/> 匹配到一个 Cecil method body。
/// matcher instance 是 snapshot；修改 method 后请创建新的 matcher。
/// </summary>
public sealed class CilMatcher
{
    private readonly CilMethodModel _model;

    private CilMatcher(MethodDefinition method)
    {
        _model = CilMethodModel.Create(method);
    }

    public MethodDefinition Method => _model.Method;

    public static CilMatcher For(MethodDefinition method) => new(method);

    /// <summary>查找所有精确 candidate。插入 IL 前请使用 <see cref="CilMatchSet.Single"/>。</summary>
    public CilMatchSet Find(CilExpressionPattern pattern)
    {
        if (pattern is null)
            throw new ArgumentNullException(nameof(pattern));

        var matches = pattern.Kind switch
        {
            CilPatternKind.Value => FindValues(pattern),
            CilPatternKind.Effect => FindEffects(pattern),
            CilPatternKind.Condition => FindConditions(pattern),
            _ => throw new ArgumentOutOfRangeException()
        };

        return new CilMatchSet(Method, pattern, matches);
    }

    private IReadOnlyList<CilMatch> FindValues(CilExpressionPattern pattern)
    {
        var result = new List<CilMatch>();
        var seen = new HashSet<(Instruction producer, Instruction use)>();
        var matcher = new ExpressionNodeMatcher(_model, pattern.Options);

        foreach (var candidate in _model.ValueCandidates)
        {
            var occurrence = TargetOccurrence.Direct(candidate, consumer: null);
            var context = new MatchContext();
            if (!matcher.TryMatch(pattern.Root, occurrence, context, out var matched))
                continue;
            if (!ApplyLocalConstraints(pattern, context, matcher))
                continue;
            if (!seen.Add((matched.Node.ProducerInstruction, matched.UseAnchor)))
                continue;

            result.Add(CreateValueMatch(pattern, matched, context, matched.Node.ProducerInstruction));
        }

        return result;
    }

    private IReadOnlyList<CilMatch> FindEffects(CilExpressionPattern pattern)
    {
        var result = new List<CilMatch>();
        var seen = new HashSet<Instruction>();
        var matcher = new ExpressionNodeMatcher(_model, pattern.Options);

        foreach (var candidate in _model.EffectCandidates)
        {
            var occurrence = TargetOccurrence.Direct(candidate.Expression, candidate.TerminalInstruction);
            var context = new MatchContext();
            if (!matcher.TryMatch(pattern.Root, occurrence, context, out var matched))
                continue;
            if (!ApplyLocalConstraints(pattern, context, matcher))
                continue;
            if (!seen.Add(candidate.TerminalInstruction))
                continue;

            result.Add(CreateValueMatch(pattern, matched, context, candidate.TerminalInstruction));
        }

        return result;
    }

    private IReadOnlyList<CilMatch> FindConditions(CilExpressionPattern pattern)
    {
        var result = new List<CilMatch>();
        var conditionMatcher = new ConditionPatternMatcher(_model, pattern.Options);
        var seen = new HashSet<(int entry, int trueTarget, int falseTarget)>();

        foreach (var block in _model.Blocks)
        {
            if (!_model.TryGetConditionExpression(block, out _))
                continue;

            var context = new MatchContext();
            if (!conditionMatcher.TryMatch(pattern.Root, block, context, out var fragment))
                continue;
            if (!ApplyLocalConstraints(pattern, context, conditionMatcher.ExpressionMatcher))
                continue;

            fragment.AnalyzeRewriteSafety();
            var key = (fragment.Entry.Id, fragment.TrueContinuation.Id, fragment.FalseContinuation.Id);
            if (!seen.Add(key))
                continue;

            var root = new ConditionInternalCapture(null, fragment);
            var captures = MaterializeCaptures(context, _model.Method.Module.TypeSystem.Boolean);
            var first = fragment.Entry.Leader;
            var last = fragment.Blocks
                .Select(static b => b.Terminator)
                .OrderBy(_model.IndexOf)
                .Last();
            result.Add(new CilMatch(Method, pattern, first, last,
                root.ToPublic(_model.Method.Module.TypeSystem.Boolean), captures));
        }

        return result;
    }

    private CilMatch CreateValueMatch(CilExpressionPattern pattern, TargetOccurrence matched,
        MatchContext context, Instruction lastInstruction)
    {
        var rootInternal = new ValueInternalCapture(null, matched);
        var captures = MaterializeCaptures(context, Method.Module.TypeSystem.Boolean);
        return new CilMatch(Method, pattern, matched.Node.FirstInstruction, lastInstruction,
            rootInternal.ToPublic(Method.Module.TypeSystem.Boolean), captures);
    }

    private IReadOnlyDictionary<string, MatchCapture> MaterializeCaptures(MatchContext context, TypeReference booleanType)
    {
        var result = new Dictionary<string, MatchCapture>(StringComparer.Ordinal);
        foreach (var pair in context.Captures)
            result[pair.Key] = pair.Value.ToPublic(booleanType);
        return result;
    }

    private bool ApplyLocalConstraints(CilExpressionPattern pattern, MatchContext context, ExpressionNodeMatcher matcher)
    {
        foreach (var constraint in pattern.LocalDefinitionConstraints)
        {
            if (!context.Captures.TryGetValue(constraint.CaptureName, out var capture)
                || capture is not ValueInternalCapture { Occurrence.Node: TargetLocalReadNode localRead })
            {
                return false;
            }

            if (!_model.LocalDefinitions.TryGetUniqueDefinition(localRead,
                    out var store, out var storedValue, out _))
            {
                return false;
            }

            var definitionContext = new MatchContext();
            var definitionOccurrence = new TargetOccurrence(storedValue, store, store);
            if (!matcher.TryMatch(constraint.Definition.Root, definitionOccurrence,
                    definitionContext, out _))
            {
                return false;
            }

            if (!ApplyLocalConstraints(constraint.Definition, definitionContext, matcher))
                return false;
        }

        return true;
    }
}

internal readonly struct TargetOccurrence
{
    public TargetOccurrence(TargetExpressionNode node, Instruction useAnchor, Instruction? consumer)
    {
        Node = node;
        UseAnchor = useAnchor;
        Consumer = consumer;
    }

    public TargetExpressionNode Node { get; }
    public Instruction UseAnchor { get; }
    public Instruction? Consumer { get; }

    public static TargetOccurrence Direct(TargetExpressionNode node, Instruction? consumer)
        => new(node, node.ProducerInstruction, consumer);

    public TargetOccurrence WithNode(TargetExpressionNode node)
        => new(node, UseAnchor, Consumer);
}

internal abstract class InternalCapture
{
    protected InternalCapture(string? name) => Name = name;
    public string? Name { get; }
    public abstract MatchCapture ToPublic(TypeReference booleanType);
}

internal sealed class ValueInternalCapture : InternalCapture
{
    public ValueInternalCapture(string? name, TargetOccurrence occurrence) : base(name)
        => Occurrence = occurrence;

    public TargetOccurrence Occurrence { get; }

    public override MatchCapture ToPublic(TypeReference booleanType)
    {
        var node = Occurrence.Node;
        if (node is TargetArgumentNode argument)
        {
            return new MatchedArgument(Name, node.ResultType!, node.FirstInstruction,
                node.ProducerInstruction, Occurrence.UseAnchor, Occurrence.Consumer,
                argument.IsThis, argument.ParameterIndex, argument.Parameter);
        }

        if (node is TargetLocalReadNode local)
        {
            return new MatchedLocal(Name, local.Variable, node.FirstInstruction,
                node.ProducerInstruction, Occurrence.UseAnchor, Occurrence.Consumer);
        }

        return new MatchedValue(Name, node.ResultType, node.FirstInstruction,
            node.ProducerInstruction, Occurrence.UseAnchor, Occurrence.Consumer);
    }
}

internal sealed class ConditionInternalCapture : InternalCapture
{
    public ConditionInternalCapture(string? name, ConditionFragment fragment) : base(name)
        => Fragment = fragment;

    public ConditionFragment Fragment { get; }

    public override MatchCapture ToPublic(TypeReference booleanType)
        => new MatchedCondition(Name, Fragment, booleanType);
}

internal sealed class MatchContext
{
    private Dictionary<string, InternalCapture> _captures = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, InternalCapture> Captures => _captures;

    public MatchContext Clone()
    {
        var clone = new MatchContext();
        clone._captures = new Dictionary<string, InternalCapture>(_captures, StringComparer.Ordinal);
        return clone;
    }

    public void CopyFrom(MatchContext other)
        => _captures = new Dictionary<string, InternalCapture>(other._captures, StringComparer.Ordinal);

    public bool TryAdd(string name, InternalCapture capture)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        if (_captures.ContainsKey(name))
            return false;
        _captures.Add(name, capture);
        return true;
    }
}

internal sealed class ExpressionNodeMatcher
{
    private readonly CilMethodModel _model;
    private readonly CilPatternOptions _options;
    private readonly HashSet<(Instruction load, Instruction definition)> _activeLocalExpansion = new();

    public ExpressionNodeMatcher(CilMethodModel model, CilPatternOptions options)
    {
        _model = model;
        _options = options;
    }

    public bool TryMatch(CilPatternNode pattern, TargetOccurrence target,
        MatchContext context, out TargetOccurrence matched)
    {
        var working = context.Clone();
        if (TryMatchCore(pattern, target, working, out matched))
        {
            context.CopyFrom(working);
            return true;
        }

        matched = default;
        return false;
    }

    private bool TryMatchCore(CilPatternNode pattern, TargetOccurrence target,
        MatchContext context, out TargetOccurrence matched)
    {
        if (pattern is MarkPatternNode mark)
        {
            if (!TryMatch(mark.Inner, target, context, out matched))
                return false;
            return context.TryAdd(mark.CaptureName, new ValueInternalCapture(mark.CaptureName, matched));
        }

        if (pattern is not LocalPatternNode && target.Node is TargetLocalReadNode localRead
            && _options.TemporaryNormalization == TemporaryNormalization.UniqueDefinitions
            && _model.LocalDefinitions.TryGetUniqueDefinition(localRead,
                out var store, out var storedValue, out _))
        {
            var key = (localRead.ProducerInstruction, store);
            if (_activeLocalExpansion.Add(key))
            {
                try
                {
                    // 匹配 unique reaching definition 存储的 semantic value 时，保留具体 ldloc
                    // 作为 use insertion point。
                    if (TryMatch(pattern, target.WithNode(storedValue), context, out matched))
                        return true;
                }
                finally
                {
                    _activeLocalExpansion.Remove(key);
                }
            }
        }

        switch (pattern)
        {
            case AnyPatternNode any:
                if (!TypeMatches(target.Node, any.ResultType))
                    break;
                matched = target;
                return context.TryAdd(any.CaptureName, new ValueInternalCapture(any.CaptureName, matched));

            case ArgumentPatternNode argumentPattern when target.Node is TargetArgumentNode argument:
                if (!TypeMatches(argument, argumentPattern.ResultType)
                    || argument.IsThis != argumentPattern.IsThis
                    || (argumentPattern.Index.HasValue && argument.ParameterIndex != argumentPattern.Index.Value))
                    break;
                matched = target;
                return argumentPattern.CaptureName is null
                       || context.TryAdd(argumentPattern.CaptureName,
                           new ValueInternalCapture(argumentPattern.CaptureName, matched));

            case LocalPatternNode localPattern when target.Node is TargetLocalReadNode local:
                if (!TypeMatches(local, localPattern.ResultType)
                    || (localPattern.Index.HasValue && local.Variable.Index != localPattern.Index.Value))
                    break;
                matched = target;
                return localPattern.CaptureName is null
                       || context.TryAdd(localPattern.CaptureName,
                           new ValueInternalCapture(localPattern.CaptureName, matched));

            case ConstantPatternNode constant when target.Node is TargetConstantNode targetConstant:
                if (!TypeMatches(targetConstant, constant.ResultType)
                    || !ConstantEquals(constant.Value, targetConstant.Value))
                    break;
                matched = target;
                return true;

            case FieldPatternNode fieldPattern when target.Node is TargetFieldNode field:
                if (!CecilIdentity.FieldMatches(field.Field, fieldPattern.Field))
                    break;
                if (!MatchOptionalChild(fieldPattern.Instance, field.Instance, field.ProducerInstruction, context))
                    break;
                matched = target;
                return true;

            case NewArrayPatternNode newArrayPattern when target.Node is TargetNewArrayNode newArray:
                if (!TypeMatches(newArray, newArrayPattern.ResultType)
                    || !CecilIdentity.TypeMatches(newArray.ElementType, newArrayPattern.ElementType)
                    || newArrayPattern.Lengths.Count != newArray.Lengths.Count)
                    break;
                for (var i = 0; i < newArrayPattern.Lengths.Count; i++)
                {
                    if (!TryMatch(newArrayPattern.Lengths[i],
                            TargetOccurrence.Direct(newArray.Lengths[i], newArray.ProducerInstruction), context, out _))
                    {
                        matched = default;
                        return false;
                    }
                }
                matched = target;
                return true;

            case ArrayElementPatternNode elementPattern when target.Node is TargetArrayElementNode element:
                if (!TypeMatches(element, elementPattern.ResultType))
                    break;
                if (!TryMatch(elementPattern.Array,
                        TargetOccurrence.Direct(element.Array, element.ProducerInstruction), context, out _)
                    || !TryMatch(elementPattern.Index,
                        TargetOccurrence.Direct(element.Index, element.ProducerInstruction), context, out _))
                    break;
                matched = target;
                return true;

            case ArrayLengthPatternNode lengthPattern when target.Node is TargetArrayLengthNode length:
                if (!TypeMatches(length, lengthPattern.ResultType))
                    break;
                if (!TryMatch(lengthPattern.Array,
                        TargetOccurrence.Direct(length.Array, length.ProducerInstruction), context, out _))
                    break;
                matched = target;
                return true;

            case ArrayStorePatternNode storePattern when target.Node is TargetArrayStoreNode arrayStore:
                if (!TryMatch(storePattern.Array,
                        TargetOccurrence.Direct(arrayStore.Array, arrayStore.ProducerInstruction), context, out _)
                    || !TryMatch(storePattern.Index,
                        TargetOccurrence.Direct(arrayStore.Index, arrayStore.ProducerInstruction), context, out _)
                    || !TryMatch(storePattern.Value,
                        TargetOccurrence.Direct(arrayStore.Value, arrayStore.ProducerInstruction), context, out _))
                    break;
                matched = target;
                return true;

            case CallPatternNode callPattern when target.Node is TargetCallNode call:
                if (!CecilIdentity.MethodMatches(call.Method, callPattern.Method)
                    || callPattern.Arguments.Count != call.Arguments.Count
                    || (!_options.IgnoreCallOpcodeDifference
                        && !CallOpcodeMatches(callPattern.Method, call.ProducerInstruction.OpCode.Code)))
                    break;
                if (!MatchOptionalChild(callPattern.Instance, call.Instance, call.ProducerInstruction, context))
                    break;
                for (var i = 0; i < callPattern.Arguments.Count; i++)
                {
                    var child = TargetOccurrence.Direct(call.Arguments[i], call.ProducerInstruction);
                    if (!TryMatch(callPattern.Arguments[i], child, context, out _))
                    {
                        matched = default;
                        return false;
                    }
                }
                matched = target;
                return true;

            case UnaryPatternNode unaryPattern when target.Node is TargetUnaryNode unary:
                if (NormalizeUnary(unaryPattern.Operation) != NormalizeUnary(unary.Operation)
                    || !TypeMatches(unary, unaryPattern.ResultType))
                    break;
                if (!TryMatch(unaryPattern.Operand,
                        TargetOccurrence.Direct(unary.Operand, unary.ProducerInstruction), context, out _))
                    break;
                matched = target;
                return true;

            case BinaryPatternNode binaryPattern when target.Node is TargetBinaryNode binary:
                if (binaryPattern.Operation is ExpressionType.AndAlso or ExpressionType.OrElse)
                    break;
                if (NormalizeBinary(binaryPattern.Operation) != NormalizeBinary(binary.Operation))
                    break;
                if (!TryMatch(binaryPattern.Left,
                        TargetOccurrence.Direct(binary.Left, binary.ProducerInstruction), context, out _)
                    || !TryMatch(binaryPattern.Right,
                        TargetOccurrence.Direct(binary.Right, binary.ProducerInstruction), context, out _))
                    break;
                matched = target;
                return true;
        }

        matched = default;
        return false;
    }

    private bool MatchOptionalChild(CilPatternNode? pattern, TargetExpressionNode? target,
        Instruction consumer, MatchContext context)
    {
        if (pattern is null || target is null)
            return pattern is null && target is null;
        return TryMatch(pattern, TargetOccurrence.Direct(target, consumer), context, out _);
    }

    private static ExpressionType NormalizeUnary(ExpressionType operation) => operation;

    private static ExpressionType NormalizeBinary(ExpressionType operation) => operation;

    private static bool CallOpcodeMatches(System.Reflection.MethodBase method, Code code)
    {
        if (method is System.Reflection.ConstructorInfo)
            return code == Code.Newobj;
        if (method.IsStatic)
            return code == Code.Call;
        return code == Code.Callvirt;
    }

    private bool TypeMatches(TargetExpressionNode target, Type patternType)
    {
        var targetType = target.ResultType;
        if (targetType is null)
            return patternType == typeof(void);
        if (patternType == typeof(void))
            return false;

        TypeReference patternTypeReference;
        try
        {
            patternTypeReference = _model.Method.Module.ImportReference(patternType);
        }
        catch
        {
            return CecilIdentity.TypeMatches(targetType, patternType);
        }

        if (targetType.IsSameWith(patternTypeReference))
            return true;

        var patternStackType = CreatePatternStackType(patternTypeReference);
        return !target.StackType.IsInvalid
               && !patternStackType.IsInvalid
               && target.StackType.StackValueEqualsTo(patternStackType);
    }

    private static StackType CreatePatternStackType(TypeReference patternType)
    {
        try
        {
            return StackType.Create(patternType);
        }
        catch
        {
            return StackType.Invalid;
        }
    }

    private static bool ConstantEquals(object? expected, object? actual)
    {
        if (Equals(expected, actual))
            return true;
        if (expected is bool boolean && actual is IConvertible convertible)
            return convertible.ToInt32(null) == (boolean ? 1 : 0);
        if (expected is IConvertible left && actual is IConvertible right)
        {
            try { return Convert.ToDecimal(left) == Convert.ToDecimal(right); }
            catch { return false; }
        }
        return false;
    }
}

internal sealed class ConditionFragment
{
    public ConditionFragment(BasicBlock entry,
        BasicBlock trueContinuation,
        BasicBlock falseContinuation,
        IEnumerable<BasicBlock> blocks,
        IEnumerable<ControlFlowEdge> trueExits,
        IEnumerable<ControlFlowEdge> falseExits)
    {
        Entry = entry;
        TrueContinuation = trueContinuation;
        FalseContinuation = falseContinuation;
        Blocks = new HashSet<BasicBlock>(blocks);
        TrueExits = trueExits.Distinct().ToList();
        FalseExits = falseExits.Distinct().ToList();
    }

    public BasicBlock Entry { get; }
    public BasicBlock TrueContinuation { get; }
    public BasicBlock FalseContinuation { get; }
    public HashSet<BasicBlock> Blocks { get; }
    public List<ControlFlowEdge> TrueExits { get; }
    public List<ControlFlowEdge> FalseExits { get; }
    public bool CanRewrite { get; private set; } = true;
    public string? RewriteFailureReason { get; private set; }

    public ConditionFragment Negated()
        => new(Entry, FalseContinuation, TrueContinuation, Blocks, FalseExits, TrueExits);

    public void AnalyzeRewriteSafety()
    {
        foreach (var block in Blocks)
        {
            if (block == Entry)
                continue;
            if (block.Predecessors.Any(edge => !Blocks.Contains(edge.From)))
            {
                CanRewrite = false;
                RewriteFailureReason = $"Condition block IL_{block.Leader.Offset:X4} has an external predecessor.";
                return;
            }
        }

        if (ReferenceEquals(TrueContinuation, FalseContinuation))
        {
            CanRewrite = false;
            RewriteFailureReason = "The matched condition has the same true and false continuation.";
        }
    }
}

internal sealed class ConditionPatternMatcher
{
    private readonly CilMethodModel _model;
    private readonly CilPatternOptions _options;

    public ConditionPatternMatcher(CilMethodModel model, CilPatternOptions options)
    {
        _model = model;
        _options = options;
        ExpressionMatcher = new ExpressionNodeMatcher(model, options);
    }

    public ExpressionNodeMatcher ExpressionMatcher { get; }

    public bool TryMatch(CilPatternNode pattern, BasicBlock entry, MatchContext context,
        out ConditionFragment fragment)
    {
        var working = context.Clone();
        if (TryMatchCore(pattern, entry, working, out fragment))
        {
            context.CopyFrom(working);
            return true;
        }

        fragment = null!;
        return false;
    }

    private bool TryMatchCore(CilPatternNode pattern, BasicBlock entry, MatchContext context,
        out ConditionFragment fragment)
    {
        if (pattern is MarkPatternNode mark)
        {
            if (!TryMatch(mark.Inner, entry, context, out fragment))
                return false;
            return context.TryAdd(mark.CaptureName,
                new ConditionInternalCapture(mark.CaptureName, fragment));
        }

        if (pattern is UnaryPatternNode { Operation: ExpressionType.Not } not)
        {
            if (!TryMatch(not.Operand, entry, context, out var inner))
            {
                fragment = null!;
                return false;
            }
            fragment = inner.Negated();
            return true;
        }

        if (pattern is BinaryPatternNode { Operation: ExpressionType.AndAlso } and)
            return TryMatchAnd(and, entry, context, out fragment);

        if (pattern is BinaryPatternNode { Operation: ExpressionType.OrElse } or)
            return TryMatchOr(or, entry, context, out fragment);

        return TryMatchLeaf(pattern, entry, context, out fragment);
    }

    private bool TryMatchAnd(BinaryPatternNode pattern, BasicBlock entry, MatchContext context,
        out ConditionFragment fragment)
    {
        if (!TryMatch(pattern.Left, entry, context, out var left))
        {
            fragment = null!;
            return false;
        }

        var rightEntry = _model.ResolveTransparentTarget(left.TrueContinuation,
            _options.IgnoreTransparentControlFlow);
        if (!TryMatch(pattern.Right, rightEntry, context, out var right))
        {
            fragment = null!;
            return false;
        }

        var leftFalse = _model.ResolveTransparentTarget(left.FalseContinuation,
            _options.IgnoreTransparentControlFlow);
        var rightFalse = _model.ResolveTransparentTarget(right.FalseContinuation,
            _options.IgnoreTransparentControlFlow);
        if (!ReferenceEquals(leftFalse, rightFalse))
        {
            fragment = null!;
            return false;
        }

        fragment = new ConditionFragment(entry,
            _model.ResolveTransparentTarget(right.TrueContinuation, _options.IgnoreTransparentControlFlow),
            leftFalse,
            left.Blocks.Concat(right.Blocks),
            right.TrueExits,
            left.FalseExits.Concat(right.FalseExits));
        return true;
    }

    private bool TryMatchOr(BinaryPatternNode pattern, BasicBlock entry, MatchContext context,
        out ConditionFragment fragment)
    {
        if (!TryMatch(pattern.Left, entry, context, out var left))
        {
            fragment = null!;
            return false;
        }

        var rightEntry = _model.ResolveTransparentTarget(left.FalseContinuation,
            _options.IgnoreTransparentControlFlow);
        if (!TryMatch(pattern.Right, rightEntry, context, out var right))
        {
            fragment = null!;
            return false;
        }

        var leftTrue = _model.ResolveTransparentTarget(left.TrueContinuation,
            _options.IgnoreTransparentControlFlow);
        var rightTrue = _model.ResolveTransparentTarget(right.TrueContinuation,
            _options.IgnoreTransparentControlFlow);
        if (!ReferenceEquals(leftTrue, rightTrue))
        {
            fragment = null!;
            return false;
        }

        fragment = new ConditionFragment(entry,
            leftTrue,
            _model.ResolveTransparentTarget(right.FalseContinuation, _options.IgnoreTransparentControlFlow),
            left.Blocks.Concat(right.Blocks),
            left.TrueExits.Concat(right.TrueExits),
            right.FalseExits);
        return true;
    }

    private bool TryMatchLeaf(CilPatternNode pattern, BasicBlock entry, MatchContext context,
        out ConditionFragment fragment)
    {
        fragment = null!;
        if (!_model.TryGetConditionExpression(entry, out var conditionExpression))
            return false;

        var trueEdge = entry.Successors.SingleOrDefault(static e => e.Kind == ControlFlowEdgeKind.True);
        var falseEdge = entry.Successors.SingleOrDefault(static e => e.Kind == ControlFlowEdgeKind.False);
        if (trueEdge is null || falseEdge is null)
            return false;

        var occurrence = TargetOccurrence.Direct(conditionExpression, entry.Terminator);
        if (!ExpressionMatcher.TryMatch(pattern, occurrence, context, out _))
            return false;

        fragment = new ConditionFragment(entry,
            _model.ResolveTransparentTarget(trueEdge.To, _options.IgnoreTransparentControlFlow),
            _model.ResolveTransparentTarget(falseEdge.To, _options.IgnoreTransparentControlFlow),
            new[] { entry }, new[] { trueEdge }, new[] { falseEdge });
        return true;
    }
}

