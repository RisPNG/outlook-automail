namespace OutlookAutomail.UI;

internal sealed partial class AccountForm
{
    private DataGridView _accounts = null!;
    private Label _guidance = null!;
    private Button _save = null!;

    private void InitializeComponent()
    {
        Text = "Choose a classic Outlook account";
        Icon = AppIcon.Default;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(620, 430);
        ClientSize = new Size(700, 460);
        MinimizeBox = false;
        MaximizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _guidance = new Label { Text = "Select the account this computer will use from the accounts already configured in your classic Outlook profile. Add accounts and sign in through Outlook itself. Selecting no account pauses automatic sending.", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 16) };
        layout.Controls.Add(_guidance, 0, 0);
        _accounts = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.Fixed3D,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AccessibleName = "Accounts in the classic Outlook profile"
        };
        _accounts.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sending account" });
        _accounts.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Display name" });
        layout.Controls.Add(_accounts, 0, 1);
        var note = new Label { Text = "Requires classic Outlook for Windows. The new Outlook app is not supported. Outlook controls authentication and may show security or profile prompts. This application does not store your password or sign out or close Outlook when it exits.", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 12) };
        layout.Controls.Add(note, 0, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        _save = new Button { Text = "&Use selected account", AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_save);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        AcceptButton = _save;
        CancelButton = cancel;
    }
}
