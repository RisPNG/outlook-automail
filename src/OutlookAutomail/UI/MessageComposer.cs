using System.Text.Json;
using System.Globalization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace OutlookAutomail.UI;

internal sealed class MessageComposer : UserControl
{
    internal const string Origin = "https://composer.outlookautomail.invalid";
    private readonly WebView2 _browser;
    private readonly ToolStrip _tools;
    private readonly ToolStripComboBox _font;
    private readonly ToolStripComboBox _fontSize;
    private readonly Label _status;
    private readonly string _cacheDirectory;
    private readonly Dictionary<string, string> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolStripButton> _formatButtons = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _documentReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _initialization;
    private string _previewSubject = "";
    private string _previewHtml = "";
    private bool _syncingFormat;

    public string Body { get; private set; } = "";
    public bool BodyIsHtml { get; private set; }
    public bool IsReady { get; private set; }
    public bool IsLoading => _initialization is not null && !_initialization.IsCompleted;
    public bool CanSave => _initialization is null || IsReady;
    public event Action? BodyChanged;
    public event Action<string>? Notice;
    public event Action<byte[]>? ImagePasted;

    public MessageComposer() : this(Path.Combine(SettingsStore.RootDirectory, "WebView2")) { }

    internal MessageComposer(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
        AccessibleName = "Visual email composer and recipient preview";
        _tools = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, Enabled = false, CanOverflow = true, AccessibleName = "Message formatting" };
        var fonts = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 125, ToolTipText = "Font", AccessibleName = "Message font" };
        _font = fonts;
        fonts.Items.AddRange(["Arial", "Aptos", "Calibri", "Courier New", "Georgia", "Segoe UI", "Tahoma", "Times New Roman", "Verdana"]);
        fonts.SelectedItem = "Arial";
        fonts.SelectedIndexChanged += async (_, _) =>
        {
            if (!_syncingFormat)
                await ExecuteCommandAsync("fontName", fonts.Text);
        };
        var sizes = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 50, ToolTipText = "Font size in points", AccessibleName = "Message font size" };
        _fontSize = sizes;
        sizes.Items.AddRange(["8", "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "36", "48"]);
        sizes.SelectedItem = "11";
        sizes.SelectedIndexChanged += async (_, _) =>
        {
            if (!_syncingFormat)
                await ExecuteCommandAsync("fontSize", sizes.Text);
        };
        _tools.Items.AddRange([fonts, sizes, new ToolStripSeparator()]);
        foreach (var (text, command, title) in new[]
        {
            ("B", "bold", "Bold (Ctrl+B)"), ("I", "italic", "Italic (Ctrl+I)"), ("U", "underline", "Underline (Ctrl+U)"),
            ("Left", "justifyLeft", "Align left"), ("Center", "justifyCenter", "Align center"), ("Right", "justifyRight", "Align right"),
            ("Bullets", "insertUnorderedList", "Bulleted list"), ("Numbers", "insertOrderedList", "Numbered list"),
            ("Indent", "indent", "Increase indent"), ("Outdent", "outdent", "Decrease indent"),
            ("Undo", "undo", "Undo (Ctrl+Z)"), ("Redo", "redo", "Redo (Ctrl+Y)"), ("Clear", "removeFormat", "Clear text formatting")
        })
        {
            var button = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = title, AccessibleName = title };
            button.Click += async (_, _) => await ExecuteCommandAsync(command, null);
            _tools.Items.Add(button);
            _formatButtons.Add(command, button);
        }
        var color = new ToolStripButton("Color") { ToolTipText = "Text color", AccessibleName = "Text color" };
        color.Click += async (_, _) =>
        {
            using var picker = new ColorDialog { FullOpen = true };
            if (picker.ShowDialog(this) == DialogResult.OK)
                await ExecuteCommandAsync("foreColor", ColorTranslator.ToHtml(picker.Color));
        };
        var link = new ToolStripButton("Link") { ToolTipText = "Insert or change a link", AccessibleName = "Insert link" };
        link.Click += async (_, _) =>
        {
            using var dialog = new Form { Text = "Insert link", Icon = AppIcon.Default, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, ClientSize = new Size(470, 115), MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
            var label = new Label { Text = "Web or email address", AutoSize = true, Location = new Point(12, 12) };
            var address = new TextBox { Location = new Point(12, 34), Width = 446, AccessibleName = "Link address" };
            var ok = new Button { Text = "Insert", DialogResult = DialogResult.OK, Location = new Point(294, 75) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(383, 75) };
            dialog.Controls.AddRange([label, address, ok, cancel]);
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                if (!Uri.TryCreate(address.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "mailto"))
                    MessageBox.Show(this, "Enter an address beginning with https://, http://, or mailto:.", "Link address", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    await ExecuteCommandAsync("createLink", uri.AbsoluteUri);
            }
        };
        var unlink = new ToolStripButton("Unlink") { ToolTipText = "Remove link", AccessibleName = "Remove link" };
        unlink.Click += async (_, _) => await ExecuteCommandAsync("unlink", null);
        _tools.Items.AddRange([color, link, unlink]);
        _browser = new WebView2 { Dock = DockStyle.Fill, AccessibleName = "Email message editor" };
        _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "Open the Message tab to write and preview your email.", BackColor = SystemColors.Window, AccessibleName = "Email editor status" };
        Controls.Add(_browser);
        Controls.Add(_status);
        Controls.Add(_tools);
    }

    public void SetMessage(string body, bool isHtml)
    {
        Body = body;
        BodyIsHtml = isHtml;
    }

    public Task InitializeAsync() => _initialization ??= InitializeEditorAsync();

    private async Task InitializeEditorAsync()
    {
        _status.Text = "Loading email editor...";
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _cacheDirectory);
            if (IsDisposed)
                return;
            await _browser.EnsureCoreWebView2Async(environment);
            if (IsDisposed)
                return;
            var core = _browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.NavigationStarting += (_, args) => args.Cancel = args.Uri != Origin + "/index.html";
            core.FrameNavigationStarting += (_, args) => args.Cancel = args.Uri != "about:srcdoc";
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.ProcessFailed += (_, _) =>
            {
                IsReady = false;
                _tools.Enabled = false;
                Notice?.Invoke("The email editor stopped. Close this configuration and reopen it before saving.");
                BodyChanged?.Invoke();
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += ServeComposerResource;
            core.WebMessageReceived += (_, args) =>
            {
                if (args.Source != Origin + "/index.html")
                    return;
                using var message = JsonDocument.Parse(args.WebMessageAsJson);
                var value = message.RootElement;
                switch (value.GetProperty("type").GetString())
                {
                    case "ready":
                        _documentReady.TrySetResult();
                        break;
                    case "changed":
                        Body = MessageTemplate.ReadBodyFromEditor(value.GetProperty("html").GetString()!);
                        BodyIsHtml = true;
                        BodyChanged?.Invoke();
                        break;
                    case "notice":
                        Notice?.Invoke(value.GetProperty("message").GetString()!);
                        break;
                    case "format":
                        foreach (var (command, button) in _formatButtons)
                            button.Checked = value.TryGetProperty(command, out var state) && state.ValueKind == JsonValueKind.True;
                        _syncingFormat = true;
                        try
                        {
                            var font = value.GetProperty("fontName").GetString()!;
                            if (font.Length == 0)
                                _font.SelectedIndex = -1;
                            else
                            {
                                if (!_font.Items.Contains(font))
                                    _font.Items.Add(font);
                                _font.SelectedItem = font;
                            }
                            var points = value.GetProperty("fontSize").GetDouble();
                            if (points <= 0)
                                _fontSize.SelectedIndex = -1;
                            else
                            {
                                var size = points.ToString("0.##", CultureInfo.InvariantCulture);
                                if (!_fontSize.Items.Contains(size))
                                    _fontSize.Items.Add(size);
                                _fontSize.SelectedItem = size;
                            }
                        }
                        finally { _syncingFormat = false; }
                        break;
                    case "image":
                        var encoded = value.GetProperty("base64").GetString()!;
                        ImagePasted?.Invoke(Convert.FromBase64String(encoded));
                        break;
                }
            };
            core.Navigate(Origin + "/index.html");
            await _documentReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (IsDisposed)
                return;
            var result = await core.ExecuteScriptAsync("composer.load(" + JsonSerializer.Serialize(new
            {
                html = MessageTemplate.PrepareBodyForEditor(Body, BodyIsHtml),
                images = _images.Select(image => new { id = image.Key, url = Origin + "/images/" + Uri.EscapeDataString(image.Key) }),
                subject = _previewSubject,
                previewHtml = _previewHtml
            }) + ")");
            Body = MessageTemplate.ReadBodyFromEditor(JsonSerializer.Deserialize<string>(result)!);
            BodyIsHtml = true;
            IsReady = true;
            _status.Visible = false;
            _tools.Enabled = true;
            BodyChanged?.Invoke();
        }
        catch (Exception) when (IsDisposed) { }
        catch (Exception exception)
        {
            AppLog.Write("The visual email editor could not start.", exception);
            _status.Text = "The email editor could not start. Run the latest Setup to install its Microsoft Edge WebView2 component, then reopen the configuration.";
            Notice?.Invoke("Email editor unavailable: " + exception.Message);
            BodyChanged?.Invoke();
        }
    }

    private async void ServeComposerResource(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        using var deferral = args.GetDeferral();
        try
        {
            var uri = new Uri(args.Request.Uri);
            Stream? stream = null;
            var contentType = "text/plain";
            if (uri.GetLeftPart(UriPartial.Authority) == Origin && args.Request.Method == "GET")
            {
                var resource = uri.AbsolutePath switch
                {
                    "/index.html" => "MessageComposer.html",
                    "/MessageComposer.css" => "MessageComposer.css",
                    "/MessageComposer.js" => "MessageComposer.js",
                    _ => null
                };
                if (resource is not null)
                {
                    stream = typeof(MessageComposer).Assembly.GetManifestResourceStream("OutlookAutomail.UI." + resource);
                    contentType = resource.EndsWith(".html", StringComparison.Ordinal) ? "text/html; charset=utf-8"
                        : resource.EndsWith(".css", StringComparison.Ordinal) ? "text/css; charset=utf-8" : "text/javascript; charset=utf-8";
                }
                else if (uri.AbsolutePath.StartsWith("/images/", StringComparison.Ordinal)
                    && _images.TryGetValue(Uri.UnescapeDataString(uri.AbsolutePath[8..]), out var path))
                {
                    (stream, contentType) = await Task.Run(() =>
                    {
                        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        var type = FolderStore.ValidateInlineImage(file);
                        file.Position = 0;
                        var copy = new MemoryStream();
                        file.CopyTo(copy);
                        copy.Position = 0;
                        return ((Stream)copy, type);
                    });
                }
            }
            if (!IsDisposed)
                args.Response = _browser.CoreWebView2.Environment.CreateWebResourceResponse(stream ?? new MemoryStream(), stream is null ? 404 : 200, stream is null ? "Not found" : "OK", "Content-Type: " + contentType + "\r\nCache-Control: " + (contentType.StartsWith("image/", StringComparison.Ordinal) ? "private, max-age=3600" : "no-store") + "\r\nX-Content-Type-Options: nosniff\r\n");
            else
                stream?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (!IsDisposed)
            {
                args.Response = _browser.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(), 404, "Photo unavailable", "Content-Type: text/plain");
                Notice?.Invoke("A photo could not be displayed: " + exception.Message);
            }
        }
    }

    public void SetImages(IEnumerable<InlineImageConfiguration> images, string root)
    {
        _images.Clear();
        foreach (var image in images)
            _images.Add(image.Id, Path.GetFullPath(Path.Combine(root, image.Path)));
    }

    public async Task ShowPreviewAsync(string subject, string html)
    {
        _previewSubject = subject;
        _previewHtml = html;
        if (IsReady && !IsDisposed)
        {
            try
            {
                await _browser.ExecuteScriptAsync("composer.preview(" + JsonSerializer.Serialize(new
                {
                    subject,
                    html,
                    images = _images.Select(image => new { id = image.Key, url = Origin + "/images/" + Uri.EscapeDataString(image.Key) })
                }) + ")");
            }
            catch (Exception) when (IsDisposed) { }
        }
    }

    public async Task InsertTextAsync(string text)
    {
        if (IsReady && !IsDisposed)
        {
            try { await _browser.ExecuteScriptAsync("composer.insertText(" + JsonSerializer.Serialize(text) + ")"); }
            catch (Exception) when (IsDisposed) { }
        }
    }

    public async Task InsertImageAsync(string id)
    {
        if (IsReady && !IsDisposed)
        {
            try { await _browser.ExecuteScriptAsync("composer.insertImage(" + JsonSerializer.Serialize(id) + ")"); }
            catch (Exception) when (IsDisposed) { }
        }
    }

    public async Task RemoveImageAsync(string id)
    {
        if (IsReady && !IsDisposed)
        {
            try { await _browser.ExecuteScriptAsync("composer.removeImage(" + JsonSerializer.Serialize(id) + ")"); }
            catch (Exception) when (IsDisposed) { }
        }
    }

    private async Task ExecuteCommandAsync(string command, string? value)
    {
        if (IsReady && !IsDisposed)
        {
            try { await _browser.ExecuteScriptAsync("composer.command(" + JsonSerializer.Serialize(command) + "," + JsonSerializer.Serialize(value) + ")"); }
            catch (Exception) when (IsDisposed) { }
        }
    }

    public async Task CaptureBodyAsync()
    {
        if (IsReady)
        {
            var result = await _browser.ExecuteScriptAsync("composer.exportBody()");
            Body = MessageTemplate.ReadBodyFromEditor(JsonSerializer.Deserialize<string>(result)!);
            BodyIsHtml = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _documentReady.TrySetCanceled();
        base.Dispose(disposing);
    }
}
