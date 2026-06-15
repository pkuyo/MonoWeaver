using Mono.Cecil.Cil;
using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace MonoWeaver.Utils;

internal readonly record struct OperandTargetResolveError(
    Type Expected,
    Type Current,
    string Message);

public static partial class CecilHelper
{
    private const string MonoModILLabelFullName = "MonoMod.Cil.ILLabel";

    private delegate Instruction? ILLabelTargetResolver(object label);

    private static readonly ConcurrentDictionary<Type, ILLabelTargetResolver?> ILLabelTargetResolvers = new();

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

        var resolver = ILLabelTargetResolvers.GetOrAdd(labelType, CreateILLabelTargetResolver);
        if (resolver is null)
        {
            error = InvalidTargetOperand(labelType,
                $"ILLabel operand type has no Target field: {labelType.FullName}.");
            return false;
        }

        target = resolver(label);
        if (target is null)
        {
            error = InvalidTargetOperand(typeof(void),
                $"ILLabel target is null: {labelType.FullName}.");
            return false;
        }

        return true;
    }

    private static ILLabelTargetResolver? CreateILLabelTargetResolver(Type labelType)
    {
        var targetField = labelType.GetField("Target",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (targetField is null || !typeof(Instruction).IsAssignableFrom(targetField.FieldType))
            return null;

        var label = Expression.Parameter(typeof(object), "label");
        var target = Expression.Field(Expression.Convert(label, labelType), targetField);
        var body = Expression.Convert(target, typeof(Instruction));
        return Expression.Lambda<ILLabelTargetResolver>(body, label).Compile();
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
