using Microsoft.Win32;

namespace Denon.App.Desktop.Services;

public sealed class WindowsStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DenonDesktop";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var existing = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(existing);
        }
        catch
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled, bool? startMinimized = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    var existing = key.GetValue(ValueName) as string ?? string.Empty;
                    var keepMinimized = startMinimized ?? existing.Contains("--minimized", StringComparison.OrdinalIgnoreCase);
                    var value = keepMinimized ? $"\"{exePath}\" --minimized" : $"\"{exePath}\"";
                    key.SetValue(ValueName, value);
                }
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Ignore registry permission errors in restricted environments
        }
    }
}
