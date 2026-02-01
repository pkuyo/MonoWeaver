using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using MonoMod.Cil;
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using MonoWeaver.Utils;

namespace MonoWeaver.CFG;

[Flags]
public enum VerificationType : uint 
{
    Unknown = 0,
    
    ByRef = 1 << 0,
    Ptr = 1 << 1,
    O = 1 << 2,      // object ref
    ValueType = 1 << 3,
    
    I1 = 1 << 4,
    I2 = 1 << 5,
    I4 = 1 << 6,
    I8 = 1 << 7,
    I =  1 << 8,      // native int
    R4 = 1 << 9,
    R8 = 1 << 10,
    
    Null = 1 << 11 | O,
    F = R4 | R8,
    All = uint.MaxValue
}

internal class StackTypeRef 
{
    public StackTypeRef(VerificationType kind) => Kind = kind;

    public StackTypeRef(TypeReference type)
    {
        type = type.StripType();
        
        var typeSystem = type.Module.TypeSystem;
       
        if (type is ByReferenceType refType)
        {
            var eleType = refType.ElementType;
            TypeDefinition typeDef = eleType.Resolve() ?? throw new ResolveFailedException(eleType);
            var enumType = typeDef.GetEnumUnderlyingType();
            Kind |= VerificationType.ByRef;

            if (enumType != null) eleType = enumType;
            
            if(typeSystem.Boolean == eleType || typeSystem.Byte == eleType || typeSystem.SByte == eleType)
            {
                Kind |= VerificationType.I1;
                return;
            }
            if(typeSystem.Int16 == eleType || typeSystem.UInt16 == eleType || typeSystem.Char == eleType)
            {
                Kind |= VerificationType.I2;
                return;
            }
            if(typeSystem.Int32 == eleType || typeSystem.UInt32 == eleType)
            {
                Kind |= VerificationType.I4;
                return;
            }
            if (typeSystem.Int64 == eleType || typeSystem.UInt64 == eleType)
            {
                Kind |= VerificationType.I8;
                return;
            }
            if (typeSystem.Single == eleType)
            {
                Kind |= VerificationType.R4;
                return;
            }
            if (typeSystem.Double == eleType)
            {
                Kind |= VerificationType.R8;
                return;
            }
            if (typeSystem.IntPtr == eleType || typeSystem.UIntPtr == eleType)
            {
                Kind |= VerificationType.I;
                return;
            }

            Type = type; //对于ByRef的类型不能直接合
        }
        else if (type is PointerType)
        {
            Kind |= VerificationType.Ptr | VerificationType.I;
            Type = type; //保留类型但可以转化为native int
        }
        else
        {
            TypeDefinition typeDef = type.Resolve() ?? throw new ResolveFailedException(type);
            var cmpType = typeDef.GetEnumUnderlyingType() ?? type;
            if(typeSystem.Boolean == cmpType ||
               typeSystem.Int32 == cmpType || typeSystem.UInt32 == cmpType || 
               typeSystem.Int16 == cmpType || typeSystem.UInt16 == cmpType ||
               typeSystem.Byte == cmpType || typeSystem.Char == cmpType ||
               typeSystem.SByte == cmpType)
            {
                Kind |= VerificationType.I4;
                return;
            }
            if (typeSystem.Int64 == cmpType || typeSystem.UInt64 == cmpType)
            {
                Kind |= VerificationType.I8;
                return;
            }
        
            if (typeSystem.IntPtr == cmpType || typeSystem.UIntPtr == cmpType)
            {
                Kind |= VerificationType.I;
                return;
            }
            if (typeSystem.Single == cmpType || typeSystem.Double == cmpType)
            {
                Kind |= VerificationType.F;
                return;
            }
        }
        if (type.IsValueType) Kind |= VerificationType.ValueType;
        else  Kind |= VerificationType.O;
        Type = type;
    } 

    public readonly VerificationType Kind;

    public readonly TypeReference? Type;
    
    public static implicit operator StackTypeRef(TypeReference type) => new(type);
    public static implicit operator StackTypeRef(VerificationType kind) => new(kind);


    public StackTypeRef? GetCompatible(StackTypeRef other)
    {
        throw new NotImplementedException();
    }

    public bool CanConvertTo(StackTypeRef? right)
    {
        if (right is null)
            return false;
        if (((uint)Kind & 0xF) != ((uint)right.Kind & 0xF)) //类别不相等
            return false;
        
        if (Kind == VerificationType.Null || right.Kind == VerificationType.Null) //已经确定类别一致则null一定可以转换
            return true;
        
        if (right.Type is null) //无细分类别
            return true; 
        
        if (Type is null) //细分类别不一致
            return false;
        throw new NotImplementedException();
    }
}

public partial class ControlFlowGraph
{
    public sealed class BasicBlock(int id, Instruction start) : IComparable<BasicBlock>
    {
        public Instruction Leader  = start;
        public ControlFlowEdge[]? edges = null;

        public readonly int Id = id;

        public int CompareTo(BasicBlock other)
        {
           return Id.CompareTo(other.Id);
        }
    }

    public sealed class ControlFlowEdge
    {
        public BasicBlock From;
        public BasicBlock To;

        public EvalStackTransfer? StackTransfer;
    }
}

public partial class ControlFlowGraph
{

    private readonly MethodDefinition _method;
    private readonly bool _verify;

    private EvalStackNode _stackPool;

    private List<BasicBlock> _blocks = null!;

    private List<ControlFlowEdge> _edges = null!;

    private Dictionary<Instruction, BasicBlock> _blockMap = new();

    private int _currentId = 1;

    

    public ControlFlowGraph(MethodDefinition method, bool verify = true)
    {
        if (!method.HasBody)
        {
            throw new ArgumentException("Method must have body", nameof(method));
        }
        _method = method;
        _verify = verify;
        _method.Body.SimplifyMacros();
        BuildGraph();
    }



    private void BuildGraph()
    {
        BuildBasicBlock();
        BuildEdge();
    }

    private void BuildBasicBlock()
    {
        HashSet<Instruction> leaders = new HashSet<Instruction>(); //添加Block起始
        _blocks = new List<BasicBlock>();
        if (_method.Body.Instructions.Count > 0)
            leaders.Add(_method.Body.Instructions[0]);

        foreach (var inst in _method.Body.Instructions)
        {
            if (inst is null) continue;
            if (inst.OpCode.FlowControl is FlowControl.Phi)
                leaders.Add(inst);
            else if (inst.OpCode.FlowControl is FlowControl.Branch
                     or FlowControl.Cond_Branch)
            {
                switch (inst.Operand)
                {
                    case ILLabel lab:   //MonoMod Runtime Detour
                        leaders.Add(lab.Target);
                        break;

                    case Instruction target:
                        leaders.Add(target);
                        break;

                    case Instruction[] targets:           // switch in Cecil
                        foreach (var t in targets) leaders.Add(t);
                        break;

                    case ILLabel[] labelTargets:
                        foreach (var l in labelTargets) leaders.Add(l.Target);
                        break;
                }
            }

            //终止指令的下一条指令也是起始
            if (inst.OpCode.FlowControl is FlowControl.Branch
                or FlowControl.Cond_Branch
                or FlowControl.Return
                or FlowControl.Throw)
            {
                if (inst.Next != null)
                    leaders.Add(inst.Next);
            }

            // JMP特殊处理
            if (inst.OpCode.Code == Code.Jmp && inst.Next != null)
                leaders.Add(inst.Next);
        }


        foreach (var eh in _method.Body.ExceptionHandlers)
        {
            if (eh.TryStart != null) leaders.Add(eh.TryStart);
            if (eh.TryEnd != null) leaders.Add(eh.TryEnd);
            if (eh.HandlerStart != null) leaders.Add(eh.HandlerStart);
            if (eh.HandlerEnd != null) leaders.Add(eh.HandlerEnd);
            if (eh.FilterStart != null) leaders.Add(eh.FilterStart);
        }

        foreach (var leader in leaders)
        {
            AddBasicBlock(leader);
        }
    }
    private void BuildEdge()
    {
        var root = new EvalStackNode();
        var module = _method.Module;
        foreach (var block in _blocks)
        {
            var leader = block.Leader;
            var stack = new Stack<TypeReference>();
            for (var inst = leader; inst != null; inst = inst.Next)
            {
                var ts = module.TypeSystem;
                StackTypeRef[] expectTypes;
                var popAll = inst.OpCode.StackBehaviourPop == StackBehaviour.PopAll;

                expectTypes = inst.OpCode.StackBehaviourPop switch
                {
                    StackBehaviour.Pop0 => [],

                    StackBehaviour.Pop1 => FindExpectType_Pop1(inst),
                    StackBehaviour.Popi   => [ts.Int32],
                    StackBehaviour.Popref => FindExpectType_Popref(inst),

                    StackBehaviour.Popi_popi   => [ts.Int32, ts.Int32],
                    StackBehaviour.Popi_popi8  => [ts.Int32, ts.Int64],
                    StackBehaviour.Popi_popr4  => [ts.Int32, ts.Single],
                    StackBehaviour.Popi_popr8  => [ts.Int32, ts.Double],
                    StackBehaviour.Popref_popi => FindExpectTypes_Popref_popi(inst),
                    StackBehaviour.Popi_pop1 => FindExpectTypes_Popi_pop1(inst),
                    StackBehaviour.Popref_pop1 => FindExpectTypes_Popref_pop1(inst),
                    StackBehaviour.Pop1_pop1 => FindExpectTypes_Pop1_pop1(inst),
                    
                    StackBehaviour.Popi_popi_popi    => [ts.Int32, ts.Int32, ts.Int32],
                    StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or
                    StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or 
                    StackBehaviour.Popref_popi_popref   => FindExpectTypes_Pop3(inst),
                    
                    StackBehaviour.Varpop => FindExpectTypes_VarPop(inst),

                    _ => throw new ArgumentOutOfRangeException()
                };
                int push = 0;
            }

            static TypeReference? Pop(Stack<TypeReference> localStack, ref EvalStackNode node, Instruction instruction)
            {
                if (localStack.Count != 0)
                    return localStack.Pop();
                if(node.Parent?.Type is null)
                    throw new CFGException(CFGExceptionType.StackUnderflow, instruction);
                node = node.Parent;
                return node.Type;
            }

            static void Append(Stack<TypeReference> localStack, ref EvalStackNode node,
                Instruction instruction, int maxDepth)
            {
                while (localStack.Count != 0)
                    node = node.AppendChild(localStack.Pop());
                if(node.Depth >= maxDepth)
                    throw new CFGException(CFGExceptionType.StackOverflow, instruction);
            }
        }
    }
    
    private BasicBlock AddBasicBlock(Instruction leader)
    {
        var block = new BasicBlock(_currentId++, leader);
        _blocks.Add(block);
        _blockMap.Add(leader, block);
        return block;
    }

}

public partial class ControlFlowGraph
{
    private StackTypeRef[] FindExpectType_Pop1(Instruction inst)
    {
        var module = _method.Module;
        switch (inst.OpCode.Code)
        {
            case Code.Starg:
            {
                if (inst.Operand is not ushort index)
                    throw new InvalidInstructionException(typeof(ushort), inst.Operand?.GetType(), inst);
                if (index >= _method.Parameters.Count + (_method.HasThis ? 1 : 0))
                    throw new OperandOutOfRangeException(inst,
                        $"Method parameters count: {_method.Parameters.Count + (_method.HasThis ? 1 : 0)}");
                return [_method.Parameters[index].ParameterType];
            }
            case Code.Stloc:
            {
                if (inst.Operand is not ushort index)
                    throw new InvalidInstructionException(typeof(ushort), inst.Operand?.GetType(), inst);
                if (index >= _method.Body.Variables.Count)
                    throw new OperandOutOfRangeException(inst,
                        $"Method local variables count: {_method.Body.Variables}");
                return [_method.Body.Variables[index].VariableType];
            }
            case Code.Stsfld:
            {
                if (inst.Operand is not FieldReference field)
                    throw new InvalidInstructionException(typeof(FieldReference), inst.Operand?.GetType(), inst);
                return [field.FieldType];
            }
            case Code.Conv_I1:
            case Code.Conv_I2:
            case Code.Conv_I4:
            case Code.Conv_Ovf_I1:
            case Code.Conv_Ovf_I2:
            case Code.Conv_Ovf_I4:
                break;
        }
        throw new NotImplementedException();
        
    }
    
    private StackTypeRef[] FindExpectTypes_Pop1_pop1(Instruction inst)
    {
        throw new NotImplementedException();
        
    }

    private StackTypeRef[] FindExpectTypes_Popi_pop1(Instruction inst)
    {
        throw new NotImplementedException();
        
    }
    
    private StackTypeRef[] FindExpectTypes_Popref_pop1(Instruction inst)
    {
        throw new NotImplementedException();
        
    }
    private StackTypeRef[] FindExpectTypes_Pop3(Instruction inst)
    {
        throw new NotImplementedException();
    }
    
    private StackTypeRef[] FindExpectTypes_Popref_popi(Instruction inst)
    {
        throw new NotImplementedException();
    }
    
    private StackTypeRef[] FindExpectType_Popref(Instruction inst)
    {
        throw new NotImplementedException();
    }
    
    static StackTypeRef[] FindExpectTypes_VarPop(Instruction inst)
    {
        if (inst.Operand is not IMethodSignature sig)
            return [];

        var paramTypes = sig.Parameters.Select(p => new StackTypeRef(p.ParameterType));

        if (sig.HasThis)
        {
            if (sig is MethodReference methodRef)
                paramTypes = paramTypes.Prepend(methodRef.DeclaringType);
            else
                paramTypes = paramTypes.Prepend(VerificationType.All);
        }

        return paramTypes.ToArray();
    }

}