using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.Utils;
using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using static MonoWeaver.Utils.CecilTypeSystem;

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
    private bool VerifyTypeOperand(Instruction inst, out TypeReference type)
        => VerifyOperand(inst, out type);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool VerifyMethodOperand(Instruction inst, out MethodReference method)
        => VerifyOperand(inst, out method);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool VerifyFieldOperand(Instruction inst, out FieldReference field)
        => VerifyOperand(inst, out field);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool VerifyMemberOperand(Instruction inst, out MemberReference member)
        => VerifyOperand(inst, out member);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool VerifyVarOperand(Instruction inst, out VariableReference variable)
        => VerifyOperand(inst, out variable);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool VerifyParameterOperand(Instruction inst, out ParameterReference param)
        => VerifyOperand(inst, out param);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool VerifyInstructionOperand(Instruction inst, out Instruction target)
    {
        if (CecilHelper.TryResolveInstructionTarget(inst.Operand, out var resolvedTarget, out var error))
        {
            target = resolvedTarget!;
            return true;
        }

        target = null!;
        ReportDiagnostic(CFGDiagnostic.InvalidOperand(error.Expected, error.Current, inst,
            message: error.Message));
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool VerifyInstructionArrayOperand(Instruction inst, out Instruction[] targets)
    {
        if (CecilHelper.TryResolveInstructionTargetArray(inst.Operand, out targets, out var error))
        {
            return true;
        }

        ReportDiagnostic(CFGDiagnostic.InvalidOperand(error.Expected, error.Current, inst,
            message: error.Message));
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool VerifyInt32Operand(Instruction inst, out int value)
        => VerifyOperand(inst, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool VerifyInt64Operand(Instruction inst, out long value)
        => VerifyOperand(inst, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool VerifyDoubleOperand(Instruction inst, out double value)
        => VerifyOperand(inst, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool VerifyStringOperand(Instruction inst, out string value)
        => VerifyOperand(inst, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool VerifyCallSiteOperand(Instruction inst, out Mono.Cecil.CallSite callSite)
        => VerifyOperand(inst, out callSite);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool VerifyNoOperand(Instruction inst)
    {
        if (inst.Operand is null)
            return true;

        ReportDiagnostic(CFGDiagnostic.InvalidOperand(typeof(void),
            inst.Operand.GetType(), inst));
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool VerifyOperand<T>(Instruction inst, out T operand)
    {
        if (inst.Operand is T value)
        {
            operand = value;
            return true;
        }

        operand = default!;
        ReportDiagnostic(CFGDiagnostic.InvalidOperand(typeof(T),
            inst.Operand?.GetType() ?? typeof(void), inst));
        return false;
    }

    /// TypeReference走隐式类型转换
    /// 否则built in会出现问题
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void VerifyType(StackType acutal, StackType expect, Instruction inst)
    {
        if (!acutal.StackValueEqualsTo(expect))
        {
            ReportStackTypeMismatch(expect, acutal, inst);
        }
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
            if (!VerifyTypeOperand(inst, out var boxType))
                return StackType.Invalid;

            var expected = StackType.Create(boxType);
            if (!type1.StackValueEqualsTo(expected))
            {
                ReportStackTypeMismatch(expected, type1, inst);
            }
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
    private StackType VerifyInt(in StackType type1, Instruction inst)
    {
        if (!IsIntegerStackType(type1))
        {
            ReportStackTypeMismatch("integer type", type1, inst);
        }

        return type1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackType VerifyByRef(in StackType type1, TypeReference expectType, Instruction inst)
    {
       
        if (type1.VerifyType != VerificationType.ByRef)
        {
            ReportStackTypeMismatch("byref type", type1, inst);
        }
     
        if(!type1.Type.IsSameWith(expectType))
        {
            ReportDiagnostic(CFGDiagnostic.TypeMismatch(expectType,
                type1.Type, inst));
            return StackType.Invalid;
        }
        return type1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StackType VerifyFloat(in StackType type1, Instruction inst)
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


    private void VerifyMethodSig(MethodReference actual, MethodDefinition expect, Instruction inst,
        GenericInstanceType? expectContext = null)
    {
        var actualContext = actual.DeclaringType as GenericInstanceType;
        var actualReturnType = InflateMethodSigType(actual.ReturnType, actualContext);
        var expectReturnType = InflateMethodSigType(expect.ReturnType, expectContext);
        if (!actualReturnType.IsILStackAssignableTo(expectReturnType))
        {
            ReportDiagnostic(CFGDiagnostic.MethodSignatureMismatch(
                TypeMismatchKind.MethodReturnType, expectReturnType, actualReturnType, inst));
        }

        if (actual.Parameters.Count != expect.Parameters.Count)
        {
            ReportDiagnostic(CFGDiagnostic.MethodSignatureMismatch(
                TypeMismatchKind.MethodParameterCount, expect.Parameters.Count.ToString(),
                actual.Parameters.Count.ToString(), inst));
            return;
        }

        for(int i = 0; i < actual.Parameters.Count; i++)
        {
            var expectParameterType = InflateMethodSigType(expect.Parameters[i].ParameterType, expectContext);
            var actualParameterType = InflateMethodSigType(actual.Parameters[i].ParameterType, actualContext);
            if (!expectParameterType.IsILStackAssignableTo(actualParameterType))
            {
                ReportDiagnostic(CFGDiagnostic.MethodSignatureMismatch(
                    TypeMismatchKind.MethodParameterType, expectParameterType, actualParameterType, inst, i));
            }
        }
    }

    private static TypeReference InflateMethodSigType(TypeReference type, GenericInstanceType? context)
    {
        if (context is null)
            return type;

        switch (type)
        {
            case GenericParameter gp when gp.Owner is TypeReference ownerRef
                                      && ownerRef.IsSameWith(context.ElementType)
                                      && context.GenericArguments.Count > gp.Position:
                return context.GenericArguments[gp.Position];

            case ByReferenceType byRef:
                return new ByReferenceType(InflateMethodSigType(byRef.ElementType, context));

            case PointerType ptr:
                return new PointerType(InflateMethodSigType(ptr.ElementType, context));

            case ArrayType array:
                var element = InflateMethodSigType(array.ElementType, context);
                var result = array.IsVector
                    ? new ArrayType(element)
                    : new ArrayType(element, array.Rank);
                foreach (var dimension in array.Dimensions)
                    result.Dimensions.Add(new ArrayDimension(dimension.LowerBound, dimension.UpperBound));
                return result;

            case GenericInstanceType genericInstance:
                var inflatedElement = InflateMethodSigType(genericInstance.ElementType, context);
                var inflatedInstance = new GenericInstanceType(inflatedElement);
                foreach (var argument in genericInstance.GenericArguments)
                    inflatedInstance.GenericArguments.Add(InflateMethodSigType(argument, context));
                return inflatedInstance;

            case OptionalModifierType optional:
                return new OptionalModifierType(optional.ModifierType,
                    InflateMethodSigType(optional.ElementType, context));

            case RequiredModifierType required:
                return new RequiredModifierType(required.ModifierType,
                    InflateMethodSigType(required.ElementType, context));

            default:
                return type;
        }
    }

    private void ReportStackTypeMismatch(StackType expected, StackType actual, Instruction inst)
    {
        ReportStackTypeMismatch(FormatStackType(expected), actual, inst);
    }

    private void ReportStackTypeMismatch(string expected, StackType actual, Instruction inst)
    {
        ReportDiagnostic(CFGDiagnostic.StackTypeMismatch(expected, FormatStackType(actual), inst));
    }

    private static string FormatStackType(StackType type)
    {
        if (type == StackType.Invalid)
            return "invalid";

        if (type == StackType.TypedRef)
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
                break;
            case Code.Box:
                {
                    var type = VerifyValueType(stacks[0], inst);
                    return type.IsInvalid ? StackType.Create(module.TypeSystem.Object) : StackType.CreateBoxed(type.Type!);
                }
            case Code.Ckfinite:
                {
                    VerifyType(stacks[0], StackType.F, inst);
                    return StackType.F;
                }
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
                {
                    VerifyType(stacks[0], module.TypeSystem.TypedReference, inst);
                    return module.ImportReference(typeof(RuntimeTypeHandle));
                }
            case Code.Refanyval:
                {
                    if (!VerifyTypeOperand(inst, out var targetType))
                        return StackType.Invalid;
                    if (stacks[0].VerifyType != VerificationType.TypedRef)
                    {
                        ReportStackTypeMismatch(StackType.TypedRef, stacks[0], inst);
                        return StackType.Invalid;
                    }
                    return StackType.CreateByRef(targetType);
                }
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
                    if (!VerifyFieldOperand(inst, out var field))
                        return StackType.Invalid;

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
        var a = stacks[0];
        var b = stacks[1];
        switch (inst.OpCode.Code)
        {
            case Code.Beq:
            case Code.Bne_Un:
            case Code.Ceq:
                {
                    VerifyComparable(a, b, inst, CompareKind.Equality);
                    break;
                }
            case Code.Cgt_Un:
                {
                    VerifyComparable(a, b, inst, CompareKind.CgtUn);
                    break;
                }
            case Code.Bge:
            case Code.Bgt:
            case Code.Ble:
            case Code.Blt:
            case Code.Bge_Un:
            case Code.Bgt_Un:
            case Code.Ble_Un:
            case Code.Blt_Un:
            case Code.Cgt:
            case Code.Clt:
            case Code.Clt_Un:
                {
                    VerifyComparable(a, b, inst, CompareKind.Ordered);
                    break;
                }

            case Code.Add:
            case Code.Sub:
            case Code.Mul:
            case Code.Div:
            case Code.Rem:
                {
                    VerifyBinary(a, b, inst, BinaryKind.Arithmetic);
                    break;
                }
            case Code.And:
            case Code.Or:
            case Code.Xor:
            case Code.Div_Un:
            case Code.Rem_Un:
                {
                    VerifyBinary(a, b, inst, BinaryKind.Integer);
                    break;
                }
            case Code.Shl:
            case Code.Shr:
            case Code.Shr_Un:
                {
                    VerifyBinary(a, b, inst, BinaryKind.Shift);
                    break;
                }
            case Code.Add_Ovf:
            case Code.Mul_Ovf:
            case Code.Mul_Ovf_Un:
            case Code.Sub_Ovf:
                {
                    VerifyBinary(a, b, inst, BinaryKind.Overflow);
                    break;
                }
            case Code.Sub_Ovf_Un:
            case Code.Add_Ovf_Un:
                {
                    VerifyBinary(a, b, inst, BinaryKind.OverflowUnsigned);
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException();
        }
        throw new NotImplementedException();
    }

    private StackType VerifyPopi_pop1(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        var value = stacks[0];
        var address = stacks[1]; //I/I4/&
        switch (inst.OpCode.Code)
        {
            case Code.Stobj:
                if (!VerifyTypeOperand(inst, out var targetType))
                    return StackType.Invalid;

                VerifyType(value, targetType, inst);
                if (!IsIntegerNative(address))
                {
                    VerifyByRef(address, targetType, inst);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        throw new NotImplementedException();
    }

    private StackType VerifyPopref_pop1(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        var value = stacks[0];
        var obj = stacks[1]; //&
        switch (inst.OpCode.Code)
        {
            case Code.Stfld:
                if (!VerifyFieldOperand(inst, out var field))
                    return StackType.Invalid;

                VerifyType(value, field.FieldType, inst);
                VerifyByRef(obj, field.FieldType, inst);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        throw new NotImplementedException();
    }

    private void VerifyPop3(Instruction inst, StackType[] stacks)
    {
        //TODO: 这里没有校验数组是否兼容对应元素数据
        var module = _method.Module;
        var value = stacks[0];
        var index = stacks[1]; //I4
        var array = stacks[2]; //1 rank array
        if (array.Type is not TypeReference type || type is not ArrayType arrayType ||
            arrayType.Rank != 1)
        {
            ReportStackTypeMismatch("1 rank array type", array, inst);
            return;
        }
        VerifyInt(index, inst);
        var expectType = inst.OpCode.Code switch
        {
            Code.Stelem_I => StackType.I,
            Code.Stelem_I1 or Code.Stelem_I2 or Code.Stelem_I2 or Code.Stelem_I4 => StackType.I4,
            Code.Stelem_I8 => StackType.I8,
            Code.Stelem_R4 or Code.Stelem_R8 => StackType.F,
            Code.Stelem_Ref => StackType.Invalid,
            Code.Stelem_Any => StackType.Invalid, //TODO: 暂时不考虑泛型
            _ => throw new ArgumentOutOfRangeException()
        };

        if (inst.OpCode.Code is Code.Stelem_Ref)
        {
            if (arrayType.ElementType.IsValueType)
            {
                ReportStackTypeMismatch("reference type", arrayType.ElementType, inst);
                return;
            }
            expectType = arrayType.ElementType;
        }

        if (inst.OpCode.Code is Code.Stelem_Any)
        {
            if (!VerifyTypeOperand(inst, out var typeRef))
                return;

            expectType = typeRef;
        }

        VerifyType(value, expectType, inst);
    }

    private StackType VerifyPopref(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        var type = stacks[0];
        if (type.VerifyType != VerificationType.O)
        {
            ReportStackTypeMismatch("reference type", type, inst);
        }

        switch (inst.OpCode.Code)
        {
            case Code.Castclass:
            case Code.Isinst:
                {
                    if (VerifyTypeOperand(inst, out var targetType))
                    {
                        return targetType;
                    }
                    return StackType.Invalid;
                }
            case Code.Unbox:
                {
                    VerifyType(type, module.TypeSystem.Object, inst);
                    if (VerifyTypeOperand(inst, out var targetType))
                    {
                        if (!targetType.IsValueType)
                        {
                            ReportStackTypeMismatch("reference type", type, inst);
                        }
                        return StackType.CreateByRef(targetType);
                    }
                    return StackType.Invalid;
                }
            case Code.Unbox_Any:
                {
                    VerifyType(type, module.TypeSystem.Object, inst);
                    if (VerifyTypeOperand(inst, out var targetType))
                    {
                        return targetType;
                    }
                    return StackType.Invalid;
                }
            case Code.Throw:
                return StackType.Invalid;
            case Code.Ldfld:
                {
                    if (VerifyFieldOperand(inst, out var field))
                    {
                        if (type == StackType.Null)
                        {
                            ReportDiagnostic(CFGDiagnostic.TypeMismatch(field.DeclaringType, null, inst, DiagnosticSeverity.Warning));
                        }
                        else if (type.Type is not null && !type.Type.IsILStackAssignableTo(field.DeclaringType))
                        {
                            ReportDiagnostic(CFGDiagnostic.TypeMismatch(field.DeclaringType, type.Type, inst));
                        }
                        return field.FieldType;
                    }
                    return StackType.Invalid;
                }
            case Code.Ldflda:
                {
                    if (VerifyFieldOperand(inst, out var field))
                    {
                        if (type == StackType.Null)
                        {
                            ReportDiagnostic(CFGDiagnostic.TypeMismatch(field.DeclaringType, null, inst, DiagnosticSeverity.Warning));
                        }
                        else if (type.Type is not null && !type.Type.IsILStackAssignableTo(field.DeclaringType))
                        {
                            ReportDiagnostic(CFGDiagnostic.TypeMismatch(field.DeclaringType, type.Type, inst));
                        }
                        return StackType.CreateByRef(field.FieldType);
                    }
                    return StackType.Invalid;
                }
            case Code.Ldlen:
                {
                    if (type.TypeSig != TypeSig.Array && 
                        (type.Type.BaseType() is not TypeReference baseType
                        || TypeSig.Create(baseType) != TypeSig.Array)) //少用一次importRef
                    {
                        ReportStackTypeMismatch("any array type", type, inst);
                    }
                    return StackType.I;
                }
            case Code.Ldvirtftn:
                {
                    if (VerifyMethodOperand(inst, out var method))
                    {
                        VerifyType(type, method.DeclaringType, inst);
                        var methodDef = ResolveWithDiagnostic(method) as MethodDefinition;
                        //对于下一句为 newobj instance void SomeDelegate::.ctor(object, native int)的情况时特殊处理进行验证
                        if (inst.Next is { } nextInst &&
                            nextInst.OpCode.Code == Code.Newobj &&
                            methodDef != null &&
                            nextInst.Operand is MethodReference ctorMethod &&
                            ctorMethod.DeclaringType.IsAssignableTo(module.ImportReference(typeof(Delegate))/*TODO: 这里暂时不知道如何优化*/) &&
                            ResolveWithDiagnostic(ctorMethod.DeclaringType) is TypeDefinition delegateType)
                        {
                            var invoke = delegateType.Methods.FirstOrDefault(m =>
                                    m.Name == "Invoke" &&
                                    !m.IsStatic);
                            if (invoke != null)
                            {
                                VerifyMethodSig(method, invoke, inst, ctorMethod.DeclaringType as GenericInstanceType);
                            }
                        }
                    }
                    
                    return StackType.I;
                }
            default:
                throw new ArgumentOutOfRangeException();
        }
        throw new NotImplementedException();
    }

    private StackType VerifyPopref_popi(Instruction inst, StackType[] stacks)
    {
        //TODO: 这里没有校验数组是否兼容对应元素数据
        var module = _method.Module;
        var index = stacks[0];
        var array = stacks[1]; //1 rank array
        if (array.Type is not TypeReference ty || ty is not ArrayType arrayType ||
            arrayType.Rank != 1)
        {
            ReportStackTypeMismatch("1 rank array type", array, inst);
            return StackType.Invalid;
        }
        var eleType = arrayType.ElementType;
        VerifyInt(index, inst);
        switch (inst.OpCode.Code)
        {
            case Code.Ldelema:
                {
                    if (VerifyTypeOperand(inst, out var type))
                    {
                        VerifyType(eleType, type, inst);
                        return StackType.CreateByRef(type);
                    }
                    return StackType.Invalid;
                }
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
        return (sig.ReturnType.IsVoid()) 
            ? StackType.Invalid : StackType.Create(sig.ReturnType);
    }


    private enum CompareKind
    {
        Equality,       // beq, bne.un, ceq
        Ordered,        // bge, bgt, ble, blt, cgt, clt
        CgtUn,          // cgt.un 特殊可以处理O和O的 主要用于object 和 null比较
    }


    private void VerifyComparable(StackType a, StackType b, Instruction inst, CompareKind kind)
    {
        if (IsComparable(a, b, kind))
            return;

        ReportStackTypeMismatch("comparable pair", a, inst);
    }

    private static bool IsComparable(StackType a, StackType b, CompareKind kind)
    {
        if (IsSamePrimitive(a, b))
            return true;

        if (IsI4NativePair(a, b))
            return true;

        if (IsObjRefPair(a, b))
            return kind is CompareKind.Equality or CompareKind.CgtUn;

        if (IsByRefPair(a, b))
            return kind is CompareKind.Equality;

        if (IsNativeByRefPair(a, b))
            return kind is CompareKind.Equality;

        return false;
    }

    private enum BinaryKind
    {
        Arithmetic,          // add/sub/mul/div/rem
        Integer,             // div.un/rem.un/and/or/xor
        Shift,               // shl/shr/shr.un
        Overflow,            // add.ovf/sub.ovf/mul.ovf
        OverflowUnsigned,    // add.ovf.un/sub.ovf.un/mul.ovf.un
    }

    private StackType VerifyBinary(StackType left, StackType right, Instruction inst, BinaryKind kind)
    {
        return kind switch
        {
            BinaryKind.Arithmetic => VerifyArithmetic(left, right, inst),
            BinaryKind.Integer => VerifyIntegerBinary(left, right, inst),
            BinaryKind.Shift => VerifyShift(left, right, inst),
            BinaryKind.Overflow => VerifyOverflow(left, right, inst, unsigned: false),
            BinaryKind.OverflowUnsigned => VerifyOverflow(left, right, inst, unsigned: true),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    
    private StackType VerifyNumericBinary(StackType left, StackType right, Instruction inst, bool allowFloat)
    {
        //I4/I <-> I4/I
        //I8 <-> I8
        //F <-> F
        if (left.BuiltInType == BuiltInType.I4 && right.BuiltInType == BuiltInType.I4)
            return StackType.I4;

        if (IsI4NativePair(left, right))
            return StackType.I;

        if (left.BuiltInType == BuiltInType.I && right.BuiltInType == BuiltInType.I)
            return StackType.I;

        if (left.BuiltInType == BuiltInType.I8 && right.BuiltInType == BuiltInType.I8)
            return StackType.I8;

        if (allowFloat && left.BuiltInType == BuiltInType.F && right.BuiltInType == BuiltInType.F)
            return StackType.F;

        ReportStackTypeMismatch("valid binary numeric pair", left, inst);
        return StackType.Invalid;
    }


    private StackType VerifyArithmetic(StackType left, StackType right, Instruction inst)
    {
        var code = inst.OpCode.Code;

        // & + I/I4
        // I/I4 + &
        if (code is Code.Add)
        {
            if (IsIntegerNative(left) && right.VerifyType == VerificationType.ByRef)
                return right;

            if (left.VerifyType == VerificationType.ByRef && IsIntegerNative(right))
                return left;
        }

        // & - I/I4
        // & - &
        if (code is Code.Sub)
        {
            if (left.VerifyType == VerificationType.ByRef && IsIntegerNative(right))
                return left;

            if (left.VerifyType == VerificationType.ByRef && right.VerifyType == VerificationType.ByRef)
                return StackType.I;
        }

        return VerifyNumericBinary(left, right, inst, allowFloat: true);
    }

    private StackType VerifyIntegerBinary(StackType left, StackType right, Instruction inst)
    {
        return VerifyNumericBinary(left, right, inst, allowFloat: false);
    }

    private StackType VerifyShift(StackType left, StackType right, Instruction inst)
    {
        // R -> I4/I
        if (!IsIntegerNative(right))
        {
            ReportStackTypeMismatch("I4 or native int shift amount", right, inst);
            return StackType.Invalid;
        }

        // L -> I4/I8/I
        return left.BuiltInType switch
        {
            BuiltInType.I4 => StackType.I4,
            BuiltInType.I8 => StackType.I8,
            BuiltInType.I => StackType.I,
            _ => ReportInvalidBinary(left, inst)
        };
    }

    private StackType VerifyOverflow(StackType left, StackType right, Instruction inst, bool unsigned)
    {
        var code = inst.OpCode.Code;

        if (unsigned)
        {
            //I/I4 + &
            //& + I/I4
            if (code is Code.Add_Ovf_Un)
            {
                if (IsIntegerNative(left) && right.VerifyType == VerificationType.ByRef)
                    return right;

                if (left.VerifyType == VerificationType.ByRef && IsIntegerNative(right))
                    return left;
            }

            // & - I/I4
            // & - &
            if (code is Code.Sub_Ovf_Un)
            {
                if (left.VerifyType == VerificationType.ByRef && IsIntegerNative(right))
                    return left;

                if (left.VerifyType == VerificationType.ByRef && right.VerifyType == VerificationType.ByRef)
                    return StackType.I;
            }
        }

        return VerifyNumericBinary(left, right, inst, allowFloat: false);
    }

    private StackType ReportInvalidBinary(StackType actual, Instruction inst)
    {
        ReportStackTypeMismatch("valid binary operand", actual, inst);
        return StackType.Invalid;
    }

    private static bool IsSamePrimitive(StackType a, StackType b)
    => a.VerifyType is VerificationType.BuiltIn
       && b.VerifyType is VerificationType.BuiltIn
       && a.BuiltInType == b.BuiltInType
       && a.BuiltInType is BuiltInType.I4 or BuiltInType.I8 or BuiltInType.I or BuiltInType.F;

    private static bool IsI4NativePair(StackType a, StackType b)
        => IsBuiltInPair(a, b, BuiltInType.I4, BuiltInType.I);

    private static bool IsNativeByRefPair(StackType a, StackType b)
        => (a.BuiltInType == BuiltInType.I && b.VerifyType == VerificationType.ByRef)
           || (b.BuiltInType == BuiltInType.I && a.VerifyType == VerificationType.ByRef);

    private static bool IsByRefPair(StackType a, StackType b)
        => a.VerifyType == VerificationType.ByRef && b.VerifyType == VerificationType.ByRef;

    private static bool IsObjRefPair(StackType a, StackType b)
        => a.VerifyType == VerificationType.O && b.VerifyType == VerificationType.O;

    private static bool IsBuiltInPair(StackType a, StackType b, BuiltInType x, BuiltInType y)
        => a.VerifyType == VerificationType.BuiltIn
           && b.VerifyType == VerificationType.BuiltIn
           && ((a.BuiltInType == x && b.BuiltInType == y)
               || (a.BuiltInType == y && b.BuiltInType == x));

    private static bool IsIntegerNative(StackType type)
        => type.VerifyType == VerificationType.BuiltIn
           && type.BuiltInType is BuiltInType.I4 or BuiltInType.I;


}
