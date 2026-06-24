using MonoWeaver.PatternTests;

namespace MonoWeaver.PatternTestFixtures;

public static partial class Target
{
    public static C ChainTransform(A value)
        => value.B().C();

    public static C Observe(A value)
        => value.B().C();

    public static int BeforeExpression(int value)
    {
        var temp = value;
        return temp;
    }

    public static bool ConditionTransform(B value)
    {
        if (Ops.CallA() && value.CallB() && (Ops.CallC() || Ops.CallD()))
            return true;
        return false;
    }

    public static bool ConditionObserve(B value)
    {
        if (value.CallB() && Ops.CallA())
            return true;
        return false;
    }

    public static int Select(bool condition, int value)
    {
        if (condition)
            return value;
        return 0;
    }

    public static void Touch()
    {
    }
}
