using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;
using MethodBody = Mono.Cecil.Cil.MethodBody;

namespace MonoWeaver.Utils;

internal readonly record struct OperandTargetResolveError(
    Type Expected,
    Type Current,
    string Message);

public static partial class CecilHelper
{
    private const string MonoModILLabelFullName = "MonoMod.Cil.ILLabel";

    private delegate Instruction? ILLabelTargetHandler(object label);
    private delegate void LabelTargetSwitcher(object il);
    private delegate object GetContextHandler(object label);

    private static ILLabelTargetHandler? _iLLabelGetTarget;
    private static LabelTargetSwitcher? _branchTargetsToLabels;
    private static LabelTargetSwitcher? _branchLabelsToTarget;
    private static GetContextHandler? _getContext;

    private static readonly object ILLabelResolverLock = new();

    public static Instruction? GetTarget(object label)
    {
        if (_iLLabelGetTarget == null)
            BuildMonoModResolveStrategy(label.GetType());
        return _iLLabelGetTarget!(label);
    }

    public static object GetContext(object label)
    {
        if (_getContext == null)
            BuildMonoModResolveStrategy(label.GetType());
        return _getContext!(label);
    }

    public static void BranchTargetsToLabels(object il)
    {
        if (_branchTargetsToLabels == null)
            BuildMonoModResolveStrategy(GetILLabelTypeFromContext(il.GetType()));
        _branchTargetsToLabels!(il);
    }

    public static void BranchLabelsToTarget(object il)
    {
        if (_branchLabelsToTarget == null)
            BuildMonoModResolveStrategy(GetILLabelTypeFromContext(il.GetType()));
        _branchLabelsToTarget!(il);
    }

    internal static bool TryResolveInstructionTarget(object? operand, out Instruction? target,
        out OperandTargetResolveError error)
    {
        target = null;
        error = default;

        if (operand is Instruction inst)
        {
            target = inst;
            return true;
        }

        if (operand is null)
        {
            error = InvalidTargetOperand(typeof(void), "Instruction target operand is null.");
            return false;
        }

        var operandType = operand.GetType();
        if (IsMonoModILLabel(operandType))
        {
            return TryResolveILLabelTarget(operand, operandType, out target, out error);
        }

        error = InvalidTargetOperand(operandType,
            $"Invalid instruction target operand type: {operandType.FullName}.");
        return false;
    }

    internal static bool TryResolveInstructionTargetArray(object? operand, out Instruction[] targets,
        out OperandTargetResolveError error)
    {
        targets = Array.Empty<Instruction>();
        error = default;

        if (operand is null)
        {
            error = InvalidTargetArrayOperand(typeof(void), "Instruction target array operand is null.");
            return false;
        }

        if (operand is Instruction[] instructions)
            return TryCopyInstructionTargets(instructions, out targets, out error);

        var operandType = operand.GetType();
        if (!operandType.IsArray)
        {
            error = InvalidTargetArrayOperand(operandType,
                $"Invalid instruction target array operand type: {operandType.FullName}.");
            return false;
        }

        var elementType = operandType.GetElementType();
        if (elementType is null || !IsMonoModILLabel(elementType))
        {
            error = InvalidTargetArrayOperand(operandType,
                $"Invalid instruction target array operand type: {operandType.FullName}.");
            return false;
        }

        var array = (Array)operand;
        targets = new Instruction[array.Length];
        if (operand is object[] labels)
        {
            for (var i = 0; i < labels.Length; i++)
            {
                if (!TryResolveInstructionTarget(labels[i], out var target, out error))
                {
                    error = error with { Message = $"{error.Message} Array index: {i}." };
                    targets = Array.Empty<Instruction>();
                    return false;
                }

                targets[i] = target!;
            }

            return true;
        }

        for (var i = 0; i < array.Length; i++)
        {
            if (!TryResolveInstructionTarget(array.GetValue(i), out var target, out error))
            {
                error = error with { Message = $"{error.Message} Array index: {i}." };
                targets = Array.Empty<Instruction>();
                return false;
            }

            targets[i] = target!;
        }
        return true;
    }

    internal static bool TryResolveOperandTargets(object? operand, out Instruction[] targets,
        out OperandTargetResolveError error)
    {
        if (operand is Array)
            return TryResolveInstructionTargetArray(operand, out targets, out error);

        if (TryResolveInstructionTarget(operand, out var target, out error))
        {
            targets = new[] { target! };
            return true;
        }

        targets = Array.Empty<Instruction>();
        return false;
    }

    private static bool TryCopyInstructionTargets(Instruction[] instructions, out Instruction[] targets,
        out OperandTargetResolveError error)
    {
        targets = new Instruction[instructions.Length];
        error = default;

        for (var i = 0; i < instructions.Length; i++)
        {
            var target = instructions[i];
            if (target is null)
            {
                error = InvalidTargetOperand(typeof(void),
                    $"Instruction target array contains a null target. Array index: {i}.");
                targets = Array.Empty<Instruction>();
                return false;
            }

            targets[i] = target;
        }

        return true;
    }

    private static bool TryResolveILLabelTarget(object label, Type labelType, out Instruction? target,
        out OperandTargetResolveError error)
    {
        target = null;
        error = default;
        BuildMonoModResolveStrategy(labelType);
        if (_iLLabelGetTarget is null)
        {
            error = InvalidTargetOperand(labelType,
                $"ILLabel operand type has no Target field: {labelType.FullName}.");
            return false;
        }

        target = _iLLabelGetTarget(label);
        if (target is null)
        {
            error = InvalidTargetOperand(typeof(void),
                $"ILLabel target is null: {labelType.FullName}.");
            return false;
        }

        return true;
    }


    internal static bool IsMonoModILLabel(Type type)
        => type.FullName == MonoModILLabelFullName;

    private static OperandTargetResolveError InvalidTargetOperand(Type current, string message)
        => new(typeof(Instruction), current,
            $"{message} Expected {typeof(Instruction).FullName} or {MonoModILLabelFullName}.");

    private static OperandTargetResolveError InvalidTargetArrayOperand(Type current, string message)
        => new(typeof(Instruction[]), current,
            $"{message} Expected {typeof(Instruction[]).FullName} or {MonoModILLabelFullName}[].");
}

public static partial class CecilHelper
{

    private static void BuildMonoModResolveStrategy(Type type)
    {
        lock (ILLabelResolverLock)
        {
            if (_iLLabelGetTarget is not null &&
                _branchLabelsToTarget is not null &&
                _branchTargetsToLabels is not null &&
                _getContext is not null)
                return;

            var labelType = IsMonoModILLabel(type) ? type : GetILLabelTypeFromContext(type);

            var bindingFlags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;
            var context = labelType.GetField("Context", bindingFlags);

            if (context is null)
                throw new MissingFieldException(labelType.FullName, "Context");

            _getContext ??= label => context.GetValue(label)!;

            using var asmDef = CreateHelperAssembly(labelType);
            using var stream = new MemoryStream();
            asmDef.Write(stream);
            var asm = System.Reflection.Assembly.Load(stream.ToArray());

            var helperType = asm.GetType("MonoWeaver.Utils.MonomodHelper", throwOnError: true)!;
            _iLLabelGetTarget = (ILLabelTargetHandler)helperType.GetMethod("GetTarget")!.CreateDelegate(typeof(ILLabelTargetHandler));
            _branchLabelsToTarget = (LabelTargetSwitcher)helperType.GetMethod("BranchLabelsToTarget")!.CreateDelegate(typeof(LabelTargetSwitcher));
            _branchTargetsToLabels = (LabelTargetSwitcher)helperType.GetMethod("BranchTargetsToLabels")!.CreateDelegate(typeof(LabelTargetSwitcher));
        }
    }

    private static Type GetILLabelTypeFromContext(Type contextType)
    {
        var bindingFlags = System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;
        var defineLabel = contextType
            .GetMethods(bindingFlags)
            .FirstOrDefault(method =>
                method.Name == "DefineLabel" &&
                method.GetParameters().Length <= 1 &&
                IsMonoModILLabel(method.ReturnType));

        if (defineLabel is null)
            throw new MissingMethodException(contextType.FullName, "DefineLabel");

        return defineLabel.ReturnType;
    }


    public static AssemblyDefinition CreateHelperAssembly(Type labelType)
    {
        var asm = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Generated.ILBranchLabelHelpers", new Version(1, 0, 0, 0)),
                "Generated.ILBranchLabelHelpers",
                ModuleKind.Dll);

        AddHelperTypeAndMethods(asm.MainModule, labelType);
        return asm;
    }

    public static TypeDefinition AddHelperTypeAndMethods(
        ModuleDefinition module,
        Type labelType)
    {
        var bindingFlags = System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance;
        var contextType = labelType.GetField("Context", bindingFlags)?.FieldType
            ?? throw new MissingFieldException(labelType.FullName, "Context");

        var ilLabelType = module.ImportReference(labelType);
        var ilContextType = module.ImportReference(contextType);

        var instructionType = module.ImportReference(typeof(Instruction));

        var helperType = new TypeDefinition(
            "MonoWeaver.Utils",
            "MonomodHelper",
            TypeAttributes.Public |
            TypeAttributes.Abstract |
            TypeAttributes.Sealed |
            TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        module.Types.Add(helperType);

        EmitGetTarget(
            module,
            helperType,
            labelType,
            ilLabelType,
            instructionType);

        EmitBranchTargetsToLabels(
            module,
            helperType,
            contextType,
            labelType,
            ilContextType,
            ilLabelType,
            instructionType);

        EmitLabelsToBranchTargets(
            module,
            helperType,
            contextType,
            labelType,
            ilContextType,
            ilLabelType,
            instructionType);

        return helperType;
    }

    private static void EmitGetTarget(
            ModuleDefinition module,
            TypeDefinition helperType,
            Type labelType,
            TypeReference ilLabelType,
            TypeReference instructionType)
    {
        var method = new MethodDefinition(
            "GetTarget",
            MethodAttributes.Public | MethodAttributes.Static,
            instructionType);

        method.Parameters.Add(new ParameterDefinition("label", ParameterAttributes.None, module.TypeSystem.Object));
        helperType.Methods.Add(method);
        var body = method.Body;
        body.InitLocals = true;

        var il = body.GetILProcessor();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, ilLabelType);
        EmitLoadLabelTarget(il, module, labelType);
        il.Emit(OpCodes.Ret);
    }
    private static void EmitBranchTargetsToLabels(
        ModuleDefinition module,
        TypeDefinition helperType,
        Type contextType,
        Type labelType,
        TypeReference ilContextType,
        TypeReference ilLabelType,
        TypeReference instructionType)
    {
        var method = new MethodDefinition(
            "BranchTargetsToLabels",
            MethodAttributes.Public | MethodAttributes.Static,
            module.TypeSystem.Void);

        method.Parameters.Add(new ParameterDefinition("il", ParameterAttributes.None, module.TypeSystem.Object));
        helperType.Methods.Add(method);

        var body = method.Body;
        body.InitLocals = true;

        var il = body.GetILProcessor();

        var objectType = module.TypeSystem.Object;
        var intType = module.TypeSystem.Int32;

        var instrArrayType = new ArrayType(instructionType);
        var labelArrayType = new ArrayType(ilLabelType);

        var getInstrs = contextType.GetProperty("Instrs").GetGetMethod();
        var getInstrsRef = module.ImportReference(getInstrs);
        var instrsType = module.ImportReference(getInstrs.ReturnType);

    

        var getOperand = module.ImportReference(
            typeof(Instruction).GetProperty(nameof(Instruction.Operand))!.GetGetMethod()!);

        var setOperand = module.ImportReference(
            typeof(Instruction).GetProperty(nameof(Instruction.Operand))!.GetSetMethod()!);

        var defineLabel = module.ImportReference(contextType.GetMethod("DefineLabel", new[] { typeof(Instruction) }));

        var getIncomingLabels = module.ImportReference(contextType.GetMethod("GetIncomingLabels", new[] { typeof(Instruction) }));

        var firstOrDefault = MakeFirstOrDefault(module, ilLabelType);

        // locals
        var vInstrs = AddLocal(body, instrsType);          // 0
        var vI = AddLocal(body, intType);                  // 1
        var vInstr = AddLocal(body, instructionType);      // 2
        var vOperand = AddLocal(body, objectType);         // 3
        var vTarget = AddLocal(body, instructionType);     // 4
        var vTargets = AddLocal(body, instrArrayType);     // 5
        var vJ = AddLocal(body, intType);                  // 6
        var vLabels = AddLocal(body, labelArrayType);      // 7
        var vLabel = AddLocal(body, ilLabelType);          // 8
        var vT = AddLocal(body, instructionType);          // 9

        var loopCheck = il.Create(OpCodes.Nop);
        var loopBody = il.Create(OpCodes.Nop);
        var nextInstr = il.Create(OpCodes.Nop);
        var end = il.Create(OpCodes.Ret);

        var notInstruction = il.Create(OpCodes.Nop);
        var notInstructionArray = il.Create(OpCodes.Nop);

        var haveLabelSingle = il.Create(OpCodes.Nop);

        var arrayLoopCheck = il.Create(OpCodes.Nop);
        var arrayLoopBody = il.Create(OpCodes.Nop);
        var arrayHaveLabel = il.Create(OpCodes.Nop);
        var arrayDone = il.Create(OpCodes.Nop);

        // var instrs = il.Instrs;
        EmitLoadContext(il, ilContextType);
        il.Emit(OpCodes.Callvirt, getInstrsRef);
        il.Emit(OpCodes.Stloc, vInstrs);

        // i = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, vI);
        il.Emit(OpCodes.Br, loopCheck);

        // loop body
        il.Append(loopBody);

        // instr = instrs[i]
        il.Emit(OpCodes.Ldloc, vInstrs);
        il.Emit(OpCodes.Ldloc, vI);
        il.Emit(OpCodes.Callvirt, module.ImportReference(getInstrs.ReturnType.GetMethod("get_Item")));
        il.Emit(OpCodes.Stloc, vInstr);

        // operand = instr.Operand
        il.Emit(OpCodes.Ldloc, vInstr);
        il.Emit(OpCodes.Callvirt, getOperand);
        il.Emit(OpCodes.Stloc, vOperand);

        // if operand is Instruction target
        il.Emit(OpCodes.Ldloc, vOperand);
        il.Emit(OpCodes.Isinst, instructionType);
        il.Emit(OpCodes.Stloc, vTarget);

        il.Emit(OpCodes.Ldloc, vTarget);
        il.Emit(OpCodes.Brfalse, notInstruction);

        // label = il.GetIncomingLabels(target).FirstOrDefault()
        EmitLoadContext(il, ilContextType);
        il.Emit(OpCodes.Ldloc, vTarget);
        il.Emit(OpCodes.Callvirt, getIncomingLabels);
        il.Emit(OpCodes.Call, firstOrDefault);
        il.Emit(OpCodes.Stloc, vLabel);

        // if label != null goto haveLabelSingle
        il.Emit(OpCodes.Ldloc, vLabel);
        il.Emit(OpCodes.Brtrue, haveLabelSingle);

        // label = il.DefineLabel(target)
        EmitLoadContext(il, ilContextType);
        il.Emit(OpCodes.Ldloc, vTarget);
        il.Emit(OpCodes.Callvirt, defineLabel);
        il.Emit(OpCodes.Stloc, vLabel);

        il.Append(haveLabelSingle);

        // instr.Operand = label
        il.Emit(OpCodes.Ldloc, vInstr);
        il.Emit(OpCodes.Ldloc, vLabel);
        il.Emit(OpCodes.Callvirt, setOperand);
        il.Emit(OpCodes.Br, nextInstr);

        il.Append(notInstruction);

        // if operand is Instruction[] targets
        il.Emit(OpCodes.Ldloc, vOperand);
        il.Emit(OpCodes.Isinst, instrArrayType);
        il.Emit(OpCodes.Stloc, vTargets);

        il.Emit(OpCodes.Ldloc, vTargets);
        il.Emit(OpCodes.Brfalse, notInstructionArray);

        // labels = new ILLabel[targets.Length]
        il.Emit(OpCodes.Ldloc, vTargets);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Newarr, ilLabelType);
        il.Emit(OpCodes.Stloc, vLabels);

        // j = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, vJ);
        il.Emit(OpCodes.Br, arrayLoopCheck);

        il.Append(arrayLoopBody);

        // t = targets[j]
        il.Emit(OpCodes.Ldloc, vTargets);
        il.Emit(OpCodes.Ldloc, vJ);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, vT);

        // label = il.GetIncomingLabels(t).FirstOrDefault()
        EmitLoadContext(il, ilContextType);
        il.Emit(OpCodes.Ldloc, vT);
        il.Emit(OpCodes.Callvirt, getIncomingLabels);
        il.Emit(OpCodes.Call, firstOrDefault);
        il.Emit(OpCodes.Stloc, vLabel);

        // if label != null goto arrayHaveLabel
        il.Emit(OpCodes.Ldloc, vLabel);
        il.Emit(OpCodes.Brtrue, arrayHaveLabel);

        // label = il.DefineLabel(t)
        EmitLoadContext(il, ilContextType);
        il.Emit(OpCodes.Ldloc, vT);
        il.Emit(OpCodes.Callvirt, defineLabel);
        il.Emit(OpCodes.Stloc, vLabel);

        il.Append(arrayHaveLabel);

        // labels[j] = label
        il.Emit(OpCodes.Ldloc, vLabels);
        il.Emit(OpCodes.Ldloc, vJ);
        il.Emit(OpCodes.Ldloc, vLabel);
        il.Emit(OpCodes.Stelem_Ref);

        // j++
        il.Emit(OpCodes.Ldloc, vJ);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, vJ);

        il.Append(arrayLoopCheck);

        // j < targets.Length
        il.Emit(OpCodes.Ldloc, vJ);
        il.Emit(OpCodes.Ldloc, vTargets);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Blt, arrayLoopBody);

        il.Append(arrayDone);

        // instr.Operand = labels
        il.Emit(OpCodes.Ldloc, vInstr);
        il.Emit(OpCodes.Ldloc, vLabels);
        il.Emit(OpCodes.Callvirt, setOperand);
        il.Emit(OpCodes.Br, nextInstr);

        il.Append(notInstructionArray);

        il.Append(nextInstr);

        // i++
        il.Emit(OpCodes.Ldloc, vI);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, vI);

        il.Append(loopCheck);

        // i < instrs.Count
        il.Emit(OpCodes.Ldloc, vI);
        il.Emit(OpCodes.Ldloc, vInstrs);
        il.Emit(OpCodes.Callvirt, module.ImportReference(getInstrs.ReturnType.GetMethod("get_Count")));
        il.Emit(OpCodes.Blt, loopBody);

        il.Append(end);
    }

    private static void EmitLabelsToBranchTargets(
        ModuleDefinition module,
        TypeDefinition helperType,
        Type contextType,
        Type labelType,
        TypeReference ilContextType,
        TypeReference ilLabelType,
        TypeReference instructionType)
    {
        var method = new MethodDefinition(
            "BranchLabelsToTarget",
            MethodAttributes.Public | MethodAttributes.Static,
            module.TypeSystem.Void);

        method.Parameters.Add(new ParameterDefinition("il", ParameterAttributes.None, module.TypeSystem.Object));
        helperType.Methods.Add(method);

        var body = method.Body;
        body.InitLocals = true;

        var il = body.GetILProcessor();

        var objectType = module.TypeSystem.Object;
        var intType = module.TypeSystem.Int32;

        var labelArrayType = new ArrayType(ilLabelType);
        var instrArrayType = new ArrayType(instructionType);

        var getInstrs = contextType.GetProperty("Instrs").GetGetMethod();
        var getInstrsRef = module.ImportReference(getInstrs);
        var instrsType = module.ImportReference(getInstrs.ReturnType);

        var instrsGetCount = module.ImportReference(getInstrs.ReturnType.GetMethod("get_Count"));
        var instrsGetItem = module.ImportReference(getInstrs.ReturnType.GetMethod("get_Item"));

        var getOperand = module.ImportReference(
            typeof(Instruction).GetProperty(nameof(Instruction.Operand))!.GetGetMethod()!);

        var setOperand = module.ImportReference(
            typeof(Instruction).GetProperty(nameof(Instruction.Operand))!.GetSetMethod()!);

        var invalidOpCtor = module.ImportReference(
            typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!);

        // locals
        var vInstrs = AddLocal(body, instrsType);       // 0
        var vI = AddLocal(body, intType);               // 1
        var vInstr = AddLocal(body, instructionType);   // 2
        var vOperand = AddLocal(body, objectType);      // 3
        var vLabel = AddLocal(body, ilLabelType);       // 4
        var vTarget = AddLocal(body, instructionType);  // 5
        var vLabels = AddLocal(body, labelArrayType);   // 6
        var vTargets = AddLocal(body, instrArrayType);  // 7
        var vJ = AddLocal(body, intType);               // 8

        var loopCheck = il.Create(OpCodes.Nop);
        var loopBody = il.Create(OpCodes.Nop);
        var nextInstr = il.Create(OpCodes.Nop);
        var end = il.Create(OpCodes.Ret);

        var notLabel = il.Create(OpCodes.Nop);
        var notLabelArray = il.Create(OpCodes.Nop);

        var singleTargetOk = il.Create(OpCodes.Nop);

        var arrayLoopCheck = il.Create(OpCodes.Nop);
        var arrayLoopBody = il.Create(OpCodes.Nop);
        var arrayTargetOk = il.Create(OpCodes.Nop);

        // var instrs = il.Instrs;
        EmitLoadContext(il, ilContextType);
        il.Emit(OpCodes.Callvirt, getInstrsRef);
        il.Emit(OpCodes.Stloc, vInstrs);

        // i = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, vI);
        il.Emit(OpCodes.Br, loopCheck);

        il.Append(loopBody);

        // instr = instrs[i]
        il.Emit(OpCodes.Ldloc, vInstrs);
        il.Emit(OpCodes.Ldloc, vI);
        il.Emit(OpCodes.Callvirt, instrsGetItem);
        il.Emit(OpCodes.Stloc, vInstr);

        // operand = instr.Operand
        il.Emit(OpCodes.Ldloc, vInstr);
        il.Emit(OpCodes.Callvirt, getOperand);
        il.Emit(OpCodes.Stloc, vOperand);

        // if operand is ILLabel label
        il.Emit(OpCodes.Ldloc, vOperand);
        il.Emit(OpCodes.Isinst, ilLabelType);
        il.Emit(OpCodes.Stloc, vLabel);

        il.Emit(OpCodes.Ldloc, vLabel);
        il.Emit(OpCodes.Brfalse, notLabel);

        // target = label.Target
        il.Emit(OpCodes.Ldloc, vLabel);
        EmitLoadLabelTarget(il, module, labelType);
        il.Emit(OpCodes.Stloc, vTarget);

        // if target != null ok
        il.Emit(OpCodes.Ldloc, vTarget);
        il.Emit(OpCodes.Brtrue, singleTargetOk);

        // throw new InvalidOperationException("Unmarked ILLabel")
        il.Emit(OpCodes.Ldstr, "Unmarked ILLabel");
        il.Emit(OpCodes.Newobj, invalidOpCtor);
        il.Emit(OpCodes.Throw);

        il.Append(singleTargetOk);

        // instr.Operand = target
        il.Emit(OpCodes.Ldloc, vInstr);
        il.Emit(OpCodes.Ldloc, vTarget);
        il.Emit(OpCodes.Callvirt, setOperand);
        il.Emit(OpCodes.Br, nextInstr);

        il.Append(notLabel);

        // if operand is ILLabel[] labels
        il.Emit(OpCodes.Ldloc, vOperand);
        il.Emit(OpCodes.Isinst, labelArrayType);
        il.Emit(OpCodes.Stloc, vLabels);

        il.Emit(OpCodes.Ldloc, vLabels);
        il.Emit(OpCodes.Brfalse, notLabelArray);

        // targets = new Instruction[labels.Length]
        il.Emit(OpCodes.Ldloc, vLabels);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Newarr, instructionType);
        il.Emit(OpCodes.Stloc, vTargets);

        // j = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, vJ);
        il.Emit(OpCodes.Br, arrayLoopCheck);

        il.Append(arrayLoopBody);

        // label = labels[j]
        il.Emit(OpCodes.Ldloc, vLabels);
        il.Emit(OpCodes.Ldloc, vJ);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, vLabel);

        // target = label.Target
        il.Emit(OpCodes.Ldloc, vLabel);
        EmitLoadLabelTarget(il, module, labelType);
        il.Emit(OpCodes.Stloc, vTarget);

        // if target != null ok
        il.Emit(OpCodes.Ldloc, vTarget);
        il.Emit(OpCodes.Brtrue, arrayTargetOk);

        // throw new InvalidOperationException("Unmarked ILLabel")
        il.Emit(OpCodes.Ldstr, "Unmarked ILLabel");
        il.Emit(OpCodes.Newobj, invalidOpCtor);
        il.Emit(OpCodes.Throw);

        il.Append(arrayTargetOk);

        // targets[j] = target
        il.Emit(OpCodes.Ldloc, vTargets);
        il.Emit(OpCodes.Ldloc, vJ);
        il.Emit(OpCodes.Ldloc, vTarget);
        il.Emit(OpCodes.Stelem_Ref);

        // j++
        il.Emit(OpCodes.Ldloc, vJ);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, vJ);

        il.Append(arrayLoopCheck);

        // j < labels.Length
        il.Emit(OpCodes.Ldloc, vJ);
        il.Emit(OpCodes.Ldloc, vLabels);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Blt, arrayLoopBody);

        // instr.Operand = targets
        il.Emit(OpCodes.Ldloc, vInstr);
        il.Emit(OpCodes.Ldloc, vTargets);
        il.Emit(OpCodes.Callvirt, setOperand);
        il.Emit(OpCodes.Br, nextInstr);

        il.Append(notLabelArray);

        il.Append(nextInstr);

        // i++
        il.Emit(OpCodes.Ldloc, vI);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, vI);

        il.Append(loopCheck);

        // i < instrs.Count
        il.Emit(OpCodes.Ldloc, vI);
        il.Emit(OpCodes.Ldloc, vInstrs);
        il.Emit(OpCodes.Callvirt, instrsGetCount);
        il.Emit(OpCodes.Blt, loopBody);

        il.Append(end);
    }

    private static VariableDefinition AddLocal(MethodBody body, TypeReference type)
    {
        var v = new VariableDefinition(type);
        body.Variables.Add(v);
        return v;
    }

    private static void EmitLoadLabelTarget(ILProcessor il, ModuleDefinition module, Type labelType)
    {
        var bindingFlags = System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;
        var targetProperty = labelType.GetProperty("Target", bindingFlags);
        var targetGetter = targetProperty?.GetGetMethod(nonPublic: true);
        if (targetGetter is not null)
        {
            il.Emit(OpCodes.Callvirt, module.ImportReference(targetGetter));
            return;
        }

        var targetField = labelType.GetField("Target", bindingFlags);
        if (targetField is not null)
        {
            il.Emit(OpCodes.Ldfld, module.ImportReference(targetField));
            return;
        }

        throw new MissingFieldException(labelType.FullName, "Target");
    }

    private static void EmitLoadContext(ILProcessor il, TypeReference ilContextType)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, ilContextType);
    }


    private static MethodReference MakeFirstOrDefault(
        ModuleDefinition module,
        TypeReference itemType)
    {
        var open = typeof(Enumerable)
            .GetMethods()
            .Single(m =>
                m.Name == nameof(Enumerable.FirstOrDefault) &&
                m.GetParameters().Length == 1);

        var importedOpen = module.ImportReference(open);

        var generic = new GenericInstanceMethod(importedOpen);
        generic.GenericArguments.Add(itemType);
        return generic;
    }
}
