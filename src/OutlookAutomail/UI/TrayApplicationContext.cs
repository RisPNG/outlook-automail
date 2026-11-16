namespace OutlookAutomail.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly MainForm _window;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _trayMenu;
    private readonly RegisteredWaitHandle _openRegistration;
    private readonly ToolStripMenuItem _checkUpdates;
    private readonly ToolStripMenuItem _installUpdate;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _engineCancellation;
    private Task _engineTask = Task.CompletedTask;
    private Task _manualTask = Task.CompletedTask;
    private Task _updateTask = Task.CompletedTask;
    private AutomationEngine? _engine;
    private bool _exiting;
    private bool _installingUpdate;

    public AppSettings Settings { get; }
    public OutlookMailClient Mail { get; }
    public UpdateService Updates { get; }
    public CancellationToken CancellationToken => _lifetime.Token;

    public TrayApplicationContext(AppSettings settings, EventWaitHandle openEvent, bool showWindow)
    {
        Settings = settings;
        Mail = new OutlookMailClient(settings);
        Updates = new UpdateService();
        _window = new MainForm(this);
        _ = _window.Handle;
        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add("&Open Outlook Automail", null, (_, _) => ShowWindow());
        _checkUpdates = new ToolStripMenuItem("Check for &updates", null, async (_, _) => await Updates.CheckAsync(_lifetime.Token));
        _installUpdate = new ToolStripMenuItem("&Install downloaded update", null, async (_, _) => await ExitAsync(true)) { Visible = false };
        _trayMenu.Items.Add(_checkUpdates);
        _trayMenu.Items.Add(_installUpdate);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("E&xit", null, async (_, _) => await ExitAsync());
        _tray = new NotifyIcon
        {
            Text = "Outlook Automail",
            Icon = AppIcon.Default,
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
        Updates.StatusChanged += status =>
        {
            if (!_exiting && !_window.IsDisposed)
                _window.BeginInvoke((Action)(() =>
                {
                    if (_exiting || _window.IsDisposed)
                        return;
                    _window.UpdateApplicationUpdateStatus(status);
                    _checkUpdates.Enabled = !status.IsChecking;
                    _installUpdate.Visible = status.CanInstall;
                }));
        };
        _window.UpdateApplicationUpdateStatus(Updates.Status);
        _updateTimer = new System.Windows.Forms.Timer { Interval = 30000 };
        _updateTimer.Tick += async (_, _) =>
        {
            if (!_exiting && !_installingUpdate && !_window.Visible && Updates.Status.CanInstall)
                await ExitAsync(true);
        };
        _updateTimer.Start();
        _openRegistration = ThreadPool.RegisterWaitForSingleObject(openEvent, (_, _) =>
        {
            if (!_exiting && !_window.IsDisposed)
                _window.BeginInvoke((Action)ShowWindow);
        }, null, Timeout.Infinite, false);
        StartupRegistration.SetEnabled(settings.StartAtSignIn);
        StartEngine();
        _updateTask = Updates.RunAsync(_lifetime.Token);
        if (showWindow || string.IsNullOrEmpty(settings.AccountId) || settings.Folders.Count == 0)
            ShowWindow();
    }

    public async Task RestartEngineAsync()
    {
        await _lifecycleGate.WaitAsync(_lifetime.Token);
        try
        {
            await StopEngineAsync();
            if (!_exiting)
                StartEngine();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task ChooseOutlookAccountAsync(IWin32Window owner)
    {
        await _lifecycleGate.WaitAsync(_lifetime.Token);
        try
        {
            await StopEngineAsync();
            var accounts = await Mail.ConnectAsync(_lifetime.Token);
            using var dialog = new AccountForm(accounts, Settings.AccountId);
            using var cancelDialog = _lifetime.Token.Register(() =>
            {
                if (dialog.IsHandleCreated && !dialog.IsDisposed)
                    dialog.BeginInvoke((Action)(() =>
                    {
                        if (!dialog.IsDisposed)
                            dialog.Close();
                    }));
            });
            _lifetime.Token.ThrowIfCancellationRequested();
            if (dialog.ShowDialog(owner) == DialogResult.OK)
            {
                Settings.AccountId = dialog.SelectedAccountId;
                SettingsStore.Save(Settings);
                if (string.IsNullOrEmpty(Settings.AccountId))
                    await Mail.DisconnectAsync(_lifetime.Token);
            }
        }
        finally
        {
            if (!_exiting)
                StartEngine();
            _lifecycleGate.Release();
        }
    }

    public async Task RemoveFolderAsync(FolderSubscription subscription)
    {
        await _lifecycleGate.WaitAsync(_lifetime.Token);
        try
        {
            await StopEngineAsync();
            Settings.Folders.Remove(subscription);
            SettingsStore.Save(Settings);
        }
        finally
        {
            if (!_exiting)
                StartEngine();
            _lifecycleGate.Release();
        }
    }

    private async Task StopEngineAsync()
    {
        if (_engineCancellation is null)
            return;
        await _engineCancellation.CancelAsync();
        try
        {
            await Task.WhenAll(_engineTask, _manualTask);
        }
        catch (OperationCanceledException) when (_engineCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Write("A folder operation ended while automation was stopping.", exception);
        }
        _engine = null;
        _engineCancellation.Dispose();
        _engineCancellation = null;
        _manualTask = Task.CompletedTask;
    }

    private void StartEngine()
    {
        _engineCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _engine = new AutomationEngine(Settings, Mail);
        _engine.StatusChanged += status =>
        {
            if (!_exiting && !_window.IsDisposed)
                _window.BeginInvoke((Action)(() => _window.UpdateFolderStatus(status)));
        };
        var token = _engineCancellation.Token;
        _engineTask = Task.Run(async () =>
        {
            try
            {
                await _engine.RunAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                AppLog.Write("Background automation stopped.", exception);
                if (!_exiting && !_window.IsDisposed)
                    _window.BeginInvoke((Action)(() => _window.ShowBackgroundFailure(exception.Message)));
            }
        }, CancellationToken.None);
    }

    public async Task RunFolderAsync(string root, bool reconcile)
    {
        await _lifecycleGate.WaitAsync(_lifetime.Token);
        try
        {
            if (_exiting || _installingUpdate || _engine is null || _engineCancellation is null)
                throw new InvalidOperationException("Automation is not ready.");
            var engine = _engine;
            var token = _engineCancellation.Token;
            if (reconcile)
                _manualTask = Task.Run(() => engine.RetryArchiveOrReconcileAsync(root, token), CancellationToken.None);
            else
                _manualTask = Task.Run(() => engine.RunNowAsync(root, token), CancellationToken.None);
            await _manualTask;
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task ConfirmSentAsync(string root, string runId)
    {
        await _lifecycleGate.WaitAsync(_lifetime.Token);
        try
        {
            if (_exiting || _installingUpdate || _engine is null || _engineCancellation is null)
                throw new InvalidOperationException("Automation is not ready.");
            var engine = _engine;
            var token = _engineCancellation.Token;
            _manualTask = Task.Run(() => engine.ConfirmSentAsync(root, runId, token), CancellationToken.None);
            await _manualTask;
        }
        finally { _lifecycleGate.Release(); }
    }

    public void ShowWindow()
    {
        if (_exiting)
            return;
        _window.Show();
        if (_window.WindowState == FormWindowState.Minimized)
            _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    public async Task ExitAsync(bool installUpdate = false)
    {
        if (_exiting || _installingUpdate)
            return;
        if (installUpdate)
        {
            if (!Updates.Status.CanInstall)
                return;
            if (!_window.CanApplyUpdate || !await _lifecycleGate.WaitAsync(0, _lifetime.Token))
            {
                _window.UpdateApplicationUpdateStatus(Updates.Status with { Message = "The downloaded update is waiting for current work to finish." });
                return;
            }
            if (_engine?.TryPause() == false || Mail.IsBusy)
            {
                _engine?.Resume();
                _window.UpdateApplicationUpdateStatus(Updates.Status with { Message = "The downloaded update is waiting for current processing to finish." });
                _lifecycleGate.Release();
                return;
            }
            _installingUpdate = true;
            _window.Enabled = false;
        }
        else
        {
            _exiting = true;
            _tray.Visible = false;
            _window.Enabled = false;
            await _lifetime.CancelAsync();
            await _lifecycleGate.WaitAsync();
        }
        try
        {
            await StopEngineAsync();
            if (installUpdate)
            {
                if (Mail.IsBusy)
                {
                    _window.UpdateApplicationUpdateStatus(Updates.Status with { Message = "The downloaded update is waiting for Outlook to finish its current operation." });
                    StartEngine();
                    return;
                }
                Updates.ScheduleRestart();
                _exiting = true;
                _tray.Visible = false;
                await _lifetime.CancelAsync();
            }
            await _updateTask;
            _updateTimer.Stop();
            _window.AllowClose = true;
            _window.Close();
            ExitThread();
        }
        catch (Exception exception) when (installUpdate && !_exiting)
        {
            AppLog.Write("The downloaded update could not be started.", exception);
            _window.UpdateApplicationUpdateStatus(Updates.Status with { Message = "The update could not be started. Try installing it again." });
            if (_engine is null)
                StartEngine();
        }
        finally
        {
            if (!_exiting)
            {
                _installingUpdate = false;
                _window.Enabled = true;
            }
            _lifecycleGate.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _openRegistration.Unregister(null);
            _updateTimer.Dispose();
            _tray.Dispose();
            _trayMenu.Dispose();
            _window.Dispose();
            Mail.Dispose();
            _engineCancellation?.Dispose();
            _lifetime.Dispose();
            _lifecycleGate.Dispose();
        }
        base.Dispose(disposing);
    }
}
