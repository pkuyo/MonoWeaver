namespace MonoWeaver.PatternTestFixtures;

public static partial class Target
{
    public static int[] NewIntArray(int length)
        => new int[length];

    public static string[] NewStringArray(int length)
        => new string[length];

    public static int LoadIntElement(int[] values)
        => values[1];

    public static string LoadStringElement(string[] values)
        => values[1];

    public static int Length(int[] values)
        => values.Length;

    public static void StoreIntElement(int[] values, int value)
        => values[1] = value;

    public static void StoreStringElement(string[] values, string value)
        => values[1] = value;
}
