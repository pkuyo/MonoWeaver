using System;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace MonoWeaver.CFG;

public enum CFGExceptionType
{
    None = 0,
    StackUnderflow,
    StackOverflow,
    TypeMismatch,
    InvalidOperation,
    OutOfRange,
    ResolveFailed
}

public class CFGException : Exception
{
    public CFGExceptionType Type { get; }
    
    public Instruction? Instruction { get; }

    public CFGException(CFGExceptionType type, Instruction? instruction, string? message = null) : base(message ?? type.ToString())
    {
        Type = type;
        Instruction = instruction;
    }
}

public class TypeMismatchException : CFGException
{
    public TypeReference ExpectType { get; }
    public TypeReference? CurrentType { get; }

    public TypeMismatchException(TypeReference expectType, TypeReference? currentType,
        Instruction instruction, string? message = null) 
        : base(CFGExceptionType.TypeMismatch, instruction, message)
    {
        ExpectType = expectType;
        CurrentType = currentType;
    }
}

public class OperandOutOfRangeException : CFGException
{
    public OperandOutOfRangeException(Instruction instruction, string? message = null) 
        : base(CFGExceptionType.OutOfRange, instruction, message)
    {
    }
}

public class InvalidInstructionException : CFGException
{
    public Type ExpectType { get; }
    public Type? CurrentType { get; }

    public InvalidInstructionException(Type expectType, Type? currentType,
        Instruction instruction, string? message = null) 
        : base(CFGExceptionType.InvalidOperation, instruction, message)
    {
        ExpectType = expectType;
        CurrentType = currentType;
    }
}

public class ResolveFailedException : CFGException
{
    public TypeReference Type { get; }

    public ResolveFailedException(TypeReference type, string? message = null) 
        : base(CFGExceptionType.InvalidOperation, null, message)
    {
        Type = type;
    }
}