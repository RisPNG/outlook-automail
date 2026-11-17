using System.Net;

namespace OutlookAutomail.UI;

internal sealed partial class FolderConfigurationForm : Form
{
    private readonly string _root;
    private readonly string _installationId;
    private readonly FolderConfiguration _configuration;
    private readonly List<InlineImageConfiguration> _inlineImages;
    private readonly SemaphoreSlim _photoImports = new(1, 1);
    private bool _saving;
    private int _pendingImports;
    private int _photoPreviewRevision;

    public FolderConfigurationForm(string root, FolderConfiguration configuration, string installationId)
    {
        _root = root;
        _installationId = installationId;
        _configuration = configuration;
        _inlineImages = [.. configuration.InlineImages];
        InitializeComponent();
        FormClosing += (_, args) => args.Cancel = _saving || _pendingImports > 0;
        _rootPath.Text = root;
        _name.Text = configuration.Name;
        _sharedEnabled.Checked = configuration.Enabled;
        _pending.Text = configuration.PendingFolder;
        _sent.Text = configuration.SentFolder;
        _to.Text = string.Join(Environment.NewLine, configuration.To);
        _cc.Text = string.Join(Environment.NewLine, configuration.Cc);
        _subject.Text = configuration.Subject;
        _body.SetMessage(configuration.Body, configuration.BodyIsHtml);
        _body.SetImages(_inlineImages, _root);
        _dateFormat.Text = configuration.DateFormat;
        _dateTimeZone.SelectedItem = configuration.DateTimeZoneId;
        _sendEmpty.Checked = configuration.SendWhenEmpty;
        _waitForFiles.Checked = configuration.WaitForFiles;
        var machineVariables = configuration.MachineVariables.FirstOrDefault(machine => string.Equals(machine.InstallationId, installationId, StringComparison.OrdinalIgnoreCase));
        foreach (var name in configuration.MachineVariables.SelectMany(machine => machine.Values.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var index = _variables.Rows.Add(name);
            _variables.Rows[index].Cells[1].Value = machineVariables?.Values.GetValueOrDefault(name);
        }
        _variables.CellParsing += (_, args) =>
        {
            if (args.ColumnIndex == 1)
            {
                args.Value = Convert.ToString(args.Value) ?? "";
                args.ParsingApplied = true;
            }
        };
        _variables.EditingControlShowing += (_, args) =>
        {
            if (_variables.CurrentCell is { ColumnIndex: 1, Value: null } && args.Control is TextBox input)
                input.Clear();
        };
        _variables.CellValueChanged += (_, _) => RefreshMessagePreview();
        _variables.RowsRemoved += (_, _) => RefreshMessagePreview();
        _addVariable.Click += (_, _) =>
        {
            var index = _variables.Rows.Add();
            _variables.CurrentCell = _variables.Rows[index].Cells[0];
            _variables.BeginEdit(true);
        };
        _removeVariable.Click += (_, _) =>
        {
            if (_variables.CurrentRow is not null)
                _variables.Rows.Remove(_variables.CurrentRow);
        };
        foreach (var image in _inlineImages)
        {
            var index = _photos.Rows.Add(image.Path);
            _photos.Rows[index].Tag = image;
        }
        foreach (var schedule in configuration.Schedules)
        {
            var index = _schedules.Rows.Add(schedule.Enabled, schedule.Time, schedule.TimeZoneId, schedule.Days.Contains(DayOfWeek.Monday), schedule.Days.Contains(DayOfWeek.Tuesday), schedule.Days.Contains(DayOfWeek.Wednesday), schedule.Days.Contains(DayOfWeek.Thursday), schedule.Days.Contains(DayOfWeek.Friday), schedule.Days.Contains(DayOfWeek.Saturday), schedule.Days.Contains(DayOfWeek.Sunday));
            _schedules.Rows[index].Tag = schedule.Id;
        }
        _addSchedule.Click += (_, _) =>
        {
            var schedule = new ScheduleConfiguration();
            var index = _schedules.Rows.Add(true, schedule.Time, schedule.TimeZoneId, true, true, true, true, true, true, true);
            _schedules.Rows[index].Tag = schedule.Id;
            _schedules.CurrentCell = _schedules.Rows[index].Cells[1];
        };
        _removeSchedule.Click += (_, _) =>
        {
            if (_schedules.CurrentRow is not null)
                _schedules.Rows.Remove(_schedules.CurrentRow);
        };
        _sendEmpty.CheckedChanged += (_, _) =>
        {
            _waitForFiles.Enabled = !_sendEmpty.Checked;
            if (_sendEmpty.Checked)
                _waitForFiles.Checked = false;
        };
        _waitForFiles.Enabled = !_sendEmpty.Checked;
        _subject.TextChanged += (_, _) => RefreshMessagePreview();
        _body.BodyChanged += RefreshMessagePreview;
        _body.Notice += message => _previewStatus.Text = message;
        _body.ImagePasted += async bytes => await ImportPhotoAsync(null, bytes);
        _dateFormat.TextChanged += (_, _) => RefreshMessagePreview();
        _dateTimeZone.SelectedIndexChanged += (_, _) => RefreshMessagePreview();
        Activated += (_, _) => RefreshMessagePreview();
        _editorTabs.SelectedIndexChanged += async (_, _) =>
        {
            if (_editorTabs.SelectedTab == _messageTab)
            {
                await _body.InitializeAsync();
                RefreshMessagePreview();
            }
        };
        _insertSubjectDate.Click += (_, _) =>
        {
            _subject.SelectedText = "{date}";
            _subject.Focus();
        };
        _insertBodyDate.Click += async (_, _) => await _body.InsertTextAsync("{date}");
        _photos.SelectionChanged += async (_, _) =>
        {
            if (_pendingImports == 0)
                await RefreshPhotoPreviewAsync();
        };
        _messageViews.SelectedIndexChanged += async (_, _) =>
        {
            if (_pendingImports == 0 && _messageViews.SelectedTab == _photosTab)
                await RefreshPhotoPreviewAsync();
        };
        _addPhoto.Click += async (_, _) =>
        {
            using var picker = new OpenFileDialog
            {
                Title = "Add a photo to the email body",
                Filter = "Image files (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp",
                CheckFileExists = true,
                Multiselect = false
            };
            if (picker.ShowDialog(this) == DialogResult.OK)
                await ImportPhotoAsync(picker.FileName, null);
        };
        _insertPhoto.Click += async (_, _) =>
        {
            if (_photos.SelectedRows.Count > 0 && _photos.SelectedRows[0].Tag is InlineImageConfiguration image)
            {
                _messageViews.SelectedIndex = 0;
                await _body.InsertImageAsync(image.Id);
            }
        };
        _removePhoto.Click += async (_, _) =>
        {
            if (_photos.SelectedRows.Count == 0 || _photos.SelectedRows[0].Tag is not InlineImageConfiguration image)
                return;
            await _body.RemoveImageAsync(image.Id);
            RefreshMessagePreview();
        };
        _save.Click += async (_, _) =>
        {
            _saving = true;
            _save.Enabled = false;
            _cancel.Enabled = false;
            _editorTabs.Enabled = false;
            UseWaitCursor = true;
            try
            {
                await _body.CaptureBodyAsync();
                _schedules.EndEdit();
                _variables.EndEdit();
                var schedules = new List<ScheduleConfiguration>();
                var days = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
                foreach (DataGridViewRow row in _schedules.Rows)
                {
                    var schedule = new ScheduleConfiguration
                    {
                        Id = (string)row.Tag!,
                        Enabled = Convert.ToBoolean(row.Cells[0].Value),
                        Time = Convert.ToString(row.Cells[1].Value)?.Trim() ?? "",
                        TimeZoneId = Convert.ToString(row.Cells[2].Value) ?? "",
                        Days = []
                    };
                    for (var index = 0; index < days.Length; index++)
                        if (Convert.ToBoolean(row.Cells[index + 3].Value))
                            schedule.Days.Add(days[index]);
                    schedules.Add(schedule);
                }
                var updated = new FolderConfiguration
                {
                    SchemaVersion = configuration.SchemaVersion,
                    Id = configuration.Id,
                    Name = _name.Text.Trim(),
                    Enabled = _sharedEnabled.Checked,
                    ActiveFromUtc = configuration.ActiveFromUtc,
                    PendingFolder = _pending.Text.Trim(),
                    SentFolder = _sent.Text.Trim(),
                    To = _to.Text.Split(['\r', '\n', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Cc = _cc.Text.Split(['\r', '\n', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Subject = _subject.Text.Trim(),
                    Body = _body.Body,
                    BodyIsHtml = _body.BodyIsHtml,
                    InlineImages = [.. _inlineImages],
                    DateFormat = _dateFormat.Text,
                    DateTimeZoneId = Convert.ToString(_dateTimeZone.SelectedItem) ?? "",
                    MachineVariables = ReadMachineVariables(),
                    SendWhenEmpty = _sendEmpty.Checked,
                    WaitForFiles = _waitForFiles.Checked && !_sendEmpty.Checked,
                    Schedules = schedules
                };
                await Task.Run(() => FolderStore.SaveConfiguration(_root, updated, _installationId));
                _saving = false;
                DialogResult = DialogResult.OK;
            }
            catch (Exception exception)
            {
                AppLog.Write("Shared configuration could not be saved.", exception);
                MessageBox.Show(this, exception.Message, "Configuration could not be saved", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _cancel.Enabled = true;
                _editorTabs.Enabled = true;
                _saving = false;
                UseWaitCursor = false;
                RefreshMessagePreview();
            }
        };
        RefreshMessagePreview();
    }

    private List<MachineVariableConfiguration> ReadMachineVariables()
    {
        var current = new MachineVariableConfiguration { InstallationId = _installationId, MachineName = Environment.MachineName };
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (DataGridViewRow row in _variables.Rows)
        {
            var name = Convert.ToString(row.Cells[0].Value)?.Trim() ?? "";
            var value = row.Cells[1].Value;
            if (name.Length == 0 && value is null)
                continue;
            if (name.Length == 0)
                throw new InvalidDataException("Enter a name for each variable, for example name.");
            if (!names.Add(name))
                throw new InvalidDataException($"The variable '{name}' appears more than once. Use a unique name for each variable.");
            if (value is not null)
                current.Values.Add(name, Convert.ToString(value) ?? "");
            else if (!_configuration.MachineVariables.Any(machine => machine.Values.ContainsKey(name)))
                throw new InvalidDataException($"Enter a value for '{name}' on this computer.");
        }
        var machines = _configuration.MachineVariables.Where(machine => !string.Equals(machine.InstallationId, _installationId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (current.Values.Count > 0)
            machines.Add(current);
        return machines;
    }

    private async Task ImportPhotoAsync(string? path, byte[]? bytes)
    {
        _pendingImports++;
        await _photoImports.WaitAsync();
        _editorTabs.Enabled = false;
        _save.Enabled = false;
        _cancel.Enabled = false;
        UseWaitCursor = true;
        try
        {
            var draft = new FolderConfiguration { PendingFolder = _pending.Text.Trim(), SentFolder = _sent.Text.Trim() };
            var image = await Task.Run(() =>
            {
                using Stream source = path is null ? new MemoryStream(bytes!, false) : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return FolderStore.ImportInlineImage(_root, draft, source);
            });
            _inlineImages.Add(image);
            var index = _photos.Rows.Add(image.Path);
            _photos.Rows[index].Tag = image;
            _photos.CurrentCell = _photos.Rows[index].Cells[0];
            _body.SetImages(_inlineImages, _root);
            RefreshMessagePreview();
            _messageViews.SelectedIndex = 0;
            await _body.InsertImageAsync(image.Id);
        }
        catch (Exception exception)
        {
            AppLog.Write("A photo could not be added to the message.", exception);
            MessageBox.Show(this, exception.Message, "Photo could not be added", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _pendingImports--;
            _editorTabs.Enabled = _pendingImports == 0;
            _cancel.Enabled = _pendingImports == 0;
            UseWaitCursor = _pendingImports > 0;
            _photoImports.Release();
            RefreshMessagePreview();
            await RefreshPhotoPreviewAsync();
        }
    }

    private async Task RefreshPhotoPreviewAsync()
    {
        var revision = ++_photoPreviewRevision;
        var previous = _photoPreview.Image;
        _photoPreview.Image = null;
        previous?.Dispose();
        var image = _photos.SelectedRows.Count == 0 ? null : _photos.SelectedRows[0].Tag as InlineImageConfiguration;
        _insertPhoto.Enabled = image is not null && _body.IsReady && !_saving && _pendingImports == 0;
        _removePhoto.Enabled = _insertPhoto.Enabled;
        if (image is null)
        {
            _photoStatus.Text = "Select a photo to preview it.";
            return;
        }
        _photoStatus.Text = "Loading photo...";
        try
        {
            var draft = new FolderConfiguration { PendingFolder = _pending.Text.Trim(), SentFolder = _sent.Text.Trim() };
            var thumbnail = await Task.Run(() =>
            {
                var path = FolderStore.ResolveInlineImage(_root, draft, image);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var source = Image.FromStream(stream);
                var scale = Math.Min(1D, Math.Min(320D / source.Width, 240D / source.Height));
                return source.GetThumbnailImage(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)), null, IntPtr.Zero);
            });
            if (IsDisposed || revision != _photoPreviewRevision)
                thumbnail.Dispose();
            else
            {
                _photoPreview.Image = thumbnail;
                _photoStatus.Text = image.Path;
            }
        }
        catch (Exception exception)
        {
            if (!IsDisposed && revision == _photoPreviewRevision)
                _photoStatus.Text = "Photo preview unavailable: " + exception.Message;
        }
    }

    private async void RefreshMessagePreview()
    {
        if (IsDisposed)
            return;
        try
        {
            _ = FolderStore.ValidateInlineImageReferences(_body.Body, _body.BodyIsHtml, _inlineImages.Select(image => image.Id));
            var preview = MessageTemplate.Render(new FolderConfiguration
            {
                Subject = _subject.Text.Trim(),
                Body = _body.Body,
                BodyIsHtml = _body.BodyIsHtml,
                MachineVariables = ReadMachineVariables(),
                DateFormat = _dateFormat.Text,
                DateTimeZoneId = Convert.ToString(_dateTimeZone.SelectedItem) ?? ""
            }, DateTimeOffset.UtcNow, _installationId);
            var html = _body.BodyIsHtml ? preview.Body : WebUtility.HtmlEncode(preview.Body).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "<br>", StringComparison.Ordinal);
            await _body.ShowPreviewAsync(preview.Subject, html);
            if (IsDisposed)
                return;
            _previewStatus.Text = "Preview uses this computer's variables, the current date, and embedded photos. Appearance can vary between email apps.";
            _save.Enabled = !_saving && _pendingImports == 0 && _body.CanSave;
        }
        catch (InvalidDataException exception)
        {
            await _body.ShowPreviewAsync("", "");
            if (IsDisposed)
                return;
            _previewStatus.Text = "Preview unavailable: " + exception.Message;
            _save.Enabled = false;
        }
        _variablesStatus.Text = _previewStatus.Text;
        _addPhoto.Enabled = _body.IsReady && !_saving && _pendingImports == 0;
        _insertBodyDate.Enabled = _addPhoto.Enabled;
        _insertPhoto.Enabled = _addPhoto.Enabled && _photos.SelectedRows.Count > 0;
        _removePhoto.Enabled = _insertPhoto.Enabled;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _photoPreviewRevision++;
            _photoPreview.Image?.Dispose();
            _photoImports.Dispose();
        }
        base.Dispose(disposing);
    }
}
