using Velopack;
using Velopack.Sources;
using System.Text.Json;

namespace SigPoAutomail;

public sealed class UpdateService
{
    public static string ReleaseRepository { get; }
    private readonly UpdateManager manager;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim checkGate = new(1, 1);
    private VelopackAsset? pending;
    private bool restarting;

    public UpdateStatus Status { get; private set; }
    public string InstalledVersion => manager.CurrentVersion?.ToString()
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "Development build";
    public event Action<UpdateStatus>? StatusChanged;

    static UpdateService()
    {
        using var stream = typeof(UpdateService).Assembly.GetManifestResourceStream("OutlookAutomail.release.json")
            ?? throw new InvalidDataException("The application release settings are missing.");
        using var document = JsonDocument.Parse(stream);
        ReleaseRepository = document.RootElement.GetProperty("repository").GetString()
            ?? throw new InvalidDataException("The application release repository is missing.");
    }

    public UpdateService() : this(new UpdateManager(new GithubSource(ReleaseRepository, null, false))) { }

    internal UpdateService(UpdateManager manager, TimeProvider? clock = null)
    {
        this.manager = manager;
        this.clock = clock ?? TimeProvider.System;
        Status = new(manager.IsInstalled ? "Updates are checked automatically." : "Install the Setup build to enable automatic updates.", false, false);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!manager.IsInstalled)
            return;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromHours(6), clock, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (!await checkGate.WaitAsync(0, cancellationToken))
            return;
        try
        {
            if (restarting)
                return;
            if (!manager.IsInstalled)
            {
                Status = new("Install the Setup build to enable automatic updates.", false, false);
                StatusChanged?.Invoke(Status);
                return;
            }
            Status = new("Checking for updates...", true, false);
            StatusChanged?.Invoke(Status);
            pending = manager.UpdatePendingRestart;
            var update = pending is null ? await manager.CheckForUpdatesAsync().WaitAsync(cancellationToken) : null;
            if (pending is null && update is not null)
            {
                Status = new($"Downloading version {update.TargetFullRelease.Version}...", true, false);
                StatusChanged?.Invoke(Status);
                await manager.DownloadUpdatesAsync(update, cancelToken: cancellationToken);
                pending = update.TargetFullRelease;
            }
            else
                pending = manager.UpdatePendingRestart;
            Status = new(pending is null ? "This version is up to date."
                : $"Version {pending.Version} is ready. Installs from the tray when idle.", false, pending is not null);
            StatusChanged?.Invoke(Status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            AppLog.Write("The update check could not be completed.", exception);
            pending = manager.UpdatePendingRestart;
            Status = new("Updates are unavailable. Check again later.", false, pending is not null);
            StatusChanged?.Invoke(Status);
        }
        finally { checkGate.Release(); }
    }

    public void ScheduleRestart()
    {
        if (!checkGate.Wait(0))
            throw new InvalidOperationException("The current update check must finish before installing.");
        try
        {
            pending = manager.UpdatePendingRestart;
            if (!Status.CanInstall || pending is null || restarting)
            {
                Status = Status with { CanInstall = false };
                StatusChanged?.Invoke(Status);
                throw new InvalidOperationException("No downloaded update is ready to install.");
            }
            manager.WaitExitThenApplyUpdates(pending, silent: true, restart: true, restartArgs: ["--tray"]);
            restarting = true;
            Status = new("Restarting to install the update...", false, false);
            StatusChanged?.Invoke(Status);
        }
        finally { checkGate.Release(); }
    }
}

public sealed record UpdateStatus(string Message, bool IsChecking, bool CanInstall);
