using System.Security.Principal;
using SigPoAutomail.UI;

namespace SigPoAutomail;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Velopack.VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => StartupRegistration.SetEnabled(false))
            .Run();
        ApplicationConfiguration.Initialize();
        var instanceName = $"Local\\SIG_PO_Automail_{WindowsIdentity.GetCurrent().User!.Value}";
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
}
