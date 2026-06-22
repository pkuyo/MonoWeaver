using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.Utils;

namespace MonoWeaver.CFG;

public enum ControlFlowEdgeKind
{
    FallThrough,
    True,
    False,
    Unconditional,
    SwitchCase
}

public sealed class BasicBlock(Instruction leader, EHandler.Region region) : IEquatable<BasicBlock>
{
    public Instruction Leader = leader;
    public Instruction Terminator = leader;
    public EvalStackNode? EntryNode = null!;
    public List<ControlFlowEdge> Edges = new();
    public List<ControlFlowEdge> Predecessors = new();
    public int _entryStackDepth = -1;
    public int Id { get; internal set; } = -1;
    public int StartIndex { get; internal set; } = -1;
    public int EndIndex { get; internal set; } = -1;
    public List<ControlFlowEdge> Successors => Edges;
    public RegionKind Kind = region.Kind;
    public EHandler.Region Region = region;
    public int EntryStackDepth => EntryNode?.Depth ?? _entryStackDepth;
    public BitArray? initLocals = null;

    public bool Equals(BasicBlock? other)
        => other is not null && Leader.Equals(other.Leader);

    public override bool Equals(object? obj)
        => obj is BasicBlock other && Equals(other);

    public override int GetHashCode()
        => Leader.GetHashCode();

    public override string ToString()
    {
        var range = EndIndex >= 0 ? $"-IL_{Terminator.Offset:X4}" : string.Empty;
        return $"B{Id}: IL_{Leader.Offset:X4}{range} [{Kind} Region: {Region.Start}-{Region.End}]";
    }
}

public sealed class ControlFlowEdge(BasicBlock from, BasicBlock to,
    ControlFlowEdgeKind kind = ControlFlowEdgeKind.Unconditional,
    Instruction? terminator = null, int? switchCase = null, bool isFallThrough = false)
{
    public BasicBlock From = from;
    public BasicBlock To = to;
    public ControlFlowEdgeKind Kind { get; } = kind;
    public Instruction Terminator { get; } = terminator ?? from.Terminator;
    public int? SwitchCase { get; } = switchCase;
    public bool IsFallThrough { get; } = isFallThrough;
}

internal sealed record ILBasicBlockGraph(
    MethodDefinition Method,
    Instruction[] Instructions,
    Dictionary<Instruction, int> InstructionIndices,
    List<BasicBlock> Blocks,
    Dictionary<Instruction, BasicBlock> BlockByInstruction,
    List<BasicBlock> EntryBlocks);

internal static class ILBasicBlockGraphBuilder
{
    public static ILBasicBlockGraph Build(MethodDefinition method)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (!method.HasBody)
            throw new ArgumentException("The method must have a CIL body.", nameof(method));
        if (method.Body.Instructions.Count == 0)
            throw new ArgumentException("The method body is empty.", nameof(method));

        var instructions = method.Body.Instructions.ToArray();
        var indices = CreateInstructionIndices(instructions);
        return CreateGraph(method, instructions, indices, CollectLeaders(method, instructions, indices),
            connectEdges: true);
    }

    private static ILBasicBlockGraph CreateGraph(MethodDefinition method, Instruction[] instructions,
        Dictionary<Instruction, int> indices,
        IEnumerable<(Instruction Instruction, EHandler.Region Region, bool IsEntry)> leaders,
        bool connectEdges)
    {
        var blocks = BuildBlocks(instructions, indices, leaders, out var entryBlocks);
        var blockByInstruction = MapInstructionsToBlocks(instructions, blocks);
        if (connectEdges)
            ConnectEdges(blocks, blockByInstruction);
        return new ILBasicBlockGraph(method, instructions, indices, blocks, blockByInstruction, entryBlocks);
    }

    private static Dictionary<Instruction, int> CreateInstructionIndices(Instruction[] instructions)
    {
        var indices = new Dictionary<Instruction, int>(instructions.Length);
        for (var i = 0; i < instructions.Length; i++)
            indices[instructions[i]] = i;
        return indices;
    }

    private static List<(Instruction Instruction, EHandler.Region Region, bool IsEntry)> CollectLeaders(
        MethodDefinition method, Instruction[] instructions, Dictionary<Instruction, int> indices)
    {
        var region = EHandler.CreateMethodRegion(instructions.Length + 1).ProtectedRegion;
        var leaders = new Dictionary<Instruction, (Instruction Instruction, EHandler.Region Region, bool IsEntry)>();
        AddLeader(instructions[0], isEntry: true);

        foreach (var instruction in instructions)
        {
            if (instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch)
            {
                if (!CecilHelper.TryResolveOperandTargets(instruction.Operand, out var targets, out _))
                    continue;

                foreach (var target in targets)
                {
                    if (indices.ContainsKey(target))
                        AddLeader(target);
                }
            }

            if (instruction.Next is not null &&
                (instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch
                     or FlowControl.Return or FlowControl.Throw ||
                 instruction.OpCode.Code is Code.Jmp))
            {
                AddLeader(instruction.Next);
            }
        }

        foreach (var handler in method.Body.ExceptionHandlers)
        {
            AddLeader(handler.TryStart, isEntry: true);
            AddLeader(handler.TryEnd, isEntry: true);
            AddLeader(handler.FilterStart, isEntry: true);
            AddLeader(handler.HandlerStart, isEntry: true);
            AddLeader(handler.HandlerEnd, isEntry: true);
        }

        return leaders.Values.OrderBy(leader => indices[leader.Instruction]).ToList();

        void AddLeader(Instruction? instruction, bool isEntry = false)
        {
            if (instruction is null || !indices.ContainsKey(instruction))
                return;
            if (leaders.TryGetValue(instruction, out var existing))
            {
                if (isEntry && !existing.IsEntry)
                    leaders[instruction] = (instruction, existing.Region, true);
                return;
            }
            leaders[instruction] = (instruction, region, isEntry);
        }
    }

    private static List<BasicBlock> BuildBlocks(Instruction[] instructions,
        Dictionary<Instruction, int> indices,
        IEnumerable<(Instruction Instruction, EHandler.Region Region, bool IsEntry)> leaders,
        out List<BasicBlock> entryBlocks)
    {
        var starts = leaders
            .GroupBy(leader => leader.Instruction)
            .Select(group => group.First())
            .OrderBy(leader => indices[leader.Instruction])
            .ToArray();

        var blocks = new List<BasicBlock>(starts.Length);
        entryBlocks = new List<BasicBlock>();
        for (var i = 0; i < starts.Length; i++)
        {
            var start = indices[starts[i].Instruction];
            var end = i + 1 < starts.Length ? indices[starts[i + 1].Instruction] - 1 : instructions.Length - 1;
            var block = new BasicBlock(instructions[start], starts[i].Region)
            {
                Id = i,
                StartIndex = start,
                EndIndex = end,
                Terminator = instructions[end],
            };
            blocks.Add(block);
            if (starts[i].IsEntry)
                entryBlocks.Add(block);
        }
        return blocks;
    }

    private static Dictionary<Instruction, BasicBlock> MapInstructionsToBlocks(
        Instruction[] instructions, List<BasicBlock> blocks)
    {
        var result = new Dictionary<Instruction, BasicBlock>(instructions.Length);
        foreach (var block in blocks)
        {
            for (var i = block.StartIndex; i <= block.EndIndex; i++)
                result[instructions[i]] = block;
        }
        return result;
    }

    private static void ConnectEdges(List<BasicBlock> blocks,
        Dictionary<Instruction, BasicBlock> blockByInstruction)
    {
        foreach (var block in blocks)
        {
            var terminator = block.Terminator;
            var nextBlock = block.Id + 1 < blocks.Count ? blocks[block.Id + 1] : null;

            if (terminator.OpCode.Code == Code.Switch)
            {
                if (!CecilHelper.TryResolveOperandTargets(terminator.Operand, out var targets, out _))
                    continue;

                var caseIndex = 0;
                foreach (var target in targets)
                {
                    if (blockByInstruction.TryGetValue(target, out var targetBlock))
                        AddEdge(block, targetBlock, ControlFlowEdgeKind.SwitchCase, terminator, caseIndex++);
                }
                if (nextBlock is not null)
                    AddEdge(block, nextBlock, ControlFlowEdgeKind.FallThrough, terminator, isFallThrough: true);
                continue;
            }

            if (CecilInstructionHelpers.IsConditionalBranch(terminator))
            {
                if (!CecilHelper.TryResolveOperandTargets(terminator.Operand, out var targets, out _))
                    continue;

                var target = targets.FirstOrDefault();
                if (target is null || nextBlock is null || !blockByInstruction.TryGetValue(target, out var targetBlock))
                    continue;

                if (CecilInstructionHelpers.IsBranchOnFalse(terminator))
                {
                    AddEdge(block, targetBlock, ControlFlowEdgeKind.False, terminator);
                    AddEdge(block, nextBlock, ControlFlowEdgeKind.True, terminator, isFallThrough: true);
                }
                else
                {
                    AddEdge(block, targetBlock, ControlFlowEdgeKind.True, terminator);
                    AddEdge(block, nextBlock, ControlFlowEdgeKind.False, terminator, isFallThrough: true);
                }
                continue;
            }

            if (CecilInstructionHelpers.IsUnconditionalBranch(terminator))
            {
                if (!CecilHelper.TryResolveOperandTargets(terminator.Operand, out var targets, out _))
                    continue;

                var target = targets.FirstOrDefault();
                if (target is not null && blockByInstruction.TryGetValue(target, out var targetBlock))
                    AddEdge(block, targetBlock, ControlFlowEdgeKind.Unconditional, terminator);
                continue;
            }

            if (terminator.OpCode.Code is Code.Jmp)
                continue;

            if (terminator.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw)
                continue;
            if (nextBlock is not null)
                AddEdge(block, nextBlock, ControlFlowEdgeKind.FallThrough, terminator, isFallThrough: true);
        }
    }

    private static void AddEdge(BasicBlock from, BasicBlock to,
        ControlFlowEdgeKind kind, Instruction terminator, int? switchCase = null, bool isFallThrough = false)
    {
        if (from.Edges.Any(edge => ReferenceEquals(edge.To, to) && edge.Kind == kind && edge.SwitchCase == switchCase))
            return;

        var edge = new ControlFlowEdge(from, to, kind, terminator, switchCase, isFallThrough);
        from.Successors.Add(edge);
        to.Predecessors.Add(edge);
    }

}
