using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace MonoWeaver.CFG;

public partial class ILCfg
{

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackTypeRef VerifyType(StackTypeRef type1, StackTypeRef? type2, Instruction inst)
    {
        throw new NotImplementedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackTypeRef VerifyValueType(StackTypeRef type1, Instruction inst)
    {
        throw new NotImplementedException();

    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackTypeRef VerifyNum(StackTypeRef type1, Instruction inst)
    {
        throw new NotImplementedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackTypeRef VerifyInt(StackTypeRef type1, Instruction inst)
    {
        throw new NotImplementedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackTypeRef VerifyByRef(StackTypeRef type1, Instruction inst)
    {
        throw new NotImplementedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackTypeRef VerifyFloat(StackTypeRef type1, Instruction inst)
    {
        throw new NotImplementedException();
    }

    private StackTypeRef? VerifyPop1(Instruction inst, StackTypeRef[] stacks, bool initAnalysis)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {
            case Code.Box:
                return VerifyValueType(stacks[0], inst);
            case Code.Ckfinite:
                return VerifyType(stacks[0], StackTypeRef.F, inst);
            case Code.Conv_I1:
            case Code.Conv_I2:
            case Code.Conv_I4:
            case Code.Conv_I8:
            case Code.Conv_I:
            case Code.Conv_U1:
            case Code.Conv_U2:
            case Code.Conv_U4:
            case Code.Conv_U8:
            case Code.Conv_U:
            case Code.Conv_R4:
            case Code.Conv_R8:
            case Code.Conv_R_Un:
            case Code.Conv_Ovf_I1:
            case Code.Conv_Ovf_I2:
            case Code.Conv_Ovf_I4:
            case Code.Conv_Ovf_I8:
            case Code.Conv_Ovf_I:
            case Code.Conv_Ovf_U1:
            case Code.Conv_Ovf_U2:
            case Code.Conv_Ovf_U4:
            case Code.Conv_Ovf_U8:
            case Code.Conv_Ovf_U:
            case Code.Conv_Ovf_I1_Un:
            case Code.Conv_Ovf_I2_Un:
            case Code.Conv_Ovf_I4_Un:
            case Code.Conv_Ovf_I8_Un:
            case Code.Conv_Ovf_I_Un:
            case Code.Conv_Ovf_U1_Un:
            case Code.Conv_Ovf_U2_Un:
            case Code.Conv_Ovf_U4_Un:
            case Code.Conv_Ovf_U8_Un:
            case Code.Conv_Ovf_U_Un:
                return VerifyNum(stacks[0], inst);
            case Code.Dup:
                return stacks[0];
            case Code.Neg:
                return VerifyNum(stacks[0], inst);
            case Code.Not:
                return VerifyInt(stacks[0], inst);
            case Code.Pop:
                return null;
            case Code.Refanytype:
                return VerifyType(stacks[0], module.TypeSystem.TypedReference, inst);
            case Code.Refanyval:
                return VerifyByRef(stacks[0], inst);
            case Code.Starg:
                {
                    if (inst.Operand is not ushort index)
                        throw new InvalidInstructionException(typeof(ushort), inst.Operand?.GetType(), inst);
                    if (index >= _method.Parameters.Count + (_method.HasThis ? 1 : 0))
                        throw new OperandOutOfRangeException(inst,
                            $"Method parameters count: {_method.Parameters.Count + (_method.HasThis ? 1 : 0)}");
                    VerifyType(stacks[0], (_method.HasThis && index == 0) ? _method.DeclaringType
                    : _method.Parameters[index - (_method.HasThis ? 1 : 0)].ParameterType, inst);
                    return null; 
                }
            case Code.Stloc:
                {
                    if (inst.Operand is not VariableReference reference)
                        throw new InvalidInstructionException(typeof(ushort), inst.Operand?.GetType(), inst);
                    VerifyType(stacks[0], reference.VariableType, inst);
                    if (initAnalysis)
                    {
                        //TODO:
                    }
                    return null;
                }
            case Code.Stsfld:
                {
                    if (inst.Operand is not FieldReference field)
                        throw new InvalidInstructionException(typeof(FieldReference), inst.Operand?.GetType(), inst);
                    stacks[0] = field.FieldType;
                    return null;
                }
            default:
                throw new ArgumentOutOfRangeException(); //TODO:
        }
    }

    private StackTypeRef? VerifyPop1_pop1(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }

    private StackTypeRef? VerifyPopi_pop1(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }

    private StackTypeRef? VerifyPopref_pop1(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }
    private StackTypeRef? VerifyPop3(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }

    private StackTypeRef? VerifyPopref(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }

    private StackTypeRef? VerifyPopref_popi(Instruction inst, StackTypeRef[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }


    private StackTypeRef? FillVarPop(Instruction inst, ref StackTypeRef?[] args, out int len)
    {
        if (inst.Operand is not IMethodSignature sig)
        {
            len = 0;
            return null;
        }
        var paramLen = sig.Parameters.Count + (sig.HasThis ? 1 : 0);
        len = paramLen;
        if(args.Length < paramLen)
        {
            Array.Resize(ref args, paramLen);
        }
        int i = 0;
        if (sig.HasThis)
        {
            if (sig is MethodReference methodRef)
                args[i++] = methodRef.DeclaringType;
            else
                args[i++] = null;
        }
        foreach(var p in sig.Parameters)
        {
            args[i++] = p.ParameterType;
        }
        return (sig.ReturnType.Namespace == "System" && sig.ReturnType.Name == "Void") 
            ? null : StackTypeRef.Create(sig.ReturnType);
    }

}