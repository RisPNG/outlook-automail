using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Xunit;

namespace SigPoAutomail.Tests;

public sealed class AutomationTests
{
    [Fact]
    public async Task MissedSchedulesAcrossDaysCoalesceAndAnotherMachineDoesNotRepeatThem()
    {
        using var folder = new TestFolder();
        folder.Configuration.SendWhenEmpty = true;
        folder.Configuration.Schedules.Add(new ScheduleConfiguration { Time = "07:00", TimeZoneId = "UTC" });
        folder.Save();
        var mail = new FakeMail();
        var first = folder.Engine(mail);
        await first.CheckSchedulesAsync(default);
        await folder.Engine(mail).CheckSchedulesAsync(default);
        await first.CheckSchedulesAsync(default);

        Assert.Equal(1, mail.SendCount);
        var run = Assert.Single(FolderStore.GetHistory(folder.Root));
        Assert.Equal(RunStatus.Archived, run.Status);
        Assert.Equal(2, run.CoveredOccurrences.Count);
        Assert.Equal(folder.Clock.GetUtcNow().Date.AddHours(7), run.OccurrenceUtc!.Value.UtcDateTime);
    }

    [Fact]
    public async Task EmptyOccurrenceIsSkippedAndLateFilesWaitForTheNextOccurrence()
    {
        using var folder = new TestFolder();
        var mail = new FakeMail();
        var engine = folder.Engine(mail);
        await engine.CheckSchedulesAsync(default);
        folder.AddFile("purchase-order.pdf", "late attachment");
        await engine.CheckSchedulesAsync(default);

        Assert.Equal(0, mail.SendCount);
        Assert.Equal(RunStatus.SkippedEmpty, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
        folder.Clock.Advance(TimeSpan.FromDays(1));
        await engine.CheckSchedulesAsync(default);
        Assert.Equal(1, mail.SendCount);
    }

    [Fact]
    public async Task WaitingForFilesKeepsTheMissedOccurrenceAvailable()
    {
        using var folder = new TestFolder();
        folder.Configuration.WaitForFiles = true;
        folder.Save();
        var mail = new FakeMail();
        var engine = folder.Engine(mail);
        await engine.CheckSchedulesAsync(default);
        Assert.Empty(FolderStore.GetHistory(folder.Root));
        folder.AddFile("po.pdf", "purchase order");
        await engine.CheckSchedulesAsync(default);
        Assert.Equal(1, mail.SendCount);
    }

    [Fact]
    public async Task RegisteredPrimaryHasPriorityAndBackupTakesOverWithoutRepeatingOnReturn()
    {
        using var folder = new TestFolder();
        folder.Configuration.WaitForFiles = true;
        folder.Save();
        var primarySettings = folder.Settings();
        var backupSettings = folder.Settings();
        FolderStore.RegisterWorker(folder.Root, primarySettings.InstallationId, "Primary");
        FolderStore.RegisterWorker(folder.Root, backupSettings.InstallationId, "Backup");
        var primaryMail = new FakeMail();
        var backupMail = new FakeMail();
        var primary = new AutomationEngine(primarySettings, primaryMail, folder.Clock);
        var backup = new AutomationEngine(backupSettings, backupMail, folder.Clock);
        await primary.CheckSchedulesAsync(default);
        folder.AddFile("po.pdf", "purchase order");
        await backup.CheckSchedulesAsync(default);
        Assert.Equal(0, backupMail.SendCount);
        folder.Clock.Advance(TimeSpan.FromSeconds(91));
        await backup.CheckSchedulesAsync(default);
        Assert.Equal(1, backupMail.SendCount);
        await primary.CheckSchedulesAsync(default);
        Assert.Equal(0, primaryMail.SendCount);
    }

    [Fact]
    public async Task TwoMachinesCannotSubmitTheSameActiveBatch()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "purchase order");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mail = new FakeMail { OnSend = async () => { entered.SetResult(); await release.Task; } };
        var firstRun = folder.Engine(mail).RunNowAsync(folder.Root, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAsync<FolderBusyException>(() => folder.Engine(mail).RunNowAsync(folder.Root, default));
        }
        finally { release.SetResult(); }
        await firstRun;
        Assert.Equal(1, mail.SendCount);
    }

    [Fact]
    public async Task UncertainSendIsNeverAutomaticallyResentAndOriginalAccountCanReconcileInStandby()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "purchase order");
        var primarySettings = folder.Settings();
        var backupSettings = folder.Settings();
        FolderStore.RegisterWorker(folder.Root, primarySettings.InstallationId, "Primary");
        FolderStore.RegisterWorker(folder.Root, backupSettings.InstallationId, "Backup");
        var backupMail = new FakeMail { Account = new("bob@example.com", "bob@example.com", "Bob"), SendError = new IOException("Connection lost") };
        var backup = new AutomationEngine(backupSettings, backupMail, folder.Clock);
        await Assert.ThrowsAsync<IOException>(() => backup.RunNowAsync(folder.Root, default));
        var primaryMail = new FakeMail { Account = new("alice@example.com", "alice@example.com", "Alice") };
        var primary = new AutomationEngine(primarySettings, primaryMail, folder.Clock);
        await primary.CheckSchedulesAsync(default);
        Assert.Equal(0, primaryMail.SendCount);
        Assert.Equal(RunStatus.NeedsReview, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
        backupMail.SubmissionStatus = MailSubmissionStatus.Sent;
        await backup.CheckSchedulesAsync(default);
        Assert.Equal(1, backupMail.SendCount);
        Assert.Equal(RunStatus.Archived, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
    }

    [Fact]
    public async Task MissingDraftAfterAnUncertainSendStillRequiresReview()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail { SendError = new IOException("Response lost") };
        var engine = folder.Engine(mail);
        await Assert.ThrowsAsync<IOException>(() => engine.RunNowAsync(folder.Root, default));
        mail.SendError = null;
        mail.SubmissionStatus = MailSubmissionStatus.Missing;
        await engine.RunNowAsync(folder.Root, default);
        Assert.Equal(1, mail.SendCount);
        var run = Assert.Single(FolderStore.GetHistory(folder.Root));
        Assert.Equal(RunStatus.NeedsReview, run.Status);
        await engine.ConfirmSentAsync(folder.Root, run.Id, default);
        Assert.Equal(RunStatus.Archived, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
        Assert.Equal(1, mail.SendCount);
    }

    [Fact]
    public async Task ArchiveFailureRetriesOnlyArchiving()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "purchase order");
        string? blocker = null;
        var mail = new FakeMail();
        mail.OnSend = () =>
        {
            blocker = Path.Combine(folder.Root, "Sent", folder.Clock.GetUtcNow().ToString("yyyy-MM-dd") + "_" + mail.LastMessage!.BatchId);
            File.WriteAllText(blocker, "blocks directory creation");
            return Task.CompletedTask;
        };
        var engine = folder.Engine(mail);
        await Assert.ThrowsAsync<IOException>(() => engine.RunNowAsync(folder.Root, default));
        Assert.Equal(RunStatus.Sent, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
        File.Delete(blocker!);
        await engine.RetryArchiveOrReconcileAsync(folder.Root, default);
        Assert.Equal(1, mail.SendCount);
        Assert.Equal(RunStatus.Archived, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
        Assert.Equal("purchase order", File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(folder.Root, "Sent"), "po.pdf", SearchOption.AllDirectories))));
    }

    [Fact]
    public async Task InterruptedPreparationReusesTheSavedDraftWithoutDuplicatingAttachments()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail { UploadError = new IOException("Upload interrupted") };
        var engine = folder.Engine(mail);
        await Assert.ThrowsAsync<IOException>(() => engine.RunNowAsync(folder.Root, default));
        Assert.Empty(Directory.GetFiles(Path.Combine(folder.Root, "To send")));
        mail.UploadError = null;
        await engine.RunNowAsync(folder.Root, default);
        Assert.Equal(0, mail.DeleteCount);
        Assert.Equal(1, mail.CreateCount);
        Assert.Equal(1, mail.SendCount);
    }

    [Fact]
    public async Task ChangedStagedAttachmentStopsSendingAndPreservesTheBatch()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "original purchase order");
        var mail = new FakeMail { UploadError = new IOException("Upload interrupted") };
        var engine = folder.Engine(mail);
        await Assert.ThrowsAsync<IOException>(() => engine.RunNowAsync(folder.Root, default));
        var staged = Assert.Single(Directory.GetFiles(Path.Combine(folder.Root, ".automail", "runs"), "po.pdf", SearchOption.AllDirectories));
        File.WriteAllText(staged, "changed purchase order");
        mail.UploadError = null;
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.RunNowAsync(folder.Root, default));
        Assert.Equal(0, mail.SendCount);
        Assert.True(File.Exists(staged));
    }

    [Fact]
    public async Task OversizedBatchIsRejectedBeforeFilesAreClaimedOrDraftIsCreated()
    {
        using var folder = new TestFolder();
        folder.Configuration.MaximumMessageSizeMb = 1;
        folder.Save();
        var file = Path.Combine(folder.Root, "To send", "po.pdf");
        File.WriteAllBytes(file, new byte[800_000]);
        var mail = new FakeMail();
        await Assert.ThrowsAsync<InvalidDataException>(() => folder.Engine(mail).RunNowAsync(folder.Root, default));
        Assert.Equal(0, mail.CreateCount);
        Assert.True(File.Exists(file));
        Assert.Empty(FolderStore.GetHistory(folder.Root));
    }

    [Fact]
    public async Task OpenOrRecentlyChangedFileDefersTheWholeBatch()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail();
        var engine = folder.Engine(mail);
        using (var writing = new FileStream(Path.Combine(folder.Root, "To send", "po.pdf"), FileMode.Open, FileAccess.Write, FileShare.None))
            await engine.RunNowAsync(folder.Root, default);
        Assert.Equal(0, mail.CreateCount);
        Assert.Empty(FolderStore.GetHistory(folder.Root));
        folder.Configuration.FileReadySeconds = 30;
        folder.Save();
        File.SetLastWriteTimeUtc(Path.Combine(folder.Root, "To send", "po.pdf"), folder.Clock.GetUtcNow().UtcDateTime);
        await engine.RunNowAsync(folder.Root, default);
        Assert.Equal(0, mail.SendCount);
        folder.Clock.Advance(TimeSpan.FromSeconds(31));
        await engine.RunNowAsync(folder.Root, default);
        Assert.Equal(1, mail.SendCount);
    }

    [Fact]
    public async Task ClearRejectionWaitsForManualRetryAndKeepsImmutableAttemptHistory()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail { SendError = new MailSubmissionRejectedException("UnresolvedRecipients", "Outlook could not resolve the recipients.") };
        var engine = folder.Engine(mail);
        await Assert.ThrowsAsync<MailSubmissionRejectedException>(() => engine.RunNowAsync(folder.Root, default));
        Assert.Equal(RunStatus.Rejected, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
        await engine.CheckSchedulesAsync(default);
        Assert.Equal(1, mail.SendCount);
        folder.Configuration.To = ["corrected@example.com"];
        folder.Save();
        mail.SendError = null;
        await engine.RunNowAsync(folder.Root, default);
        Assert.Equal(2, mail.SendCount);
        Assert.Equal("corrected@example.com", Assert.Single(mail.LastMessage!.To));
        var run = Assert.Single(FolderStore.GetHistory(folder.Root));
        Assert.Equal(RunStatus.Archived, run.Status);
        var runDirectory = Path.Combine(folder.Root, ".automail", "runs", run.Id);
        Assert.Equal(2, Directory.GetFiles(runDirectory, "submission-*.json").Length);
        Assert.Single(Directory.GetFiles(runDirectory, "rejected-*.json"));
    }

    [Fact]
    public async Task SubmissionMarkerOverridesAStalePreparingRecord()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail { SendError = new IOException("Response lost") };
        var engine = folder.Engine(mail);
        await Assert.ThrowsAsync<IOException>(() => engine.RunNowAsync(folder.Root, default));
        var run = Assert.Single(FolderStore.GetHistory(folder.Root));
        run.Status = RunStatus.Preparing;
        run.AccountId = "stale-worker";
        run.DraftId = "stale-draft";
        File.WriteAllText(Path.Combine(folder.Root, ".automail", "runs", run.Id, "run.json"), JsonSerializer.Serialize(run, TestFolder.JsonOptions));
        mail.SendError = null;
        await engine.RunNowAsync(folder.Root, default);
        Assert.Equal(1, mail.SendCount);
        Assert.Equal(RunStatus.NeedsReview, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
    }

    [Fact]
    public async Task SentMarkerOverridesStaleUncertaintyAndNeverResends()
    {
        using var folder = new TestFolder();
        folder.AddFile("run.json", "attachment named like execution metadata");
        var mail = new FakeMail();
        var engine = folder.Engine(mail);
        await engine.RunNowAsync(folder.Root, default);
        var run = Assert.Single(FolderStore.GetHistory(folder.Root));
        run.Status = RunStatus.Preparing;
        File.WriteAllText(Path.Combine(folder.Root, ".automail", "runs", run.Id, "run.json"), JsonSerializer.Serialize(run, TestFolder.JsonOptions));
        await engine.CheckSchedulesAsync(default);
        Assert.Equal(1, mail.SendCount);
        Assert.Equal(RunStatus.Archived, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
    }

    [Fact]
    public async Task DeterministicOccurrenceAndFrozenManifestProtectNewFilesFromStaleScheduleState()
    {
        using var folder = new TestFolder();
        folder.AddFile("first.pdf", "first purchase order");
        var mail = new FakeMail();
        var engine = folder.Engine(mail);
        await engine.RunNowAsync(folder.Root, default);
        folder.AddFile("later.pdf", "a newly arriving purchase order");
        var statePath = Path.Combine(folder.Root, ".automail", "state.json");
        using var state = JsonDocument.Parse(File.ReadAllText(statePath));
        var stale = new
        {
            folderId = folder.Configuration.Id,
            workers = state.RootElement.GetProperty("workers").Clone(),
            completedThrough = new Dictionary<string, DateTimeOffset>()
        };
        File.WriteAllText(statePath, JsonSerializer.Serialize(stale));
        await engine.CheckSchedulesAsync(default);
        Assert.Equal(1, mail.SendCount);
        Assert.True(File.Exists(Path.Combine(folder.Root, "To send", "later.pdf")));
        Assert.Single(FolderStore.GetHistory(folder.Root));
    }

    [Fact]
    public async Task BackupCanRebuildAnUnsubmittedDraftWithoutAccessToTheOriginalMailbox()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "purchase order");
        var primaryMail = new FakeMail { UploadError = new IOException("Primary went offline") };
        await Assert.ThrowsAsync<IOException>(() => folder.Engine(primaryMail).RunNowAsync(folder.Root, default));
        var backupMail = new FakeMail { Account = new("backup@example.com", "backup@example.com", "Backup") };
        await folder.Engine(backupMail).RunNowAsync(folder.Root, default);
        Assert.Equal(0, primaryMail.SendCount);
        Assert.Equal(0, backupMail.DeleteCount);
        Assert.Equal(1, backupMail.SendCount);
        Assert.Equal("backup@example.com", Assert.Single(FolderStore.GetHistory(folder.Root)).AccountAddress);
    }

    [Fact]
    public async Task MissingInitialProgressFileCanRecoverFromTheFrozenManifest()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail { UploadError = new IOException("Interrupted preparation") };
        var engine = folder.Engine(mail);
        await Assert.ThrowsAsync<IOException>(() => engine.RunNowAsync(folder.Root, default));
        var run = Assert.Single(FolderStore.GetHistory(folder.Root));
        File.Delete(Path.Combine(folder.Root, ".automail", "runs", run.Id, "run.json"));
        mail.UploadError = null;
        await engine.RunNowAsync(folder.Root, default);
        Assert.Equal(1, mail.SendCount);
        Assert.Equal(RunStatus.Archived, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
    }

    [Fact]
    public async Task ActiveMembershipSurvivesStaleStateAndResumesClaimedFilesBeforeNewFiles()
    {
        using var folder = new TestFolder();
        folder.AddFile("first.pdf", "first purchase order");
        var mail = new FakeMail { UploadError = new IOException("Worker interrupted") };
        var engine = folder.Engine(mail);
        await Assert.ThrowsAsync<IOException>(() => engine.RunNowAsync(folder.Root, default));
        var statePath = Path.Combine(folder.Root, ".automail", "state.json");
        using var state = JsonDocument.Parse(File.ReadAllText(statePath));
        File.WriteAllText(statePath, JsonSerializer.Serialize(new
        {
            folderId = folder.Configuration.Id,
            workers = state.RootElement.GetProperty("workers").Clone(),
            completedThrough = new Dictionary<string, DateTimeOffset>()
        }));
        folder.AddFile("later.pdf", "new purchase order");
        mail.UploadError = null;
        await engine.RunNowAsync(folder.Root, default);
        Assert.Equal("first.pdf", Assert.Single(mail.LastMessage!.Attachments).Name);
        Assert.Equal(1, mail.SendCount);
        Assert.True(File.Exists(Path.Combine(folder.Root, "To send", "later.pdf")));
        Assert.Empty(Directory.GetFiles(Path.Combine(folder.Root, ".automail", "active"), "*.json"));
    }

    [Fact]
    public async Task QueuedOutlookMessageKeepsItsFilesAndBlocksNewBatchesUntilSentIsConfirmed()
    {
        using var folder = new TestFolder();
        folder.AddFile("first.pdf", "first purchase order");
        var mail = new FakeMail { StatusAfterSend = MailSubmissionStatus.Queued };
        var engine = folder.Engine(mail);
        await engine.RunNowAsync(folder.Root, default);
        var queued = Assert.Single(FolderStore.GetHistory(folder.Root));
        Assert.Equal(RunStatus.Queued, queued.Status);
        Assert.True(File.Exists(Assert.Single(mail.LastMessage!.Attachments).Path));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(folder.Root, "Sent")));
        using (var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder.Root, ".automail", "state.json"))))
            Assert.Empty(state.RootElement.GetProperty("completedThrough").EnumerateObject());
        folder.AddFile("later.pdf", "later purchase order");
        folder.Clock.Advance(TimeSpan.FromDays(1));
        await engine.RunNowAsync(folder.Root, default);
        Assert.Equal(1, mail.SendCount);
        Assert.Equal(RunStatus.Queued, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
        mail.SubmissionStatus = MailSubmissionStatus.Sent;
        await engine.RetryArchiveOrReconcileAsync(folder.Root, default);
        Assert.Equal(1, mail.SendCount);
        Assert.Equal(RunStatus.Archived, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
        Assert.True(File.Exists(Path.Combine(folder.Root, "To send", "later.pdf")));
    }

    [Fact]
    public async Task UnknownSendCanBeFoundInOutboxWithoutSubmittingAgain()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail { SendError = new IOException("Outlook became unavailable during Send") };
        var engine = folder.Engine(mail);
        await Assert.ThrowsAsync<IOException>(() => engine.RunNowAsync(folder.Root, default));
        mail.SubmissionStatus = MailSubmissionStatus.Queued;
        mail.SendError = null;
        await engine.RetryArchiveOrReconcileAsync(folder.Root, default);
        Assert.Equal(RunStatus.Queued, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
        Assert.Equal(1, mail.SendCount);
        mail.SubmissionStatus = MailSubmissionStatus.Sent;
        await engine.RetryArchiveOrReconcileAsync(folder.Root, default);
        Assert.Equal(RunStatus.Archived, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
        Assert.Equal(1, mail.SendCount);
    }

    [Fact]
    public async Task QueuedProofOverridesAStaleDraftRecordAndPreventsPreparationOrSend()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail { StatusAfterSend = MailSubmissionStatus.Queued };
        var engine = folder.Engine(mail);
        await engine.RunNowAsync(folder.Root, default);
        var run = Assert.Single(FolderStore.GetHistory(folder.Root));
        run.Status = RunStatus.Preparing;
        File.WriteAllText(Path.Combine(folder.Root, ".automail", "runs", run.Id, "run.json"), JsonSerializer.Serialize(run, TestFolder.JsonOptions));
        await engine.RunNowAsync(folder.Root, default);
        Assert.Equal(RunStatus.Queued, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
        Assert.Equal(1, mail.CreateCount);
        Assert.Equal(1, mail.SendCount);
        Assert.Equal(0, mail.DeleteCount);
    }

    [Fact]
    public async Task MissingQueuedMessageRequiresReviewInsteadOfCreatingAReplacement()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail { StatusAfterSend = MailSubmissionStatus.Queued };
        var engine = folder.Engine(mail);
        await engine.RunNowAsync(folder.Root, default);
        mail.SubmissionStatus = MailSubmissionStatus.Missing;
        await engine.RunNowAsync(folder.Root, default);
        Assert.Equal(RunStatus.NeedsReview, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
        Assert.Equal(1, mail.SendCount);
        Assert.Equal(1, mail.CreateCount);
        Assert.Equal(0, mail.DeleteCount);
    }

    [Fact]
    public async Task UserCanExplicitlyConfirmAQueuedMessageAfterCheckingSentItems()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail { StatusAfterSend = MailSubmissionStatus.Queued };
        var engine = folder.Engine(mail);
        await engine.RunNowAsync(folder.Root, default);
        var run = Assert.Single(FolderStore.GetHistory(folder.Root));
        await engine.ConfirmSentAsync(folder.Root, run.Id, default);
        Assert.Equal(RunStatus.Archived, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
        Assert.Equal(1, mail.SendCount);
    }

    [Fact]
    public async Task DifferentOutlookAccountCannotLookUpOrReplaceAnOriginalQueuedMessage()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "purchase order");
        var original = new FakeMail { StatusAfterSend = MailSubmissionStatus.Queued };
        await folder.Engine(original).RunNowAsync(folder.Root, default);
        var backup = new FakeMail { Account = new("backup@example.com", "backup@example.com", "Backup") };
        await folder.Engine(backup).RunNowAsync(folder.Root, default);
        Assert.Equal(0, backup.LookupCount);
        Assert.Equal(0, backup.CreateCount);
        Assert.Equal(0, backup.SendCount);
        var queued = Assert.Single(FolderStore.GetHistory(folder.Root));
        Assert.Equal(RunStatus.Queued, queued.Status);
        Assert.Equal("sender@example.com", queued.AccountId);
    }

    [Fact]
    public async Task OldConnectorBatchIsRejectedBeforeItsStatusOrMessageIdCanBeUsed()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail { StatusAfterSend = MailSubmissionStatus.Queued };
        var engine = folder.Engine(mail);
        await engine.RunNowAsync(folder.Root, default);
        var run = Assert.Single(FolderStore.GetHistory(folder.Root));
        var path = Path.Combine(folder.Root, ".automail", "runs", run.Id, "run.json");
        var oldRecord = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        oldRecord.Remove("transport");
        oldRecord["status"] = "accepted";
        oldRecord["draftId"] = "a-previous-connectors-message-id";
        File.WriteAllText(path, oldRecord.ToJsonString());
        var lookups = mail.LookupCount;
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.RunNowAsync(folder.Root, default));
        Assert.Equal(lookups, mail.LookupCount);
        Assert.Equal(1, mail.SendCount);
    }

    [Fact]
    public async Task DraftFoundAlreadyQueuedDuringPreparationIsNotDeletedOrResubmitted()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail { UploadError = new IOException("Interrupted preparation") };
        var engine = folder.Engine(mail);
        await Assert.ThrowsAsync<IOException>(() => engine.RunNowAsync(folder.Root, default));
        mail.UploadError = null;
        mail.SubmissionStatus = MailSubmissionStatus.Queued;
        await engine.RunNowAsync(folder.Root, default);
        Assert.Equal(RunStatus.Queued, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
        Assert.Equal(1, mail.CreateCount);
        Assert.Equal(0, mail.DeleteCount);
        Assert.Equal(0, mail.SendCount);
        mail.SubmissionStatus = MailSubmissionStatus.Sent;
        await engine.RetryArchiveOrReconcileAsync(folder.Root, default);
        Assert.Equal(RunStatus.Archived, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
    }

    [Fact]
    public async Task DraftSavedBeforeALostCreateResultIsFoundByItsPersistedBatchIdentity()
    {
        using var folder = new TestFolder();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail { CreateErrorAfterSave = new IOException("The saved draft result was lost") };
        var engine = folder.Engine(mail);
        await Assert.ThrowsAsync<IOException>(() => engine.RunNowAsync(folder.Root, default));
        var interrupted = Assert.Single(FolderStore.GetHistory(folder.Root));
        Assert.Equal(interrupted.Id, interrupted.DraftId);
        mail.CreateErrorAfterSave = null;
        await engine.RunNowAsync(folder.Root, default);
        Assert.Equal(1, mail.CreateCount);
        Assert.Equal(0, mail.DeleteCount);
        Assert.Equal(1, mail.SendCount);
        Assert.Equal(RunStatus.Archived, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
    }

    [Fact]
    public async Task ManualBatchRendersBothMessageFieldsInTheConfiguredDateTimeZone()
    {
        using var folder = new TestFolder();
        folder.Clock.Advance(TimeSpan.FromHours(12));
        folder.Configuration.Schedules = [];
        folder.Configuration.DateFormat = "dd MMMM yyyy";
        folder.Configuration.DateTimeZoneId = "Singapore Standard Time";
        folder.Configuration.Subject = "Purchase orders for {date}";
        folder.Configuration.Body = "Created {date:ddd, dd MMM yyyy}. Reference {{date}}.";
        folder.Save();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail();

        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        Assert.Equal("Purchase orders for 04 January 2030", mail.LastMessage!.Subject);
        Assert.Equal("Created Fri, 04 Jan 2030. Reference {date}.", mail.LastMessage.Body);
        var run = Assert.Single(FolderStore.GetHistory(folder.Root));
        Assert.Equal(folder.Clock.GetUtcNow(), run.CreatedUtc);
        Assert.Null(run.OccurrenceUtc);
        Assert.Equal(mail.LastMessage.Subject, run.Subject);
        Assert.Equal(mail.LastMessage.Body, run.Body);
    }

    [Fact]
    public async Task ScheduledCatchUpRendersTheCreationDateInsteadOfTheMissedOccurrenceDate()
    {
        using var folder = new TestFolder();
        folder.Configuration.Schedules[0].Time = "23:00";
        folder.Configuration.Subject = "Purchase orders {date:yyyy-MM-dd}";
        folder.Configuration.Body = "Prepared on {date}.";
        folder.Save();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail();

        await folder.Engine(mail).CheckSchedulesAsync(default);

        var run = Assert.Single(FolderStore.GetHistory(folder.Root));
        Assert.Equal(DateTimeOffset.Parse("2030-01-02T23:00:00Z"), run.OccurrenceUtc);
        Assert.Equal("Purchase orders 2030-01-03", mail.LastMessage!.Subject);
        Assert.Equal("Prepared on 03/01/2030.", mail.LastMessage.Body);
    }

    [Fact]
    public async Task HtmlBatchKeepsMarkupAndEncodesOnlyDateReplacements()
    {
        using var folder = new TestFolder();
        folder.Configuration.Subject = "Purchase orders {date:yyyy-MM-dd}";
        folder.Configuration.BodyIsHtml = true;
        folder.Configuration.Body = "<p>{date:'<strong>&'}</p><style>.po { color: red; }</style>";
        folder.Save();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail();

        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        Assert.True(mail.LastMessage!.IsHtml);
        Assert.Equal("Purchase orders 2030-01-03", mail.LastMessage.Subject);
        Assert.Equal("<p>&lt;strong&gt;&amp;</p><style>.po { color: red; }</style>", mail.LastMessage.Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryKeepsRenderedMessageAcrossDaysConfigurationChangesAndBackupTakeover(bool useBackup)
    {
        using var folder = new TestFolder();
        folder.Configuration.Subject = "Purchase orders {date:yyyy-MM-dd}";
        folder.Configuration.Body = "Prepared on {date}.";
        folder.Save();
        folder.AddFile("po.pdf", "purchase order");
        var original = new FakeMail { UploadError = new IOException("Preparation interrupted") };
        var engine = folder.Engine(original);
        await Assert.ThrowsAsync<IOException>(() => engine.RunNowAsync(folder.Root, default));
        folder.Clock.Advance(TimeSpan.FromDays(2));
        folder.Configuration.Subject = "Changed subject {date}";
        folder.Configuration.Body = "Changed body {date}";
        folder.Configuration.DateFormat = "yyyy/MM/dd";
        folder.Configuration.DateTimeZoneId = "Singapore Standard Time";
        folder.Save();
        original.UploadError = null;
        var mail = useBackup
            ? new FakeMail { Account = new("backup@example.com", "backup@example.com", "Backup") }
            : original;

        await (useBackup ? folder.Engine(mail) : engine).RunNowAsync(folder.Root, default);

        Assert.Equal("Purchase orders 2030-01-03", mail.LastMessage!.Subject);
        Assert.Equal("Prepared on 03/01/2030.", mail.LastMessage.Body);
        var run = Assert.Single(FolderStore.GetHistory(folder.Root));
        Assert.Equal("Purchase orders 2030-01-03", run.Subject);
        Assert.Equal("Prepared on 03/01/2030.", run.Body);
        Assert.Equal(DateTimeOffset.Parse("2030-01-03T08:00:00Z"), run.CreatedUtc);
        Assert.Equal(RunStatus.Archived, run.Status);
    }

    [Fact]
    public async Task ExplicitRejectedRetryUsesEditedTemplatesWithTheOriginalCreationDate()
    {
        using var folder = new TestFolder();
        folder.Configuration.Subject = "Purchase orders {date:yyyy-MM-dd}";
        folder.Configuration.Body = "Original {date}";
        folder.Save();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail { SendError = new MailSubmissionRejectedException("UnresolvedRecipients", "Recipient correction required.") };
        var engine = folder.Engine(mail);
        await Assert.ThrowsAsync<MailSubmissionRejectedException>(() => engine.RunNowAsync(folder.Root, default));
        folder.Clock.Advance(TimeSpan.FromDays(2));
        folder.Configuration.Subject = "Revised {date:dd MMM yyyy}";
        folder.Configuration.Body = "Updated {date}";
        folder.Configuration.DateFormat = "yyyy/MM/dd";
        folder.Configuration.To = ["corrected@example.com"];
        folder.Save();
        mail.SendError = null;

        await engine.RunNowAsync(folder.Root, default);

        Assert.Equal("Revised 03 Jan 2030", mail.LastMessage!.Subject);
        Assert.Equal("Updated 2030/01/03", mail.LastMessage.Body);
        Assert.Equal("corrected@example.com", Assert.Single(mail.LastMessage.To));
        Assert.Equal(1, mail.DeleteCount);
        Assert.Equal(2, mail.CreateCount);
        Assert.Equal(2, mail.SendCount);
        Assert.Equal(DateTimeOffset.Parse("2030-01-03T08:00:00Z"), Assert.Single(FolderStore.GetHistory(folder.Root)).CreatedUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpandedMessageFieldExceedingTheLimitIsRejectedBeforeClaimingFiles(bool subject)
    {
        using var folder = new TestFolder();
        folder.Configuration.MaximumMessageSizeMb = 1;
        var template = string.Concat(Enumerable.Repeat("{date}", subject ? 110_000 : 55_000));
        if (subject)
            folder.Configuration.Subject = template;
        else
            folder.Configuration.Body = template;
        folder.Save();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail();

        await Assert.ThrowsAsync<InvalidDataException>(() => folder.Engine(mail).RunNowAsync(folder.Root, default));

        Assert.Equal(0, mail.CreateCount);
        Assert.Equal(0, mail.SendCount);
        Assert.True(File.Exists(Path.Combine(folder.Root, "To send", "po.pdf")));
        Assert.Empty(FolderStore.GetHistory(folder.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedExpandedRetryPreservesTheExistingDraftAndSubmissionHistory(bool subject)
    {
        using var folder = new TestFolder();
        folder.Configuration.MaximumMessageSizeMb = 1;
        folder.Configuration.Subject = "Original subject {date}";
        folder.Configuration.Body = "Original {date}";
        folder.Save();
        folder.AddFile("po.pdf", "purchase order");
        var mail = new FakeMail { SendError = new MailSubmissionRejectedException("UnresolvedRecipients", "Recipient correction required.") };
        var engine = folder.Engine(mail);
        await Assert.ThrowsAsync<MailSubmissionRejectedException>(() => engine.RunNowAsync(folder.Root, default));
        var template = string.Concat(Enumerable.Repeat("{date}", subject ? 110_000 : 55_000));
        if (subject)
            folder.Configuration.Subject = template;
        else
            folder.Configuration.Body = template;
        folder.Save();
        mail.SendError = null;

        await Assert.ThrowsAsync<InvalidDataException>(() => engine.RunNowAsync(folder.Root, default));

        Assert.Equal(0, mail.DeleteCount);
        Assert.Equal(1, mail.CreateCount);
        Assert.Equal(1, mail.SendCount);
        var run = Assert.Single(FolderStore.GetHistory(folder.Root));
        Assert.Equal(RunStatus.Rejected, run.Status);
        Assert.Equal("Original subject 03/01/2030", run.Subject);
        Assert.Equal("Original 03/01/2030", run.Body);
        Assert.Equal(1, run.SubmissionAttempt);
    }

    [Theory]
    [InlineData("../outside", "Sent")]
    [InlineData(".AUTOMAIL/inbox", "Sent")]
    [InlineData("To send", "To send/Sent")]
    [InlineData("To send/child", "To send")]
    public void ConfigurationCannotEscapeTheRootOrMixPendingAndSent(string pending, string sent)
    {
        using var folder = new TestFolder();
        folder.Configuration.PendingFolder = pending;
        folder.Configuration.SentFolder = sent;
        Assert.Throws<InvalidDataException>(folder.Save);
    }

    [Fact]
    public void MissingStableYamlScheduleIdentityIsRejected()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Root, "config.yml");
        var content = File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("- id:", StringComparison.Ordinal));
        File.WriteAllLines(path, content);
        Assert.Throws<InvalidDataException>(() => FolderStore.LoadConfiguration(folder.Root));
    }

    [Fact]
    public void EditingConfigurationCannotInterruptAnActiveWorker()
    {
        using var folder = new TestFolder();
        using var ownership = new FileStream(Path.Combine(folder.Root, ".automail", "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<FolderBusyException>(folder.Save);
        Assert.Equal(folder.Configuration.Id, FolderStore.LoadConfiguration(folder.Root).Id);
    }

    [Fact]
    public void AmbiguousDaylightTimeRunsOnceAtTheEarlierInstantAndMissingClockTimeIsSkipped()
    {
        using var folder = new TestFolder();
        folder.Configuration.Schedules[0].TimeZoneId = "Eastern Standard Time";
        folder.Configuration.Schedules[0].Time = "01:30";
        folder.Configuration.ActiveFromUtc = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var occurrence = SchedulePlanner.GetDueOccurrence(folder.Configuration, new Dictionary<string, DateTimeOffset>(), DateTimeOffset.Parse("2026-11-01T07:00:00Z"));
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T05:30:00Z"), occurrence!.DueUtc);
        var completed = new Dictionary<string, DateTimeOffset> { [occurrence.ScheduleId] = occurrence.DueUtc };
        Assert.Null(SchedulePlanner.GetDueOccurrence(folder.Configuration, completed, DateTimeOffset.Parse("2026-11-01T08:00:00Z")));
        folder.Configuration.ActiveFromUtc = DateTimeOffset.Parse("2026-03-08T00:00:00Z");
        folder.Configuration.Schedules[0].Time = "02:30";
        Assert.Null(SchedulePlanner.GetDueOccurrence(folder.Configuration, new Dictionary<string, DateTimeOffset>(), DateTimeOffset.Parse("2026-03-08T12:00:00Z")));
    }
}

internal sealed class TestFolder : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "SIG-Automail-tests", Guid.NewGuid().ToString("N"));
    public ManualClock Clock { get; } = new(DateTimeOffset.Parse("2030-01-03T08:00:00Z"));
    public FolderConfiguration Configuration { get; }

    public TestFolder()
    {
        Directory.CreateDirectory(Root);
        Configuration = new FolderConfiguration
        {
            To = ["buyer@example.com"],
            FileReadySeconds = 0,
            ActiveFromUtc = Clock.GetUtcNow().AddDays(-3),
            Schedules = [new ScheduleConfiguration { Time = "06:00", TimeZoneId = "UTC" }]
        };
        Save();
    }

    public void Save() => FolderStore.SaveConfiguration(Root, Configuration);
    public void AddFile(string name, string content) => File.WriteAllText(Path.Combine(Root, "To send", name), content);
    public AppSettings Settings() => new() { Folders = [new FolderSubscription { RootPath = Root }] };
    public AutomationEngine Engine(IMailClient mail) => new(Settings(), mail, Clock);

    public void Dispose()
    {
        var path = Path.GetFullPath(Root);
        var parent = Path.Combine(Path.GetTempPath(), "SIG-Automail-tests") + Path.DirectorySeparatorChar;
        if (!path.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The test cleanup path is invalid.");
        Directory.Delete(path, true);
    }
}

internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset current = now;
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan duration) => current += duration;
}

internal sealed class FakeMail : IMailClient
{
    public MailAccount? Account { get; set; } = new("sender@example.com", "sender@example.com", "Sender");
    public Exception? SendError { get; set; }
    public Exception? UploadError { get; set; }
    public Exception? CreateErrorAfterSave { get; set; }
    public Func<Task>? OnSend { get; set; }
    public int CreateCount { get; private set; }
    public int DeleteCount { get; private set; }
    public int SendCount { get; private set; }
    public int LookupCount { get; private set; }
    public MailMessage? LastMessage { get; private set; }
    public MailSubmissionStatus SubmissionStatus { get; set; } = MailSubmissionStatus.Draft;
    public MailSubmissionStatus StatusAfterSend { get; set; } = MailSubmissionStatus.Sent;
    public Task<MailAccount?> GetAccountAsync(CancellationToken cancellationToken) => Task.FromResult(Account);
    public Task<string> CreateDraftAsync(MailMessage message, CancellationToken cancellationToken)
    {
        LastMessage = message;
        CreateCount++;
        SubmissionStatus = MailSubmissionStatus.Draft;
        if (CreateErrorAfterSave is not null)
            return Task.FromException<string>(CreateErrorAfterSave);
        return Task.FromResult(message.BatchId);
    }
    public Task AddAttachmentsAsync(string draftId, IReadOnlyList<MailAttachment> attachments, CancellationToken cancellationToken)
    {
        foreach (var attachment in attachments)
            Assert.Equal(attachment.Length, new FileInfo(attachment.Path).Length);
        return UploadError is null ? Task.CompletedTask : Task.FromException(UploadError);
    }
    public async Task SendDraftAsync(string draftId, CancellationToken cancellationToken)
    {
        SendCount++;
        if (OnSend is not null)
            await OnSend();
        if (SendError is not null)
            throw SendError;
        SubmissionStatus = StatusAfterSend;
    }
    public Task<MailSubmissionStatus> GetSubmissionStatusAsync(string draftId, CancellationToken cancellationToken)
    {
        LookupCount++;
        return Task.FromResult(CreateCount == 0 && SubmissionStatus == MailSubmissionStatus.Draft ? MailSubmissionStatus.Missing : SubmissionStatus);
    }
    public Task DeleteDraftAsync(string draftId, CancellationToken cancellationToken)
    {
        DeleteCount++;
        return Task.CompletedTask;
    }
}
