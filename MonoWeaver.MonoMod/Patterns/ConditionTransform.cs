using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;

namespace MonoWeaver.MonoMod.Patterns;

public static class ConditionTransformExtensions
{
    /// <summary>
    /// 将 captured condition 的 true/false结果传递给 <paramref name="callback"/>，
    /// 并把返回结果给回原始位置。
    /// </summary>
    public static void Transform<TDelegate>(this MatchedCondition condition, ILContext context,
        TDelegate callback, Action<DelegateArguments>? additionalArguments = null)
        where TDelegate : Delegate
    {
        if (condition is null)
            throw new ArgumentNullException(nameof(condition));
        if (context is null)
            throw new ArgumentNullException(nameof(context));
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        if (!condition.CanRewrite)
            throw new InvalidOperationException(condition.RewriteFailureReason
                ?? "The captured condition cannot be safely rewritten.");

        var invoke = typeof(TDelegate).GetMethod("Invoke")
                     ?? throw new ArgumentException($"'{typeof(TDelegate)}' is not a delegate type.");
        var parameters = invoke.GetParameters();
        if (parameters.Length == 0 || parameters[0].ParameterType != typeof(bool)
            || invoke.ReturnType != typeof(bool))
        {
            throw new ArgumentException("A condition transform delegate must have Boolean as its first parameter and return Boolean.", nameof(callback));
        }

        var arguments = new DelegateArguments(context);
        additionalArguments?.Invoke(arguments);
        if (parameters.Length != arguments.Count + 1)
        {
            throw new ArgumentException(
                $"Condition delegate expects {parameters.Length} parameters, but the condition plus additional sources supply {arguments.Count + 1}.",
                nameof(callback));
        }

        var fragment = condition.Fragment;
        var trueTarget = fragment.TrueContinuation.Leader;
        var falseTarget = fragment.FalseContinuation.Leader;
        var exitGroups = fragment.TrueExits.Select(static edge => new ExitInfo(edge, true))
            .Concat(fragment.FalseExits.Select(static edge => new ExitInfo(edge, false)))
            .GroupBy(static exit => exit.Edge.From)
            .Select(static group => new ExitGroup(group.Key, group.ToArray()))
            .ToArray();

        foreach (var group in exitGroups)
        {
            if (group.FallExitCount > 1 || group.BranchExitCount > 1)
                throw new NotSupportedException($"Condition block IL_{group.Source.Leader.Offset:X4} has an unsupported exit shape.");

            EnsureSameExceptionRegion(context.Body, group.Source.Terminator, trueTarget);
            EnsureSameExceptionRegion(context.Body, group.Source.Terminator, falseTarget);
        }

        if (CanUseSharedBridge(context.Body, exitGroups))
        {
            EmitSharedBridge(context, callback, arguments, trueTarget, falseTarget, exitGroups);
            return;
        }

        EmitPerExitBridges(context, callback, arguments, trueTarget, falseTarget, exitGroups);
    }

    private static void EmitSharedBridge<TDelegate>(ILContext context, TDelegate callback,
        DelegateArguments arguments, Instruction trueTarget, Instruction falseTarget,
        IReadOnlyList<ExitGroup> exitGroups)
        where TDelegate : Delegate
    {
        var anchor = exitGroups.FirstOrDefault(static group => group.FallExit is not null)
                     ?? exitGroups[0];
        var anchorFallExit = anchor.FallExit;
        var firstValue = anchorFallExit?.Value ?? true;

        var cursor = new ILCursor(context).Goto(anchor.Source.Terminator, MoveType.After);
        if (anchorFallExit is null && anchor.BranchExit is not null)
        {
            if (anchor.AllFallThrough is null)
                throw new InvalidOperationException("The source condition has no fall-through edge.");
            EnsureSameExceptionRegion(context.Body, anchor.Source.Terminator, anchor.AllFallThrough.To.Leader);
            cursor.Emit(OpCodes.Br, context.DefineLabel(anchor.AllFallThrough.To.Leader));
        }

        var firstEntry = EmitSharedCallback(cursor, context, callback, arguments, firstValue,
            trueTarget, falseTarget, out var callbackEntry);
        var secondEntry = EmitValueBranch(cursor, context, !firstValue, callbackEntry);
        var trueValueEntry = firstValue ? firstEntry : secondEntry;
        var falseValueEntry = firstValue ? secondEntry : firstEntry;

        foreach (var group in exitGroups)
        {
            if (!ReferenceEquals(group, anchor) && group.FallExit is not null)
            {
                var fallTarget = group.FallExit.Value ? trueValueEntry : falseValueEntry;
                var fallCursor = new ILCursor(context).Goto(group.Source.Terminator, MoveType.After);
                fallCursor.Emit(OpCodes.Br, context.DefineLabel(fallTarget));
            }

            if (group.BranchExit is not null)
            {
                RetargetTakenBranch(group.Source.Terminator,
                    group.BranchExit.Value ? trueValueEntry : falseValueEntry);
            }
        }
    }

    private static bool CanUseSharedBridge(Mono.Cecil.Cil.MethodBody body, IReadOnlyList<ExitGroup> exitGroups)
    {
        if (exitGroups.Count == 0)
            return false;

        var anchor = exitGroups.FirstOrDefault(static group => group.FallExit is not null)
                     ?? exitGroups[0];
        var anchorRegion = GetRegionSignature(body, anchor.Source.Terminator);
        return exitGroups.All(group => GetRegionSignature(body, group.Source.Terminator) == anchorRegion);
    }

    private static void EmitPerExitBridges<TDelegate>(ILContext context, TDelegate callback,
        DelegateArguments arguments, Instruction trueTarget, Instruction falseTarget,
        IReadOnlyList<ExitGroup> exitGroups)
        where TDelegate : Delegate
    {
        foreach (var group in exitGroups)
        {
            var source = group.Source;
            var cursor = new ILCursor(context).Goto(source.Terminator, MoveType.After);

            if (group.FallExit is null && group.BranchExit is not null)
            {
                if (group.AllFallThrough is null)
                    throw new InvalidOperationException("The source condition has no fall-through edge.");
                EnsureSameExceptionRegion(context.Body, source.Terminator, group.AllFallThrough.To.Leader);
                cursor.Emit(OpCodes.Br, context.DefineLabel(group.AllFallThrough.To.Leader));
            }

            if (group.FallExit is not null)
            {
                EmitBridge(cursor, context, callback, arguments, group.FallExit.Value,
                    trueTarget, falseTarget);
            }

            if (group.BranchExit is not null)
            {
                var bridge = EmitBridge(cursor, context, callback, arguments,
                    group.BranchExit.Value, trueTarget, falseTarget);
                RetargetTakenBranch(source.Terminator, bridge);
            }
        }
    }

    private static Instruction EmitSharedCallback<TDelegate>(ILCursor cursor, ILContext context,
        TDelegate callback, DelegateArguments arguments, bool originalValue,
        Instruction trueTarget, Instruction falseTarget, out Instruction callbackEntry)
        where TDelegate : Delegate
    {
        cursor.Emit(OpCodes.Ldc_I4, originalValue ? 1 : 0);
        var valueEntry = cursor.Prev;
        cursor.Emit(OpCodes.Nop);
        callbackEntry = cursor.Prev;
        arguments.Emit(cursor);
        cursor.EmitDelegate(callback);
        cursor.Emit(OpCodes.Brtrue, context.DefineLabel(trueTarget));
        cursor.Emit(OpCodes.Br, context.DefineLabel(falseTarget));
        return valueEntry;
    }

    private static Instruction EmitValueBranch(ILCursor cursor, ILContext context,
        bool originalValue, Instruction callbackEntry)
    {
        cursor.Emit(OpCodes.Ldc_I4, originalValue ? 1 : 0);
        var valueEntry = cursor.Prev;
        cursor.Emit(OpCodes.Br, context.DefineLabel(callbackEntry));
        return valueEntry;
    }

    private static Instruction EmitBridge<TDelegate>(ILCursor cursor, ILContext context,
        TDelegate callback, DelegateArguments arguments, bool originalValue,
        Instruction trueTarget, Instruction falseTarget)
        where TDelegate : Delegate
    {
        cursor.Emit(OpCodes.Ldc_I4, originalValue ? 1 : 0);
        var first = cursor.Prev;
        arguments.Emit(cursor);
        cursor.EmitDelegate(callback);
        cursor.Emit(OpCodes.Brtrue, context.DefineLabel(trueTarget));
        cursor.Emit(OpCodes.Br, context.DefineLabel(falseTarget));
        return first;
    }

    private static void RetargetTakenBranch(Instruction terminator, Instruction newTarget)
    {
        switch (terminator.Operand)
        {
            case ILLabel label:
                label.Target = newTarget;
                break;
            case Instruction:
                terminator.Operand = newTarget;
                break;
            default:
                throw new NotSupportedException(
                    $"Branch operand type '{terminator.Operand?.GetType()}' is not supported for condition rewriting.");
        }
    }

    private static void EnsureSameExceptionRegion(Mono.Cecil.Cil.MethodBody body, Instruction source, Instruction target)
    {
        if (GetRegionSignature(body, source) == GetRegionSignature(body, target))
            return;

        throw new NotSupportedException(
            $"Condition rewriting would create an explicit branch from IL_{source.Offset:X4} to IL_{target.Offset:X4} across an exception-region boundary.");
    }

    private static string GetRegionSignature(Mono.Cecil.Cil.MethodBody body, Instruction instruction)
    {
        var index = body.Instructions.IndexOf(instruction);
        var parts = new List<string>();
        for (var i = 0; i < body.ExceptionHandlers.Count; i++)
        {
            var handler = body.ExceptionHandlers[i];
            Add("T", handler.TryStart, handler.TryEnd);
            Add("F", handler.FilterStart, handler.HandlerStart);
            Add("H", handler.HandlerStart, handler.HandlerEnd);

            void Add(string kind, Instruction? start, Instruction? end)
            {
                if (start is null)
                    return;
                var startIndex = body.Instructions.IndexOf(start);
                var endIndex = end is null ? body.Instructions.Count : body.Instructions.IndexOf(end);
                if (index >= startIndex && index < endIndex)
                    parts.Add($"{i}:{kind}");
            }
        }
        return string.Join("|", parts);
    }

    private sealed class ExitInfo
    {
        public ExitInfo(ControlFlowEdge edge, bool value)
        {
            Edge = edge;
            Value = value;
        }

        public ControlFlowEdge Edge { get; }
        public bool Value { get; }
    }

    private sealed class ExitGroup
    {
        public ExitGroup(BasicBlock source, IReadOnlyList<ExitInfo> exits)
        {
            Source = source;
            FallExitCount = exits.Count(static exit => exit.Edge.IsFallThrough);
            BranchExitCount = exits.Count(static exit => !exit.Edge.IsFallThrough);
            FallExit = exits.SingleOrDefault(static exit => exit.Edge.IsFallThrough);
            BranchExit = exits.SingleOrDefault(static exit => !exit.Edge.IsFallThrough);
            AllFallThrough = source.Successors.SingleOrDefault(static edge => edge.IsFallThrough);
        }

        public BasicBlock Source { get; }
        public int FallExitCount { get; }
        public int BranchExitCount { get; }
        public ExitInfo? FallExit { get; }
        public ExitInfo? BranchExit { get; }
        public ControlFlowEdge? AllFallThrough { get; }
    }
}
