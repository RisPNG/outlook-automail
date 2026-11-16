using System.Diagnostics;
using System.ComponentModel;

namespace OutlookAutomail.UI;

internal sealed partial class MainForm : Form
{
    private readonly TrayApplicationContext _application;
    private readonly Dictionary<string, FolderStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy;
    private bool _refreshingHistory;
    private bool _refreshingAccount;
    private bool _initialized;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AllowClose { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CanApplyUpdate => !_busy && !_refreshingAccount && !_refreshingHistory && OwnedForms.Length == 0;

    public MainForm(TrayApplicationContext application)
    {
        _application = application;
        InitializeComponent();
        Text = $"Outlook Automail - {application.Updates.InstalledVersion}";
        _startAtSignIn.Checked = application.Settings.StartAtSignIn;
        Shown += async (_, _) =>
        {
            if (_initialized)
                return;
            _initialized = true;
            await RefreshAccountAsync();
            await ReloadFoldersAsync();
        };
        FormClosing += (_, args) =>
        {
            if (!AllowClose && args.CloseReason == CloseReason.UserClosing)
            {
                args.Cancel = true;
                Hide();
            }
        };
        _accountSetup.Click += async (_, _) =>
        {
            SetBusy(true, "Connecting to classic Outlook. Check Outlook for a profile or security prompt if this takes longer than expected...");
            try
            {
                await _application.ChooseOutlookAccountAsync(this);
                await RefreshAccountAsync();
            }
            catch (OperationCanceledException)
            {
                _status.Text = "The Outlook connection was canceled.";
            }
            catch (Exception exception)
            {
                _account.Text = "Classic Outlook is unavailable. Open classic Outlook and check its profile or security prompts, then connect again.";
                ReportFailure("Could not connect to classic Outlook.", exception);
            }
            finally
            {
                SetBusy(false);
            }
        };
        _startAtSignIn.CheckedChanged += (_, _) =>
        {
            try
            {
                StartupRegistration.SetEnabled(_startAtSignIn.Checked);
                _application.Settings.StartAtSignIn = _startAtSignIn.Checked;
                SettingsStore.Save(_application.Settings);
            }
            catch (Exception exception)
            {
                ReportFailure("Startup preference could not be saved.", exception);
            }
        };
        _add.Click += async (_, _) => await AddFolderAsync();
        _edit.Click += async (_, _) => await EditFolderAsync();
        _pause.Click += async (_, _) =>
        {
            if (_folders.SelectedRows.Count == 0 || _folders.SelectedRows[0].Tag is not FolderSubscription subscription)
                return;
            SetBusy(true, "Updating folder monitoring...");
            try
            {
                subscription.Enabled = !subscription.Enabled;
                SettingsStore.Save(_application.Settings);
                await _application.RestartEngineAsync();
                await ReloadFoldersAsync(subscription.RootPath);
            }
            catch (Exception exception)
            {
                ReportFailure("Folder monitoring could not be updated.", exception);
            }
            finally
            {
                SetBusy(false);
            }
        };
        _remove.Click += async (_, _) =>
        {
            if (_folders.SelectedRows.Count == 0 || _folders.SelectedRows[0].Tag is not FolderSubscription subscription)
                return;
            if (MessageBox.Show(this, $"Remove this folder from this computer?\n\n{subscription.RootPath}\n\nThe shared configuration, files, and history will remain available to other users.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            SetBusy(true, "Removing local folder selection...");
            try
            {
                await _application.RemoveFolderAsync(subscription);
                await ReloadFoldersAsync();
            }
            catch (Exception exception)
            {
                ReportFailure("The local folder selection could not be removed.", exception);
            }
            finally
            {
                SetBusy(false);
            }
        };
        _runNow.Click += async (_, _) => await ProcessSelectedFolderAsync(false);
        _reconcile.Click += async (_, _) => await ProcessSelectedFolderAsync(true);
        _openFolder.Click += (_, _) =>
        {
            if (_folders.SelectedRows.Count == 0 || _folders.SelectedRows[0].Tag is not FolderSubscription subscription)
                return;
            try
            {
                Process.Start(new ProcessStartInfo(subscription.RootPath) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                ReportFailure("The folder could not be opened.", exception);
            }
        };
        _openLogs.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.RootDirectory);
                Process.Start(new ProcessStartInfo(SettingsStore.RootDirectory) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                ReportFailure("The local log folder could not be opened.", exception);
            }
        };
        _folders.SelectionChanged += async (_, _) =>
        {
            UpdateSelection();
            await RefreshHistoryAsync();
        };
        _history.SelectionChanged += (_, _) => UpdateSelection();
        _refresh.Click += async (_, _) =>
        {
            await ReloadFoldersAsync(_folders.SelectedRows.Count == 0 ? null : (_folders.SelectedRows[0].Tag as FolderSubscription)?.RootPath);
            await RefreshAccountAsync();
        };
        _confirmSent.Click += async (_, _) =>
        {
            if (_folders.SelectedRows.Count == 0 || _folders.SelectedRows[0].Tag is not FolderSubscription subscription || _history.SelectedRows.Count == 0 || _history.SelectedRows[0].Tag is not RunRecord run)
                return;
            if (MessageBox.Show(this, $"Only continue after verifying this exact email in Outlook Sent Items or with the recipients. An email waiting in Outbox has not been confirmed sent.\n\nRun: {run.Id}\nAccount: {run.AccountAddress}\n\nConfirm that this email was already sent? This records your confirmation and archives its attachments without sending another email.", "Confirm an email was sent", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            SetBusy(true, "Recording confirmation and archiving...");
            try
            {
                await _application.ConfirmSentAsync(subscription.RootPath, run.Id);
                await RefreshHistoryAsync();
            }
            catch (Exception exception)
            {
                ReportFailure("The confirmation could not be completed.", exception);
            }
            finally
            {
                SetBusy(false);
            }
        };
        _exit.Click += async (_, _) => await _application.ExitAsync();
        _checkUpdates.Click += async (_, _) => await _application.Updates.CheckAsync(_application.CancellationToken);
        _installUpdate.Click += async (_, _) => await _application.ExitAsync(true);
        _historyTimer.Tick += async (_, _) =>
        {
            if (Visible && !_busy)
            {
                await RefreshHistoryAsync();
                await RefreshAccountAsync();
            }
        };
        _historyTimer.Start();
        UpdateSelection();
    }

    private async Task RefreshAccountAsync()
    {
        if (_refreshingAccount || IsDisposed)
            return;
        _refreshingAccount = true;
        try
        {
            var account = await _application.Mail.GetAccountAsync(_application.CancellationToken);
            _account.Text = account is not null ? $"Classic Outlook account: {account.Address}" : string.IsNullOrEmpty(_application.Settings.AccountId) ? "No Outlook account selected. Connect to classic Outlook to choose an account." : $"Selected Outlook account unavailable: {_application.Settings.AccountId}. Open classic Outlook or choose another account.";
            _accountSetup.Text = string.IsNullOrEmpty(_application.Settings.AccountId) ? "&Connect to classic Outlook..." : "&Choose Outlook account...";
        }
        catch (Exception exception)
        {
            _account.Text = "Classic Outlook is unavailable. Open Outlook and check its profile or security prompts, then connect again.";
            AppLog.Write("Account status could not be read.", exception);
        }
        finally
        {
            _refreshingAccount = false;
        }
    }

    private async Task ReloadFoldersAsync(string? selectedRoot = null)
    {
        var subscriptions = _application.Settings.Folders.ToArray();
        var rows = await Task.Run(() => subscriptions.Select(subscription =>
        {
            try
            {
                var configuration = FolderStore.LoadConfiguration(subscription.RootPath);
                return (Subscription: subscription, Name: configuration.Name, State: configuration.Enabled ? "Waiting" : "Shared configuration paused", Detail: "");
            }
            catch (Exception exception)
            {
                return (Subscription: subscription, Name: Path.GetFileName(subscription.RootPath.TrimEnd(Path.DirectorySeparatorChar)), State: "Unavailable", Detail: exception.Message);
            }
        }).ToArray());
        if (IsDisposed)
            return;
        _folders.Rows.Clear();
        foreach (var item in rows)
        {
            _statuses.TryGetValue(item.Subscription.RootPath, out var current);
            var index = _folders.Rows.Add(item.Name, item.Subscription.RootPath, item.Subscription.Enabled ? "Enabled" : "Paused", item.Subscription.Enabled ? current?.State ?? item.State : "Paused on this computer", current?.Detail ?? item.Detail);
            _folders.Rows[index].Tag = item.Subscription;
            if (string.Equals(item.Subscription.RootPath, selectedRoot, StringComparison.OrdinalIgnoreCase))
                _folders.Rows[index].Selected = true;
        }
        UpdateSelection();
        await RefreshHistoryAsync();
    }

    private async Task AddFolderAsync()
    {
        using var picker = new FolderBrowserDialog { Description = "Select the shared folder that contains or will contain config.yml", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;
        var root = Path.GetFullPath(picker.SelectedPath).TrimEnd(Path.DirectorySeparatorChar);
        if (_application.Settings.Folders.Any(folder => string.Equals(folder.RootPath, root, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "This folder is already selected on this computer.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        SetBusy(true, "Reading shared folder configuration...");
        try
        {
            var existing = await Task.Run(() => File.Exists(Path.Combine(root, "config.yml")));
            if (!existing)
            {
                using var editor = new FolderConfigurationForm(root, new FolderConfiguration { Name = Path.GetFileName(root) });
                if (editor.ShowDialog(this) != DialogResult.OK)
                    return;
            }
            else
            {
                await Task.Run(() => FolderStore.LoadConfiguration(root));
            }
            await Task.Run(() => FolderStore.RegisterWorker(root, _application.Settings.InstallationId));
            _application.Settings.Folders.Add(new FolderSubscription { RootPath = root, Enabled = false });
            SettingsStore.Save(_application.Settings);
            await _application.RestartEngineAsync();
            await ReloadFoldersAsync(root);
            _status.Text = "Folder added and paused on this computer. Review its shared configuration, then choose Enable on this computer.";
        }
        catch (Exception exception)
        {
            ReportFailure("The folder could not be added.", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task EditFolderAsync()
    {
        if (_folders.SelectedRows.Count == 0 || _folders.SelectedRows[0].Tag is not FolderSubscription subscription)
            return;
        SetBusy(true, "Reading shared configuration...");
        try
        {
            var configuration = await Task.Run(() => FolderStore.LoadConfiguration(subscription.RootPath));
            using var editor = new FolderConfigurationForm(subscription.RootPath, configuration);
            if (editor.ShowDialog(this) == DialogResult.OK)
            {
                await _application.RestartEngineAsync();
                await ReloadFoldersAsync(subscription.RootPath);
            }
        }
        catch (Exception exception)
        {
            ReportFailure("The shared configuration could not be edited.", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ProcessSelectedFolderAsync(bool reconcile)
    {
        if (_folders.SelectedRows.Count == 0 || _folders.SelectedRows[0].Tag is not FolderSubscription subscription)
            return;
        if (!reconcile && MessageBox.Show(this, "Send the ready files in this folder now using its shared recipients and message?\n\nThis uses the same shared lock and run history as scheduled sending.", "Run folder now", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        SetBusy(true, reconcile ? "Checking Outlook send status and archive..." : "Processing the folder. Check classic Outlook if it asks for permission to send...");
        try
        {
            await _application.RunFolderAsync(subscription.RootPath, reconcile);
            await RefreshHistoryAsync();
        }
        catch (OperationCanceledException) when (_application.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportFailure(reconcile ? "The run could not be reconciled." : "The folder could not be processed.", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RefreshHistoryAsync()
    {
        if (_refreshingHistory || IsDisposed)
            return;
        if (_folders.SelectedRows.Count == 0 || _folders.SelectedRows[0].Tag is not FolderSubscription subscription)
        {
            _history.Rows.Clear();
            return;
        }
        _refreshingHistory = true;
        var selectedRun = _history.SelectedRows.Count == 0 ? null : (_history.SelectedRows[0].Tag as RunRecord)?.Id;
        try
        {
            var history = await Task.Run(() => FolderStore.GetHistory(subscription.RootPath));
            if (IsDisposed || _folders.SelectedRows.Count == 0 || !ReferenceEquals(_folders.SelectedRows[0].Tag, subscription))
                return;
            _history.Rows.Clear();
            foreach (var run in history.OrderByDescending(run => run.CreatedUtc))
            {
                var index = _history.Rows.Add(run.Status.ToString(), run.OccurrenceUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "Manual", run.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), run.AccountAddress, run.Detail);
                _history.Rows[index].Tag = run;
                if (run.Id == selectedRun)
                    _history.Rows[index].Selected = true;
            }
            UpdateSelection();
        }
        catch (Exception exception)
        {
            _status.Text = $"Shared history unavailable: {exception.Message}";
        }
        finally
        {
            _refreshingHistory = false;
            if (!IsDisposed && _folders.SelectedRows.Count > 0 && !ReferenceEquals(_folders.SelectedRows[0].Tag, subscription))
                await RefreshHistoryAsync();
        }
    }

    public void UpdateFolderStatus(FolderStatus status)
    {
        _statuses[status.RootPath] = status;
        foreach (DataGridViewRow row in _folders.Rows)
        {
            if (row.Tag is FolderSubscription subscription && string.Equals(subscription.RootPath, status.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                row.Cells[0].Value = status.Name;
                row.Cells[3].Value = subscription.Enabled ? status.State : "Paused on this computer";
                row.Cells[4].Value = status.Detail;
                break;
            }
        }
    }

    public void ShowBackgroundFailure(string message)
    {
        _status.Text = $"Background processing stopped: {message}. Review the local log and restart the application.";
    }

    public void UpdateApplicationUpdateStatus(UpdateStatus status)
    {
        _updateStatus.Text = status.Message;
        _updateStatus.ToolTipText = status.Message;
        _checkUpdates.Enabled = !_busy && !status.IsChecking;
        _installUpdate.Visible = status.CanInstall;
        _installUpdate.Enabled = !_busy && status.CanInstall;
    }

    private void UpdateSelection()
    {
        var selected = _folders.SelectedRows.Count > 0;
        foreach (var button in new[] { _edit, _pause, _remove, _runNow, _reconcile, _openFolder })
            button.Enabled = selected && !_busy;
        _pause.Text = selected && _folders.SelectedRows[0].Tag is FolderSubscription { Enabled: true } ? "&Pause on this computer" : "&Enable on this computer";
        _confirmSent.Enabled = selected && _history.SelectedRows.Count > 0 && _history.SelectedRows[0].Tag is RunRecord { Status: RunStatus.Submitting or RunStatus.Queued or RunStatus.NeedsReview } && !_busy;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        UseWaitCursor = busy;
        foreach (var control in new Control[] { _accountSetup, _startAtSignIn, _add, _refresh })
            control.Enabled = !busy;
        UpdateSelection();
        UpdateApplicationUpdateStatus(_application.Updates.Status);
        if (message is not null)
            _status.Text = message;
        else if (!busy && _status.Text?.StartsWith("Folder added", StringComparison.Ordinal) != true)
            _status.Text = "Closing this window keeps automation running in the system tray. Use Exit to stop.";
    }

    private void ReportFailure(string message, Exception exception)
    {
        AppLog.Write(message, exception);
        _status.Text = message;
        MessageBox.Show(this, $"{message}\n\n{exception.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _historyTimer.Dispose();
        base.Dispose(disposing);
    }
}
