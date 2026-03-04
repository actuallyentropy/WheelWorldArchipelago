namespace WheelWorldArchipelago.Utils;

public static class ArchipelagoConsole
{
    public static void Awake()
    {
        new ArchipelagoWindow().Show();
    }

    public static void LogMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Plugin.BepinLogger.LogMessage(message);
        ArchipelagoWindow.Instance?.LogMessage(message);
    }

    /// <summary>No-op: GUI is handled by the separate OS window.</summary>
    public static void OnGUI() { }
}
