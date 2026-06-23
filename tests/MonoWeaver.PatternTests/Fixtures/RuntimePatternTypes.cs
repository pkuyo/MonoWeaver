using System;

namespace MonoWeaver.PatternTests;

public sealed class A
{
    public B B() => throw new NotSupportedException();
}

public class B
{
    public C C() => throw new NotSupportedException();
    public C D() => throw new NotSupportedException();
    public int Select(int value) => throw new NotSupportedException();
    public int Select(string value) => throw new NotSupportedException();
    public bool CallB() => throw new NotSupportedException();
}

public sealed class C
{
}

public sealed class MemberHost
{
    public static int StaticField;
    public int InstanceField;

    public MemberHost(int value)
    {
        InstanceField = value;
    }

    public int Property => InstanceField;

    public int Add(int value) => InstanceField + value;

    public static int StaticAdd(int left, int right) => left + right;
}

public interface ICompute
{
    int Compute(int value);
}

public sealed class InstancePatternTarget
{
    public InstancePatternTarget IdentityThis() => this;
}

public sealed class Ops
{
    public static B IdentityB(B value) => value;
    public static bool IdentityBool(bool value) => value;
    public static int IdentityInt(int value) => value;
    public static T Identity<T>(T value) => value;
    public static void ObserveB(B value) { }
    public static void ObserveInt(int value) { }
    public static int FortyTwo() => 42;
    public static bool CallA() => throw new NotSupportedException();
    public static bool CallC() => throw new NotSupportedException();
    public static bool CallD() => throw new NotSupportedException();
    public static bool XXX() => throw new NotSupportedException();
    public static int Add(int left, int right) => left + right;
    public static void ConsumeInt(int value) { }
    public static void ConsumeNothing() { }
    public static void Mutate(ref int value) => value++;
    public static object? AcceptObject(object? value) => value;
}
