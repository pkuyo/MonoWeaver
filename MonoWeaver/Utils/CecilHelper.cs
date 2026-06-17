using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using COpCodes = Mono.Cecil.Cil.OpCodes;
using GenericParameterAttributes = Mono.Cecil.GenericParameterAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using OpCodes = System.Reflection.Emit.OpCodes;
using StackBehaviour = Mono.Cecil.Cil.StackBehaviour;
using TypeAttributes = Mono.Cecil.TypeAttributes;


namespace MonoWeaver.Utils;

public static partial class CecilHelper
{
    public static ILMethodAnalyzer Analyze(this MethodDefinition method, VerifyOptions options = VerifyOptions.Full) 
        => new ILMethodAnalyzer(method, options);

    public static ILMethodAnalyzer Analyze(this Mono.Cecil.Cil.MethodBody body, VerifyOptions options = VerifyOptions.Full)
        => new ILMethodAnalyzer(body.Method, options);
}

public static partial class CecilHelper
{

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PopCount(this Instruction inst, MethodReference method) 
    => inst.OpCode.StackBehaviourPop switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or
            StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or
            StackBehaviour.Popref_popi or StackBehaviour.Popi_pop1 or
            StackBehaviour.Popref_pop1 or StackBehaviour.Pop1_pop1 => 2,
        StackBehaviour.Popi_popi_popi or
        StackBehaviour.Popref_popi_popi or
            StackBehaviour.Popref_popi_popi8 or
            StackBehaviour.Popref_popi_popr4 or
            StackBehaviour.Popref_popi_popr8 or
            StackBehaviour.Popref_popi_popref => 3,
        StackBehaviour.PopAll => 0xFF, //PopAll
        StackBehaviour.Varpop => VarPopCount(inst, method),
        _ => throw new ArgumentOutOfRangeException()
    };


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PushCount(this Instruction inst) 
        => inst.OpCode.StackBehaviourPush switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushref or
        StackBehaviour.Pushr8 or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 => 1,
        StackBehaviour.Push1_push1 => 2,
        StackBehaviour.Varpush => VarPushCount(inst),
        _ => throw new ArgumentOutOfRangeException()
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PopCount(this StackBehaviour behaviour)
        => behaviour switch
    {
      StackBehaviour.Pop0 => 0,
      StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
      StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or
          StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or
          StackBehaviour.Popref_popi or StackBehaviour.Popi_pop1 or
          StackBehaviour.Popref_pop1 or StackBehaviour.Pop1_pop1 => 2,
      StackBehaviour.Popi_popi_popi or
      StackBehaviour.Popref_popi_popi or
          StackBehaviour.Popref_popi_popi8 or
          StackBehaviour.Popref_popi_popr4 or
          StackBehaviour.Popref_popi_popr8 or
          StackBehaviour.Popref_popi_popref => 3,
      StackBehaviour.PopAll => 0xFF, //PopAll
      StackBehaviour.Varpop => -1, //Unknown
      _ => throw new ArgumentOutOfRangeException()
  };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PushCount(this StackBehaviour behaviour)
    => behaviour switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushref or
            StackBehaviour.Pushr4 or StackBehaviour.Pushr8 => 1,
        StackBehaviour.Push1_push1 => 2,
        StackBehaviour.Varpush => -1, //Unknown
        _ => throw new ArgumentOutOfRangeException()
    };

    public static int VarPopCount(Instruction inst, MethodReference method)
    {
        if (inst.Operand is not IMethodSignature sig)
        {
            if (inst.OpCode.Code is Code.Ret)
            {
                return method.ReturnType.StripType().IsVoid()
                    ? 0 : 1;
            }
            throw new Exception(); //TODO:
        }
        return sig.Parameters.Count + (sig.HasThis && (inst.OpCode.Code is not Code.Newobj) ? 1 : 0);
    }

    public static int ReturnCount(this MethodDefinition method)
    {
        if (method.IsRuntimeAsync() && method.ReturnType.FullName is "System.Threading.Tasks.ValueTask" or "System.Threading.Tasks.Task")
        {
            return 0;
        }
        return method.ReturnType.StripType().IsVoid() ? 0 : 1;
    }

    public static int VarPushCount(Instruction inst)
    {
        if (inst.Operand is not IMethodSignature sig)
        {
            throw new Exception(); //TODO:
        }
        return (sig.ReturnType.StripType().IsVoid()) ? 0 : 1;
    }

    private static Func<object, Instruction>? _monoModresolveStrategy = null!;

   
    

    internal static IEnumerable<Instruction> OperandToTargets(object operand)
    {
        if( operand is Instruction inst)
            yield return inst;
        else if(operand is Instruction[] insts)
        {
            foreach(var i in insts)
                yield return i;
        }
        else
        {
            var type = operand.GetType();
            if(type.IsArray)
            {
                var eleType = type.GetElementType();
                var array = (Array)operand;
                if(eleType.FullName == "MonoMod.Cil.ILLabel")
                {
                    if (_monoModresolveStrategy is null)
                        BuildMonoModResolveStrategy(eleType);
                    foreach (var i in array)
                    {
                        yield return _monoModresolveStrategy!(i);
                    }
                }
            }
            else if(type.FullName == "MonoMod.Cil.ILLabel")
            {
                if (_monoModresolveStrategy is null)
                    BuildMonoModResolveStrategy(type);
                yield return _monoModresolveStrategy!(operand);
            }
        }
    }

    public static string SafeToString(this Instruction inst)
    {
        try
        {
            return inst.ToString();
        }
        catch
        {
            return $"IL_{inst.Offset:X4}: {inst.OpCode.Name} _____";
        }
    }

    public static bool HasRequiredModifier(this TypeReference type, string modifierFullName)
    {
        while (type is TypeSpecification spec)
        {
            if (type is RequiredModifierType req &&
                req.ModifierType.FullName == modifierFullName)
            {
                return true;
            }

            type = spec.ElementType;
        }

        return false;
    }

    public static bool IsRuntimeAsync(this MethodDefinition method)
    {
        return (((ushort)method.ImplAttributes) & MethodImplAsync) != 0;
    }

    private const ushort MethodImplAsync = 0x2000;

    internal static bool IsInitSetter(MethodDefinition method)
    {
        return method.IsSpecialName
            && method.Name.StartsWith("set_")
            && HasRequiredModifier(
                method.ReturnType,
                "System.Runtime.CompilerServices.IsExternalInit");
    }
}
