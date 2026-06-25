using MonoWeaver.PatternTests;

namespace MonoWeaver.PatternTestFixtures;

public static partial class Target
{
    public static C Chain(A value)
        => value.B().C();

    public static C Temporary(A value)
    {
        var temp = value.B();
        return temp.C();
    }

    public static bool TemporaryValueUsedAfterStore(A value)
    {
        var temp = value.B();
        return temp != null;
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

    public static int StaticCall(int left, int right)
        => Ops.Add(left, right);

    public static void VoidCall(int value)
        => Ops.ConsumeInt(value);

    public static void Discarded(A value)
    {
        _ = value.B();
    }

    public static MemberHost NewMemberHost(int value)
        => new(value);

    public static int InstanceCall(MemberHost value, int amount)
        => value.Add(amount);

    public static int ReadInstanceField(MemberHost value)
        => value.InstanceField;

    public static int ReadStaticField()
        => MemberHost.StaticField;

    public static int ReadProperty(MemberHost value)
        => value.Property;

    public static int GenericCall(int value)
        => Ops.Identity(value);

    public static int InterfaceCall(ICompute value)
        => value.Compute(7);

    public static object? NullArgument()
        => Ops.AcceptObject(null);
}

public sealed class DirectCaller : B
{
    public C CallBase()
        => base.C();
}
