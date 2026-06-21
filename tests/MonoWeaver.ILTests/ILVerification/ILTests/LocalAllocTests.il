// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

.assembly extern System.Runtime
{
}

.assembly extern LocalAllocTestsFriend
{
}

.assembly LocalAllocTests
{
}

.class public auto ansi beforefieldinit LocAllocTests
        extends [System.Runtime]System.Object
{
    .method private hidebysig instance void Load.LocAllocShouldFailWhenStackNotEmpty_Invalid_Unverifiable_LocallocStackNotEmpty() cil managed
    {
        ldnull // pretend there's meaningfull value on stack
        ldc.i4.2 // size parameter for localloc
        localloc
        pop // pop localloc pointer
        pop // pop ldnull
        ret
    }

    // TODO:实际多个错误，未来可能修改
    .method private hidebysig instance void Load.LocAllocShouldFailWhenSizeIsAbsent_Invalid_StackUnderflow() cil managed
    {
        localloc
        pop // pop localloc pointer
        ret
    }

    .method private hidebysig instance void Load.LocAllocShouldSuccesWhenSizeIsPresent_Warning_Unverifiable() cil managed
    {
        ldc.i4.2 // size parameter for localloc
        localloc
        pop // pop localloc pointer
        ret
    }
}
