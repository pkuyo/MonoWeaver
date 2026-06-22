using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoWeaver.Patterns;

namespace MonoWeaver.MonoMod.Patterns;

/// <summary>改写 captured short-circuit condition 的 logical result。</summary>
public static class ConditionTransformExtensions
{
    /// <summary>
    /// 将每个 true/false exit materialize 为 Boolean，调用 <paramref name="callback"/>，
    /// 并把 callback result 路由回原始 true 或 false continuation。matched condition 内部的
    /// 原始 evaluation order 和 short-circuit 行为会被保留。
    /// </summary>
    /// <remarks>
    /// delegate 的第一个 parameter 和 return type 必须是 <see cref="bool"/>。additional argument
    /// 使用 matched value call 同一套 builder 加载。当 captured condition 有 external entry，
    /// 或跨越 exception-region boundary 且需要 unsafe synthetic branch 时，rewrite 会被拒绝。
    /// </remarks>
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
        var exits = fragment.TrueExits.Select(static edge => (edge, value: true))
            .Concat(fragment.FalseExits.Select(static edge => (edge, value: false)))
            .ToArray();

        foreach (var sourceGroup in exits.GroupBy(static item => item.edge.From))
        {
            var source = sourceGroup.Key;
            var sourceExits = sourceGroup.ToArray();
            if (sourceExits.Count(static item => item.edge.IsFallThrough) > 1
                || sourceExits.Count(static item => !item.edge.IsFallThrough) > 1)
            {
                throw new NotSupportedException($"Condition block IL_{source.Leader.Offset:X4} has an unsupported exit shape.");
            }

            EnsureSameExceptionRegion(context.Body, source.Terminator, trueTarget);
            EnsureSameExceptionRegion(context.Body, source.Terminator, falseTarget);

            var allFallThrough = source.Successors.SingleOrDefault(static edge => edge.IsFallThrough);
            var fallExit = sourceExits.SingleOrDefault(static item => item.edge.IsFallThrough);
            var branchExit = sourceExits.SingleOrDefault(static item => !item.edge.IsFallThrough);
            var cursor = new ILCursor(context).Goto(source.Terminator, MoveType.After);

            // 如果只有 taken branch 离开 fragment，则显式 branch 会保留原始 fall-through path，
            // bridge code 放在它后面。
            if (fallExit.edge is null && branchExit.edge is not null)
            {
                if (allFallThrough is null)
                    throw new InvalidOperationException("The source condition has no fall-through edge.");
                EnsureSameExceptionRegion(context.Body, source.Terminator, allFallThrough.To.Leader);
                cursor.Emit(OpCodes.Br, context.DefineLabel(allFallThrough.To.Leader));
            }

            if (fallExit.edge is not null)
            {
                EmitBridge(cursor, context, callback, arguments, fallExit.value, trueTarget, falseTarget);
            }

            if (branchExit.edge is not null)
            {
                var bridge = EmitBridge(cursor, context, callback, arguments,
                    branchExit.value, trueTarget, falseTarget);
                RetargetTakenBranch(source.Terminator, bridge);
            }
        }
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
}
