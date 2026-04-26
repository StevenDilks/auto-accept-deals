namespace AutoAcceptDeals;

internal static class ModState
{
    public static bool Enabled { get; private set; } = true;
    public static bool InGameScene { get; private set; }

    public static bool ShouldRun => Enabled && InGameScene;

    internal static void Toggle()
    {
        Enabled = !Enabled;
    }

    internal static bool EnterScene()
    {
        if (InGameScene) return false;
        InGameScene = true;
        return true;
    }

    internal static bool LeaveScene()
    {
        if (!InGameScene) return false;
        InGameScene = false;
        return true;
    }
}
