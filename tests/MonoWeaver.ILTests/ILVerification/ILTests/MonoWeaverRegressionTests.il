// MonoWeaver 回归语料：覆盖 jmp 校验、calli 函数指针栈项、AccessTest 门控修复。

.assembly extern System.Runtime
{
}

.assembly MonoWeaverRegressionTests
{
}

.class public auto ansi beforefieldinit C
        extends [System.Runtime]System.Object
{
    .method private hidebysig static void JmpVoidTarget() cil managed
    {
        ret
    }

    .method public hidebysig static void JmpIntTarget(int32 x) cil managed
    {
        ret
    }

    .method public hidebysig static void JmpStringTarget(string s) cil managed
    {
        ret
    }

    // jmp：签名一致、栈为空，合法
    .method public hidebysig static void Jmp.MatchingSignature_Valid(int32 x) cil managed
    {
        jmp void C::JmpIntTarget(int32)
    }

    // jmp：参数类型不一致
    .method public hidebysig static void Jmp.ParameterMismatch_Invalid_TypeMismatch(int32 x) cil managed
    {
        jmp void C::JmpStringTarget(string)
    }

    // jmp：返回类型不一致
    .method public hidebysig static int32 Jmp.ReturnMismatch_Invalid_TypeMismatch(int32 x) cil managed
    {
        jmp void C::JmpIntTarget(int32)
    }

    // jmp：instance 方法跳转到 static 目标，调用约定不一致
    .method public hidebysig instance void Jmp.ThisMismatch_Invalid_TypeMismatch() cil managed
    {
        jmp void C::JmpVoidTarget()
    }

    // jmp：求值栈必须为空
    .method public hidebysig static void Jmp.NonEmptyStack_Invalid_InvalidExitStackHeight() cil managed
    {
        ldc.i4.0
        jmp void C::JmpVoidTarget()
    }

    // jmp：不允许出现在 try 区域内
    .method public hidebysig static void Jmp.InsideTry_Invalid_InvalidInstruction() cil managed
    {
        .try
        {
            jmp void C::JmpVoidTarget()
            leave.s DONE
        }
        catch [System.Runtime]System.Object
        {
            pop
            leave.s DONE
        }
        DONE: ret
    }

    // calli：空栈时必须报函数指针下溢（修复前不弹函数指针，检测不到）
    .method public hidebysig static void Calli.EmptyStack_Invalid_StackUnderflow() cil managed
    {
        calli void()
        ret
    }

    // calli：栈顶不是函数指针/native int
    .method public hidebysig static void Calli.NonPointerOperand_Full_Invalid_TypeMismatch() cil managed
    {
        ldc.i4.0
        ldc.i4.1
        calli void(int32)
        ret
    }

    // calli：ldftn 产生的函数指针直接调用，合法
    .method public hidebysig static void Calli.LdftnArgument_Valid() cil managed
    {
        ldc.i4.s 42
        ldftn void C::JmpIntTarget(int32)
        calli void(int32)
        ret
    }

    // AccessTest 门控修复：.locals init 存在时（普通 C# 方法的常态）访问检查也必须生效
    .method public hidebysig static void Access.InitLocalsPrivateCall_Full_Invalid_MethodAccess() cil managed
    {
        .locals init ([0] int32 tmp)
        ldc.i4.0
        stloc.0
        call void D::PrivateHelper()
        ret
    }

    .method public hidebysig static void Access.InitLocalsPublicCall_Valid() cil managed
    {
        .locals init ([0] int32 tmp)
        ldc.i4.0
        stloc.0
        call void D::PublicHelper()
        ret
    }

    .method public hidebysig specialname rtspecialname instance void .ctor() cil managed
    {
        ldarg.0
        call instance void [System.Runtime]System.Object::.ctor()
        ret
    }
}

.class public auto ansi beforefieldinit D
        extends [System.Runtime]System.Object
{
    .method private hidebysig static void PrivateHelper() cil managed
    {
        ret
    }

    .method public hidebysig static void PublicHelper() cil managed
    {
        ret
    }
}
