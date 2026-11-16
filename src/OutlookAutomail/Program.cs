using System.Security.Principal;
using OutlookAutomail.UI;
using Velopack.Locators;
using Velopack.Windows;

namespace OutlookAutomail;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Velopack.VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnAfterInstallFastCallback(_ => CreateUninstallShortcut(VelopackLocator.Current))
            .OnAfterUpdateFastCallback(_ => CreateUninstallShortcut(VelopackLocator.Current))
            .OnBeforeUninstallFastCallback(_ =>
            {
                StartupRegistration.SetEnabled(false);
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        if (Directory.Exists(SettingsStore.RootDirectory))
                            Directory.Delete(SettingsStore.RootDirectory, true);
                        if (VelopackLocator.Current.AppId is { } appId)
                        {
                            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "velopack", $"velopack_{appId}.log");
                            File.Delete(log + ".old");
                            File.Delete(log);
                        }
                        break;
                    }
                    catch (IOException exception) when (attempt < 2 && (exception.HResult & 0xffff) is 32 or 33)
                    {
                        Thread.Sleep(500);
                    }
                }
            })
            .Run();
        ApplicationConfiguration.Initialize();
        var instanceName = $"Local\\OutlookAutomail_{WindowsIdentity.GetCurrent().User!.Value}";
        using var mutex = new Mutex(true, instanceName, out var firstInstance);
        if (!firstInstance)
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(instanceName + "_Open");
                existing.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                MessageBox.Show("Outlook Automail is starting. Open it from the system tray.", "Outlook Automail", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        using var openEvent = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + "_Open");
        try
        {
            var settings = SettingsStore.Load();
            using var context = new TrayApplicationContext(settings, openEvent, !args.Contains("--tray", StringComparer.OrdinalIgnoreCase));
            Application.Run(context);
        }
        catch (Exception exception)
        {
            AppLog.Write("Application could not start.", exception);
            MessageBox.Show($"Outlook Automail could not start.\n\n{exception.Message}", "Outlook Automail", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    internal static void CreateUninstallShortcut(IVelopackLocator locator)
    {
        using var shortcut = new ShellLink
        {
            Target = locator.UpdateExePath!,
            Arguments = "uninstall",
            WorkingDirectory = locator.RootAppDir!,
            IconPath = Path.Combine(locator.AppContentDir!, "OutlookAutomail.exe"),
            Description = "Uninstall Outlook Automail"
        };
        shortcut.Save(Path.Combine(locator.RootAppDir!, "Uninstall Outlook Automail.lnk"));
    }
}
