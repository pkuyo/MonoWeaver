using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;

namespace MonoWeaver.Patterns;

internal abstract class TargetExpressionNode
{
    protected TargetExpressionNode(TypeReference? resultType, Instruction firstInstruction,
        Instruction producerInstruction, StackType? stackType = null)
    {
        ResultType = resultType;
        StackType = stackType ?? CreateStackType(resultType);
        FirstInstruction = firstInstruction;
        ProducerInstruction = producerInstruction;
    }

    public TypeReference? ResultType { get; }
    public StackType StackType { get; }
    public Instruction FirstInstruction { get; }
    public Instruction ProducerInstruction { get; }

    private static StackType CreateStackType(TypeReference? resultType)
    {
        if (resultType is null || resultType.MetadataType == MetadataType.Void)
            return StackType.Invalid;

        try
        {
            return StackType.Create(resultType);
        }
        catch
        {
            return StackType.Invalid;
        }
    }
}

internal sealed class TargetUnknownNode : TargetExpressionNode
{
    public TargetUnknownNode(TypeReference? type, Instruction anchor, string reason)
        : base(type, anchor, anchor)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

internal sealed class TargetArgumentNode : TargetExpressionNode
{
    public TargetArgumentNode(TypeReference type, Instruction instruction, bool isThis, int parameterIndex,
        ParameterDefinition? parameter, StackType? stackType = null)
        : base(type, instruction, instruction, stackType)
    {
        IsThis = isThis;
        ParameterIndex = parameterIndex;
        Parameter = parameter;
    }

    public bool IsThis { get; }
    public int ParameterIndex { get; }
    public ParameterDefinition? Parameter { get; }
}

internal sealed class TargetLocalReadNode : TargetExpressionNode
{
    public TargetLocalReadNode(VariableDefinition variable, Instruction instruction)
        : base(variable.VariableType, instruction, instruction)
    {
        Variable = variable;
    }

    public VariableDefinition Variable { get; }
}

internal sealed class TargetConstantNode : TargetExpressionNode
{
    public TargetConstantNode(object? value, TypeReference? resultType, Instruction instruction,
        StackType? stackType = null)
        : base(resultType, instruction, instruction, stackType)
    {
        Value = value;
    }

    public object? Value { get; }
}

internal sealed class TargetFieldNode : TargetExpressionNode
{
    public TargetFieldNode(FieldReference field, TargetExpressionNode? instance, Instruction instruction)
        : base(field.FieldType, instance?.FirstInstruction ?? instruction, instruction)
    {
        Field = field;
        Instance = instance;
    }

    public FieldReference Field { get; }
    public TargetExpressionNode? Instance { get; }
}

internal sealed class TargetCallNode : TargetExpressionNode
{
    public TargetCallNode(MethodReference method, TargetExpressionNode? instance,
        IReadOnlyList<TargetExpressionNode> arguments, TypeReference? resultType, Instruction instruction)
        : base(resultType, FindFirst(instance, arguments, instruction), instruction)
    {
        Method = method;
        Instance = instance;
        Arguments = arguments;
    }

    public MethodReference Method { get; }
    public TargetExpressionNode? Instance { get; }
    public IReadOnlyList<TargetExpressionNode> Arguments { get; }

    private static Instruction FindFirst(TargetExpressionNode? instance,
        IReadOnlyList<TargetExpressionNode> arguments, Instruction fallback)
    {
        if (instance is not null)
            return instance.FirstInstruction;
        return arguments.Count == 0 ? fallback : arguments[0].FirstInstruction;
    }
}

internal sealed class TargetUnaryNode : TargetExpressionNode
{
    public TargetUnaryNode(ExpressionType operation, TargetExpressionNode operand, TypeReference? resultType,
        Instruction instruction, StackType? stackType = null)
        : base(resultType, operand.FirstInstruction, instruction, stackType)
    {
        Operation = operation;
        Operand = operand;
    }

    public ExpressionType Operation { get; }
    public TargetExpressionNode Operand { get; }
}

internal sealed class TargetBinaryNode : TargetExpressionNode
{
    public TargetBinaryNode(ExpressionType operation, TargetExpressionNode left, TargetExpressionNode right,
        TypeReference? resultType, Instruction instruction, StackType? stackType = null)
        : base(resultType, left.FirstInstruction, instruction, stackType)
    {
        Operation = operation;
        Left = left;
        Right = right;
    }

    public ExpressionType Operation { get; }
    public TargetExpressionNode Left { get; }
    public TargetExpressionNode Right { get; }
}

internal sealed class TargetOperationNode : TargetExpressionNode
{
    public TargetOperationNode(Code code, IReadOnlyList<TargetExpressionNode> inputs, TypeReference? resultType,
        Instruction instruction, StackType? stackType = null)
        : base(resultType, inputs.Count == 0 ? instruction : inputs[0].FirstInstruction, instruction, stackType)
    {
        Code = code;
        Inputs = inputs;
    }

    public Code Code { get; }
    public IReadOnlyList<TargetExpressionNode> Inputs { get; }
}

internal sealed class TargetEffect
{
    public TargetEffect(TargetExpressionNode expression, Instruction terminalInstruction)
    {
        Expression = expression;
        TerminalInstruction = terminalInstruction;
    }

    public TargetExpressionNode Expression { get; }
    public Instruction TerminalInstruction { get; }
}
