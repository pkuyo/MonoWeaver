using System.Collections.Generic;
using Mono.Cecil;
// ReSharper disable InconsistentNaming

namespace MonoWeaver.CFG;

public class EvalStackTransfer
{
    public List<TypeReference> Pushed = new();
    public List<TypeReference> Popped = new();
}

public class EvalStackNode
{
    public EvalStackNode(int depth = 0, TypeReference? type = null)
    {
        Type = type;
    }
    
    public TypeReference? Type;
    
    public EvalStackNode? Parent;

    public int Depth = 0;

    public void AppendChild(EvalStackNode node)
    {
        node.Parent = this;
    }
    
    public EvalStackNode AppendChild(TypeReference type)
    {
        var node = new EvalStackNode(Depth + 1, type)
        {
            Parent = this
        };
        return node;
    }
}

    
