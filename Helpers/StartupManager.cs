using Microsoft.Win32;

namespace DynamicIsland.Helpers;

/// <summary>
/// "Start with Windows" through the per-user Run key (HKCU\Software\Microsoft\Windows\CurrentVersion\Run):
/// no admin rights, no scheduled task, and it shows up in Task Manager's Startup apps page.
/// </summary>
internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DynamicIsland";

    private static string? Command => Environment.ProcessPath is { } exe ? $"\"{exe}\"" : null;

    /// <summary>True only if the Run entry points at this exact executable (a moved exe reads as "off").</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is string value
                    && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
        set
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (value && Command is { } command)
                    key.SetValue(ValueName, command);
                else
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                Log.Error("Could not change the startup setting", ex);
            }
        }
    }
}
