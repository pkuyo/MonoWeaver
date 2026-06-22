using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Reflection;

namespace MonoWeaver.Utils;

internal readonly record struct OperandTargetResolveError(
    Type Expected,
    Type Current,
    string Message);

public static partial class CecilHelper
{
    private const string MonoModILLabelFullName = "MonoMod.Cil.ILLabel";

    private delegate Instruction? ILLabelTargetHandler(object label);

    private static ILLabelTargetHandler? ILLabelTargetResolver;
    private static readonly object ILLabelResolverLock = new();

    internal static bool TryResolveInstructionTarget(object? operand, out Instruction? target,
        out OperandTargetResolveError error)
    {
        target = null;
        error = default;

        if (operand is Instruction inst)
        {
            target = inst;
            return true;
        }

        if (operand is null)
        {
            error = InvalidTargetOperand(typeof(void), "Instruction target operand is null.");
            return false;
        }

        var operandType = operand.GetType();
        if (IsMonoModILLabel(operandType))
        {
            return TryResolveILLabelTarget(operand, operandType, out target, out error);
        }

        error = InvalidTargetOperand(operandType,
            $"Invalid instruction target operand type: {operandType.FullName}.");
        return false;
    }

    internal static bool TryResolveInstructionTargetArray(object? operand, out Instruction[] targets,
        out OperandTargetResolveError error)
    {
        targets = Array.Empty<Instruction>();
        error = default;

        if (operand is null)
        {
            error = InvalidTargetArrayOperand(typeof(void), "Instruction target array operand is null.");
            return false;
        }

        if (operand is Instruction[] instructions)
            return TryCopyInstructionTargets(instructions, out targets, out error);

        var operandType = operand.GetType();
        if (!operandType.IsArray)
        {
            error = InvalidTargetArrayOperand(operandType,
                $"Invalid instruction target array operand type: {operandType.FullName}.");
            return false;
        }

        var elementType = operandType.GetElementType();
        if (elementType is null || !IsMonoModILLabel(elementType))
        {
            error = InvalidTargetArrayOperand(operandType,
                $"Invalid instruction target array operand type: {operandType.FullName}.");
            return false;
        }

        var array = (Array)operand;
        targets = new Instruction[array.Length];
        if (operand is object[] labels)
        {
            for (var i = 0; i < labels.Length; i++)
            {
                if (!TryResolveInstructionTarget(labels[i], out var target, out error))
                {
                    error = error with { Message = $"{error.Message} Array index: {i}." };
                    targets = Array.Empty<Instruction>();
                    return false;
                }

                targets[i] = target!;
            }

            return true;
        }

        for (var i = 0; i < array.Length; i++)
        {
            if (!TryResolveInstructionTarget(array.GetValue(i), out var target, out error))
            {
                error = error with { Message = $"{error.Message} Array index: {i}." };
                targets = Array.Empty<Instruction>();
                return false;
            }

            targets[i] = target!;
        }
        return true;
    }

    internal static bool TryResolveOperandTargets(object? operand, out Instruction[] targets,
        out OperandTargetResolveError error)
    {
        if (operand is Array)
            return TryResolveInstructionTargetArray(operand, out targets, out error);

        if (TryResolveInstructionTarget(operand, out var target, out error))
        {
            targets = new[] { target! };
            return true;
        }

        targets = Array.Empty<Instruction>();
        return false;
    }

    private static bool TryCopyInstructionTargets(Instruction[] instructions, out Instruction[] targets,
        out OperandTargetResolveError error)
    {
        targets = new Instruction[instructions.Length];
        error = default;

        for (var i = 0; i < instructions.Length; i++)
        {
            var target = instructions[i];
            if (target is null)
            {
                error = InvalidTargetOperand(typeof(void),
                    $"Instruction target array contains a null target. Array index: {i}.");
                targets = Array.Empty<Instruction>();
                return false;
            }

            targets[i] = target;
        }

        return true;
    }

    private static bool TryResolveILLabelTarget(object label, Type labelType, out Instruction? target,
        out OperandTargetResolveError error)
    {
        target = null;
        error = default;
        BuildMonoModResolveStrategy(labelType);
        if (ILLabelTargetResolver is null)
        {
            error = InvalidTargetOperand(labelType,
                $"ILLabel operand type has no Target field: {labelType.FullName}.");
            return false;
        }

        target = ILLabelTargetResolver(label);
        if (target is null)
        {
            error = InvalidTargetOperand(typeof(void),
                $"ILLabel target is null: {labelType.FullName}.");
            return false;
        }

        return true;
    }

    private static void BuildMonoModResolveStrategy(Type type)
    {
        lock (ILLabelResolverLock)
        {
            if (ILLabelTargetResolver is not null)
                return;

            // ILLabel.Target is public in current and legacy MonoMod versions, but use
            // non-public lookup as well so the core package remains tolerant of adapters.
            var bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var property = type.GetProperty("Target", bindingFlags);
            if (property is not null && typeof(Instruction).IsAssignableFrom(property.PropertyType))
            {
                // Do not emit or compile a dynamic helper here. Reflection is used only while
                // constructing a method model and avoids a hard MonoMod reference in the core package.
                ILLabelTargetResolver = label => (Instruction?)property.GetValue(label, null);
                return;
            }

            // Some MonoMod versions expose Target as a field rather than a property.
            var field = type.GetField("Target", bindingFlags);
            if (field is not null && typeof(Instruction).IsAssignableFrom(field.FieldType))
                ILLabelTargetResolver = label => (Instruction?)field.GetValue(label);
        }
    }

    private static bool IsMonoModILLabel(Type type)
        => type.FullName == MonoModILLabelFullName;

    private static OperandTargetResolveError InvalidTargetOperand(Type current, string message)
        => new(typeof(Instruction), current,
            $"{message} Expected {typeof(Instruction).FullName} or {MonoModILLabelFullName}.");

    private static OperandTargetResolveError InvalidTargetArrayOperand(Type current, string message)
        => new(typeof(Instruction[]), current,
            $"{message} Expected {typeof(Instruction[]).FullName} or {MonoModILLabelFullName}[].");
}
