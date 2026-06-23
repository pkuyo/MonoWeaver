namespace Hooks;

public static class Callbacks
{
    public static int Transform(bool value)
        => value ? 1 : 0;
}
