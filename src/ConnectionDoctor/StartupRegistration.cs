using System.Diagnostics;
using Microsoft.Win32;

namespace ConnectionDoctor;

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ConnectionDoctor";

    public static string Install()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the ConnectionDoctor executable path.");
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Publish ConnectionDoctor and run install from ConnectionDoctor.exe, not through dotnet run.");
        }

        var command = $"\"{executable}\" collect";
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, command, RegistryValueKind.String);

        var status = BackgroundCollector.ReadStatus();
        if (!status.IsRunning)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "collect",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        return $"Installed per-user startup registration: {command}";
    }

    public static string Uninstall()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        return "Removed ConnectionDoctor per-user startup registration.";
    }
}
