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
        if (!type1.StackValueEqualsTo(type2))
        {
            ReportStackTypeMismatch(type2, type1, inst);
        }

        return type2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackType VerifyValueType(StackType type1, Instruction inst)
    {
        if (!type1.IsValueType)
        {
            ReportStackTypeMismatch("value type", type1, inst);
        }

        if (inst.OpCode.Code is Code.Box)
        {
            if (inst.Operand is not TypeReference boxType)
            {
                ReportDiagnostic(CFGDiagnostic.InvalidOperand(typeof(TypeReference),
                    inst.Operand?.GetType() ?? typeof(void), inst));
                return StackType.Invalid;
            }

            var expected = StackType.Create(boxType);
            if (!type1.StackValueEqualsTo(expected))
            {
                ReportStackTypeMismatch(expected, type1, inst);
            }

            return boxType.IsValueType ? StackType.CreateBoxed(boxType) : StackType.Create(boxType);
        }

        return type1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackType VerifyNum(StackType type1, Instruction inst)
    {
        if (!IsNumericStackType(type1))
        {
            ReportStackTypeMismatch("numeric type", type1, inst);
        }

        return inst.OpCode.Code switch
        {
            Code.Conv_I1 or Code.Conv_I2 or Code.Conv_I4 or
            Code.Conv_U1 or Code.Conv_U2 or Code.Conv_U4 or
            Code.Conv_Ovf_I1 or Code.Conv_Ovf_I2 or Code.Conv_Ovf_I4 or
            Code.Conv_Ovf_U1 or Code.Conv_Ovf_U2 or Code.Conv_Ovf_U4 or
            Code.Conv_Ovf_I1_Un or Code.Conv_Ovf_I2_Un or Code.Conv_Ovf_I4_Un or
            Code.Conv_Ovf_U1_Un or Code.Conv_Ovf_U2_Un or Code.Conv_Ovf_U4_Un
                => StackType.I4,

            Code.Conv_I8 or Code.Conv_U8 or
            Code.Conv_Ovf_I8 or Code.Conv_Ovf_U8 or
            Code.Conv_Ovf_I8_Un or Code.Conv_Ovf_U8_Un
                => StackType.I8,

            Code.Conv_I or Code.Conv_U or
            Code.Conv_Ovf_I or Code.Conv_Ovf_U or
            Code.Conv_Ovf_I_Un or Code.Conv_Ovf_U_Un
                => StackType.I,

            Code.Conv_R4 or Code.Conv_R8 or Code.Conv_R_Un
                => StackType.F,

            _ => type1
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackType VerifyInt(StackType type1, Instruction inst)
    {
        if (!IsIntegerStackType(type1))
        {
            ReportStackTypeMismatch("integer type", type1, inst);
        }

        return type1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackType VerifyByRef(StackType type1, Instruction inst)
    {
        if (inst.OpCode.Code is Code.Refanyval)
        {
            VerifyType(type1, StackType.TypeRef, inst);

            if (inst.Operand is not TypeReference targetType)
            {
                ReportDiagnostic(CFGDiagnostic.InvalidOperand(typeof(TypeReference),
                    inst.Operand?.GetType() ?? typeof(void), inst));
                return StackType.Invalid;
            }

            return StackType.CreateByRef(targetType);
        }

        if (type1.VerifyType is not VerificationType.ByRef)
        {
            ReportStackTypeMismatch("managed pointer", type1, inst);
            return StackType.Invalid;
        }

        return type1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackType VerifyFloat(StackType type1, Instruction inst)
    {
        if (type1.BuiltInType is not BuiltInType.F)
        {
            ReportStackTypeMismatch(StackType.F, type1, inst);
        }

        return StackType.F;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNumericStackType(StackType type)
        => type.VerifyType is VerificationType.BuiltIn
           && type.BuiltInType is BuiltInType.I4 or BuiltInType.I8 or BuiltInType.I or BuiltInType.F;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIntegerStackType(StackType type)
        => type.VerifyType is VerificationType.BuiltIn
           && type.BuiltInType is BuiltInType.I4 or BuiltInType.I8 or BuiltInType.I;

    private void ReportStackTypeMismatch(StackType expected, StackType actual, Instruction inst)
    {
        ReportStackTypeMismatch(FormatStackType(expected), actual, inst);
    }

    private void ReportStackTypeMismatch(string expected, StackType actual, Instruction inst)
    {
        ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.TypeMismatch, inst,
            message: $"Stack type mismatch: expect {expected}, got {FormatStackType(actual)}"));
    }

    private static string FormatStackType(StackType type)
    {
        if (type == StackType.Invalid)
            return "invalid";

        if (type == StackType.TypeRef)
            return "typedref";

        if (type.BuiltInType is not BuiltInType.None)
            return type.IsPtr ? $"{type.Type!.FullName}*" : type.BuiltInType.ToString();

        return type.VerifyType switch
        {
            VerificationType.ByRef => $"&{type.Type?.FullName ?? "<null>"}",
            VerificationType.O => type.BoxedType is null
                ? $"O({type.Type?.FullName ?? "null"})"
                : $"boxed({type.BoxedType.FullName})",
            VerificationType.ValueType => $"valuetype({type.Type?.FullName ?? "<null>"})",
            VerificationType.TypedRef => "typedref",
            _ => type.VerifyType.ToString()
        };
    }
}


public partial class ILMethodAnalyzer
{

    private StackType VerifyPop1(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {
            case Code.Stloc_0:
            case Code.Stloc_1:
            case Code.Stloc_2:
            case Code.Stloc_3:
            case Code.Starg_S:
            case Code.Stloc_S:
                break;
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

        throw new NotImplementedException();
    }

    private StackType VerifyPop1_pop1(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {
            case Code.Beq_S:
            case Code.Bge_S:
            case Code.Bgt_S:
            case Code.Ble_S:
            case Code.Blt_S:
            case Code.Bne_Un_S:
            case Code.Bge_Un_S:
            case Code.Bgt_Un_S:
            case Code.Ble_Un_S:
            case Code.Blt_Un_S:
            case Code.Beq:
            case Code.Bge:
            case Code.Bgt:
            case Code.Ble:
            case Code.Blt:
            case Code.Bne_Un:
            case Code.Bge_Un:
            case Code.Bgt_Un:
            case Code.Ble_Un:
            case Code.Blt_Un:
            case Code.Add:
            case Code.Sub:
            case Code.Mul:
            case Code.Div:
            case Code.Div_Un:
            case Code.Rem:
            case Code.Rem_Un:
            case Code.And:
            case Code.Or:
            case Code.Xor:
            case Code.Shl:
            case Code.Shr:
            case Code.Shr_Un:
            case Code.Add_Ovf:
            case Code.Add_Ovf_Un:
            case Code.Mul_Ovf:
            case Code.Mul_Ovf_Un:
            case Code.Sub_Ovf:
            case Code.Sub_Ovf_Un:
            case Code.Ceq:
            case Code.Cgt:
            case Code.Cgt_Un:
            case Code.Clt:
            case Code.Clt_Un:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        throw new NotImplementedException();
    }

    private StackType VerifyPopi_pop1(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {
            case Code.Stobj:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        throw new NotImplementedException();
    }

    private StackType VerifyPopref_pop1(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {
            case Code.Stfld:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        throw new NotImplementedException();
    }

    private StackType VerifyPop3(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {
            case Code.Stelem_I:
            case Code.Stelem_I1:
            case Code.Stelem_I2:
            case Code.Stelem_I4:
            case Code.Stelem_I8:
            case Code.Stelem_R4:
            case Code.Stelem_R8:
            case Code.Stelem_Ref:
            case Code.Stelem_Any:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        throw new NotImplementedException();
    }

    private StackType VerifyPopref(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {
            case Code.Castclass:
            case Code.Isinst:
            case Code.Unbox:
            case Code.Throw:
            case Code.Ldfld:
            case Code.Ldflda:
            case Code.Ldlen:
            case Code.Unbox_Any:
            case Code.Ldvirtftn:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        throw new NotImplementedException();
    }

    private StackType VerifyPopref_popi(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {
            case Code.Ldelema:
            case Code.Ldelem_I1:
            case Code.Ldelem_U1:
            case Code.Ldelem_I2:
            case Code.Ldelem_U2:
            case Code.Ldelem_I4:
            case Code.Ldelem_U4:
            case Code.Ldelem_I8:
            case Code.Ldelem_I:
            case Code.Ldelem_R4:
            case Code.Ldelem_R8:
            case Code.Ldelem_Ref:
            case Code.Ldelem_Any:
                break;
            default:
                throw new ArgumentOutOfRangeException();
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

public partial class ILMethodAnalyzer
{
    private StackType VerifyPush1(Instruction inst, StackType retType)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {
            case Code.Ldarg_0:
            case Code.Ldarg_1:
            case Code.Ldarg_2:
            case Code.Ldarg_3:
            case Code.Ldloc_0:
            case Code.Ldloc_1:
            case Code.Ldloc_2:
            case Code.Ldloc_3:
            case Code.Ldarg_S:
            case Code.Ldloc_S:
            case Code.Add:
            case Code.Sub:
            case Code.Mul:
            case Code.Div:
            case Code.Div_Un:
            case Code.Rem:
            case Code.Rem_Un:
            case Code.And:
            case Code.Or:
            case Code.Xor:
            case Code.Shl:
            case Code.Shr:
            case Code.Shr_Un:
            case Code.Neg:
            case Code.Not:
            case Code.Ldobj:
            case Code.Ldfld:
            case Code.Ldsfld:
            case Code.Ldelem_Any:
            case Code.Unbox_Any:
            case Code.Mkrefany:
            case Code.Add_Ovf:
            case Code.Add_Ovf_Un:
            case Code.Mul_Ovf:
            case Code.Mul_Ovf_Un:
            case Code.Sub_Ovf:
            case Code.Sub_Ovf_Un:
            case Code.Ldarg:
            case Code.Ldloc:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        return retType;
    }

    private StackType VerifyPushref(Instruction inst, StackType retType)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {
            case Code.Ldnull:
            case Code.Ldind_Ref:
            case Code.Ldstr:
            case Code.Newobj:
            case Code.Castclass:
            case Code.Box:
            case Code.Newarr:
            case Code.Ldelem_Ref:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        return retType;
    }
}
