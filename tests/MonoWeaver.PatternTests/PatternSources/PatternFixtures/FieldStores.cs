using MonoWeaver.PatternTests;

namespace MonoWeaver.PatternTestFixtures;

public static partial class Target
{
    public static void WriteInstanceField(MemberHost host, int amount)
        => host.InstanceField = amount;

    public static void WriteStaticField(int amount)
        => MemberHost.StaticField = amount;

    public static void WriteComputedInstanceField(MemberHost host, int amount)
        => host.InstanceField = amount * 2;

    public static int WriteFieldThenRead(MemberHost host, int amount)
    {
        host.InstanceField = amount;
        return host.InstanceField;
    }

    public static int WriteAndReturnStaticField(int amount)
        => MemberHost.StaticField = amount;

    public static int WriteAndReturnInstanceField(MemberHost host, int amount)
        => host.InstanceField = amount;
}
