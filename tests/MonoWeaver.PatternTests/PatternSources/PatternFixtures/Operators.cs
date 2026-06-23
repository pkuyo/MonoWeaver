namespace MonoWeaver.PatternTestFixtures;

public static partial class Target
{
    public static int IdentityInt(int value)
        => value;

    public static int Add(int left, int right)
        => left + right;

    public static int AddChecked(int left, int right)
        => checked(left + right);

    public static int Subtract(int left, int right)
        => left - right;

    public static int SubtractChecked(int left, int right)
        => checked(left - right);

    public static int Multiply(int left, int right)
        => left * right;

    public static int MultiplyChecked(int left, int right)
        => checked(left * right);

    public static int Divide(int left, int right)
        => left / right;

    public static uint DivideUnsigned(uint left, uint right)
        => left / right;

    public static int Modulo(int left, int right)
        => left % right;

    public static uint ModuloUnsigned(uint left, uint right)
        => left % right;

    public static int BitAnd(int left, int right)
        => left & right;

    public static int BitOr(int left, int right)
        => left | right;

    public static int Xor(int left, int right)
        => left ^ right;

    public static int ShiftLeft(int value, int count)
        => value << count;

    public static int ShiftRight(int value, int count)
        => value >> count;

    public static uint ShiftRightUnsigned(uint value, int count)
        => value >> count;

    public static uint AddCheckedUnsigned(uint left, uint right)
        => checked(left + right);

    public static int Negate(int value)
        => -value;

    public static int BitNot(int value)
        => ~value;

    public static bool NotValue(bool value)
        => !value;

    public static long ConvertToInt64(int value)
        => value;

    public static byte ConvertCheckedToByte(int value)
        => checked((byte)value);

    public static byte ConvertUncheckedToByte(int value)
        => unchecked((byte)value);

    public static string CastToString(object value)
        => (string)value;

    public static string? AsString(object value)
        => value as string;

    public static object BoxInt(int value)
        => value;

    public static int UnboxInt(object value)
        => (int)value;

    public static bool Equal(int left, int right)
        => left == right;

    public static bool NotEqual(int left, int right)
        => left != right;

    public static bool GreaterThan(int left, int right)
        => left > right;

    public static bool GreaterThanOrEqual(int left, int right)
        => left >= right;

    public static bool GreaterThanUnsigned(uint left, uint right)
        => left > right;

    public static bool LessThan(int left, int right)
        => left < right;

    public static bool LessThanOrEqual(int left, int right)
        => left <= right;

    public static bool LessThanUnsigned(uint left, uint right)
        => left < right;

    public static int IntConstant()
        => 123;

    public static long LongConstant()
        => 1234567890123L;

    public static float FloatConstant()
        => 1.25f;

    public static double DoubleConstant()
        => 2.5d;

    public static string StringConstant()
        => "mono-weaver";

    public static object? NullConstant()
        => null;

    public static double Constants()
    {
        MonoWeaver.PatternTests.Ops.ConsumeInt(1);
        return 1.0;
    }
}
