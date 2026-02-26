using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;

namespace MonoWeaver.CFG;

public partial class ILMethodAnalyzer
{

    private IMemberDefinition? ResolveWithDiagnostic(MemberReference memberReference)
    {
        if (memberReference.Resolve() is { } re)
            return re;
        ReportDiagnostic(CFGDiagnostic.ResolveFailed(memberReference));
        return null;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackType VerifyType(StackType type1, StackType type2, Instruction inst)
    {
        throw new NotImplementedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackType VerifyValueType(StackType type1, Instruction inst)
    {
        throw new NotImplementedException();

    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackType VerifyNum(StackType type1, Instruction inst)
    {
        throw new NotImplementedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackType VerifyInt(StackType type1, Instruction inst)
    {
        throw new NotImplementedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackType VerifyByRef(StackType type1, Instruction inst)
    {
        throw new NotImplementedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackType VerifyFloat(StackType type1, Instruction inst)
    {
        throw new NotImplementedException();
    }

    private StackType VerifyPop1(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {
            case Code.Box:
                return VerifyValueType(stacks[0], inst);
            case Code.Ckfinite:
                return VerifyType(stacks[0], StackType.F, inst);
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
                return StackType.Invalid;
            case Code.Refanytype:
                return VerifyType(stacks[0], module.TypeSystem.TypedReference, inst);
            case Code.Refanyval:
                return VerifyByRef(stacks[0], inst);
            case Code.Starg:
                {
                    if (inst.Operand is not ParameterReference pr)
                    {
                        ReportDiagnostic(CFGDiagnostic.InvalidOperand(typeof(ParameterReference),
                            inst.Operand?.GetType() ?? typeof(void), inst));
                        return StackType.Invalid;
                    }

                    var index = pr.Index;
                    if (index >= _method.Parameters.Count || index < 0)
                    {
                        if (index != -1 || !_method.HasThis)
                            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.OutOfRange, inst));
                    }

                    VerifyType(stacks[0], (_method.HasThis && index == 0) ? _method.DeclaringType
                    : _method.Parameters[index - (_method.HasThis ? 1 : 0)].ParameterType, inst);
                    return StackType.Invalid; 
                }
            case Code.Stloc:
                {
                    if (inst.Operand is not VariableReference reference)
                    {
                        ReportDiagnostic(CFGDiagnostic.InvalidOperand(typeof(VariableReference),
                            inst.Operand?.GetType() ?? typeof(void), inst));
                        return StackType.Invalid;
                    }
                    VerifyType(stacks[0], reference.VariableType, inst);
                    return StackType.Invalid;
                }
            case Code.Stsfld:
                {
                    if (inst.Operand is not FieldReference field)
                    {
                        ReportDiagnostic(CFGDiagnostic.InvalidOperand(typeof(FieldReference),
                            inst.Operand?.GetType() ?? typeof(void), inst));
                        return StackType.Invalid;
                    }
                    stacks[0] = field.FieldType;
                    return StackType.Invalid;
                }
            default:
                throw new ArgumentOutOfRangeException(); //TODO:
        }
    }

    private StackType VerifyPop1_pop1(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }

    private StackType VerifyPopi_pop1(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }

    private StackType VerifyPopref_pop1(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }
    private StackType VerifyPop3(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }

    private StackType VerifyPopref(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }

    private StackType VerifyPopref_popi(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {

        }
        throw new NotImplementedException();
    }

  

    private StackType VerifyVarPop(Instruction inst, ref StackType[] args, out int len)
    {
        if (inst.Operand is not IMethodSignature sig)
        {
            throw new Exception();
        }
        var paramLen = sig.Parameters.Count + (sig.HasThis ? 1 : 0);
        len = paramLen;
        if(args.Length < paramLen)
        {
            Array.Resize(ref args, paramLen);
        }
        int i = 0;
        if (sig.HasThis && (inst.OpCode.Code is not Code.Newobj))
        {
            if (sig is MethodReference methodRef)
                args[i++] = methodRef.DeclaringType;
            else
                args[i++] = StackType.Invalid;
        }
        foreach(var p in sig.Parameters)
        {
            args[i++] = p.ParameterType;
        }
        return (sig.ReturnType.Namespace == "System" && sig.ReturnType.Name == "Void") 
            ? StackType.Invalid : StackType.Create(sig.ReturnType);
    }

}