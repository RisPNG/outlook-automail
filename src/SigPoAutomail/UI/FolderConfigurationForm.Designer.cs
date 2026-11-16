namespace SigPoAutomail.UI;

internal sealed partial class FolderConfigurationForm
{
    private Label _rootPath = null!;
    private TextBox _name = null!;
    private CheckBox _sharedEnabled = null!;
    private TextBox _pending = null!;
    private TextBox _sent = null!;
    private TextBox _to = null!;
    private TextBox _cc = null!;
    private TextBox _subject = null!;
    private TextBox _body = null!;
    private CheckBox _html = null!;
    private ComboBox _dateFormat = null!;
    private ComboBox _dateTimeZone = null!;
    private Button _insertSubjectDate = null!;
    private Button _insertBodyDate = null!;
    private TextBox _previewSubject = null!;
    private TextBox _previewBody = null!;
    private Label _previewBodyLabel = null!;
    private Label _previewStatus = null!;
    private Button _addPhoto = null!;
    private Button _insertPhoto = null!;
    private Button _removePhoto = null!;
    private DataGridView _photos = null!;
    private PictureBox _photoPreview = null!;
    private Label _photoStatus = null!;
    private TabControl _messagePreviewTabs = null!;
    private TabPage _photosTab = null!;
    private DataGridView _schedules = null!;
    private Button _addSchedule = null!;
    private Button _removeSchedule = null!;
    private CheckBox _sendEmpty = null!;
    private CheckBox _waitForFiles = null!;
    private NumericUpDown _readySeconds = null!;
    private NumericUpDown _maximumMb = null!;
    private Button _save = null!;
    private Button _cancel = null!;
    private TabControl _editorTabs = null!;

    private void InitializeComponent()
    {
        Text = "Shared folder configuration";
        Icon = SystemIcons.Application;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 680);
        ClientSize = new Size(1040, 720);
        MinimizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var guidance = new Label { Text = "These settings are saved as config.yml in the shared folder and apply to every computer using it. Each computer sends from its selected classic Outlook account.", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 12) };
        layout.Controls.Add(guidance, 0, 0);
        _editorTabs = new TabControl { Dock = DockStyle.Fill };
        var folderTab = new TabPage("Folders and recipients") { Padding = new Padding(12) };
        var messageTab = new TabPage("Message") { Padding = new Padding(12) };
        var scheduleTab = new TabPage("Schedule and sending") { Padding = new Padding(12) };
        _editorTabs.TabPages.AddRange([folderTab, messageTab, scheduleTab]);
        layout.Controls.Add(_editorTabs, 0, 1);

        var folderLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7 };
        folderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < 6; index++)
            folderLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        folderLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _rootPath = new Label { AutoSize = true, Dock = DockStyle.Fill, AutoEllipsis = true, Margin = new Padding(3, 6, 3, 8) };
        _name = new TextBox { Dock = DockStyle.Fill, AccessibleName = "Folder display name" };
        _sharedEnabled = new CheckBox { Text = "Enable scheduled sending for everyone using this shared configuration", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 8, 3, 8) };
        _pending = new TextBox { Dock = DockStyle.Fill, AccessibleName = "To send folder relative path" };
        _sent = new TextBox { Dock = DockStyle.Fill, AccessibleName = "Sent folder relative path" };
        var fields = new (string Label, Control Control)[]
        {
            ("Shared root", _rootPath),
            ("&Name", _name),
            ("Scheduled sending", _sharedEnabled),
            ("&To send folder", _pending),
            ("&Sent folder", _sent)
        };
        for (var index = 0; index < fields.Length; index++)
        {
            folderLayout.Controls.Add(new Label { Text = fields[index].Label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 14, 6) }, 0, index);
            folderLayout.Controls.Add(fields[index].Control, 1, index);
        }
        var pathsNote = new Label { Text = "Enter separate paths relative to the shared root, for example To send and Sent. Files are collected from the top level of the To send folder.", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 8, 3, 12) };
        folderLayout.Controls.Add(pathsNote, 0, 5);
        folderLayout.SetColumnSpan(pathsNote, 2);
        var recipients = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = Padding.Empty };
        recipients.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        recipients.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        recipients.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        recipients.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        recipients.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        recipients.Controls.Add(new Label { Text = "&To recipients", AutoSize = true, Margin = new Padding(3, 0, 3, 6) }, 0, 0);
        recipients.Controls.Add(new Label { Text = "&Cc recipients (optional)", AutoSize = true, Margin = new Padding(8, 0, 3, 6) }, 1, 0);
        _to = new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AccessibleName = "To recipient email addresses", Margin = new Padding(3, 3, 8, 3) };
        _cc = new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AccessibleName = "Cc recipient email addresses", Margin = new Padding(8, 3, 3, 3) };
        recipients.Controls.Add(_to, 0, 1);
        recipients.Controls.Add(_cc, 1, 1);
        var recipientNote = new Label { Text = "Enter email addresses on separate lines, or separate them with semicolons. The sender is the classic Outlook account selected on the computer processing the run.", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 8, 3, 0) };
        recipients.Controls.Add(recipientNote, 0, 2);
        recipients.SetColumnSpan(recipientNote, 2);
        folderLayout.Controls.Add(recipients, 0, 6);
        folderLayout.SetColumnSpan(recipients, 2);
        folderTab.Controls.Add(folderLayout);

        var messageLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
        messageLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        messageLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        messageLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        messageLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        messageLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        messageLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        messageLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        messageLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var dateSettings = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 0, 0, 8) };
        dateSettings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dateSettings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        dateSettings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dateSettings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        dateSettings.Controls.Add(new Label { Text = "Date &format", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 6) }, 0, 0);
        _dateFormat = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AccessibleName = "Default message date format", Margin = new Padding(3, 3, 16, 3) };
        _dateFormat.Items.AddRange(["dd/MM/yyyy", "yyyy-MM-dd", "dd MMM yyyy", "MMMM d, yyyy", "dddd, dd MMMM yyyy", "yyyyMMdd"]);
        dateSettings.Controls.Add(_dateFormat, 1, 0);
        dateSettings.Controls.Add(new Label { Text = "Date time &zone", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 6) }, 2, 0);
        _dateTimeZone = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Message date time zone" };
        _dateTimeZone.Items.AddRange(TimeZoneInfo.GetSystemTimeZones().Select(zone => zone.Id).Append(_configuration.DateTimeZoneId).Distinct().Order().ToArray());
        dateSettings.Controls.Add(_dateTimeZone, 3, 0);
        messageLayout.Controls.Add(dateSettings, 0, 0);
        messageLayout.Controls.Add(new Label { Text = "Use {date} for the selected format, or {date:yyyy-MM-dd} for an inline format. MM means month. Choose a preset or type a custom format. Dates use the time an email batch is created in the selected zone. Catch-up runs use the processing date.", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 12) }, 0, 1);
        messageLayout.Controls.Add(new Label { Text = "Email &subject template", AutoSize = true }, 0, 2);
        var subjectEditor = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 3, 0, 8) };
        subjectEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        subjectEditor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _subject = new TextBox { Dock = DockStyle.Fill, AccessibleName = "Email subject template" };
        _insertSubjectDate = new Button { Text = "Insert {date} in subject", AutoSize = true };
        subjectEditor.Controls.Add(_subject, 0, 0);
        subjectEditor.Controls.Add(_insertSubjectDate, 1, 0);
        messageLayout.Controls.Add(subjectEditor, 0, 3);
        var bodyActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 5) };
        _html = new CheckBox { Text = "Body contains HTML markup", AutoSize = true };
        _insertBodyDate = new Button { Text = "Insert {date} in body", AutoSize = true };
        _addPhoto = new Button { Text = "Add &photo...", AutoSize = true };
        bodyActions.Controls.AddRange([_insertBodyDate, _addPhoto, _html]);
        messageLayout.Controls.Add(bodyActions, 0, 4);
        var messageSplit = new SplitContainer { Size = new Size(920, 320), Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 7, Panel1MinSize = 240, Panel2MinSize = 240, SplitterDistance = 460 };
        var bodyGroup = new GroupBox { Text = "Message &body template", Dock = DockStyle.Fill, Padding = new Padding(8) };
        _body = new TextBox { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Both, AccessibleName = "Email message body template" };
        bodyGroup.Controls.Add(_body);
        messageSplit.Panel1.Controls.Add(bodyGroup);
        var previewGroup = new GroupBox { Text = "Live message preview", Dock = DockStyle.Fill, Padding = new Padding(8) };
        _messagePreviewTabs = new TabControl { Dock = DockStyle.Fill };
        var sourcePreviewTab = new TabPage("Message source") { Padding = new Padding(5) };
        _photosTab = new TabPage("Photos") { Padding = new Padding(5), AutoScroll = true };
        _messagePreviewTabs.TabPages.AddRange([sourcePreviewTab, _photosTab]);
        var previewLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewLayout.Controls.Add(new Label { Text = "Subject preview", AutoSize = true }, 0, 0);
        _previewSubject = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, AccessibleName = "Rendered email subject preview", Margin = new Padding(3, 3, 3, 10) };
        previewLayout.Controls.Add(_previewSubject, 0, 1);
        _previewBodyLabel = new Label { Text = "Body preview", AutoSize = true };
        previewLayout.Controls.Add(_previewBodyLabel, 0, 2);
        _previewBody = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, AccessibleName = "Rendered email body preview" };
        previewLayout.Controls.Add(_previewBody, 0, 3);
        sourcePreviewTab.Controls.Add(previewLayout);
        var photosLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 280), ColumnCount = 1, RowCount = 5 };
        photosLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        photosLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        photosLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        photosLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        photosLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        photosLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _photos = new DataGridView
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
            AccessibleName = "Photos embedded in the email body"
        };
        _photos.Height = 60;
        _photos.MinimumSize = new Size(0, 60);
        _photos.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Shared image file" });
        photosLayout.Controls.Add(_photos, 0, 0);
        var photoActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3) };
        _insertPhoto = new Button { Text = "Insert selected photo", AutoSize = true, Enabled = false };
        _removePhoto = new Button { Text = "Remove photo", AutoSize = true, Enabled = false };
        photoActions.Controls.AddRange([_insertPhoto, _removePhoto]);
        photosLayout.Controls.Add(photoActions, 0, 1);
        _photoPreview = new PictureBox { Dock = DockStyle.Fill, Height = 80, MinimumSize = new Size(0, 80), SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, AccessibleName = "Selected photo thumbnail", TabStop = false };
        photosLayout.Controls.Add(_photoPreview, 0, 2);
        _photoStatus = new Label { Text = "Select a photo to preview it.", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 5, 3, 3) };
        photosLayout.Controls.Add(_photoStatus, 0, 3);
        photosLayout.Controls.Add(new Label { Text = "The first photo converts plain text to HTML and is added at the end. In HTML, photos are inserted at the cursor. Removing a photo keeps its shared file for reuse.", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 0) }, 0, 4);
        _photosTab.Controls.Add(photosLayout);
        previewGroup.Controls.Add(_messagePreviewTabs);
        messageSplit.Panel2.Controls.Add(previewGroup);
        messageLayout.Controls.Add(messageSplit, 0, 5);
        _previewStatus = new Label { AutoSize = true, Dock = DockStyle.Fill, AccessibleName = "Message preview status", Margin = new Padding(3, 8, 3, 0) };
        messageLayout.Controls.Add(_previewStatus, 0, 6);
        messageTab.Controls.Add(messageLayout);

        var scheduleLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        scheduleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scheduleLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scheduleLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        scheduleLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scheduleLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scheduleLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scheduleLayout.Controls.Add(new Label { Text = "Use 24-hour HH:mm times and select the days for each schedule. Missed occurrences are checked when an eligible computer is available. Remove every schedule to use manual sending only.", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 10) }, 0, 0);
        _schedules = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            RowHeadersVisible = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.Fixed3D,
            EditMode = DataGridViewEditMode.EditOnEnter,
            AccessibleName = "Sending schedules"
        };
        _schedules.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "On", Width = 42 });
        _schedules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Time", Width = 74 });
        var zones = TimeZoneInfo.GetSystemTimeZones().Select(zone => zone.Id).Concat(_configuration.Schedules.Select(schedule => schedule.TimeZoneId)).Distinct().Order().ToArray();
        _schedules.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "Time zone", DataSource = zones, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 180, FlatStyle = FlatStyle.Standard, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton });
        foreach (var day in new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" })
            _schedules.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = day, Width = 46 });
        scheduleLayout.Controls.Add(_schedules, 0, 1);
        var scheduleActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 8) };
        _addSchedule = new Button { Text = "&Add schedule", AutoSize = true };
        _removeSchedule = new Button { Text = "&Remove schedule", AutoSize = true };
        scheduleActions.Controls.AddRange([_addSchedule, _removeSchedule]);
        scheduleLayout.Controls.Add(scheduleActions, 0, 2);
        var sending = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 4 };
        sending.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sending.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _sendEmpty = new CheckBox { Text = "Send the message even when there are no attachments", AutoSize = true, Dock = DockStyle.Fill };
        _waitForFiles = new CheckBox { Text = "When empty, keep the missed schedule pending until files arrive", AutoSize = true, Dock = DockStyle.Fill };
        sending.Controls.Add(_sendEmpty, 0, 0);
        sending.SetColumnSpan(_sendEmpty, 2);
        sending.Controls.Add(_waitForFiles, 0, 1);
        sending.SetColumnSpan(_waitForFiles, 2);
        sending.Controls.Add(new Label { Text = "File readiness delay (seconds)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 10, 14, 6) }, 0, 2);
        _readySeconds = new NumericUpDown { Minimum = 0, Maximum = 86400, Width = 100, AccessibleName = "File readiness delay in seconds", Margin = new Padding(3, 7, 3, 3) };
        sending.Controls.Add(_readySeconds, 1, 2);
        sending.Controls.Add(new Label { Text = "Maximum message size (MB)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 14, 6) }, 0, 3);
        _maximumMb = new NumericUpDown { Minimum = 1, Maximum = 150, Width = 100, AccessibleName = "Maximum message size in megabytes" };
        sending.Controls.Add(_maximumMb, 1, 3);
        scheduleLayout.Controls.Add(sending, 0, 3);
        scheduleLayout.Controls.Add(new Label { Text = "Files must remain unchanged for the readiness delay before sending. The message size includes attachment encoding. Your Outlook account's mail limits still apply. Files are archived after Outlook confirms the message is in Sent Items.", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 12, 3, 0) }, 0, 4);
        scheduleTab.Controls.Add(scheduleLayout);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 12, 0, 0) };
        _cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        _save = new Button { Text = "&Save shared configuration", AutoSize = true };
        buttons.Controls.AddRange([_cancel, _save]);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        AcceptButton = _save;
        CancelButton = _cancel;
    }
}
