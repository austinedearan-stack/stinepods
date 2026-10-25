using Microsoft.Win32;

namespace StinePods.Windows;

/// <summary>"Start with Windows" via the current user's Run key (no admin rights needed).</summary>
internal static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "StinePods";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) is string;
    }

    /// <summary>Points the entry at the running exe, so moving the app folder keeps start-up working.</summary>
    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(Name, throwOnMissingValue: false);
    }
}
