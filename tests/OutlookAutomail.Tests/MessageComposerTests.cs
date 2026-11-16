using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using OutlookAutomail.UI;
using Xunit;

namespace OutlookAutomail.Tests;

public sealed class MessageComposerTests
{
    private static readonly byte[] Photo = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");

    [Fact]
    public Task PhotoInsertionRetainsTheCaretAndVisibleLineBreaksAndRoundTripsWithFormatting() => RunComposerAsync("test\n\nbest regards,\nrecipient", false, async session =>
    {
        var pasted = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Composer.ImagePasted += bytes => pasted.TrySetResult(bytes);
        await session.Browser.ExecuteScriptAsync("""
            (() => {
                const bytes = Uint8Array.from(atob('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII='), value => value.charCodeAt(0));
                const clipboard = new DataTransfer();
                clipboard.items.add(new File([bytes], 'pasted.png', { type: 'image/png' }));
                document.getElementById('editor').dispatchEvent(new ClipboardEvent('paste', { clipboardData: clipboard, bubbles: true, cancelable: true }));
            })()
            """);
        var pastedBytes = await pasted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Photo, pastedBytes);
        var photo = Path.Combine(session.Root, "photo.png");
        File.WriteAllBytes(photo, pastedBytes);
        session.Composer.SetImages([new InlineImageConfiguration { Id = "signature", Path = "photo.png" }], session.Root);
        await session.UpdatePreviewAsync("Documents", session.Composer.Body);
        await session.ReadAsync<bool>("""
            (() => {
                const editor = document.getElementById('editor');
                const text = editor.firstChild;
                const selection = window.getSelection();
                const range = document.createRange();
                editor.focus();
                range.setStart(text, 4);
                range.collapse(true);
                selection.removeAllRanges();
                selection.addRange(range);
                document.dispatchEvent(new Event('selectionchange'));
                editor.blur();
                return true;
            })()
            """);
        await session.Composer.InsertImageAsync("signature");
        await session.Composer.CaptureBodyAsync();

        var editor = await session.ReadAsync<JsonElement>("""
            (() => {
                const editor = document.getElementById('editor');
                const image = editor.querySelector('img');
                return { text: editor.innerText, before: image.previousSibling.textContent, source: image.src, breaks: editor.querySelectorAll('br').length };
            })()
            """);
        Assert.Contains("test\n\nbest regards,\nrecipient", editor.GetProperty("text").GetString());
        Assert.DoesNotContain("<br>", editor.GetProperty("text").GetString());
        Assert.Equal("test", editor.GetProperty("before").GetString());
        Assert.StartsWith(MessageComposer.Origin + "/images/", editor.GetProperty("source").GetString());
        Assert.True(editor.GetProperty("breaks").GetInt32() >= 3);
        Assert.True(session.Composer.BodyIsHtml);
        Assert.Contains("cid:signature", session.Composer.Body);
        Assert.DoesNotContain(MessageComposer.Origin, session.Composer.Body);

        await session.ReadAsync<bool>("""
            (() => {
                const editor = document.getElementById('editor');
                const range = document.createRange();
                range.setStart(editor.firstChild, 0);
                range.setEnd(editor.firstChild, 4);
                const selection = window.getSelection();
                selection.removeAllRanges();
                selection.addRange(range);
                document.dispatchEvent(new Event('selectionchange'));
                return true;
            })()
            """);
        await session.ClickFormattingAsync("Bold (Ctrl+B)");
        await session.Composer.CaptureBodyAsync();
        var saved = session.Composer.Body;
        var payload = JsonSerializer.Serialize(new { html = MessageTemplate.PrepareBodyForEditor(saved, true), images = new[] { new { id = "signature", url = MessageComposer.Origin + "/images/signature" } }, subject = "Documents", previewHtml = saved });
        await session.Browser.ExecuteScriptAsync("composer.load(" + payload + ")");
        var bold = await session.ReadAsync<bool>("""
            (() => {
                const editor = document.getElementById('editor');
                const walker = document.createTreeWalker(editor, NodeFilter.SHOW_TEXT);
                while (walker.nextNode())
                    if (walker.currentNode.data.includes('test'))
                        return Number(getComputedStyle(walker.currentNode.parentElement).fontWeight) >= 600;
                return false;
            })()
            """);
        Assert.True(bold);
        await session.UpdatePreviewAsync("Documents", saved);
        var received = await session.ReadRenderedPreviewAsync();
        Assert.Contains("best regards,", received.Text);
        Assert.DoesNotContain("<br>", received.Text);
        Assert.Equal(1, received.Images);
        Assert.Equal(1, received.ImageWidth);
        Assert.Equal(1, received.ImageHeight);
        await session.Composer.RemoveImageAsync("signature");
        await session.Composer.CaptureBodyAsync();
        Assert.DoesNotContain("cid:signature", session.Composer.Body);
        await session.ClickFormattingAsync("Undo (Ctrl+Z)");
        await session.Composer.CaptureBodyAsync();
        Assert.Equal("signature", Assert.Single(FolderStore.ValidateInlineImageReferences(session.Composer.Body, true, ["signature"])));
    });

    [Fact]
    public Task TypedDatesSurvivePartialFormattingAndFontSizeAppliesToNewTyping() => RunComposerAsync("", false, async session =>
    {
        const string expression = "{date:'<Documents &>' yyyy} / {{date}}";
        await session.Browser.ExecuteScriptAsync("document.getElementById('editor').focus(); document.execCommand('insertText', false, " + JsonSerializer.Serialize(expression) + ");");
        await session.ReadAsync<bool>("""
            (() => {
                const editor = document.getElementById('editor');
                const walker = document.createTreeWalker(editor, NodeFilter.SHOW_TEXT);
                while (walker.nextNode()) {
                    const text = walker.currentNode;
                    const start = text.data.indexOf('date');
                    if (start < 0) continue;
                    const range = document.createRange();
                    range.setStart(text, start);
                    range.setEnd(text, start + 4);
                    const selection = window.getSelection();
                    selection.removeAllRanges();
                    selection.addRange(range);
                    document.dispatchEvent(new Event('selectionchange'));
                    return true;
                }
                return false;
            })()
            """);
        await session.ClickFormattingAsync("Bold (Ctrl+B)");
        await session.Composer.CaptureBodyAsync();
        var configuration = new FolderConfiguration { Subject = "Documents {date}", Body = session.Composer.Body, BodyIsHtml = true, DateFormat = "dd/MM/yyyy", DateTimeZoneId = "UTC" };
        var rendered = MessageTemplate.Render(configuration, new DateTimeOffset(2026, 11, 16, 6, 0, 0, TimeSpan.Zero));
        Assert.Contains("&lt;Documents &amp;&gt; 2026", rendered.Body);
        Assert.Contains("{date}", rendered.Body);
        Assert.DoesNotContain("{date:", rendered.Body);
        await session.ReadAsync<bool>("""
            (() => {
                const editor = document.getElementById('editor');
                editor.focus();
                const range = document.createRange();
                range.selectNodeContents(editor);
                range.collapse(false);
                const selection = window.getSelection();
                selection.removeAllRanges();
                selection.addRange(range);
                document.dispatchEvent(new Event('selectionchange'));
                return true;
            })()
            """);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() => changed.TrySetResult();
        session.Composer.BodyChanged += Changed;
        session.Composer.Controls.OfType<ToolStrip>().Single().Items.OfType<ToolStripComboBox>().Single(item => item.AccessibleName == "Message font size").SelectedItem = "14";
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.Composer.BodyChanged -= Changed;
        await session.Browser.ExecuteScriptAsync("document.execCommand('insertText', false, ' sized text');");
        var size = await session.ReadAsync<double>("""
            (() => {
                const walker = document.createTreeWalker(document.getElementById('editor'), NodeFilter.SHOW_TEXT);
                while (walker.nextNode())
                    if (walker.currentNode.data.includes('sized text'))
                        return parseFloat(getComputedStyle(walker.currentNode.parentElement).fontSize) * 72 / 96;
                return 0;
            })()
            """);
        Assert.InRange(size, 13.9, 14.1);
        await session.Composer.CaptureBodyAsync();
        Assert.Contains("sized text", session.Composer.Body);
        await session.ClickFormattingAsync("Undo (Ctrl+Z)");
        await session.Composer.CaptureBodyAsync();
        Assert.DoesNotContain("sized text", session.Composer.Body);
        await session.ClickFormattingAsync("Redo (Ctrl+Y)");
        await session.Composer.CaptureBodyAsync();
        Assert.Contains("sized text", session.Composer.Body);
        var restoredSize = await session.ReadAsync<double>("""
            (() => {
                const walker = document.createTreeWalker(document.getElementById('editor'), NodeFilter.SHOW_TEXT);
                while (walker.nextNode())
                    if (walker.currentNode.data.includes('sized text'))
                        return parseFloat(getComputedStyle(walker.currentNode.parentElement).fontSize) * 72 / 96;
                return 0;
            })()
            """);
        Assert.InRange(restoredSize, 13.9, 14.1);
        await session.UpdatePreviewAsync(rendered.Subject, rendered.Body);
        var received = await session.ReadRenderedPreviewAsync();
        Assert.Contains("<Documents &> 2026", received.Text);
        Assert.Contains("/ {date}", received.Text);
    });

    [Fact]
    public Task ExistingFontSelectionCanBeChangedDirectlyToTheDisplayedDefaults() => RunComposerAsync("<p style='font-family:Times New Roman;font-size:20pt'>Imported text</p>", true, async session =>
    {
        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Browser.CoreWebView2.WebMessageReceived += (_, args) =>
        {
            using var message = JsonDocument.Parse(args.WebMessageAsJson);
            var data = message.RootElement;
            if (data.GetProperty("type").GetString() == "format"
                && data.GetProperty("fontName").GetString() == "Times New Roman"
                && data.GetProperty("fontSize").GetDouble() == 20)
                selected.TrySetResult();
        };
        await session.ReadAsync<bool>("""
            (() => {
                const editor = document.getElementById('editor');
                editor.focus();
                const range = document.createRange();
                range.selectNodeContents(editor.querySelector('p'));
                const selection = window.getSelection();
                selection.removeAllRanges();
                selection.addRange(range);
                document.dispatchEvent(new Event('selectionchange'));
                return true;
            })()
            """);
        await selected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var toolbar = session.Composer.Controls.OfType<ToolStrip>().Single();
        var fonts = toolbar.Items.OfType<ToolStripComboBox>().Single(item => item.AccessibleName == "Message font");
        var sizes = toolbar.Items.OfType<ToolStripComboBox>().Single(item => item.AccessibleName == "Message font size");
        Assert.Equal("Times New Roman", fonts.Text);
        Assert.Equal("20", sizes.Text);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() => changed.TrySetResult();
        session.Composer.BodyChanged += Changed;
        fonts.SelectedItem = "Arial";
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sizes.SelectedItem = "11";
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.Composer.BodyChanged -= Changed;
        var formatted = await session.ReadAsync<JsonElement>("""
            (() => {
                const walker = document.createTreeWalker(document.getElementById('editor'), NodeFilter.SHOW_TEXT);
                while (walker.nextNode()) {
                    if (!walker.currentNode.data.includes('Imported text')) continue;
                    const style = getComputedStyle(walker.currentNode.parentElement);
                    return { font: style.fontFamily, points: parseFloat(style.fontSize) * 72 / 96 };
                }
                return null;
            })()
            """);
        Assert.Contains("Arial", formatted.GetProperty("font").GetString());
        Assert.InRange(formatted.GetProperty("points").GetDouble(), 10.9, 11.1);
    });

    [Fact]
    public Task UnsafeMessageContentAndExternalNavigationStayIsolated() => RunComposerAsync("<p>Keep <strong>bold</strong></p><script>window.unsafe=true</script><img src='https://blocked.example.invalid/photo.png' onerror='window.unsafe=true'><img src='file:///C:/secret.png'><a href='javascript:alert(1)'>link</a>", true, async session =>
    {
        var editor = await session.ReadAsync<JsonElement>("""
            (() => {
                const editor = document.getElementById('editor');
                return { text: editor.innerText, unsafe: window.unsafe === true, dangerous: editor.querySelectorAll('script, iframe, object, img, [onerror], a[href^="javascript:"]').length, sandbox: document.getElementById('preview').getAttribute('sandbox') };
            })()
            """);
        Assert.Contains("Keep bold", editor.GetProperty("text").GetString());
        Assert.False(editor.GetProperty("unsafe").GetBoolean());
        Assert.Equal(0, editor.GetProperty("dangerous").GetInt32());
        Assert.Equal("", editor.GetProperty("sandbox").GetString());
        Assert.False(session.Browser.CoreWebView2.Settings.AreHostObjectsAllowed);
        Assert.DoesNotContain("blocked.example.invalid", session.Composer.Body);
        Assert.DoesNotContain("file:", session.Composer.Body);

        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var externalRequests = 0;
        session.Browser.CoreWebView2.WebMessageReceived += (_, args) =>
        {
            using var message = JsonDocument.Parse(args.WebMessageAsJson);
            if (message.RootElement.GetProperty("type").GetString() == "security-probe")
                blocked.TrySetResult();
        };
        session.Browser.CoreWebView2.WebResourceRequested += (_, args) =>
        {
            if (args.Request.Uri.StartsWith("https://blocked.example.invalid/", StringComparison.Ordinal))
                externalRequests++;
        };
        await session.Browser.ExecuteScriptAsync("""
            document.addEventListener('securitypolicyviolation', event => {
                if (event.blockedURI.startsWith('https://blocked.example.invalid/'))
                    window.chrome.webview.postMessage({ type: 'security-probe' });
            });
            const image = new Image();
            image.src = 'https://blocked.example.invalid/blocked.png';
            document.body.append(image);
            """);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, externalRequests);
        var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Browser.CoreWebView2.NavigationStarting += (_, args) =>
        {
            if (args.Uri.StartsWith("https://blocked.example.invalid/", StringComparison.Ordinal))
                navigation.TrySetResult(args.Cancel);
        };
        session.Browser.CoreWebView2.Navigate("https://blocked.example.invalid/page");
        Assert.True(await navigation.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.StartsWith(MessageComposer.Origin, session.Browser.CoreWebView2.Source);
    });

    private static async Task RunComposerAsync(string body, bool isHtml, Func<ComposerSession, Task> exercise)
    {
        var root = Path.Combine(Path.GetTempPath(), "OutlookAutomail-composer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        uint browserProcessId = 0;
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? error = null;
            using var form = new OffscreenForm { StartPosition = FormStartPosition.Manual, Location = new Point(-2400, -1600), ClientSize = new Size(1200, 700), ShowInTaskbar = false };
            using var composer = new MessageComposer(Path.Combine(root, "WebView2")) { Dock = DockStyle.Fill };
            form.Controls.Add(composer);
            var session = new ComposerSession(root, composer);
            form.Shown += async (_, _) =>
            {
                try
                {
                    composer.SetMessage(body, isHtml);
                    string? notice = null;
                    composer.Notice += message => notice = message;
                    await composer.InitializeAsync();
                    Assert.True(composer.IsReady, notice ?? "The real WebView2 composer did not initialize.");
                    await exercise(session);
                }
                catch (Exception exception) { error = exception; }
                finally
                {
                    browserProcessId = session.BrowserProcessId;
                    composer.Dispose();
                    if (session.BrowserProcessId != 0)
                    {
                        try { await session.BrowserExited.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
                        catch (Exception exception) { error = error is null ? exception : new AggregateException(error, exception); }
                    }
                    form.Close();
                }
            };
            try { Application.Run(form); }
            catch (Exception exception) { error = error is null ? exception : new AggregateException(error, exception); }
            finished.TrySetResult(error);
        })
        { IsBackground = true, Name = "Message composer integration test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var failure = await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "The composer STA thread did not exit.");
        if (browserProcessId != 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)browserProcessId);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException) { }
        }
        Directory.Delete(root, true);
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class OffscreenForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    private sealed class ComposerSession
    {
        public string Root { get; }
        public MessageComposer Composer { get; }
        public WebView2 Browser { get; }
        public CoreWebView2Frame? PreviewFrame { get; private set; }
        public uint BrowserProcessId { get; private set; }
        public TaskCompletionSource BrowserExited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool>? _previewNavigation;
        private ulong _previewNavigationId;

        public ComposerSession(string root, MessageComposer composer)
        {
            Root = root;
            Composer = composer;
            Browser = composer.Controls.OfType<WebView2>().Single();
            Browser.CoreWebView2InitializationCompleted += (_, args) =>
            {
                if (args.InitializationException is not null)
                    return;
                BrowserProcessId = Browser.CoreWebView2.BrowserProcessId;
                Browser.CoreWebView2.Environment.BrowserProcessExited += (_, exited) =>
                {
                    if (exited.BrowserProcessId == BrowserProcessId)
                        BrowserExited.TrySetResult();
                };
                Browser.CoreWebView2.FrameCreated += (_, frame) =>
                {
                    PreviewFrame = frame.Frame;
                    PreviewFrame.NavigationStarting += (_, navigation) =>
                    {
                        _previewNavigationId = navigation.NavigationId;
                        _previewNavigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    };
                    PreviewFrame.NavigationCompleted += (_, navigation) =>
                    {
                        if (navigation.NavigationId == _previewNavigationId)
                            _previewNavigation?.TrySetResult(navigation.IsSuccess);
                    };
                };
            };
        }

        public async Task<T> ReadAsync<T>(string script) => JsonSerializer.Deserialize<T>(await Browser.ExecuteScriptAsync(script))!;

        public async Task<(string Text, int Images, double ImageWidth, double ImageHeight)> ReadRenderedPreviewAsync()
        {
            using var targets = JsonDocument.Parse(await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Target.getTargets", "{}"));
            var target = Assert.Single(targets.RootElement.GetProperty("targetInfos").EnumerateArray(), item => item.GetProperty("type").GetString() == "iframe" && item.GetProperty("url").GetString() == "about:srcdoc");
            using var attachment = JsonDocument.Parse(await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Target.attachToTarget", JsonSerializer.Serialize(new { targetId = target.GetProperty("targetId").GetString(), flatten = true })));
            var sessionId = attachment.RootElement.GetProperty("sessionId").GetString()!;
            string snapshotJson;
            try { snapshotJson = await Browser.CoreWebView2.CallDevToolsProtocolMethodForSessionAsync(sessionId, "DOMSnapshot.captureSnapshot", "{\"computedStyles\":[]}"); }
            finally { await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Target.detachFromTarget", JsonSerializer.Serialize(new { sessionId })); }
            using var snapshot = JsonDocument.Parse(snapshotJson);
            var strings = snapshot.RootElement.GetProperty("strings").EnumerateArray().Select(item => item.GetString()!).ToArray();
            var preview = Assert.Single(snapshot.RootElement.GetProperty("documents").EnumerateArray(), document => strings[document.GetProperty("documentURL").GetInt32()] == "about:srcdoc");
            var text = string.Join("\n", preview.GetProperty("layout").GetProperty("text").EnumerateArray().Select(index => index.GetInt32()).Where(index => index >= 0).Select(index => strings[index]).Where(value => value.Length > 0));
            var nodes = preview.GetProperty("nodes");
            var imageNodes = nodes.GetProperty("nodeName").EnumerateArray().Select((name, index) => (Name: strings[name.GetInt32()], Index: index)).Where(node => node.Name == "IMG").Select(node => node.Index).ToHashSet();
            var layout = preview.GetProperty("layout");
            var imageWidth = layout.GetProperty("nodeIndex").EnumerateArray().Select((node, index) => (Node: node.GetInt32(), Index: index)).Where(node => imageNodes.Contains(node.Node)).Select(node => layout.GetProperty("bounds")[node.Index][2].GetDouble()).DefaultIfEmpty().Max();
            var imageHeight = layout.GetProperty("nodeIndex").EnumerateArray().Select((node, index) => (Node: node.GetInt32(), Index: index)).Where(node => imageNodes.Contains(node.Node)).Select(node => layout.GetProperty("bounds")[node.Index][3].GetDouble()).DefaultIfEmpty().Max();
            return (text, imageNodes.Count, imageWidth, imageHeight);
        }

        public async Task UpdatePreviewAsync(string subject, string html)
        {
            Assert.NotNull(PreviewFrame);
            if (_previewNavigation is not null)
                Assert.True(await _previewNavigation.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            var before = await ReadAsync<string>("document.getElementById('preview').srcdoc");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Starting(object? _, CoreWebView2NavigationStartingEventArgs args) => started.TrySetResult();
            PreviewFrame.NavigationStarting += Starting;
            try
            {
                await Composer.ShowPreviewAsync(subject, html);
                var after = await ReadAsync<string>("document.getElementById('preview').srcdoc");
                if (before != after || started.Task.IsCompleted)
                {
                    await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.NotNull(_previewNavigation);
                    Assert.True(await _previewNavigation.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                }
            }
            finally { PreviewFrame.NavigationStarting -= Starting; }
        }

        public async Task ClickFormattingAsync(string accessibleName)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed() => changed.TrySetResult();
            Composer.BodyChanged += Changed;
            try
            {
                Composer.Controls.OfType<ToolStrip>().Single().Items.OfType<ToolStripButton>().Single(item => item.AccessibleName == accessibleName).PerformClick();
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { Composer.BodyChanged -= Changed; }
        }
    }
}
