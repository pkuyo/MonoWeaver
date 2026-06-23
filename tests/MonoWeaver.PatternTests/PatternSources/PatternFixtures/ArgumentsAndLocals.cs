using MonoWeaver.PatternTests;

namespace MonoWeaver.PatternTestFixtures;

public static partial class Target
{
    public static int Argument0(int value, int other)
        => value;

    public static string Argument1(int value, string text)
        => text;

    public static string AssignableArgument(string value)
        => value;

    public static int LocalRead(int value)
    {
        var local = value + 1;
        return local;
    }

    public static int TwoLocals(int first, int second)
    {
        var firstLocal = first + 1;
        var secondLocal = second + 2;
        return firstLocal + secondLocal;
    }

    public static int TransparentLocal(int value)
    {
        var temporary = value + 1;
        return temporary * 2;
    }

    public static bool LocalCondition()
    {
        var ret = Ops.XXX();
        return ret ? true : false;
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

    public static int AddressTakenLocal(int value)
    {
        var local = value;
        Ops.Mutate(ref local);
        return local + 1;
    }
}
