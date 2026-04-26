namespace AutoAcceptDeals;

internal static class ModState
{
    public static bool Enabled { get; private set; } = true;
    public static bool InGameScene { get; internal set; }

    public static bool ShouldRun => Enabled && InGameScene;

    internal static void Toggle()
    {
        Enabled = !Enabled;
    }
}
