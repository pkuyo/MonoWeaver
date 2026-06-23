using MonoWeaver.PatternTests;

namespace MonoWeaver.PatternTestFixtures;

public static class Target
{
    public static C Chain(A value)
        => value.B().C();

    public static C Temporary(A value)
    {
        var temp = value.B();
        return temp.C();
    }

    public static bool Condition(B value)
    {
        if (Ops.CallA() && value.CallB() && (Ops.CallC() || Ops.CallD()))
            return true;
        return false;
    }

    public static bool LocalCondition()
    {
        var ret = Ops.XXX();
        return ret ? true : false;
    }

    public static B Ambiguous(A value)
    {
        _ = value.B();
        return value.B();
    }

    public static C Context(A value)
    {
        _ = value.B().C();
        return value.B().D();
    }

    public static int Overloads(B value)
    {
        _ = value.Select(1);
        return value.Select("selected");
    }

    public static string AssignableArgument(string value)
        => value;

    public static double Constants()
    {
        Ops.ConsumeInt(1);
        return 1.0;
    }

    public static bool MultipleDefinitions(bool condition)
    {
        bool ret;
        if (condition)
            ret = Ops.XXX();
        else
            ret = Ops.CallA();
        return ret;
    }

    public static void Discarded(A value)
    {
        _ = value.B();
    }

    public static int[] NewIntArray(int length)
        => new int[length];

    public static int LoadIntElement(int[] values)
        => values[1];

    public static int Length(int[] values)
        => values.Length;

    public static void StoreIntElement(int[] values, int value)
        => values[1] = value;

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

    public static int IdentityInt(int value)
        => value;

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

public sealed class DirectCaller : B
{
    public C CallBase()
        => base.C();
}
