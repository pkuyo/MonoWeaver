namespace MonoWeaver.Test;

public class CilPlayground
{
    public static int CheckedAdd(int a, int b) => checked(a + b);

    public static int TryCatchFinally(bool cond)
    {
        int sum = 0;
        try
        {
            if (cond) sum += 1;
            else throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException)
        {
            sum += 10;
        }
        catch (Exception)
        {
            sum += 100;
        }
        finally
        {
            sum += 1000;
        }
        return sum;
    }

    public static int CatchWhenFilter(int x)
    {
        try
        {
            if (x < 0) throw new ArgumentOutOfRangeException(nameof(x));
            return x + 1;
        }
        catch (Exception ex) when (x == 0 && ex is ArgumentOutOfRangeException)
        {
            return 42;
        }
    }

    public static void UsingPattern()
    {
        using var m = new MemoryStream();
        m.WriteByte(1);
    }

    public static void VolatileWrite(ref int x, int v)
    {
        Volatile.Write(ref x, v);
    }

    public static void ConstrainedDispose<T>(T value) where T : struct, IDisposable
    {
        value.Dispose();
    }
}