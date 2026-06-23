namespace Game;

public sealed class Player
{
    public int GetState()
        => 1;
}

public static class Host
{
    public static int ReadState(Player player)
        => player.GetState();
}

public static class LocalCallbacks
{
    public static int LocalTransform(int value)
        => value;
}
