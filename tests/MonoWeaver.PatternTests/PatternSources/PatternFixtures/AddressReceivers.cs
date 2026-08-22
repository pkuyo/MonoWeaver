using MonoWeaver.PatternTests;

namespace MonoWeaver.PatternTestFixtures;

public static partial class Target
{
    public static int StructArgCall(GamePoint point)
        => point.Sum();

    public static int StructArgCallWithArgument(GamePoint point, int factor)
        => point.Scaled(factor);

    public static int StructPropertyRead(GamePoint point)
        => point.First;

    public static bool NullableHasValue(int? value)
        => value.HasValue;

    public static int NullableValue(int? value)
        => value!.Value;

    public static int StructArrayElementCall(GamePoint[] points)
        => points[0].Sum();

    public static int RefArgumentCall(int value)
    {
        Ops.Mutate(ref value);
        return value;
    }
}
