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

        //对于T[] T[,...]的特殊处理
        if (memberReference is MethodReference methodReference &&
            TryCreateArrayRuntimeMethodDefinition(methodReference, out var arrayRuntimeMethod))
            return arrayRuntimeMethod;
        if (memberReference is not GenericParameter) //GenericParameter不进行Resolve校验
        {
            ReportDiagnostic(CFGDiagnostic.ResolveFailed(memberReference));
        }
        return null;
    }

    private bool TryCreateArrayRuntimeMethodDefinition(MethodReference methodReference,
        out MethodDefinition methodDefinition)
    {
        methodDefinition = null!;
        if (methodReference.DeclaringType is not ArrayType arrayType ||
            !methodReference.HasThis ||
            methodReference.HasGenericParameters ||
            methodReference is GenericInstanceMethod)
        {
            return false;
        }

        var module = methodReference.Module ?? arrayType.Module ?? _method.Module;
        var int32Type = module.TypeSystem.Int32;
        var voidType = module.TypeSystem.Void;
        TypeReference expectedReturnType;
        TypeReference[] expectedParameterTypes;
        var attributes = MethodAttributes.Public | MethodAttributes.HideBySig;

        switch (methodReference.Name)
        {
            case ".ctor":
                if (!IsValidArrayCtorParameterCount(arrayType, methodReference.Parameters.Count))
                    return false;

                attributes |= MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
                expectedReturnType = voidType;
                expectedParameterTypes = CreateRepeatedTypeArray(int32Type, methodReference.Parameters.Count);
                break;

            case "Get":
                if (methodReference.Parameters.Count != arrayType.Rank)
                    return false;

                expectedReturnType = arrayType.ElementType;
                expectedParameterTypes = CreateRepeatedTypeArray(int32Type, arrayType.Rank);
                break;

            case "Address":
                if (methodReference.Parameters.Count != arrayType.Rank)
                    return false;

                expectedReturnType = new ByReferenceType(arrayType.ElementType);
                expectedParameterTypes = CreateRepeatedTypeArray(int32Type, arrayType.Rank);
                break;

            case "Set":
                if (methodReference.Parameters.Count != arrayType.Rank + 1)
                    return false;

                expectedReturnType = voidType;
                expectedParameterTypes = CreateRepeatedTypeArray(int32Type, arrayType.Rank + 1);
                expectedParameterTypes[expectedParameterTypes.Length - 1] = arrayType.ElementType;
                break;

            default:
                return false;
        }

        if (!IsSameRuntimeSignatureType(methodReference.ReturnType, expectedReturnType) ||
            !HasSameParameterTypes(methodReference, expectedParameterTypes))
        {
            return false;
        }

        methodDefinition = new MethodDefinition(methodReference.Name, attributes, expectedReturnType)
        {
            CallingConvention = methodReference.CallingConvention,
            ExplicitThis = methodReference.ExplicitThis,
            HasThis = methodReference.HasThis,
            ImplAttributes = MethodImplAttributes.Runtime
        };

        for (int i = 0; i < expectedParameterTypes.Length; i++)
        {
            var sourceParameter = methodReference.Parameters[i];
            methodDefinition.Parameters.Add(new ParameterDefinition(sourceParameter.Name,
                sourceParameter.Attributes, expectedParameterTypes[i]));
        }

        return true;
    }

    private static bool IsValidArrayCtorParameterCount(ArrayType arrayType, int parameterCount)
    {
        if (parameterCount == arrayType.Rank)
            return true;

        return !arrayType.IsVector && parameterCount == arrayType.Rank * 2;
    }

    private static TypeReference[] CreateRepeatedTypeArray(TypeReference type, int count)
    {
        var result = new TypeReference[count];
        for (int i = 0; i < result.Length; i++)
            result[i] = type;
        return result;
    }

    private static bool HasSameParameterTypes(MethodReference methodReference, TypeReference[] expectedParameterTypes)
    {
        if (methodReference.Parameters.Count != expectedParameterTypes.Length)
            return false;

        for (int i = 0; i < expectedParameterTypes.Length; i++)
        {
            if (!methodReference.Parameters[i].ParameterType.IsSameWith(expectedParameterTypes[i]))
                return false;
        }

        return true;
    }

    private static bool IsSameRuntimeSignatureType(TypeReference actual, TypeReference expected)
    {
        if (expected.IsVoid())
            return actual.IsVoid();

        return actual.IsSameWith(expected);
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
    private bool VerifyByteOperand(Instruction inst, out byte value)
        => VerifyOperand(inst, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool VerifySByteOperand(Instruction inst, out sbyte value)
        => VerifyOperand(inst, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool VerifySingleOperand(Instruction inst, out float value)
        => VerifyOperand(inst, out value);

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

    private bool TryGetParameterType(Instruction inst, out TypeReference type)
    {
        type = null!;
        if (TryGetMacroArgumentSlot(inst.OpCode.Code, out var slot))
            return TryGetParameterTypeBySlot(slot, inst, out type);

        if (!VerifyParameterOperand(inst, out var parameter))
            return false;

        return TryGetParameterType(parameter, inst, out type);
    }

    private bool TryGetParameterType(ParameterReference parameter, Instruction inst, out TypeReference type)
    {
        type = null!;
        if (parameter.Index == -1)
        {
            if (_method.HasThis)
            {
                type = _method.DeclaringType;
                return true;
            }

            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.OutOfRange, inst));
            return false;
        }

        if (parameter.Index < 0 || parameter.Index >= _method.Parameters.Count)
        {
            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.OutOfRange, inst));
            return false;
        }

        type = _method.Parameters[parameter.Index].ParameterType;
        return true;
    }

    private bool TryGetParameterTypeBySlot(int slot, Instruction inst, out TypeReference type)
    {
        type = null!;
        var parameterIndex = _method.HasThis ? slot - 1 : slot;
        if (parameterIndex == -1)
        {
            type = _method.DeclaringType;
            return true;
        }

        if (parameterIndex < 0 || parameterIndex >= _method.Parameters.Count)
        {
            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.OutOfRange, inst));
            return false;
        }

        type = _method.Parameters[parameterIndex].ParameterType;
        return true;
    }

    private static bool TryGetMacroArgumentSlot(Code code, out int slot)
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

    private bool TryGetVariableType(Instruction inst, out TypeReference type)
    {
        type = null!;
        if (TryGetMacroVariableIndex(inst.OpCode.Code, out var index))
            return TryGetVariableTypeByIndex(index, inst, out type);

        if (!VerifyVarOperand(inst, out var variable))
            return false;

        return TryGetVariableTypeByIndex(variable.Index, inst, out type);
    }

    private bool TryGetVariableIndex(Instruction inst, out int index)
    {
        if (TryGetMacroVariableIndex(inst.OpCode.Code, out index))
            return TryValidateVariableIndex(index, inst);

        if (!VerifyVarOperand(inst, out var variable))
        {
            index = -1;
            return false;
        }

        index = variable.Index;
        return TryValidateVariableIndex(index, inst);
    }

    private bool TryGetVariableTypeByIndex(int index, Instruction inst, out TypeReference type)
    {
        type = null!;
        if (!TryValidateVariableIndex(index, inst))
            return false;

        type = _method.Body.Variables[index].VariableType;
        return true;
    }

    private bool TryValidateVariableIndex(int index, Instruction inst)
    {
        if (index >= 0 && index < _method.Body.Variables.Count)
            return true;

        ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.OutOfRange, inst));
        return false;
    }

    private static bool TryGetMacroVariableIndex(Code code, out int index)
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

    private static bool IsLdlocCode(Code code)
        => code is Code.Ldloc or Code.Ldloc_S
            or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3;

    private static bool IsLdlocaCode(Code code)
        => code is Code.Ldloca or Code.Ldloca_S;

    private static bool IsStlocCode(Code code)
        => code is Code.Stloc or Code.Stloc_S
            or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3;

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
            return StackType.Invalid;
        }
     
        if(type1.Type is null || !type1.Type.IsSameWith(expectType))
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
    private StackType VerifyPop0(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;

        switch (inst.OpCode.Code)
        {
            case Code.Nop:
            case Code.Break:
            case Code.Jmp:
            case Code.Br:
            case Code.Br_S:
            case Code.Endfinally:
            case Code.Unaligned:
            case Code.Volatile:
            case Code.Tail:
            case Code.Constrained:
            case Code.No:
            case Code.Rethrow:
            case Code.Readonly:
                return StackType.Invalid;

            case Code.Ldnull:
                return StackType.Null;

            case Code.Ldc_I4_M1:
            case Code.Ldc_I4_0:
            case Code.Ldc_I4_1:
            case Code.Ldc_I4_2:
            case Code.Ldc_I4_3:
            case Code.Ldc_I4_4:
            case Code.Ldc_I4_5:
            case Code.Ldc_I4_6:
            case Code.Ldc_I4_7:
            case Code.Ldc_I4_8:
            case Code.Ldc_I4_S:
            case Code.Ldc_I4:
                return StackType.I4;

            case Code.Ldc_I8:
                return StackType.I8;

            case Code.Ldc_R4:
            case Code.Ldc_R8:
                return StackType.F;

            case Code.Ldstr:
                return module.TypeSystem.String;

            case Code.Ldsfld:
                {
                    if (VerifyFieldOperand(inst, out var field))
                        return field.FieldType;
                    return StackType.Invalid;
                }

            case Code.Ldsflda:
                {
                    if (VerifyFieldOperand(inst, out var field))
                        return StackType.CreateByRef(field.FieldType);
                    return StackType.Invalid;
                }

            case Code.Ldtoken:
                {
                    if (!VerifyMemberOperand(inst, out var member))
                        return StackType.Invalid;

                    return member switch
                    {
                        TypeReference => module.ImportReference(typeof(RuntimeTypeHandle)),
                        FieldReference => module.ImportReference(typeof(RuntimeFieldHandle)),
                        MethodReference => module.ImportReference(typeof(RuntimeMethodHandle)),
                        _ => StackType.Invalid
                    };
                }

            case Code.Arglist:
                return module.ImportReference(typeof(RuntimeArgumentHandle));

            case Code.Ldftn:
                return StackType.I;

            case Code.Ldarg_0:
            case Code.Ldarg_1:
            case Code.Ldarg_2:
            case Code.Ldarg_3:
            case Code.Ldarg_S:
            case Code.Ldarg:
                {
                    if (TryGetParameterType(inst, out var paramType))
                        return paramType;
                    return StackType.Invalid;
                }

            case Code.Ldarga_S:
            case Code.Ldarga:
                {
                    if (TryGetParameterType(inst, out var paramType))
                        return StackType.CreateByRef(paramType);
                    return StackType.Invalid;
                }

            case Code.Ldloc_0:
            case Code.Ldloc_1:
            case Code.Ldloc_2:
            case Code.Ldloc_3:
            case Code.Ldloc_S:
            case Code.Ldloc:
                {
                    if (TryGetVariableType(inst, out var variableType))
                        return variableType;
                    return StackType.Invalid;
                }

            case Code.Ldloca_S:
            case Code.Ldloca:
                {
                    if (TryGetVariableType(inst, out var variableType))
                        return StackType.CreateByRef(variableType);
                    return StackType.Invalid;
                }

            case Code.Sizeof:
                return StackType.I;

            default:
                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.UnExpected, inst));
                return StackType.Invalid;
        }
    }
    private StackType VerifyPop1(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {
            case Code.Stloc_0:
            case Code.Stloc_1:
            case Code.Stloc_2:
            case Code.Stloc_3:
            case Code.Stloc_S:
            case Code.Stloc:
                {
                    if (TryGetVariableType(inst, out var variableType))
                        VerifyType(stacks[0], variableType, inst);
                    return StackType.Invalid;
                }
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
            case Code.Starg_S:
            case Code.Starg:
                {
                    if (TryGetParameterType(inst, out var parameterType))
                        VerifyType(stacks[0], parameterType, inst);
                    return StackType.Invalid; 
                }
            case Code.Stsfld:
                {
                    if (!VerifyFieldOperand(inst, out var field))
                        return StackType.Invalid;

                    VerifyType(stacks[0], field.FieldType, inst);
                    return StackType.Invalid;
                }
            default:
                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.UnExpected, inst));
                break;
        }

        return StackType.Invalid;
    }

    private StackType VerifyPopi(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        var value = stacks[0];
        switch (inst.OpCode.Code)
        {
            case Code.Brfalse_S:
            case Code.Brtrue_S:
            case Code.Brfalse:
            case Code.Brtrue:
            case Code.Switch:
            case Code.Endfilter:
                VerifyInt(value, inst);
                return StackType.Invalid;

            case Code.Ldind_I1:
            case Code.Ldind_U1:
            case Code.Ldind_I2:
            case Code.Ldind_U2:
            case Code.Ldind_I4:
            case Code.Ldind_U4:
                VerifyIndirectAddress(value, inst);
                return StackType.I4;

            case Code.Ldind_I8:
                VerifyIndirectAddress(value, inst);
                return StackType.I8;

            case Code.Ldind_I:
                VerifyIndirectAddress(value, inst);
                return StackType.I;

            case Code.Ldind_R4:
            case Code.Ldind_R8:
                VerifyIndirectAddress(value, inst);
                return StackType.F;

            case Code.Ldind_Ref:
                VerifyIndirectAddress(value, inst);
                return module.TypeSystem.Object;

            case Code.Ldobj:
                {
                    if (!VerifyTypeOperand(inst, out var targetType))
                        return StackType.Invalid;

                    VerifyTypedAddress(value, targetType, inst);
                    return targetType;
                }

            case Code.Newarr:
                {
                    VerifyInt(value, inst);
                    if (!VerifyTypeOperand(inst, out var elementType))
                        return StackType.Invalid;

                    return new ArrayType(elementType);
                }

            case Code.Mkrefany:
                {
                    if (!VerifyTypeOperand(inst, out var targetType))
                        return StackType.Invalid;

                    VerifyTypedAddress(value, targetType, inst);
                    return StackType.TypedRef;
                }

            case Code.Localloc:
                VerifyInt(value, inst);
                return StackType.I;

            case Code.Initobj:
                {
                    if (!VerifyTypeOperand(inst, out var targetType))
                        return StackType.Invalid;

                    VerifyTypedAddress(value, targetType, inst);
                    return StackType.Invalid;
                }

            default:
                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.UnExpected, inst));
                return StackType.Invalid;
        }
    }

    private StackType VerifyPop1_pop1(Instruction inst, StackType[] stacks)
    {
        var module = _method.Module;
        var a = stacks[0];
        var b = stacks[1];
        switch (inst.OpCode.Code)
        {
            case Code.Beq_S:
            case Code.Bne_Un_S:
            case Code.Beq:
            case Code.Bne_Un:
            case Code.Ceq:
                {
                    VerifyComparable(a, b, inst, CompareKind.Equality);
                    return StackType.I4;
                }
            case Code.Cgt_Un:
                {
                    VerifyComparable(a, b, inst, CompareKind.CgtUn);
                    return StackType.I4;
                }
            case Code.Bge_S:
            case Code.Bgt_S:
            case Code.Ble_S:
            case Code.Blt_S:
            case Code.Bge_Un_S:
            case Code.Bgt_Un_S:
            case Code.Ble_Un_S:
            case Code.Blt_Un_S:
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
                    return StackType.I4;
                }
            case Code.Add:
            case Code.Sub:
            case Code.Mul:
            case Code.Div:
            case Code.Rem:
                    return VerifyBinary(a, b, inst, BinaryKind.Arithmetic);
            case Code.And:
            case Code.Or:
            case Code.Xor:
            case Code.Div_Un:
            case Code.Rem_Un:
                    return VerifyBinary(a, b, inst, BinaryKind.Integer);
                
            case Code.Shl:
            case Code.Shr:
            case Code.Shr_Un:
                    return VerifyBinary(a, b, inst, BinaryKind.Shift);
            case Code.Add_Ovf:
            case Code.Mul_Ovf:
            case Code.Sub_Ovf:
                return VerifyBinary(a, b, inst, BinaryKind.Overflow);
            case Code.Sub_Ovf_Un:
            case Code.Add_Ovf_Un:
            case Code.Mul_Ovf_Un:
                return VerifyBinary(a, b, inst, BinaryKind.OverflowUnsigned);
            default:
                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.UnExpected, inst));
                break;
        }
        return StackType.Invalid;
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
                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.UnExpected, inst));
                break;
        }
        return StackType.Invalid;
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
                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.UnExpected, inst));
                break;
        }
        return StackType.Invalid;
    }

    private void VerifyPop3(Instruction inst, StackType[] stacks)
    {
        //TODO: 这里没有校验数组是否兼容对应元素数据
        var module = _method.Module;
        var value = stacks[0];
        var index = stacks[1]; //I4
        var array = stacks[2]; //1 rank array
        if (array.Type is not TypeReference type || type is not ArrayType arrayType ||
            arrayType.Rank != 1 || !arrayType.IsVector)
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
            _ => StackType.Invalid
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
        else if (inst.OpCode.Code is Code.Stelem_Any)
        {
            if (!VerifyTypeOperand(inst, out var typeRef))
                return;

            expectType = typeRef;
        }
        if (expectType != StackType.Invalid)
        {
            VerifyType(value, expectType, inst);
        }
        else
        {
            ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.UnExpected, inst));
        }
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
                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.UnExpected, inst));
                break;
        }
        return StackType.Invalid;
    }

    private StackType VerifyPopref_popi(Instruction inst, StackType[] stacks)
    {
        //TODO: 这里没有校验数组是否兼容对应元素数据
        var module = _method.Module;
        var index = stacks[0];
        var array = stacks[1]; //1 rank array
        if (array.Type is not TypeReference ty || ty is not ArrayType arrayType ||
            arrayType.Rank != 1 || !arrayType.IsVector)
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
                return StackType.I4;
            case Code.Ldelem_I8:
                return StackType.I8;
            case Code.Ldelem_I:
                return StackType.I;
            case Code.Ldelem_R4:
            case Code.Ldelem_R8:
                return StackType.F;
            case Code.Ldelem_Ref:
                if (arrayType.ElementType.IsValueType)
                {
                    ReportStackTypeMismatch("reference type", arrayType.ElementType, inst);
                    return StackType.Invalid;
                }
                return arrayType.ElementType;
            case Code.Ldelem_Any:
                {
                    if (VerifyTypeOperand(inst, out var type))
                    {
                        VerifyType(eleType, type, inst);
                        return type;
                    }
                    return StackType.Invalid;
                }
            default:
                ReportDiagnostic(CFGDiagnostic.InstructionInvalid(CFGExceptionType.UnExpected, inst));
                break;
        }
        return StackType.Invalid;
    }

    private StackType VerifyVarPop(Instruction inst, ref StackType[] args, out int len)
    {
        if (inst.Operand is not IMethodSignature sig)
        {
            if (inst.OpCode.Code is Code.Ret)
            {
                if (_method.ReturnType.IsVoid())
                {
                    len = 0;
                    return StackType.Invalid;
                }

                len = 1;
                if (args.Length < len)
                    Array.Resize(ref args, len);
                args[0] = _method.ReturnType;
                return StackType.Invalid;
            }

            len = 0;
            ReportDiagnostic(CFGDiagnostic.InvalidOperand(typeof(IMethodSignature),
                inst.Operand?.GetType() ?? typeof(void), inst));
            return StackType.Invalid;
        }
        var paramLen = sig.Parameters.Count + (sig.HasThis && (inst.OpCode.Code is not Code.Newobj) ? 1 : 0);
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
        foreach (var p in sig.Parameters)
        {
            args[i++] = p.ParameterType;
        }
        if (inst.OpCode.Code == Code.Newobj && sig is MethodReference ctor)
            return StackType.Create(ctor.DeclaringType);

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
       && a.BuiltInType == b.BuiltInType;

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

    private void VerifyIndirectAddress(StackType address, Instruction inst)
    {
        if (IsIntegerNative(address) || address.VerifyType == VerificationType.ByRef)
            return;

        ReportStackTypeMismatch("I4, native int, or byref address", address, inst);
    }

    private void VerifyTypedAddress(StackType address, TypeReference targetType, Instruction inst)
    {
        if (IsIntegerNative(address))
            return;

        VerifyByRef(address, targetType, inst);
    }

    private static bool IsIntegerNative(StackType type)
        => type.VerifyType == VerificationType.BuiltIn
           && type.BuiltInType is BuiltInType.I4 or BuiltInType.I;
}
