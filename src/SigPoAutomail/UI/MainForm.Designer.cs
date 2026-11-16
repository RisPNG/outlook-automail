namespace SigPoAutomail.UI;

internal sealed partial class MainForm
{
    private Label _account = null!;
    private Button _accountSetup = null!;
    private CheckBox _startAtSignIn = null!;
    private Button _openLogs = null!;
    private Button _checkUpdates = null!;
    private Button _installUpdate = null!;
    private DataGridView _folders = null!;
    private DataGridView _history = null!;
    private Button _add = null!;
    private Button _edit = null!;
    private Button _pause = null!;
    private Button _remove = null!;
    private Button _openFolder = null!;
    private Button _runNow = null!;
    private Button _reconcile = null!;
    private Button _refresh = null!;
    private Button _confirmSent = null!;
    private Button _exit = null!;
    private ToolStripStatusLabel _status = null!;
    private ToolStripStatusLabel _updateStatus = null!;
    private System.Windows.Forms.Timer _historyTimer = null!;

    private void InitializeComponent()
    {
        Text = "Outlook Automail";
        Icon = SystemIcons.Application;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 670);
        ClientSize = new Size(1180, 760);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 14, 16, 8), ColumnCount = 1, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _account = new Label { AutoSize = true, Dock = DockStyle.Fill, Text = "Checking classic Outlook account...", Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold), Margin = new Padding(3, 3, 3, 10) };
        layout.Controls.Add(_account, 0, 0);
        var accountActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 6) };
        _accountSetup = new Button { Text = "&Connect to classic Outlook...", AutoSize = true };
        _startAtSignIn = new CheckBox { Text = "Start at Windows sign-in", AutoSize = true, Margin = new Padding(16, 7, 8, 3) };
        _openLogs = new Button { Text = "Open local &logs", AutoSize = true };
        _checkUpdates = new Button { Text = "Check for &updates", AutoSize = true };
        _installUpdate = new Button { Text = "&Install update", AutoSize = true, Visible = false };
        accountActions.Controls.AddRange([_accountSetup, _startAtSignIn, _openLogs, _checkUpdates, _installUpdate]);
        layout.Controls.Add(accountActions, 0, 1);
        var guidance = new Label { Text = "Select a shared folder to reuse its recipients, message, and schedule. Newly added folders are paused on this computer until you enable them.", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 10) };
        layout.Controls.Add(guidance, 0, 2);
        var split = new SplitContainer { Size = new Size(1120, 500), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 7, Panel1MinSize = 180, Panel2MinSize = 160, SplitterDistance = 245 };
        var folderLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        folderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        folderLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        folderLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        folderLayout.Controls.Add(new Label { Text = "Shared folders", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold), Margin = new Padding(3, 0, 3, 8) }, 0, 0);
        _folders = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.Fixed3D,
            AccessibleName = "Configured shared folders"
        };
        _folders.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Folder", FillWeight = 19 });
        _folders.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Path", FillWeight = 28 });
        _folders.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "This computer", FillWeight = 13 });
        _folders.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", FillWeight = 18 });
        _folders.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Details", FillWeight = 28 });
        folderLayout.Controls.Add(_folders, 0, 1);
        var folderActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 5, 0, 3) };
        _add = new Button { Text = "&Add folder...", AutoSize = true };
        _edit = new Button { Text = "Edit shared &configuration...", AutoSize = true };
        _pause = new Button { Text = "&Enable on this computer", AutoSize = true };
        _remove = new Button { Text = "&Remove", AutoSize = true };
        _openFolder = new Button { Text = "Open &folder", AutoSize = true };
        folderActions.Controls.AddRange([_add, _edit, _pause, _remove, _openFolder]);
        folderLayout.Controls.Add(folderActions, 0, 2);
        split.Panel1.Controls.Add(folderLayout);
        var historyLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        historyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        historyLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        historyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        historyLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        historyLayout.Controls.Add(new Label { Text = "Shared run history for the selected folder (times shown on this computer)", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold), Margin = new Padding(3, 6, 3, 8) }, 0, 0);
        _history = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.Fixed3D,
            AccessibleName = "Shared email run history"
        };
        _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", FillWeight = 13 });
        _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Scheduled for", FillWeight = 17 });
        _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Started", FillWeight = 19 });
        _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sending account", FillWeight = 23 });
        _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Details", FillWeight = 38 });
        historyLayout.Controls.Add(_history, 0, 1);
        var recovery = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 5, 0, 0) };
        _confirmSent = new Button { Text = "Confirm selected email was sent...", AutoSize = true };
        recovery.Controls.Add(_confirmSent);
        recovery.Controls.Add(new Label { Text = "Check Sent Items or the recipients. A message in Outbox is still queued.", AutoSize = true, Margin = new Padding(8, 8, 0, 3) });
        historyLayout.Controls.Add(recovery, 0, 2);
        split.Panel2.Controls.Add(historyLayout);
        layout.Controls.Add(split, 0, 3);
        var bottomActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 10, 0, 3) };
        _runNow = new Button { Text = "&Run now...", AutoSize = true };
        _reconcile = new Button { Text = "Retry archive / check send status", AutoSize = true };
        _refresh = new Button { Text = "Re&fresh", AutoSize = true };
        _exit = new Button { Text = "E&xit application", AutoSize = true, Margin = new Padding(20, 3, 3, 3) };
        bottomActions.Controls.AddRange([_runNow, _reconcile, _refresh, _exit]);
        layout.Controls.Add(bottomActions, 0, 4);
        var footer = new Label { Text = "Closing this window keeps automation running in the system tray. Sending requires classic Outlook, a configured account, access to the share, and an awake computer.", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 6, 3, 8) };
        layout.Controls.Add(footer, 0, 5);
        var statusStrip = new StatusStrip { SizingGrip = true };
        _status = new ToolStripStatusLabel { Text = "Ready", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _updateStatus = new ToolStripStatusLabel { Text = "Updates are checked automatically.", AutoToolTip = true };
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(_updateStatus);
        Controls.Add(layout);
        Controls.Add(statusStrip);
        _historyTimer = new System.Windows.Forms.Timer { Interval = 20000 };
    }
}
