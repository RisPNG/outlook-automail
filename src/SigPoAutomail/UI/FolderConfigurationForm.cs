using System.Net;
using System.Text.RegularExpressions;

namespace SigPoAutomail.UI;

internal sealed partial class FolderConfigurationForm : Form
{
    private readonly string _root;
    private readonly FolderConfiguration _configuration;
    private readonly List<InlineImageConfiguration> _inlineImages;
    private bool _saving;
    private bool _importing;
    private int _photoPreviewRevision;

    public FolderConfigurationForm(string root, FolderConfiguration configuration)
    {
        _root = root;
        _configuration = configuration;
        _inlineImages = [.. configuration.InlineImages];
        InitializeComponent();
        FormClosing += (_, args) => args.Cancel = _saving || _importing;
        _rootPath.Text = root;
        _name.Text = configuration.Name;
        _sharedEnabled.Checked = configuration.Enabled;
        _pending.Text = configuration.PendingFolder;
        _sent.Text = configuration.SentFolder;
        _to.Text = string.Join(Environment.NewLine, configuration.To);
        _cc.Text = string.Join(Environment.NewLine, configuration.Cc);
        _subject.Text = configuration.Subject;
        _body.Text = configuration.Body;
        _html.Checked = configuration.BodyIsHtml;
        _dateFormat.Text = configuration.DateFormat;
        _dateTimeZone.SelectedItem = configuration.DateTimeZoneId;
        _sendEmpty.Checked = configuration.SendWhenEmpty;
        _waitForFiles.Checked = configuration.WaitForFiles;
        _readySeconds.Value = configuration.FileReadySeconds;
        _maximumMb.Value = configuration.MaximumMessageSizeMb;
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
        _body.TextChanged += (_, _) => RefreshMessagePreview();
        _html.CheckedChanged += (_, _) => RefreshMessagePreview();
        _dateFormat.TextChanged += (_, _) => RefreshMessagePreview();
        _dateTimeZone.SelectedIndexChanged += (_, _) => RefreshMessagePreview();
        Activated += (_, _) => RefreshMessagePreview();
        _editorTabs.SelectedIndexChanged += (_, _) => RefreshMessagePreview();
        _insertSubjectDate.Click += (_, _) =>
        {
            _subject.SelectedText = "{date}";
            _subject.Focus();
        };
        _insertBodyDate.Click += (_, _) =>
        {
            _body.SelectedText = "{date}";
            _body.Focus();
        };
        _photos.SelectionChanged += async (_, _) =>
        {
            if (!_importing)
                await RefreshPhotoPreviewAsync();
        };
        _messagePreviewTabs.SelectedIndexChanged += async (_, _) =>
        {
            if (!_importing && _messagePreviewTabs.SelectedTab == _photosTab)
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
            if (picker.ShowDialog(this) != DialogResult.OK)
                return;
            _importing = true;
            _editorTabs.Enabled = false;
            _save.Enabled = false;
            _cancel.Enabled = false;
            UseWaitCursor = true;
            try
            {
                var draft = new FolderConfiguration { PendingFolder = _pending.Text.Trim(), SentFolder = _sent.Text.Trim() };
                var sourcePath = picker.FileName;
                var image = await Task.Run(() => FolderStore.ImportInlineImage(_root, draft, sourcePath));
                _inlineImages.Add(image);
                var index = _photos.Rows.Add(image.Path);
                _photos.Rows[index].Tag = image;
                _photos.CurrentCell = _photos.Rows[index].Cells[0];
                InsertPhoto(image);
                _messagePreviewTabs.SelectedTab = _photosTab;
            }
            catch (Exception exception)
            {
                AppLog.Write("A photo could not be added to the message.", exception);
                MessageBox.Show(this, exception.Message, "Photo could not be added", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _importing = false;
                _editorTabs.Enabled = true;
                _cancel.Enabled = true;
                UseWaitCursor = false;
                RefreshMessagePreview();
                await RefreshPhotoPreviewAsync();
            }
            _body.Focus();
        };
        _insertPhoto.Click += (_, _) =>
        {
            if (_photos.SelectedRows.Count > 0 && _photos.SelectedRows[0].Tag is InlineImageConfiguration image)
                InsertPhoto(image);
        };
        _removePhoto.Click += (_, _) =>
        {
            if (_photos.SelectedRows.Count == 0 || _photos.SelectedRows[0].Tag is not InlineImageConfiguration image)
                return;
            var body = Regex.Replace(_body.Text, @"<img\b(?:[^""'<>]|""[^""]*""|'[^']*')*>", tag =>
                Regex.Matches(tag.Value, @"(?<name>[^\s=<>/]+)(?:\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>/]+)))?")
                    .Any(attribute => attribute.Groups["name"].Value.Equals("src", StringComparison.OrdinalIgnoreCase)
                        && WebUtility.HtmlDecode(attribute.Groups["value"].Value) is var source
                        && source.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)
                        && source[4..].Equals(image.Id, StringComparison.Ordinal))
                    ? "" : tag.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            _inlineImages.Remove(image);
            _body.Text = body;
            _photos.Rows.Remove(_photos.SelectedRows[0]);
            RefreshMessagePreview();
        };
        _save.Click += async (_, _) =>
        {
            _schedules.EndEdit();
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
                Body = _body.Text,
                BodyIsHtml = _html.Checked,
                InlineImages = [.. _inlineImages],
                DateFormat = _dateFormat.Text,
                DateTimeZoneId = Convert.ToString(_dateTimeZone.SelectedItem) ?? "",
                SendWhenEmpty = _sendEmpty.Checked,
                WaitForFiles = _waitForFiles.Checked && !_sendEmpty.Checked,
                FileReadySeconds = (int)_readySeconds.Value,
                MaximumMessageSizeMb = (int)_maximumMb.Value,
                Schedules = schedules
            };
            _save.Enabled = false;
            _cancel.Enabled = false;
            _editorTabs.Enabled = false;
            _saving = true;
            UseWaitCursor = true;
            try
            {
                await Task.Run(() => FolderStore.SaveConfiguration(_root, updated));
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
                _save.Enabled = true;
                _cancel.Enabled = true;
                _editorTabs.Enabled = true;
                _saving = false;
                UseWaitCursor = false;
            }
        };
        RefreshMessagePreview();
    }

    private void InsertPhoto(InlineImageConfiguration image)
    {
        var position = _body.SelectionStart;
        if (!_html.Checked)
        {
            _body.Text = MessageTemplate.ConvertBodyToHtml(_body.Text);
            position = _body.TextLength;
            _html.Checked = true;
        }
        _body.SelectionStart = position;
        _body.SelectionLength = 0;
        _body.SelectedText = $"<img src=\"cid:{image.Id}\" alt=\"Photo\">";
        _body.Focus();
    }

    private async Task RefreshPhotoPreviewAsync()
    {
        var revision = ++_photoPreviewRevision;
        var previous = _photoPreview.Image;
        _photoPreview.Image = null;
        previous?.Dispose();
        var image = _photos.SelectedRows.Count == 0 ? null : _photos.SelectedRows[0].Tag as InlineImageConfiguration;
        _insertPhoto.Enabled = image is not null && _save.Enabled;
        _removePhoto.Enabled = image is not null;
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
            {
                thumbnail.Dispose();
                return;
            }
            _photoPreview.Image = thumbnail;
            _photoStatus.Text = image.Path;
        }
        catch (Exception exception)
        {
            if (!IsDisposed && revision == _photoPreviewRevision)
                _photoStatus.Text = "Photo preview unavailable: " + exception.Message;
        }
    }

    private void RefreshMessagePreview()
    {
        _previewBodyLabel.Text = _html.Checked ? "Body preview (HTML source)" : "Body preview";
        try
        {
            _ = FolderStore.ValidateInlineImageReferences(_body.Text, _html.Checked, _inlineImages.Select(image => image.Id));
            var preview = MessageTemplate.Render(new FolderConfiguration
            {
                Subject = _subject.Text.Trim(),
                Body = _body.Text,
                BodyIsHtml = _html.Checked,
                DateFormat = _dateFormat.Text,
                DateTimeZoneId = Convert.ToString(_dateTimeZone.SelectedItem) ?? ""
            }, DateTimeOffset.UtcNow);
            _previewSubject.Text = preview.Subject;
            _previewBody.Text = preview.Body;
            _previewStatus.Text = "Preview uses the current time and updates as you edit. Saved batch dates stay unchanged during recovery.";
            _save.Enabled = !_saving && !_importing;
            _addPhoto.Enabled = _save.Enabled;
            _insertPhoto.Enabled = _save.Enabled && _photos.SelectedRows.Count > 0;
        }
        catch (InvalidDataException exception)
        {
            _previewSubject.Clear();
            _previewBody.Clear();
            _previewStatus.Text = "Preview unavailable: " + exception.Message;
            _save.Enabled = false;
            _addPhoto.Enabled = false;
            _insertPhoto.Enabled = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _photoPreviewRevision++;
            _photoPreview.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
