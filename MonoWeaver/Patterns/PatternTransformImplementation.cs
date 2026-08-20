using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Patterns;
using MonoWeaver.Utils;

namespace MonoWeaver.Cecil;

public static partial class PatternTransformExtensions
{
    internal static void RequireReturn(MethodReference callback, bool requireVoid,
        string operation)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));
        RequireReturn(callback.ReturnType, requireVoid, operation);
    }

    internal static void RequireReturn(TypeReference returnType, bool requireVoid,
        string operation)
    {
        if (returnType is null)
            throw new ArgumentNullException(nameof(returnType));
        var isVoid = returnType.IsVoid();
        if (requireVoid != isVoid)
        {
            var requirement = requireVoid ? "Void" : "a non-Void value";
            throw new ArgumentException($"{operation} requires a callback returning {requirement}.");
        }
    }

    private static void RequireBooleanReturn(TypeReference returnType, string operation)
    {
        RequireReturn(returnType, requireVoid: false, operation);
        if (returnType.MetadataType != MetadataType.Boolean)
            throw new ArgumentException($"{operation} requires a callback returning System.Boolean.");
    }

    private static void RequireConditionRewrite(ConditionTarget target, string operation)
    {
        if (target.CanRewrite)
            return;

        var participle = operation switch
        {
            "transform" => "transformed",
            "observe" => "observed",
            "replace" => "replaced",
            _ => operation,
        };
        throw new NotSupportedException(target.RewriteFailureReason
            ?? $"The matched condition cannot be safely {participle}.");
    }

    internal static Func<ModuleDefinition, IReadOnlyList<Instruction>> CreateMethodCallEmitter(
        MethodReference callback)
        => module =>
        {
            var imported = ReferenceEquals(callback.Module, module)
                ? callback
                : module.ImportReference(callback);
            return new[] { Instruction.Create(OpCodes.Call, imported) };
        };

    internal static void RequireAssignable(TypeReference actual, TypeReference expected,
        bool actualIsNull, string actualName, string expectedName)
    {
        var actualStackType = actualIsNull ? StackType.Null : StackType.Create(actual);
        if (actualStackType.StackValueEqualsTo(StackType.Create(expected)))
            return;

        throw new ArgumentException(
            $"{actualName} '{actual.FullName}' is not compatible with " +
            $"{expectedName} '{expected.FullName}'.");
    }

    private enum ConditionExitRewriteKind
    {
        Transform,
        Observe,
    }

    private static void ApplyConditionTransform(ConditionTarget condition,
        CallArguments arguments,
        Func<ModuleDefinition, IReadOnlyList<Instruction>> callbackEmitter,
        int extraStackSlots)
        => ApplyConditionExitRewrite(condition, arguments, callbackEmitter,
            extraStackSlots, ConditionExitRewriteKind.Transform);

    private static void ApplyConditionObserve(ConditionTarget condition,
        CallArguments arguments,
        Func<ModuleDefinition, IReadOnlyList<Instruction>> callbackEmitter,
        int extraStackSlots, RewritePlan plan)
        => ApplyConditionExitRewrite(condition, arguments, callbackEmitter,
            extraStackSlots, ConditionExitRewriteKind.Observe,
            plan ?? throw new ArgumentNullException(nameof(plan)));

    private static void ApplyConditionExitRewrite(ConditionTarget condition,
        CallArguments arguments,
        Func<ModuleDefinition, IReadOnlyList<Instruction>> callbackEmitter,
        int extraStackSlots, ConditionExitRewriteKind kind,
        RewritePlan? resultPlan = null)
    {
        if (arguments is null)
            throw new ArgumentNullException(nameof(arguments));
        if (callbackEmitter is null)
            throw new ArgumentNullException(nameof(callbackEmitter));
        if (extraStackSlots < 0)
            throw new ArgumentOutOfRangeException(nameof(extraStackSlots));

        var operation = kind == ConditionExitRewriteKind.Transform ? "transform" : "observe";
        RequireConditionRewrite(condition, operation);

        var method = condition.Method;
        var fragment = condition.Fragment;
        var trueTarget = fragment.TrueContinuation.Leader;
        var falseTarget = fragment.FalseContinuation.Leader;
        var exits = fragment.TrueExits.Select(static edge => (Edge: edge, Value: true))
            .Concat(fragment.FalseExits.Select(static edge => (Edge: edge, Value: false)))
            .ToArray();

        if (exits.Length == 0)
            throw new InvalidOperationException("The matched condition has no exit edges.");

        var groups = exits.GroupBy(static exit => exit.Edge.From).ToArray();
        ValidateConditionExitGroups(method, groups, trueTarget, falseTarget);

        method.Body.MaxStackSize = checked(method.Body.MaxStackSize + 1
            + arguments.ArgPlans.Count + extraStackSlots);
        BranchModifier.ExpandShortBranches(method.Body);
        var processor = method.Body.GetILProcessor();

        foreach (var group in groups)
        {
            var fallExit = group.SingleOrDefault(static exit => exit.Edge.IsFallThrough);
            var branchExit = group.SingleOrDefault(static exit => !exit.Edge.IsFallThrough);
            var hasFallExit = fallExit.Edge is not null;
            var hasBranchExit = branchExit.Edge is not null;
            var emitted = new List<Instruction>();

            if (!hasFallExit && hasBranchExit)
            {
                var originalFallThrough = group.Key.Successors
                    .SingleOrDefault(static edge => edge.IsFallThrough)
                    ?? throw new InvalidOperationException(
                        "The source condition has no fall-through edge.");
                EnsureSameExceptionRegion(method.Body, group.Key.Terminator,
                    originalFallThrough.To.Leader);
                emitted.Add(Instruction.Create(OpCodes.Br, originalFallThrough.To.Leader));
            }

            if (hasFallExit)
            {
                emitted.AddRange(CreateConditionExitBridge(method, arguments,
                    callbackEmitter, kind, fallExit.Value, trueTarget, falseTarget,
                    resultPlan));
            }

            Instruction? branchBridge = null;
            if (hasBranchExit)
            {
                var bridge = CreateConditionExitBridge(method, arguments,
                    callbackEmitter, kind, branchExit.Value, trueTarget, falseTarget,
                    resultPlan);
                branchBridge = bridge[0];
                emitted.AddRange(bridge);
            }

            InsertAfter(processor, group.Key.Terminator, emitted);
            if (branchBridge is not null)
                RetargetTakenBranch(group.Key.Terminator, branchBridge);
        }
    }

    private static IReadOnlyList<Instruction> CreateConditionExitBridge(
        MethodDefinition method, CallArguments arguments,
        Func<ModuleDefinition, IReadOnlyList<Instruction>> callbackEmitter,
        ConditionExitRewriteKind kind, bool originalValue,
        Instruction trueTarget, Instruction falseTarget,
        RewritePlan? resultPlan)
    {
        var result = new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldc_I4, originalValue ? 1 : 0),
        };
        result.AddRange(arguments.CreateLoadInstructions(method.Module));
        result.AddRange(callbackEmitter(method.Module));

        if (kind == ConditionExitRewriteKind.Transform)
        {
            result.Add(Instruction.Create(OpCodes.Brtrue, trueTarget));
            result.Add(Instruction.Create(OpCodes.Br, falseTarget));
        }
        else
        {
            if (resultPlan is null)
                throw new InvalidOperationException("Condition.Observe result plan is missing.");
            result.AddRange(resultPlan.CreateDestinationInstructions());
            result.Add(Instruction.Create(OpCodes.Br,
                originalValue ? trueTarget : falseTarget));
        }

        return result;
    }

    private static void ValidateConditionExitGroups(MethodDefinition method,
        IEnumerable<IGrouping<BasicBlock, (ControlFlowEdge Edge, bool Value)>> groups,
        Instruction trueTarget, Instruction falseTarget)
    {
        foreach (var group in groups)
        {
            var fallExitCount = group.Count(static exit => exit.Edge.IsFallThrough);
            var branchExitCount = group.Count(static exit => !exit.Edge.IsFallThrough);
            if (fallExitCount > 1 || branchExitCount > 1)
            {
                throw new NotSupportedException(
                    $"Condition block IL_{group.Key.Leader.Offset:X4} has an unsupported exit shape.");
            }

            EnsureAnchor(method, group.Key.Terminator);
            EnsureSameExceptionRegion(method.Body, group.Key.Terminator, trueTarget);
            EnsureSameExceptionRegion(method.Body, group.Key.Terminator, falseTarget);
        }
    }

    private static void ApplyConditionReplacement(ConditionTarget condition,
        Func<ModuleDefinition, IEnumerable<Instruction>> replacementEmitter,
        int extraStackSlots)
    {
        var method = condition.Method;
        RequireConditionRewrite(condition, "replace");
        if (extraStackSlots < 0)
            throw new ArgumentOutOfRangeException(nameof(extraStackSlots));

        var fragment = condition.Fragment;
        var entry = fragment.Entry.Leader;
        var trueTarget = fragment.TrueContinuation.Leader;
        var falseTarget = fragment.FalseContinuation.Leader;
        EnsureAnchor(method, entry);
        EnsureAnchor(method, trueTarget);
        EnsureAnchor(method, falseTarget);
        foreach (var block in fragment.Blocks)
            EnsureAnchor(method, block.Terminator);
        EnsureSameExceptionRegion(method.Body, entry, trueTarget);
        EnsureSameExceptionRegion(method.Body, entry, falseTarget);

        var replacement = replacementEmitter(method.Module)?.ToArray()
                          ?? throw new InvalidOperationException(
                              "The condition replacement emitter returned null.");
        ValidateReplacementInstructions(method.Body, replacement,
            allowEmpty: false, "Condition.Replace");
        var stackSlots = CalculateSelfContainedStackSlots(method, replacement,
            expectedFinalDepth: 1, "Condition.Replace");

        method.Body.MaxStackSize = checked(method.Body.MaxStackSize
            + stackSlots + extraStackSlots);
        BranchModifier.ExpandShortBranches(method.Body);

        var processor = method.Body.GetILProcessor();
        var firstReplacement = replacement[0];
        processor.InsertBefore(entry, firstReplacement);
        var current = firstReplacement;
        for (var i = 1; i < replacement.Length; i++)
        {
            processor.InsertAfter(current, replacement[i]);
            current = replacement[i];
        }

        var branchTrue = Instruction.Create(OpCodes.Brtrue, trueTarget);
        var branchFalse = Instruction.Create(OpCodes.Br, falseTarget);
        processor.InsertAfter(current, branchTrue);
        processor.InsertAfter(branchTrue, branchFalse);
        BranchModifier.RetargetIncoming(method.Body, entry, firstReplacement);
    }

    private static void ApplyLinearReplacement(MethodDefinition method,
        Instruction first, Instruction last,
        Func<ModuleDefinition, IEnumerable<Instruction>> replacementEmitter,
        int expectedFinalDepth, int extraStackSlots, string operation,
        bool allowEmptyReplacement = false)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (first is null)
            throw new ArgumentNullException(nameof(first));
        if (last is null)
            throw new ArgumentNullException(nameof(last));
        if (replacementEmitter is null)
            throw new ArgumentNullException(nameof(replacementEmitter));
        if (extraStackSlots < 0)
            throw new ArgumentOutOfRangeException(nameof(extraStackSlots));

        EnsureReplaceRange(method, first, last, out var oldRange);
        var replacement = replacementEmitter(method.Module)?.ToArray()
                          ?? throw new InvalidOperationException(
                              $"The {operation} emitter returned null.");

        if (replacement.Length == 0)
        {
            if (!allowEmptyReplacement)
            {
                var hint = expectedFinalDepth == 0
                    ? " Use Effect.Remove() to delete an effect."
                    : string.Empty;
                throw new ArgumentException(
                    $"{operation} must emit at least one instruction.{hint}");
            }

            // Remove 仍保留一个合法 incoming/EH anchor。
            replacement = new[] { Instruction.Create(OpCodes.Nop) };
        }

        ValidateReplacementInstructions(method.Body, replacement,
            allowEmpty: false, operation);
        ValidateReplacementTargets(replacement, oldRange, operation);
        var stackSlots = CalculateSelfContainedStackSlots(method, replacement,
            expectedFinalDepth, operation);

        method.Body.MaxStackSize = checked(method.Body.MaxStackSize
            + stackSlots + extraStackSlots);
        BranchModifier.ExpandShortBranches(method.Body);

        var processor = method.Body.GetILProcessor();
        var firstReplacement = replacement[0];
        processor.InsertBefore(first, firstReplacement);
        var current = firstReplacement;
        for (var i = 1; i < replacement.Length; i++)
        {
            processor.InsertAfter(current, replacement[i]);
            current = replacement[i];
        }

        BranchModifier.RetargetIncoming(method.Body, first, firstReplacement);
        foreach (var instruction in oldRange)
            processor.Remove(instruction);
    }

    private static void EnsureReplaceRange(MethodDefinition method, Instruction first,
        Instruction last, out IReadOnlyList<Instruction> range)
    {
        if (!method.HasBody)
            throw new ArgumentException("The target method has no IL body.", nameof(method));

        var body = method.Body;
        var firstIndex = body.Instructions.IndexOf(first);
        var lastIndex = body.Instructions.IndexOf(last);
        if (firstIndex < 0 || lastIndex < 0 || lastIndex < firstIndex)
            throw new InvalidOperationException(
                "The match is stale. Re-run the matcher after modifying IL.");

        var oldRange = body.Instructions.Skip(firstIndex)
            .Take(lastIndex - firstIndex + 1).ToArray();
        var interior = new HashSet<Instruction>(oldRange.Skip(1));
        var oldSet = new HashSet<Instruction>(oldRange);
        foreach (var instruction in body.Instructions)
        {
            if (oldSet.Contains(instruction))
                continue;

            if (instruction.Operand is Instruction target && interior.Contains(target))
            {
                throw new NotSupportedException(
                    "Cannot replace a range that has an incoming branch to its interior.");
            }
            if (instruction.Operand is Instruction[] targets && targets.Any(interior.Contains))
            {
                throw new NotSupportedException(
                    "Cannot replace a range that has an incoming switch edge to its interior.");
            }
        }

        foreach (var handler in body.ExceptionHandlers)
        {
            if (IsInteriorBoundary(handler.TryStart)
                || IsInteriorBoundary(handler.TryEnd)
                || IsInteriorBoundary(handler.HandlerStart)
                || IsInteriorBoundary(handler.HandlerEnd)
                || IsInteriorBoundary(handler.FilterStart))
            {
                throw new NotSupportedException(
                    "Cannot replace a range containing an exception-handler boundary in its interior.");
            }
        }

        range = oldRange;
        return;

        bool IsInteriorBoundary(Instruction? instruction)
            => instruction is not null && interior.Contains(instruction);
    }

    private static void ValidateReplacementInstructions(MethodBody body,
        IReadOnlyList<Instruction> replacement, bool allowEmpty, string operation)
    {
        if (!allowEmpty && replacement.Count == 0)
            throw new ArgumentException($"{operation} must emit at least one instruction.");

        foreach (var instruction in replacement)
        {
            if (instruction is null)
                throw new ArgumentException($"{operation} cannot contain null instructions.");
            if (body.Instructions.Contains(instruction))
            {
                throw new ArgumentException(
                    $"{operation} instructions must not already belong to the target method body.");
            }
            if (instruction.Previous is not null || instruction.Next is not null)
            {
                throw new ArgumentException(
                    $"{operation} instructions must be detached from any instruction list.");
            }
        }
    }

    private static void ValidateReplacementTargets(IReadOnlyList<Instruction> replacement,
        IReadOnlyList<Instruction> removed, string operation)
    {
        var removedSet = new HashSet<Instruction>(removed);
        foreach (var instruction in replacement)
        {
            if (instruction.Operand is Instruction target && removedSet.Contains(target))
            {
                throw new ArgumentException(
                    $"{operation} cannot branch into the range being removed.");
            }
            if (instruction.Operand is Instruction[] targets && targets.Any(removedSet.Contains))
            {
                throw new ArgumentException(
                    $"{operation} cannot switch into the range being removed.");
            }
        }
    }

    private static int CalculateSelfContainedStackSlots(MethodDefinition method,
        IReadOnlyList<Instruction> replacement, int expectedFinalDepth,
        string operation)
    {
        var depth = 0;
        var maxDepth = 0;
        foreach (var instruction in replacement)
        {
            if (instruction.OpCode.FlowControl is FlowControl.Branch
                or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw)
            {
                throw new NotSupportedException(
                    $"{operation} accepts only straight-line replacement IL.");
            }

            var pop = instruction.PopCount(method);
            if (pop == 0xFF)
            {
                throw new NotSupportedException(
                    $"{operation} cannot contain pop-all instructions.");
            }
            if (pop > depth)
            {
                throw new ArgumentException(
                    $"{operation} must be self-contained and cannot consume values " +
                    "from before the matched range.");
            }

            depth -= pop;
            depth += instruction.PushCount();
            maxDepth = Math.Max(maxDepth, depth);
        }

        if (depth != expectedFinalDepth)
        {
            var expected = expectedFinalDepth == 0
                ? "leave no value"
                : $"leave exactly {expectedFinalDepth} value" +
                  (expectedFinalDepth == 1 ? string.Empty : "s");
            throw new ArgumentException($"{operation} must {expected} on the stack.");
        }

        return maxDepth;
    }

    private static void InsertAfter(ILProcessor processor, Instruction anchor,
        IReadOnlyList<Instruction> instructions)
    {
        var current = anchor;
        foreach (var instruction in instructions)
        {
            processor.InsertAfter(current, instruction);
            current = instruction;
        }
    }

    private static void RetargetTakenBranch(Instruction terminator,
        Instruction newTarget)
    {
        if (terminator.Operand is Instruction)
        {
            terminator.Operand = newTarget;
            return;
        }

        throw new NotSupportedException(
            $"Branch operand type '{terminator.Operand?.GetType()}' " +
            "is not supported for condition rewriting.");
    }

    private static void EnsureAnchor(MethodDefinition method, Instruction instruction)
    {
        if (!method.HasBody || !method.Body.Instructions.Contains(instruction))
            throw new InvalidOperationException(
                "The condition match is stale. Re-run the matcher after modifying IL.");
    }

    private static void EnsureSameExceptionRegion(MethodBody body,
        Instruction source, Instruction target)
    {
        if (GetRegionSignature(body, source) == GetRegionSignature(body, target))
            return;

        throw new NotSupportedException(
            $"Condition rewriting would branch from IL_{source.Offset:X4} " +
            $"to IL_{target.Offset:X4} across an exception-region boundary.");
    }

    private static string GetRegionSignature(MethodBody body, Instruction instruction)
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
                var endIndex = end is null
                    ? body.Instructions.Count
                    : body.Instructions.IndexOf(end);
                if (index >= startIndex && index < endIndex)
                    parts.Add(i + ":" + kind);
            }
        }
        return string.Join("|", parts);
    }
}
