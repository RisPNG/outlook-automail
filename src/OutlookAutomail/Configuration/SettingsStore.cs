using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using System.Runtime.Versioning;
using Velopack.Locators;

namespace OutlookAutomail;

public static class SettingsStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string RootDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OutlookAutomailData");

    public static AppSettings Load()
    {
        lock (Gate)
        {
            var path = Path.Combine(RootDirectory, "settings.json");
            if (!File.Exists(path))
            {
                var settings = new AppSettings();
                Save(settings);
                return settings;
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("The local settings file is empty. Restore it or rename it before starting the app.");
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(RootDirectory);
            var path = Path.Combine(RootDirectory, "settings.json");
            var temporaryPath = path + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporaryPath, path, true);
        }
    }
}

[SupportedOSPlatform("windows")]
public static class StartupRegistration
{
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
        {
            var installed = VelopackLocator.IsCurrentSet && VelopackLocator.Current.CurrentlyInstalledVersion is not null;
            var executable = installed ? VelopackLocator.Current.UpdateExePath : Environment.ProcessPath;
            if (executable is null)
                throw new InvalidOperationException("The app executable path is unavailable.");
            key.SetValue("Outlook Automail", $"\"{executable}\"{(installed ? " start --" : "")} --tray", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("Outlook Automail", false);
        }
    }
}

public static class AppLog
{
    private static readonly object Gate = new();
    public static string Path { get; } = System.IO.Path.Combine(SettingsStore.RootDirectory, "logs", "application.log");

    public static void Write(string message, Exception? exception = null)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > 5 * 1024 * 1024)
                {
                    File.Move(Path, Path + ".previous", true);
                }
                var detail = exception is null ? "" : $" [{exception.GetType().Name}] {exception.Message}";
                File.AppendAllText(Path, $"{DateTimeOffset.UtcNow:O} {message}{detail}{Environment.NewLine}");
            }
            catch (Exception loggingError) when (loggingError is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Application log unavailable: {loggingError.GetType().Name}");
            }
        }
    }
}
