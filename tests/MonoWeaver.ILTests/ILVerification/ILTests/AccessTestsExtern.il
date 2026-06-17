// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

.assembly extern System.Runtime
{
}

.assembly extern AccessTests
{
}

.assembly extern AccessTestsFriend
{
}

.assembly AccessTestsExtern
{
}

.class public auto ansi beforefieldinit AccessTestsType
        extends [System.Runtime]System.Object
{
    .method public hidebysig instance void Instantiate.PublicClass_Full_Valid() cil managed
    {
        newobj      instance void [AccessTests]SimpleClass::.ctor()
        pop
        ret
    }

    .method public hidebysig instance void Instantiate.PrivateClass_Full_Invalid_MethodAccess() cil managed
    {
        newobj      instance void [AccessTests]PrivateClass::.ctor()
        pop
        ret
    }

    .method public hidebysig instance void Instantiate.PublicNested_Full_Valid() cil managed
    {
        newobj      instance void [AccessTests]SimpleClass/PublicNestedClass::.ctor()
        pop
        ret
    }

    .method public hidebysig instance void Instantiate.PrivateNested_Full_Invalid_MethodAccess() cil managed
    {
        newobj      instance void [AccessTests]SimpleClass/PrivateNestedClass::.ctor()
        pop
        ret
    }

    .method public hidebysig instance void Instantiate.AssemblyNested_Full_Invalid_MethodAccess() cil managed
    {
        newobj      instance void [AccessTests]SimpleClass/AssemblyNestedClass::.ctor()
        pop
        ret
    }

    .method public hidebysig instance void Instantiate.FamOrAssemNested_Full_Invalid_MethodAccess() cil managed
    {
        newobj      instance void [AccessTests]SimpleClass/FamilyOrAssemblyNestedClass::.ctor()
        pop
        ret
    }

    .method public hidebysig instance void Instantiate.FamAndAssemNested_Full_Invalid_MethodAccess() cil managed
    {
        newobj      instance void [AccessTests]SimpleClass/FamilyAndAssemblyNestedClass::.ctor()
        pop
        ret
    }

    .method public hidebysig instance void Instantiate.FamilyNested_Full_Invalid_MethodAccess() cil managed
    {
        newobj      instance void [AccessTests]SimpleClass/FamilyNestedClass::.ctor()
        pop
        ret
    }

    .method public hidebysig instance void Call.PrivateMethod_Full_Invalid_MethodAccess() cil managed
    {
        call      void [AccessTests]SimpleClass::PrivateMethod()
        ret
    }

    .method public hidebysig instance void Call.FamilyMethod_Full_Invalid_MethodAccess() cil managed
    {
        call      void [AccessTests]SimpleClass::FamilyMethod()
        ret
    }

    .method public hidebysig instance void Call.AssemblyMethod_Full_Invalid_MethodAccess() cil managed
    {
        call      void [AccessTests]SimpleClass::AssemblyMethod()
        ret
    }

    .method public hidebysig instance void Call.FamOrAssemMethod_Full_Invalid_MethodAccess() cil managed
    {
        call      void [AccessTests]SimpleClass::FamilyOrAssemblyMethod()
        ret
    }

    .method public hidebysig instance void Call.FamAndAssemMethod_Full_Invalid_MethodAccess() cil managed
    {
        call      void [AccessTests]SimpleClass::FamilyAndAssemblyMethod()
        ret
    }

    .method private hidebysig instance void Load.AssemFieldOfFriendAssemblyWithSpace_Full_Valid() cil managed
    {
        ldsfld       int32 [AccessTestsFriend]AccessTestsFriendType::assemblyField
        pop
        ret
    }
}

.class public auto ansi beforefieldinit DerivedType
        extends [AccessTests]SimpleClass
{
    .method public hidebysig instance void Call.DerivedFamMethod_Full_Valid() cil managed
    {
        call      void [AccessTests]SimpleClass::FamilyMethod()
        ret
    }

    .method public hidebysig instance void Call.DerivedFamOrAssemMethod_Full_Valid() cil managed
    {
        call      void [AccessTests]SimpleClass::FamilyOrAssemblyMethod()
        ret
    }

    .method public hidebysig instance void Call.DerivedFamAndAssemMethod_Full_Invalid_MethodAccess() cil managed
    {
        call      void [AccessTests]SimpleClass::FamilyAndAssemblyMethod()
        ret
    }
}

.class public auto ansi beforefieldinit DerivedClass
        extends [AccessTests]SimpleClass
{
    .method public hidebysig instance void Instantiate.FamAndAssemNestedClassFromDerived_Full_Invalid_MethodAccess() cil managed
    {
        newobj      instance void [AccessTests]SimpleClass/FamilyAndAssemblyNestedClass::.ctor()
        pop
        ret
    }

    .method public hidebysig instance void Instantiate.FamOrAssemNestedClassFromDerived_Full_Valid() cil managed
    {
        newobj      instance void [AccessTests]SimpleClass/FamilyOrAssemblyNestedClass::.ctor()
        pop
        ret
    }

    .method public hidebysig instance void Instantiate.FamNestedClassFromDerived_Full_Valid() cil managed
    {
        newobj      instance void [AccessTests]SimpleClass/FamilyNestedClass::.ctor()
        pop
        ret
    }
}
