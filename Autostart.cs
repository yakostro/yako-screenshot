using Microsoft.Win32;

namespace Yako.Screenshot;

/// <summary>
/// Registers the app to start when the user signs in, via the per-user HKCU Run key - no
/// admin rights, no scheduled task, no installer. A tray app that exists to answer a hotkey
/// is useless while it is not running, so this happens on first launch without asking.
/// </summary>
internal static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "yako-screenshot";

    public static void Ensure(Settings settings)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        string wanted = $"\"{exe}\"";     // quoted, in case the path contains spaces

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;

            if (key.GetValue(ValueName) is not string existing)
            {
                // Create it once only. If the entry is missing on a later run the user took
                // it out deliberately - putting it back behind their back would be rude.
                if (settings.AutostartRegistered) return;

                key.SetValue(ValueName, wanted, RegistryValueKind.String);
                settings.AutostartRegistered = true;
                settings.Save();
                return;
            }

            // Keep the path right if the exe moved. Rewriting the value does not re-enable an
            // entry disabled in Task Manager - that lives separately under StartupApproved -
            // so this cannot override that choice either.
            if (!string.Equals(existing, wanted, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, wanted, RegistryValueKind.String);
        }
        catch
        {
            // Autostart is a convenience. Never let it stop the app from starting.
        }
    }

    /// <summary>Takes the app out of Windows startup. Safe to call when it was never there.</summary>
    public static void Remove()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Nothing to do: the entry is either gone or not ours to touch.
        }
    }

    /// <summary>The registered command, or null when the app is not set to start with Windows.</summary>
    public static string? RegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }
}
