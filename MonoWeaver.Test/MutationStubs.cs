namespace MonoWeaver.Test;

public static class MutationStubs
{
    public static void M_InvalidOpCode() { }
    public static void M_EhRegionOverlap() { }
    public static void M_EhRegionNonTryDuplication() { }
    public static void M_EhNestedInFilter() { }
    public static void M_TryAndHandlerNotInSameEnclosingRegion() { }
    public static void M_InvalidEhTableOrdering() { }

    public static void M_InvalidInstruction() { }
    public static void M_TypeMismatch() { }
    public static void M_InconsistentFieldAccess() { }
    public static void M_StackUnderflow() { }
    public static void M_StackOverflow() { }
    public static void M_InvalidFallThrough() { }
    public static void M_UninitializedLocal() { }
    public static void M_IncompatibleMergeTypes(bool cond) { }
    public static void M_IncompatibleMergeDepth(bool cond) { }
    public static void M_InvalidBrTarget() { }
    public static void M_BrTargetCrossEhRegion() { }
    public static void M_ArguementOutOfRange() { }
}


public class FieldOwner
{
    public int InstanceField;
    public static int StaticField;
}