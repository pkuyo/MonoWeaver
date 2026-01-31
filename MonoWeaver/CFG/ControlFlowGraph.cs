using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using MonoMod.Cil;
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace MonoWeaver.CFG;

[Flags]
public enum StackTypeKind : uint 
{
    Unknown = 0,
    I4 = 1,
    I8 = 1 << 1,
    I =  1 << 2,      // native int
    R4 = 1 << 3,
    R8 = 1 << 4,
    ByRef = 1 << 5,
    Ptr = 1 << 6,
    O = 1 << 7,      // object ref
    ValueType = 1 << 8,
    
    Num = I4 | I8 | R4 | R8 | I,
    F = R4 | R8,
    
    All = int.MaxValue
}

internal class StackTypeRef 
{
    public StackTypeRef(StackTypeKind kind) => Kind = kind;

    public StackTypeRef(TypeReference type, StackTypeKind kind = StackTypeKind.Unknown)
    {
        Kind = kind;
        var typeSystem = type.Module.TypeSystem;
        if(typeSystem.Int32 == type || typeSystem.UInt32 == type || 
         typeSystem.Int16 == type || typeSystem.UInt16 == type ||
         typeSystem.Byte == type || typeSystem.Char == type)
        {
            Kind |= StackTypeKind.I4;
            return;
        }
        
        throw new NotImplementedException();
        Type = type;
    } 

    public readonly StackTypeKind Kind;

    public readonly TypeReference? Type;
    
    public static implicit operator StackTypeRef(TypeReference type) => new(type);
    public static implicit operator StackTypeRef(StackTypeKind kind) => new(kind);
    public bool Match(TypeReference? right) 
    {
        if (right is null)
            return false;
        if (Type is not null)
            return Type == right;
        var typeSystem = right.Module.TypeSystem;
        if(Kind.HasFlag(StackTypeKind.I4) && 
           (typeSystem.Int32 == right || typeSystem.UInt32 == right || 
            typeSystem.Int16 == right || typeSystem.UInt16 == right ||
            typeSystem.Byte == right || typeSystem.Char == right))
            return true;
        if(Kind.HasFlag(StackTypeKind.I8) &&  
           (typeSystem.Int64 == right || typeSystem.UInt64 == right))
            return true;
        if(Kind.HasFlag(StackTypeKind.I) && 
           (typeSystem.IntPtr == right || typeSystem.UIntPtr == right))
            return true;
        if(Kind.HasFlag(StackTypeKind.R4) && typeSystem.Single == right)
            return true;
        if(Kind.HasFlag(StackTypeKind.R8) && typeSystem.Double == right)
            return true;
        return false;
    }

    public StackTypeRef? GetCompatible(StackTypeRef other)
    {
        throw new NotImplementedException();
    }

    public bool Match(StackTypeRef? right)
    {
        if (right is null)
            return false;
        if (this.Type is not null)
            return right.Match(Type);
        if (right.Type is not null)
            return Match(right.Type);
        return (right.Kind | Kind) != 0;
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

                    StackBehaviour.Popi   => [ts.Int32],
                    StackBehaviour.Popref => FindExpectType_Popref(inst),

                    StackBehaviour.Popi_popi   => [ts.Int32, ts.Int32],
                    StackBehaviour.Popi_popi8  => [ts.Int32, ts.Int64],
                    StackBehaviour.Popi_popr4  => [ts.Int32, ts.Single],
                    StackBehaviour.Popi_popr8  => [ts.Int32, ts.Double],
                    StackBehaviour.Popref_popi => FindExpectTypes_Popref_popi(inst),

                    StackBehaviour.Popi_popi_popi       => [ts.Int32, ts.Int32, ts.Int32],
                    StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or
                    StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or 
                    StackBehaviour.Popref_popi_popref   => FindExpectTypes_Pop3(inst),
                    
                    
                    StackBehaviour.Pop1 => FindExpectType_Pop1(inst),

                    StackBehaviour.Popi_pop1 => FindExpectTypes_Popi_pop1(inst),

                    StackBehaviour.Popref_pop1 => FindExpectTypes_Popref_pop1(inst),

                    StackBehaviour.Pop1_pop1 => FindExpectTypes_Pop1_pop1(inst),

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
                    throw new InvalidOperationException(typeof(ushort), inst.Operand?.GetType(), inst);
                if (index >= _method.Parameters.Count + (_method.HasThis ? 1 : 0))
                    throw new OperandOutOfRangeException(inst,
                        $"Method parameters count: {_method.Parameters.Count + (_method.HasThis ? 1 : 0)}");
                return [_method.Parameters[index].ParameterType];
            }
            case Code.Stloc:
            {
                if (inst.Operand is not ushort index)
                    throw new InvalidOperationException(typeof(ushort), inst.Operand?.GetType(), inst);
                if (index >= _method.Body.Variables.Count)
                    throw new OperandOutOfRangeException(inst,
                        $"Method local variables count: {_method.Body.Variables}");
                return [_method.Body.Variables[index].VariableType];
            }
            case Code.Stsfld:
            {
                if (inst.Operand is not FieldReference field)
                    throw new InvalidOperationException(typeof(FieldReference), inst.Operand?.GetType(), inst);
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
                paramTypes = paramTypes.Prepend(StackTypeKind.All);
        }

        return paramTypes.ToArray();
    }

}