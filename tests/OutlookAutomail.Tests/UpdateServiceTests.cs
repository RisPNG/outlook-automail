using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using Xunit;

namespace OutlookAutomail.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task UninstalledBuildNeverContactsAnUpdateFeedOrLaunchesTheUpdater()
    {
        using var fixture = new UpdateFixture();
        fixture.Manager.Installed = false;
        var service = new UpdateService(fixture.Manager);

        await service.CheckAsync(default);
        await service.RunAsync(default);

        Assert.Equal(0, fixture.Manager.CheckCount);
        Assert.Equal(0, fixture.Manager.DownloadCount);
        Assert.False(service.Status.CanInstall);
        Assert.Throws<InvalidOperationException>(service.ScheduleRestart);
        Assert.Empty(fixture.Process.Starts);
    }

    [Fact]
    public async Task DownloadedUpdateWaitsForGracefulExitAndRestartsInTheTray()
    {
        using var fixture = new UpdateFixture();
        fixture.Manager.Available = new UpdateInfo(fixture.Asset, false);
        var service = new UpdateService(fixture.Manager);

        await service.CheckAsync(default);

        Assert.True(service.Status.CanInstall);
        Assert.False(service.Status.IsChecking);
        Assert.Empty(fixture.Process.Starts);
        service.ScheduleRestart();
        var start = Assert.Single(fixture.Process.Starts);
        Assert.Equal(Path.Combine(fixture.Root, "Update.exe"), start.Executable);
        Assert.Contains("apply", start.Arguments);
        Assert.Contains("--waitPid", start.Arguments);
        Assert.Contains("1234", start.Arguments);
        Assert.Contains("--silent", start.Arguments);
        Assert.Equal(["--", "--tray"], start.Arguments.TakeLast(2));
        Assert.False(start.ShowWindow);
        Assert.False(fixture.Process.ExitRequested);
    }

    [Fact]
    public async Task UpToDateApplicationDoesNotDownloadOrScheduleAnything()
    {
        using var fixture = new UpdateFixture();
        var service = new UpdateService(fixture.Manager);

        await service.CheckAsync(default);

        Assert.Equal(1, fixture.Manager.CheckCount);
        Assert.Equal(0, fixture.Manager.DownloadCount);
        Assert.False(service.Status.CanInstall);
        Assert.False(service.Status.IsChecking);
        Assert.Empty(fixture.Process.Starts);
    }

    [Fact]
    public async Task OfflineFeedPreservesAnAlreadyDownloadedUpdate()
    {
        using var fixture = new UpdateFixture();
        fixture.Manager.Downloaded = fixture.Asset;
        File.WriteAllBytes(Path.Combine(fixture.Root, "packages", fixture.Asset.FileName), [1, 2, 3]);
        fixture.Manager.CheckError = new HttpRequestException("The update server is offline.");
        var service = new UpdateService(fixture.Manager);

        await service.CheckAsync(default);

        Assert.True(service.Status.CanInstall);
        Assert.False(service.Status.IsChecking);
        Assert.Equal(0, fixture.Manager.DownloadCount);
        Assert.Empty(fixture.Process.Starts);
    }

    [Fact]
    public async Task ReadyUpdateIsInstalledBeforeAttemptingAnotherDownloadThatCouldRemoveItsPackage()
    {
        using var fixture = new UpdateFixture();
        fixture.Manager.Available = new UpdateInfo(fixture.Asset, false);
        var service = new UpdateService(fixture.Manager);
        await service.CheckAsync(default);
        fixture.Manager.Available = new UpdateInfo(new VelopackAsset
        {
            PackageId = fixture.Asset.PackageId,
            Version = SemanticVersion.Parse("1.3.3"),
            FileName = "OutlookAutomail-1.3.3-full.nupkg",
            Type = VelopackAssetType.Full
        }, false);
        fixture.Manager.DownloadError = new IOException("A later download would fail and clean the package cache.");

        await service.CheckAsync(default);
        service.ScheduleRestart();

        Assert.Equal(1, fixture.Manager.CheckCount);
        Assert.Equal(1, fixture.Manager.DownloadCount);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "packages", fixture.Asset.FileName)));
        Assert.Contains(Path.Combine(fixture.Root, "packages", fixture.Asset.FileName), Assert.Single(fixture.Process.Starts).Arguments, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemovedPackageCannotCauseAnExitWithoutInstallingAnUpdate()
    {
        using var fixture = new UpdateFixture();
        fixture.Manager.Available = new UpdateInfo(fixture.Asset, false);
        var service = new UpdateService(fixture.Manager);
        await service.CheckAsync(default);
        File.Delete(Path.Combine(fixture.Root, "packages", fixture.Asset.FileName));

        Assert.Throws<InvalidOperationException>(service.ScheduleRestart);
        Assert.False(service.Status.CanInstall);
        Assert.Empty(fixture.Process.Starts);
    }

    [Fact]
    public async Task FailedDownloadCannotBeInstalledAndACheckCanRetryLater()
    {
        using var fixture = new UpdateFixture();
        fixture.Manager.Available = new UpdateInfo(fixture.Asset, false);
        fixture.Manager.DownloadError = new IOException("The download was interrupted.");
        var service = new UpdateService(fixture.Manager);

        await service.CheckAsync(default);
        Assert.False(service.Status.CanInstall);
        Assert.False(service.Status.IsChecking);
        Assert.Throws<InvalidOperationException>(service.ScheduleRestart);
        fixture.Manager.DownloadError = null;
        await service.CheckAsync(default);

        Assert.True(service.Status.CanInstall);
        Assert.Equal(2, fixture.Manager.CheckCount);
        Assert.Equal(2, fixture.Manager.DownloadCount);
        Assert.Empty(fixture.Process.Starts);
    }

    [Fact]
    public async Task ManualAndAutomaticChecksShareOneInFlightDownload()
    {
        using var fixture = new UpdateFixture();
        fixture.Manager.PendingCheck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new UpdateService(fixture.Manager);
        var first = service.CheckAsync(default);
        await service.CheckAsync(default);
        Assert.Equal(1, fixture.Manager.CheckCount);
        fixture.Manager.PendingCheck.SetResult(new UpdateInfo(fixture.Asset, false));
        await first;

        Assert.Equal(1, fixture.Manager.DownloadCount);
        Assert.True(service.Status.CanInstall);
    }

    [Fact]
    public async Task CommittedRestartCannotBeChangedByAnotherCheckOrInstalledTwice()
    {
        using var fixture = new UpdateFixture();
        fixture.Manager.Available = new UpdateInfo(fixture.Asset, false);
        var service = new UpdateService(fixture.Manager);
        await service.CheckAsync(default);
        service.ScheduleRestart();

        await service.CheckAsync(default);

        Assert.Equal(1, fixture.Manager.CheckCount);
        Assert.Equal(1, fixture.Manager.DownloadCount);
        Assert.Single(fixture.Process.Starts);
        Assert.False(service.Status.CanInstall);
        Assert.Throws<InvalidOperationException>(service.ScheduleRestart);
    }
}

internal sealed class UpdateFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "OutlookAutomail-update-tests", Guid.NewGuid().ToString("N"));
    public RecordingUpdateProcess Process { get; } = new();
    public FakeUpdateManager Manager { get; }
    public VelopackAsset Asset { get; } = new()
    {
        PackageId = "OutlookAutomail",
        Version = SemanticVersion.Parse("1.3.2"),
        FileName = "OutlookAutomail-1.3.2-full.nupkg",
        Type = VelopackAssetType.Full
    };

    public UpdateFixture()
    {
        Directory.CreateDirectory(Path.Combine(Root, "packages"));
        File.WriteAllBytes(Path.Combine(Root, "Update.exe"), []);
        var locator = new RecordingUpdateLocator(Root, Process);
        Manager = new FakeUpdateManager(Root, locator);
    }

    public void Dispose()
    {
        var path = Path.GetFullPath(Root);
        var parent = Path.Combine(Path.GetTempPath(), "OutlookAutomail-update-tests") + Path.DirectorySeparatorChar;
        if (!path.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The update test cleanup path is invalid.");
        Directory.Delete(path, true);
    }
}

internal sealed class FakeUpdateManager(string root, IVelopackLocator locator)
    : UpdateManager(new SimpleFileSource(new DirectoryInfo(root)), locator: locator)
{
    public bool Installed { get; set; } = true;
    public UpdateInfo? Available { get; set; }
    public VelopackAsset? Downloaded { get; set; }
    public Exception? CheckError { get; set; }
    public Exception? DownloadError { get; set; }
    public TaskCompletionSource<UpdateInfo?>? PendingCheck { get; set; }
    public int CheckCount { get; private set; }
    public int DownloadCount { get; private set; }
    public override bool IsInstalled => Installed;
    public override VelopackAsset? UpdatePendingRestart => Downloaded is not null && File.Exists(Path.Combine(root, "packages", Downloaded.FileName)) ? Downloaded : null;

    public override Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        CheckCount++;
        return CheckError is not null ? Task.FromException<UpdateInfo?>(CheckError)
            : PendingCheck?.Task ?? Task.FromResult(Available);
    }

    public override Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress = null, CancellationToken cancelToken = default)
    {
        DownloadCount++;
        if (DownloadError is not null)
            return Task.FromException(DownloadError);
        File.WriteAllBytes(Path.Combine(root, "packages", updates.TargetFullRelease.FileName), [1, 2, 3]);
        Downloaded = updates.TargetFullRelease;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingUpdateLocator(string root, IProcessImpl process)
    : TestVelopackLocator("OutlookAutomail", "1.3.1", Path.Combine(root, "packages"), Path.Combine(root, "current"), root,
        Path.Combine(root, "Update.exe"), "win")
{
    public override IProcessImpl Process => process;
}

internal sealed class RecordingUpdateProcess : IProcessImpl
{
    public List<(string Executable, string[] Arguments, string WorkingDirectory, bool ShowWindow)> Starts { get; } = [];
    public bool ExitRequested { get; private set; }
    public string GetCurrentProcessPath() => "OutlookAutomail.exe";
    public uint GetCurrentProcessId() => 1234;
    public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow)
        => Starts.Add((exePath, args.ToArray(), workDir, showWindow));
    public void Exit(int exitCode) => ExitRequested = true;
}
