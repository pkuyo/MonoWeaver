using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoWeaver.Utils;
using Xunit;
using BindingFlags = System.Reflection.BindingFlags;

namespace MonoWeaver.PatternTests;

public sealed class MonoModILLabelHelperTests
{
    [Fact]
    public void GetContextAndTargetResolveRealMonoModLabel()
    {
        ResetMonoModHelperCache();
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Select");
        using var context = new ILContext(method);
        var target = context.Instrs.Last();
        var label = context.DefineLabel(target);

        Assert.Same(context, CecilHelper.GetContext(label));
        Assert.Same(target, CecilHelper.GetTarget(label));
    }

    [Fact]
    public void BranchLabelsAndInstructionTargetsRoundTripInRealILContext()
    {
        ResetMonoModHelperCache();
        using var module = CreateBranchAndSwitchModule(out var method);
        using var context = new ILContext(method);
        var branch = context.Instrs.Single(instruction => instruction.OpCode.Code == Code.Br);
        var switchInstruction = context.Instrs.Single(instruction => instruction.OpCode.Code == Code.Switch);
        var branchTarget = ResolveTarget(branch.Operand);
        var switchTargets = ResolveTargets(switchInstruction.Operand);

        CecilHelper.BranchLabelsToTarget(context);

        Assert.Same(branchTarget, branch.Operand);
        Assert.Equal(switchTargets, Assert.IsType<Instruction[]>(switchInstruction.Operand));

        CecilHelper.BranchTargetsToLabels(context);

        var branchLabel = Assert.IsType<ILLabel>(branch.Operand);
        Assert.Same(branchTarget, CecilHelper.GetTarget(branchLabel));
        var switchLabels = Assert.IsType<ILLabel[]>(switchInstruction.Operand);
        Assert.Equal(switchTargets.Length, switchLabels.Length);
        for (var i = 0; i < switchTargets.Length; i++)
            Assert.Same(switchTargets[i], CecilHelper.GetTarget(switchLabels[i]));

        CecilHelper.BranchLabelsToTarget(context);

        Assert.Same(branchTarget, branch.Operand);
        Assert.Equal(switchTargets, Assert.IsType<Instruction[]>(switchInstruction.Operand));
    }

    [Fact]
    public void FindMonoModLabelOperandDetectsSwitchOnlyLabels()
    {
        ResetMonoModHelperCache();
        using var module = CreateBranchAndSwitchModule(out var method);

        //纯 Cecil 状态下不应误报
        Assert.Null(CecilHelper.FindMonoModLabelOperand(method.Body));

        using var context = new ILContext(method);
        CecilHelper.BranchTargetsToLabels(context);

        //把唯一的 br 还原成 Instruction 目标，只保留 switch 的 ILLabel[]
        var branch = context.Instrs.Single(instruction => instruction.OpCode.Code == Code.Br);
        branch.Operand = CecilHelper.GetTarget(Assert.IsType<ILLabel>(branch.Operand))
            ?? throw new InvalidOperationException("The branch label has no target.");

        var label = CecilHelper.FindMonoModLabelOperand(method.Body);

        Assert.IsType<ILLabel>(label);
        Assert.Same(context, CecilHelper.GetContext(label!));
    }

    private static ModuleDefinition CreateBranchAndSwitchModule(out MethodDefinition method)
    {
        var module = ModuleDefinition.CreateModule("MonoModILLabelHelperFixtures", ModuleKind.Dll);
        var type = new TypeDefinition(
            "MonoWeaver.PatternTests",
            "MonoModILLabelHelperFixture",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
            module.TypeSystem.Object);
        module.Types.Add(type);

        method = new MethodDefinition(
            "BranchAndSwitch",
            MethodAttributes.Public | MethodAttributes.Static,
            module.TypeSystem.Int32);
        method.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.Int32));
        type.Methods.Add(method);

        var il = method.Body.GetILProcessor();
        var loadSwitchValue = il.Create(OpCodes.Ldarg_0);
        var returnZero = il.Create(OpCodes.Ldc_I4_0);
        var returnOne = il.Create(OpCodes.Ldc_I4_1);
        var returnFallback = il.Create(OpCodes.Ldc_I4_M1);

        il.Append(il.Create(OpCodes.Br, loadSwitchValue));
        il.Append(il.Create(OpCodes.Ldc_I4, 42));
        il.Append(il.Create(OpCodes.Ret));
        il.Append(loadSwitchValue);
        il.Append(il.Create(OpCodes.Switch, new[] { returnZero, returnOne }));
        il.Append(returnFallback);
        il.Append(il.Create(OpCodes.Ret));
        il.Append(returnZero);
        il.Append(il.Create(OpCodes.Ret));
        il.Append(returnOne);
        il.Append(il.Create(OpCodes.Ret));

        return module;
    }

    private static Instruction ResolveTarget(object operand)
        => operand switch
        {
            Instruction instruction => instruction,
            ILLabel label => CecilHelper.GetTarget(label)
                ?? throw new InvalidOperationException("The ILLabel has no target."),
            _ => throw new InvalidOperationException($"Unexpected branch operand: {operand.GetType().FullName}."),
        };

    private static Instruction[] ResolveTargets(object operand)
        => operand switch
        {
            Instruction[] instructions => instructions,
            ILLabel[] labels => labels.Select(label => CecilHelper.GetTarget(label)
                    ?? throw new InvalidOperationException("The ILLabel has no target."))
                .ToArray(),
            _ => throw new InvalidOperationException($"Unexpected switch operand: {operand.GetType().FullName}."),
        };

    private static void ResetMonoModHelperCache()
    {
        var fields = new[]
        {
            "_iLLabelGetTarget",
            "_branchTargetsToLabels",
            "_branchLabelsToTarget",
            "_getContext",
        };

        foreach (var name in fields)
        {
            var field = typeof(CecilHelper).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(CecilHelper).FullName, name);
            field.SetValue(null, null);
        }
    }
}
