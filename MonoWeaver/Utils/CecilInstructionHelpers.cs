using Mono.Cecil;
using Mono.Cecil.Cil;

namespace MonoWeaver.Utils;

internal static class CecilInstructionHelpers
{
    public static bool TryGetArgument(MethodDefinition method, Instruction instruction,
        out bool isThis, out int parameterIndex, out ParameterDefinition? parameter)
    {
        isThis = false;
        parameterIndex = -1;
        parameter = null;

        if (TryGetMacroArgumentSlot(instruction.OpCode.Code, out var slot))
            return SetArgumentFromSlot(method, slot, out isThis, out parameterIndex, out parameter);

        if (!IsArgumentOperandCode(instruction.OpCode.Code))
            return false;

        if (instruction.Operand is ParameterReference parameterReference)
        {
            return SetArgumentFromParameterIndex(method, parameterReference.Index,
                out isThis, out parameterIndex, out parameter);
        }

        if (!TryReadNumericIndex(instruction.Operand, out slot))
            return false;

        return SetArgumentFromSlot(method, slot, out isThis, out parameterIndex, out parameter);
    }

    public static bool TryGetLocal(MethodDefinition method, Instruction instruction,
        out int index, out VariableDefinition? variable)
    {
        index = -1;
        variable = null;

        if (TryGetMacroLocalIndex(instruction.OpCode.Code, out index))
            return SetLocalFromIndex(method, index, out variable);

        if (!IsLocalOperandCode(instruction.OpCode.Code))
            return false;

        if (instruction.Operand is VariableReference variableReference)
        {
            index = variableReference.Index;
            return SetLocalFromIndex(method, index, out variable);
        }

        if (!TryReadNumericIndex(instruction.Operand, out index))
            return false;

        return SetLocalFromIndex(method, index, out variable);
    }

    public static bool TryGetMacroArgumentSlot(Code code, out int slot)
    {
        switch (code)
        {
            case Code.Ldarg_0:
                slot = 0;
                return true;
            case Code.Ldarg_1:
                slot = 1;
                return true;
            case Code.Ldarg_2:
                slot = 2;
                return true;
            case Code.Ldarg_3:
                slot = 3;
                return true;
            default:
                slot = -1;
                return false;
        }
    }

    public static bool TryGetMacroLocalIndex(Code code, out int index)
    {
        switch (code)
        {
            case Code.Ldloc_0:
            case Code.Stloc_0:
                index = 0;
                return true;
            case Code.Ldloc_1:
            case Code.Stloc_1:
                index = 1;
                return true;
            case Code.Ldloc_2:
            case Code.Stloc_2:
                index = 2;
                return true;
            case Code.Ldloc_3:
            case Code.Stloc_3:
                index = 3;
                return true;
            default:
                index = -1;
                return false;
        }
    }

    public static bool IsLoadArgument(Instruction instruction)
        => IsLoadArgument(instruction.OpCode.Code);

    public static bool IsLoadArgument(Code code)
        => code is Code.Ldarg or Code.Ldarg_S
            or Code.Ldarg_0 or Code.Ldarg_1 or Code.Ldarg_2 or Code.Ldarg_3;

    public static bool IsLoadArgumentAddress(Instruction instruction)
        => IsLoadArgumentAddress(instruction.OpCode.Code);

    public static bool IsLoadArgumentAddress(Code code)
        => code is Code.Ldarga or Code.Ldarga_S;

    public static bool IsStoreArgument(Instruction instruction)
        => IsStoreArgument(instruction.OpCode.Code);

    public static bool IsStoreArgument(Code code)
        => code is Code.Starg or Code.Starg_S;

    public static bool IsLoadLocal(Instruction instruction)
        => IsLoadLocal(instruction.OpCode.Code);

    public static bool IsLoadLocal(Code code)
        => code is Code.Ldloc or Code.Ldloc_S
            or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3;

    public static bool IsStoreLocal(Instruction instruction)
        => IsStoreLocal(instruction.OpCode.Code);

    public static bool IsStoreLocal(Code code)
        => code is Code.Stloc or Code.Stloc_S
            or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3;

    public static bool IsLoadLocalAddress(Instruction instruction)
        => IsLoadLocalAddress(instruction.OpCode.Code);

    public static bool IsLoadLocalAddress(Code code)
        => code is Code.Ldloca or Code.Ldloca_S;

    public static bool IsConditionalBranch(Instruction instruction)
        => instruction.OpCode.FlowControl == FlowControl.Cond_Branch && instruction.OpCode.Code != Code.Switch;

    public static bool IsUnconditionalBranch(Instruction instruction)
        => instruction.OpCode.FlowControl == FlowControl.Branch;

    public static bool IsBranchOnFalse(Instruction instruction)
        => instruction.OpCode.Code is Code.Brfalse or Code.Brfalse_S;

    public static bool IsBranchOnTrue(Instruction instruction)
        => instruction.OpCode.Code is Code.Brtrue or Code.Brtrue_S;

    private static bool IsArgumentOperandCode(Code code)
        => code is Code.Ldarg or Code.Ldarg_S
            or Code.Ldarga or Code.Ldarga_S
            or Code.Starg or Code.Starg_S;

    private static bool IsLocalOperandCode(Code code)
        => IsLoadLocal(code) || IsStoreLocal(code) || IsLoadLocalAddress(code);

    private static bool SetArgumentFromSlot(MethodDefinition method, int slot,
        out bool isThis, out int parameterIndex, out ParameterDefinition? parameter)
    {
        isThis = false;
        parameterIndex = method.HasThis ? slot - 1 : slot;
        parameter = null;

        if (method.HasThis && slot == 0)
        {
            isThis = true;
            return true;
        }

        if (parameterIndex >= 0 && parameterIndex < method.Parameters.Count)
            parameter = method.Parameters[parameterIndex];
        return parameter is not null;
    }

    private static bool SetArgumentFromParameterIndex(MethodDefinition method, int index,
        out bool isThis, out int parameterIndex, out ParameterDefinition? parameter)
    {
        isThis = false;
        parameterIndex = index;
        parameter = null;

        if (index == -1)
        {
            isThis = method.HasThis;
            return isThis;
        }

        if (index >= 0 && index < method.Parameters.Count)
            parameter = method.Parameters[index];
        return parameter is not null;
    }

    private static bool SetLocalFromIndex(MethodDefinition method, int index,
        out VariableDefinition? variable)
    {
        if (index >= 0 && index < method.Body.Variables.Count)
            variable = method.Body.Variables[index];
        else
            variable = null;

        return variable is not null;
    }

    private static bool TryReadNumericIndex(object? operand, out int index)
    {
        switch (operand)
        {
            case byte b:
                index = b;
                return true;
            case sbyte b:
                index = b;
                return true;
            case short s:
                index = s;
                return true;
            case ushort s:
                index = s;
                return true;
            case int i:
                index = i;
                return true;
            case uint i when i <= int.MaxValue:
                index = (int)i;
                return true;
            default:
                index = -1;
                return false;
        }
    }
}
