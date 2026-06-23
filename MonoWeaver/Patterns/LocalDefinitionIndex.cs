using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Utils;

namespace MonoWeaver.Patterns;

/// <summary>
/// local在某个ld位置时候其st的可能来源。它只用于消除 local 变量的歧义
/// </summary>
internal sealed class LocalDefinitionIndex
{
    private readonly CilMethodModel _model;
    private readonly Dictionary<Instruction, Instruction[]> _definitionsAtLoad;
    private readonly bool[] _addressTaken;

    private LocalDefinitionIndex(CilMethodModel model,
        Dictionary<Instruction, Instruction[]> definitionsAtLoad, bool[] addressTaken)
    {
        _model = model;
        _definitionsAtLoad = definitionsAtLoad;
        _addressTaken = addressTaken;
    }

    public static LocalDefinitionIndex Create(CilMethodModel model)
    {
        var localCount = model.Method.Body.Variables.Count;
        var addressTaken = new bool[localCount];
        foreach (var instruction in model.Instructions)
        {
            if (CecilInstructionHelpers.IsLoadLocalAddress(instruction)
                && CecilInstructionHelpers.TryGetLocal(model.Method, instruction, out var index, out _)
                && index >= 0 && index < localCount)
            {
                addressTaken[index] = true;
            }
        }

        var incoming = new Dictionary<BasicBlock, HashSet<Instruction>[]>();
        var outgoing = new Dictionary<BasicBlock, HashSet<Instruction>[]>();
        foreach (var block in model.Blocks)
        {
            incoming[block] = NewState(localCount);
            outgoing[block] = NewState(localCount);
        }
        var updateBlock = model.EntryBlocks.ToList();


        var tempMerged = NewState(localCount);
        var tempTransferred = NewState(localCount);
        int iteration = 0;
        for (; iteration < updateBlock.Count && iteration < model.Blocks.Count * 4 + 8; iteration++)
        {
            var block = updateBlock[iteration];
            var merged = tempMerged;
            foreach (var set in tempMerged) set.Clear();

            foreach (var predecessor in block.Predecessors)
                UnionInto(merged, outgoing[predecessor.From]);

            if (!StateEquals(incoming[block], merged))
            {
                tempMerged = incoming[block];
                incoming[block] = merged;
            }
            var transferred = tempTransferred;
            CloneState(incoming[block], transferred);
            TransferBlock(model, block, transferred); //记录更新stloc
            if (!StateEquals(outgoing[block], transferred))
            {
                tempTransferred = outgoing[block];
                foreach (var set in tempTransferred)
                    set.Clear();

                foreach (var succ in block.Successors)
                    updateBlock.Add(succ.To); //更新后继
                outgoing[block] = transferred;
            }
        }

        if (iteration == model.Blocks.Count * 4 + 8)
        {
            throw new InvalidOperationException(
                $"Local definition analysis did not converge for {model.Method.FullName}.");
        }

        var definitionsAtLoad = new Dictionary<Instruction, Instruction[]>();
        var tempBlock = NewState(localCount);
        foreach (var block in model.Blocks)
        {
            var state = tempBlock;

            CloneState(incoming[block], state);
            for (var i = block.StartIndex; i <= block.EndIndex; i++) //block内逐语句更新并记录ldloc -> stloc[]
            {
                var instruction = model.Instructions[i];
                if (CecilInstructionHelpers.IsLoadLocal(instruction)
                    && CecilInstructionHelpers.TryGetLocal(model.Method, instruction, out var loadIndex, out _)
                    && loadIndex >= 0 && loadIndex < localCount)
                {
                    definitionsAtLoad[instruction] = state[loadIndex]
                        .OrderBy(model.IndexOf)
                        .ToArray();
                }

                if (CecilInstructionHelpers.IsStoreLocal(instruction)
                    && CecilInstructionHelpers.TryGetLocal(model.Method, instruction, out var storeIndex, out _)
                    && storeIndex >= 0 && storeIndex < localCount)
                {
                    state[storeIndex].Clear();
                    state[storeIndex].Add(instruction);
                }
            }
        }

        return new LocalDefinitionIndex(model, definitionsAtLoad, addressTaken);
    }

    public bool IsAddressTaken(int localIndex)
        => localIndex >= 0 && localIndex < _addressTaken.Length && _addressTaken[localIndex];

    public IReadOnlyList<Instruction> GetDefinitions(TargetLocalReadNode localRead)
        => _definitionsAtLoad.TryGetValue(localRead.ProducerInstruction, out var definitions)
            ? definitions
            : Array.Empty<Instruction>();

    public bool TryGetUniqueDefinition(TargetLocalReadNode localRead,
        out Instruction storeInstruction, out TargetExpressionNode storedValue, out string failure)
    {
        storeInstruction = null!;
        storedValue = null!;
        failure = string.Empty;

        if (IsAddressTaken(localRead.Variable.Index))
        {
            failure = $"Local V_{localRead.Variable.Index} has its address taken.";
            return false;
        }

        var definitions = GetDefinitions(localRead);
        if (definitions.Count != 1)
        {
            failure = $"Local V_{localRead.Variable.Index} has {definitions.Count} reaching definitions at IL_{localRead.ProducerInstruction.Offset:X4}.";
            return false;
        }

        storeInstruction = definitions[0];
        if (!_model.TryGetStoredValue(storeInstruction, out storedValue))
        {
            failure = $"The value stored by IL_{storeInstruction.Offset:X4} could not be reconstructed.";
            return false;
        }

        return true;
    }

    private static HashSet<Instruction>[] NewState(int count)
    {
        var state = new HashSet<Instruction>[count];
        for (var i = 0; i < count; i++)
            state[i] = new HashSet<Instruction>();
        return state;
    }

    private static void CloneState(HashSet<Instruction>[] source, HashSet<Instruction>[] dest)
    {
        for (var i = 0; i < source.Length; i++)
        {
            dest[i].Clear();
            foreach (var item in source[i])
                dest[i].Add(item);
        }
    }

    private static void UnionInto(HashSet<Instruction>[] target, HashSet<Instruction>[] source)
    {
        for (var i = 0; i < target.Length; i++)
            target[i].UnionWith(source[i]);
    }

    private static bool StateEquals(HashSet<Instruction>[] left, HashSet<Instruction>[] right)
    {
        if (left.Length != right.Length)
            return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (!left[i].SetEquals(right[i]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// 更新每个local的stloc位置
    /// </summary>
    /// <param name="model"></param>
    /// <param name="block"></param>
    /// <param name="state"></param>
    private static void TransferBlock(CilMethodModel model, BasicBlock block,
        HashSet<Instruction>[] state)
    {
        for (var i = block.StartIndex; i <= block.EndIndex; i++)
        {
            var instruction = model.Instructions[i];
            if (CecilInstructionHelpers.IsStoreLocal(instruction)
                && CecilInstructionHelpers.TryGetLocal(model.Method, instruction, out var index, out _)
                && index >= 0 && index < state.Length)
            {
                state[index].Clear();
                state[index].Add(instruction); //如果出现新的store把既有的store clear（被覆盖）
            }
        }
    }
}

