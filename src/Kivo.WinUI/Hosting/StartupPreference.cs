using Microsoft.Win32;

namespace Kivo.Desktop;

internal static class StartupPreference
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string Command => $"\"{Environment.ProcessPath}\" --background";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue("Kivo") as string, Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue("Kivo", Command);
        else if (string.Equals(key.GetValue("Kivo") as string, Command, StringComparison.OrdinalIgnoreCase))
            key.DeleteValue("Kivo", throwOnMissingValue: false);
    }
}
