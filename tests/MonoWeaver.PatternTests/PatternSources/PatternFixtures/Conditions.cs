using MonoWeaver.PatternTests;

namespace MonoWeaver.PatternTestFixtures;

public static partial class Target
{
    public static bool BoolCondition(bool value)
    {
        if (value)
            return true;
        return false;
    }

    public static bool NotCondition(bool value)
    {
        if (!value)
            return true;
        return false;
    }

    public static bool AndCondition(bool left, bool right)
    {
        if (left && right)
            return true;
        return false;
    }

    public static bool OrCondition(bool left, bool right)
    {
        if (left || right)
            return true;
        return false;
    }

    public static bool ShortCircuitAndCondition()
    {
        if (Ops.CallA() && Ops.CallC())
            return true;
        return false;
    }

    public static bool ShortCircuitOrCondition()
    {
        if (Ops.CallA() || Ops.CallC())
            return true;
        return false;
    }

    public static bool Condition(B value)
    {
        if (Ops.CallA() && value.CallB() && (Ops.CallC() || Ops.CallD()))
            return true;
        return false;
    }

    public static bool EqualCondition(int left, int right)
    {
        if (left == right)
            return true;
        return false;
    }

    public static bool NotEqualCondition(int left, int right)
    {
        if (left != right)
            return true;
        return false;
    }

    public static bool GreaterCondition(int left, int right)
    {
        if (left > right)
            return true;
        return false;
    }

    public static bool GreaterOrEqualCondition(int left, int right)
    {
        if (left >= right)
            return true;
        return false;
    }

    public static bool LessCondition(int left, int right)
    {
        if (left < right)
            return true;
        return false;
    }

    public static bool LessOrEqualCondition(int left, int right)
    {
        if (left <= right)
            return true;
        return false;
    }
}
