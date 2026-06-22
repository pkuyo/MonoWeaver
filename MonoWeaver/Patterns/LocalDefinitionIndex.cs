using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Utils;

namespace MonoWeaver.Patterns;

/// <summary>
/// local 的最小 reaching-definition index。它只用于消除 captured local 的歧义，
/// 或将已证明 single-definition 的 compiler temporary 视为 transparent。
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

        var changed = true;
        var iterations = 0;
        while (changed && iterations++ < model.Blocks.Count * 4 + 8)
        {
            changed = false;
            foreach (var block in model.Blocks)
            {
                var merged = NewState(localCount);
                foreach (var predecessor in block.Predecessors)
                    UnionInto(merged, outgoing[predecessor.From]);

                if (!StateEquals(incoming[block], merged))
                {
                    incoming[block] = merged;
                    changed = true;
                }

                var transferred = CloneState(merged);
                TransferBlock(model, block, transferred);
                if (!StateEquals(outgoing[block], transferred))
                {
                    outgoing[block] = transferred;
                    changed = true;
                }
            }
        }

        var definitionsAtLoad = new Dictionary<Instruction, Instruction[]>();
        foreach (var block in model.Blocks)
        {
            var state = CloneState(incoming[block]);
            for (var i = block.StartIndex; i <= block.EndIndex; i++)
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

    private static HashSet<Instruction>[] CloneState(HashSet<Instruction>[] source)
    {
        var clone = new HashSet<Instruction>[source.Length];
        for (var i = 0; i < source.Length; i++)
            clone[i] = new HashSet<Instruction>(source[i]);
        return clone;
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
                state[index].Add(instruction);
            }
        }
    }
}

