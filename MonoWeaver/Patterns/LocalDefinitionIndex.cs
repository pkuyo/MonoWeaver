using System;
using System.Collections.Generic;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;
using MonoWeaver.Utils;

namespace MonoWeaver.Patterns;

/// <summary>
/// local在某个ld位置时候其st的可能来源。它只用于消除 local 变量的歧义。
/// 实现为标准的 reaching-definitions：每条 stloc 编一个号，块状态是按 store 编号的位集，
/// 因此内存是 O(block × store/64) 而不是 O(block × local) 个 HashSet。
/// </summary>
internal sealed class LocalDefinitionIndex
{
    private readonly MethodModel _model;
    private readonly Dictionary<Instruction, Instruction[]> _definitionsAtLoad;
    private readonly bool[] _addressTaken;

    private LocalDefinitionIndex(MethodModel model,
        Dictionary<Instruction, Instruction[]> definitionsAtLoad, bool[] addressTaken)
    {
        _model = model;
        _definitionsAtLoad = definitionsAtLoad;
        _addressTaken = addressTaken;
    }

    public static LocalDefinitionIndex Create(MethodModel model)
    {
        var method = model.Method;
        var instructions = model.Instructions;
        var localCount = method.Body.Variables.Count;
        var addressTaken = new bool[localCount];
        var definitionsAtLoad = new Dictionary<Instruction, Instruction[]>();
        var storeLocal = new List<int>();
        var storeInstructions = new List<Instruction>();
        var loadCount = 0;
        for (var i = 0; i < instructions.Length; i++)
        {
            var instruction = instructions[i];
            var code = instruction.OpCode.Code;
            if (CecilInstructionHelpers.IsLoadLocalAddress(code))
            {
                if (CecilInstructionHelpers.TryGetLocal(method, instruction, out var index, out _)
                    && index >= 0 && index < localCount)
                {
                    addressTaken[index] = true;
                }
            }
            else if (CecilInstructionHelpers.IsStoreLocal(code))
            {
                if (CecilInstructionHelpers.TryGetLocal(method, instruction, out var index, out _)
                    && index >= 0 && index < localCount)
                {
                    storeLocal.Add(index);
                    storeInstructions.Add(instruction);
                }
            }
            else if (CecilInstructionHelpers.IsLoadLocal(code))
            {
                loadCount++;
            }
        }

        var storeCount = storeLocal.Count;
        if (storeCount == 0 || loadCount == 0)
            return new LocalDefinitionIndex(model, definitionsAtLoad, addressTaken);

        var words = (storeCount + 63) >> 6;

        var localMask = new ulong[localCount * words];
        for (var s = 0; s < storeCount; s++)
            localMask[storeLocal[s] * words + (s >> 6)] |= 1UL << (s & 63);

        var blocks = model.Blocks;
        var blockCount = blocks.Count;
        var gen = new ulong[blockCount * words];
        var kill = new ulong[blockCount * words];
        var storeCursor = 0;
        for (var b = 0; b < blockCount; b++)
        {
            var block = blocks[b];
            var offset = b * words;
            for (var i = block.StartIndex; i <= block.EndIndex; i++)
            {
                if (storeCursor >= storeCount || !ReferenceEquals(instructions[i], storeInstructions[storeCursor]))
                    continue;

                var local = storeLocal[storeCursor];
                var maskOffset = local * words;
                for (var w = 0; w < words; w++)
                {
                    kill[offset + w] |= localMask[maskOffset + w];
                    gen[offset + w] &= ~localMask[maskOffset + w];
                }
                gen[offset + (storeCursor >> 6)] |= 1UL << (storeCursor & 63);
                storeCursor++;
            }
        }

        var inSet = new ulong[blockCount * words];
        var outSet = new ulong[blockCount * words];
        var visited = new bool[blockCount];
        var queued = new bool[blockCount];
        var worklist = new Queue<BasicBlock>();
        foreach (var entry in model.EntryBlocks)
        {
            if (queued[entry.Id])
                continue;
            queued[entry.Id] = true;
            worklist.Enqueue(entry);
        }

        while (worklist.Count != 0)
        {
            var block = worklist.Dequeue();
            var b = block.Id;
            queued[b] = false;
            var offset = b * words;

            for (var w = 0; w < words; w++)
                inSet[offset + w] = 0;
            foreach (var predecessor in block.Predecessors)
            {
                var predecessorOffset = predecessor.From.Id * words;
                for (var w = 0; w < words; w++)
                    inSet[offset + w] |= outSet[predecessorOffset + w];
            }

            var changed = !visited[b];
            visited[b] = true;
            for (var w = 0; w < words; w++)
            {
                var next = (inSet[offset + w] & ~kill[offset + w]) | gen[offset + w];
                if (next != outSet[offset + w])
                {
                    outSet[offset + w] = next;
                    changed = true;
                }
            }

            if (!changed)
                continue;

            foreach (var successor in block.Successors)
            {
                var target = successor.To;
                if (queued[target.Id])
                    continue;
                queued[target.Id] = true;
                worklist.Enqueue(target);
            }
        }

        var state = new ulong[words];
        storeCursor = 0;
        for (var b = 0; b < blockCount; b++)
        {
            var block = blocks[b];
            Array.Copy(inSet, b * words, state, 0, words);
            for (var i = block.StartIndex; i <= block.EndIndex; i++)
            {
                var instruction = instructions[i];
                var code = instruction.OpCode.Code;
                if (CecilInstructionHelpers.IsLoadLocal(code))
                {
                    if (CecilInstructionHelpers.TryGetLocal(method, instruction, out var loadIndex, out _)
                        && loadIndex >= 0 && loadIndex < localCount)
                    {
                        var definitions = CollectDefinitions(state, localMask, loadIndex * words, words, storeInstructions);
                        if (definitions.Length != 0)
                            definitionsAtLoad[instruction] = definitions;
                    }
                }
                else if (storeCursor < storeCount && ReferenceEquals(instruction, storeInstructions[storeCursor]))
                {
                    var maskOffset = storeLocal[storeCursor] * words;
                    for (var w = 0; w < words; w++)
                        state[w] &= ~localMask[maskOffset + w];
                    state[storeCursor >> 6] |= 1UL << (storeCursor & 63);
                    storeCursor++;
                }
            }
        }

        return new LocalDefinitionIndex(model, definitionsAtLoad, addressTaken);
    }

    /// <summary>store 编号即指令顺序，按位升序枚举天然就是指令顺序。</summary>
    private static Instruction[] CollectDefinitions(ulong[] state, ulong[] localMask, int maskOffset, int words,
        List<Instruction> storeInstructions)
    {
        var count = 0;
        for (var w = 0; w < words; w++)
            count += PopCount(state[w] & localMask[maskOffset + w]);
        if (count == 0)
            return Array.Empty<Instruction>();

        var result = new Instruction[count];
        var n = 0;
        for (var w = 0; w < words; w++)
        {
            var bits = state[w] & localMask[maskOffset + w];
            while (bits != 0)
            {
                var bit = TrailingZeroCount(bits);
                result[n++] = storeInstructions[(w << 6) + bit];
                bits &= bits - 1;
            }
        }
        return result;
    }

    private static int PopCount(ulong value)
    {
        var count = 0;
        while (value != 0)
        {
            value &= value - 1;
            count++;
        }
        return count;
    }

    private static int TrailingZeroCount(ulong value)
    {
        var count = 0;
        while ((value & 1) == 0)
        {
            value >>= 1;
            count++;
        }
        return count;
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
}
