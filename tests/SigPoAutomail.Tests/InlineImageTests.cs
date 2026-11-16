using System.Security.Cryptography;
using Xunit;

namespace SigPoAutomail.Tests;

public sealed class InlineImageTests
{
    private static readonly byte[] Photo = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");

    [Fact]
    public void ImportedPhotoIsReusableAndRoundTripsThroughSharedConfiguration()
    {
        using var folder = new TestFolder();
        var image = AddPhoto(folder);
        folder.Save();

        var loaded = FolderStore.LoadConfiguration(folder.Root);
        var saved = Assert.Single(loaded.InlineImages);
        Assert.Equal(image.Id, saved.Id);
        Assert.Equal(image.Path, saved.Path);
        var path = FolderStore.ResolveInlineImage(folder.Root, loaded, saved);
        Assert.Equal(Photo, File.ReadAllBytes(path));
        Assert.Equal("image/png", FolderStore.ValidateInlineImage(path));
        Assert.Empty(Directory.GetFiles(Path.Combine(folder.Root, "To send")));
    }

    [Fact]
    public void RemovingTheLastPhotoDoesNotAllowItsReusableFolderToBecomeAPendingQueue()
    {
        using var folder = new TestFolder();
        var image = AddPhoto(folder);
        folder.Save();
        var path = FolderStore.ResolveInlineImage(folder.Root, folder.Configuration, image);
        folder.Configuration.InlineImages.Clear();
        folder.Configuration.Body = "No embedded photos.";
        folder.Save();
        folder.Configuration.PendingFolder = FolderStore.MessageAssetsFolder;

        Assert.Throws<InvalidDataException>(folder.Save);
        Assert.Equal(Photo, File.ReadAllBytes(path));
        Assert.Equal("To send", FolderStore.LoadConfiguration(folder.Root).PendingFolder);
    }

    [Theory]
    [InlineData("Message assets", "Sent")]
    [InlineData("Message assets/pending", "Sent")]
    [InlineData("To send", "Message assets")]
    public void ImportRejectsAssetFolderOverlapBeforeCopying(string pending, string sent)
    {
        using var folder = new TestFolder();
        var source = Path.Combine(folder.Root, "source.png");
        File.WriteAllBytes(source, Photo);
        folder.Configuration.PendingFolder = pending;
        folder.Configuration.SentFolder = sent;

        Assert.Throws<InvalidDataException>(() => FolderStore.ImportInlineImage(folder.Root, folder.Configuration, source));

        Assert.False(Directory.Exists(Path.Combine(folder.Root, FolderStore.MessageAssetsFolder)));
        Assert.Equal(Photo, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("To send/photo.png")]
    [InlineData("Sent/photo.png")]
    [InlineData(".automail/photo.png")]
    public void ConfiguredPhotoMustRemainInsideTheSharedAssetFolder(string path)
    {
        using var folder = new TestFolder();
        folder.Configuration.InlineImages.Add(new() { Path = path });
        Assert.Throws<InvalidDataException>(folder.Save);
    }

    [Fact]
    public void DanglingDuplicateAndPlainTextImageReferencesAreRejected()
    {
        using var folder = new TestFolder();
        folder.Configuration.BodyIsHtml = true;
        folder.Configuration.Body = "<img src=\"cid:missing\">";
        Assert.Throws<InvalidDataException>(folder.Save);
        var image = AddPhoto(folder);
        folder.Configuration.InlineImages.Add(new() { Id = image.Id, Path = image.Path });
        Assert.Throws<InvalidDataException>(folder.Save);
        folder.Configuration.InlineImages.RemoveAt(1);
        folder.Configuration.BodyIsHtml = false;
        Assert.Throws<InvalidDataException>(folder.Save);
    }

    [Fact]
    public void InvalidImageDoesNotLeaveAnImportedAsset()
    {
        using var folder = new TestFolder();
        var source = Path.Combine(folder.Root, "photo.png");
        File.WriteAllText(source, "This is not an image.");

        Assert.Throws<InvalidDataException>(() => FolderStore.ImportInlineImage(folder.Root, folder.Configuration, source));

        Assert.False(Directory.Exists(Path.Combine(folder.Root, FolderStore.MessageAssetsFolder)));
    }

    [Theory]
    [InlineData("/9j/2QA=", "image/jpeg")]
    [InlineData("R0lGODlhAQABAAAAAA==", "image/gif")]
    [InlineData("Qk0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "image/bmp")]
    public void RecognizedRasterHeadersDetermineMimeType(string encoded, string mime)
    {
        using var stream = new MemoryStream(Convert.FromBase64String(encoded));
        Assert.Equal(mime, FolderStore.ValidateInlineImage(stream));
    }

    [Fact]
    public async Task BodyPhotoIsFrozenAndSentWithDocumentsButSharedSourceIsNeverArchived()
    {
        using var folder = new TestFolder();
        var image = AddPhoto(folder);
        folder.Save();
        folder.AddFile("one.pdf", "first purchase order");
        folder.AddFile("two.pdf", "second purchase order");
        var mail = new FakeMail();

        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        Assert.Equal(1, mail.SendCount);
        var photo = Assert.Single(mail.LastMessage!.Attachments, attachment => attachment.ContentId is not null);
        Assert.Equal(image.Id, photo.ContentId);
        Assert.Equal("image/png", photo.ContentType);
        Assert.Equal(Photo, File.ReadAllBytes(photo.Path));
        Assert.Equal(Photo, File.ReadAllBytes(FolderStore.ResolveInlineImage(folder.Root, folder.Configuration, image)));
        var run = Assert.Single(FolderStore.GetHistory(folder.Root));
        var recorded = Assert.Single(run.Attachments, attachment => attachment.ContentId is not null);
        Assert.Equal(image.Path, recorded.SourcePath);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Photo)), recorded.Sha256);
        Assert.Equal(RunStatus.Archived, run.Status);
        var archived = Directory.GetFiles(Path.Combine(folder.Root, "Sent"), "*", SearchOption.AllDirectories);
        Assert.Equal(new[] { "one.pdf", "two.pdf" }, archived.Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public async Task UnreferencedConfiguredPhotoIsNotReadOrAttached()
    {
        using var folder = new TestFolder();
        var image = AddPhoto(folder);
        folder.Configuration.InlineImages.Add(new() { Path = Path.Combine(FolderStore.MessageAssetsFolder, "unused-missing.png") });
        folder.Save();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail();

        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        Assert.Equal(image.Id, Assert.Single(mail.LastMessage!.Attachments, attachment => attachment.ContentId is not null).ContentId);
    }

    [Fact]
    public async Task SignatureAloneDoesNotTurnAnEmptyScheduledFolderIntoAnEmail()
    {
        using var folder = new TestFolder();
        var image = AddPhoto(folder);
        folder.Save();
        var mail = new FakeMail();

        await folder.Engine(mail).CheckSchedulesAsync(default);

        Assert.Equal(0, mail.CreateCount);
        Assert.Equal(0, mail.SendCount);
        var run = Assert.Single(FolderStore.GetHistory(folder.Root));
        Assert.Equal(RunStatus.SkippedEmpty, run.Status);
        Assert.Empty(run.Attachments);
        Assert.True(File.Exists(FolderStore.ResolveInlineImage(folder.Root, folder.Configuration, image)));
    }

    [Fact]
    public async Task ExplicitSendWhenEmptySupportsAnImageOnlyMessage()
    {
        using var folder = new TestFolder();
        AddPhoto(folder);
        folder.Configuration.SendWhenEmpty = true;
        folder.Save();
        var mail = new FakeMail();

        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        Assert.NotNull(Assert.Single(mail.LastMessage!.Attachments).ContentId);
        Assert.Equal(1, mail.SendCount);
        Assert.Equal(RunStatus.Archived, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
    }

    [Fact]
    public async Task PhotoEncodingCountsTowardTheMessageLimitBeforeAnyBatchIsPublished()
    {
        using var folder = new TestFolder();
        AddPhoto(folder, [.. Photo, .. new byte[800_000]]);
        folder.Configuration.MaximumMessageSizeMb = 1;
        folder.Save();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail();

        await Assert.ThrowsAsync<InvalidDataException>(() => folder.Engine(mail).RunNowAsync(folder.Root, default));

        Assert.Equal(0, mail.CreateCount);
        Assert.Equal(0, mail.SendCount);
        Assert.Empty(FolderStore.GetHistory(folder.Root));
        Assert.True(File.Exists(Path.Combine(folder.Root, "To send", "po.pdf")));
    }

    [Fact]
    public async Task PhotoReadinessDelayLeavesDocumentsUnclaimed()
    {
        using var folder = new TestFolder();
        var image = AddPhoto(folder);
        folder.Configuration.FileReadySeconds = 30;
        folder.Save();
        folder.AddFile("po.pdf", "purchase order");
        File.SetLastWriteTimeUtc(FolderStore.ResolveInlineImage(folder.Root, folder.Configuration, image), folder.Clock.GetUtcNow().UtcDateTime);
        var mail = new FakeMail();
        var engine = folder.Engine(mail);

        await engine.RunNowAsync(folder.Root, default);

        Assert.Equal(0, mail.CreateCount);
        Assert.Empty(FolderStore.GetHistory(folder.Root));
        Assert.True(File.Exists(Path.Combine(folder.Root, "To send", "po.pdf")));
        folder.Clock.Advance(TimeSpan.FromSeconds(31));
        await engine.RunNowAsync(folder.Root, default);
        Assert.Equal(1, mail.SendCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedPreparationUsesFrozenPhotoAfterSharedSourceChangesOrDisappears(bool removeSource)
    {
        using var folder = new TestFolder();
        var image = AddPhoto(folder);
        folder.Save();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail { UploadError = new IOException("Preparation interrupted") };
        var engine = folder.Engine(mail);
        await Assert.ThrowsAsync<IOException>(() => engine.RunNowAsync(folder.Root, default));
        var originalBody = mail.LastMessage!.Body;
        var path = FolderStore.ResolveInlineImage(folder.Root, folder.Configuration, image);
        if (removeSource)
            File.Delete(path);
        else
            File.WriteAllBytes(path, [.. Photo, 123]);
        folder.Configuration.Body = "Changed message";
        folder.Configuration.InlineImages = [];
        folder.Save();
        mail.UploadError = null;

        await engine.RunNowAsync(folder.Root, default);

        Assert.Equal(1, mail.CreateCount);
        Assert.Equal(1, mail.SendCount);
        Assert.Equal(originalBody, mail.LastMessage.Body);
        var attachment = Assert.Single(mail.LastMessage.Attachments, attachment => attachment.ContentId is not null);
        Assert.Equal(Photo, File.ReadAllBytes(attachment.Path));
        Assert.Equal(!removeSource, File.Exists(path));
        if (!removeSource)
            Assert.Equal(new byte[] { 123 }, File.ReadAllBytes(path)[Photo.Length..]);
    }

    [Fact]
    public async Task CorruptedFrozenPhotoBlocksRecoveryBeforeSending()
    {
        using var folder = new TestFolder();
        AddPhoto(folder);
        folder.Save();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail { UploadError = new IOException("Preparation interrupted") };
        var engine = folder.Engine(mail);
        await Assert.ThrowsAsync<IOException>(() => engine.RunNowAsync(folder.Root, default));
        var attachment = Assert.Single(mail.LastMessage!.Attachments, attachment => attachment.ContentId is not null);
        var changed = File.ReadAllBytes(attachment.Path);
        changed[^1] ^= 1;
        File.WriteAllBytes(attachment.Path, changed);
        mail.UploadError = null;

        await Assert.ThrowsAsync<InvalidDataException>(() => engine.RunNowAsync(folder.Root, default));

        Assert.Equal(0, mail.SendCount);
        Assert.Equal(RunStatus.Preparing, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
    }

    [Fact]
    public async Task RejectedRetryCannotIntroduceAnImageOutsideTheFrozenBatch()
    {
        using var folder = new TestFolder();
        AddPhoto(folder);
        folder.Save();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail { SendError = new MailSubmissionRejectedException("Recipients", "Correct the recipient.") };
        var engine = folder.Engine(mail);
        await Assert.ThrowsAsync<MailSubmissionRejectedException>(() => engine.RunNowAsync(folder.Root, default));
        AddPhoto(folder);
        folder.Save();
        mail.SendError = null;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => engine.RunNowAsync(folder.Root, default));

        Assert.Contains("original frozen photos", error.Message);
        Assert.Equal(0, mail.DeleteCount);
        Assert.Equal(1, mail.CreateCount);
        Assert.Equal(1, mail.SendCount);
        Assert.Equal(RunStatus.Rejected, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedRetryCanKeepOrRemoveItsOriginalFrozenPhoto(bool removeReference)
    {
        using var folder = new TestFolder();
        var image = AddPhoto(folder);
        folder.Save();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail { SendError = new MailSubmissionRejectedException("Recipients", "Correct the recipient.") };
        var engine = folder.Engine(mail);
        await Assert.ThrowsAsync<MailSubmissionRejectedException>(() => engine.RunNowAsync(folder.Root, default));
        File.WriteAllBytes(FolderStore.ResolveInlineImage(folder.Root, folder.Configuration, image), [.. Photo, 123]);
        if (removeReference)
        {
            folder.Configuration.Body = "A corrected message without the signature.";
            folder.Configuration.BodyIsHtml = false;
            folder.Configuration.InlineImages = [];
        }
        else
            folder.Configuration.Body = $"<p>Corrected message</p><img src='cid:{image.Id}'>";
        folder.Save();
        mail.SendError = null;

        await engine.RunNowAsync(folder.Root, default);

        Assert.Equal(1, mail.DeleteCount);
        Assert.Equal(2, mail.CreateCount);
        Assert.Equal(2, mail.SendCount);
        if (removeReference)
            Assert.DoesNotContain(mail.LastMessage!.Attachments, attachment => attachment.ContentId is not null);
        else
            Assert.Equal(Photo, File.ReadAllBytes(Assert.Single(mail.LastMessage!.Attachments, attachment => attachment.ContentId is not null).Path));
        Assert.Equal(RunStatus.Archived, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
    }

    [Fact]
    public void InterruptedImageSnapshotDoesNotPublishAnIncompleteBatchOrLeaveTemporaryCopies()
    {
        using var folder = new TestFolder();
        var run = new RunRecord { Transport = FolderStore.CurrentTransport };
        using var image = new InterruptedImageCopy(Photo);

        Assert.Throws<IOException>(() => FolderStore.CreateBatch(folder.Root, run, new Dictionary<string, Stream> { ["photo.png"] = image }));

        Assert.Empty(FolderStore.GetHistory(folder.Root));
        Assert.Empty(Directory.GetDirectories(Path.Combine(folder.Root, ".automail", "preparing")));
        Assert.False(Directory.Exists(Path.Combine(folder.Root, ".automail", "runs", run.Id)));
    }

    [Fact]
    public void ImportAlsoChecksSavedFolderPathsBeforeWritingAnAsset()
    {
        using var folder = new TestFolder();
        var configurationPath = Path.Combine(folder.Root, "config.yml");
        var yaml = new YamlDotNet.RepresentationModel.YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(configurationPath)));
        var mapping = (YamlDotNet.RepresentationModel.YamlMappingNode)yaml.Documents[0].RootNode;
        mapping.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("pending_folder")] = new YamlDotNet.RepresentationModel.YamlScalarNode(FolderStore.MessageAssetsFolder);
        using (var writer = new StreamWriter(configurationPath))
            yaml.Save(writer, false);
        var edited = folder.Configuration;
        edited.PendingFolder = "New pending";
        var source = Path.Combine(folder.Root, "photo.png");
        File.WriteAllBytes(source, Photo);

        Assert.Throws<InvalidDataException>(() => FolderStore.ImportInlineImage(folder.Root, edited, source));

        Assert.False(Directory.Exists(Path.Combine(folder.Root, FolderStore.MessageAssetsFolder)));
    }

    private static InlineImageConfiguration AddPhoto(TestFolder folder, byte[]? bytes = null)
    {
        var source = Path.Combine(folder.Root, Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(source, bytes ?? Photo);
        var image = FolderStore.ImportInlineImage(folder.Root, folder.Configuration, source);
        folder.Configuration.InlineImages.Add(image);
        folder.Configuration.BodyIsHtml = true;
        folder.Configuration.Body = $"<p>Please find the purchase orders attached.</p><img src=\"cid:{image.Id}\" alt=\"Signature\">";
        return image;
    }

    private sealed class InterruptedImageCopy(byte[] bytes) : MemoryStream(bytes)
    {
        public override void CopyTo(Stream destination, int bufferSize)
        {
            destination.WriteByte(137);
            throw new IOException("The network write was interrupted.");
        }
    }
}

public sealed class ProcessingPauseTests
{
    [Fact]
    public async Task UpdatePauseRefusesAnActiveSendAndPreventsFutureOperationsUntilResumed()
    {
        using var folder = new TestFolder();
        folder.AddFile("first.pdf", "first order");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mail = new FakeMail { OnSend = async () => { entered.TrySetResult(); await release.Task; } };
        var engine = folder.Engine(mail);
        var sending = engine.RunNowAsync(folder.Root, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(engine.TryPause());
        }
        finally { release.TrySetResult(); }
        await sending;
        Assert.True(engine.TryPause());
        folder.AddFile("second.pdf", "second order");

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunNowAsync(folder.Root, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RetryArchiveOrReconcileAsync(folder.Root, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ConfirmSentAsync(folder.Root, Guid.NewGuid().ToString("N"), default));
        await engine.CheckSchedulesAsync(default);
        Assert.Equal(1, mail.SendCount);
        Assert.True(File.Exists(Path.Combine(folder.Root, "To send", "second.pdf")));

        engine.Resume();
        mail.OnSend = null;
        mail.SubmissionStatus = MailSubmissionStatus.Missing;
        await engine.RunNowAsync(folder.Root, default);
        Assert.Equal(2, mail.SendCount);
    }
}
