using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace OutlookAutomail.Tests;

public sealed class MultipartTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("attachments")]
    public async Task ConfirmedPreparationLimitSendsBalancedWholeFilesWithStablePartLabels(string failureStage)
    {
        using var folder = new TestFolder();
        var documents = new Dictionary<string, string>
        {
            ["a.pdf"] = new('a', 9_000),
            ["b.pdf"] = new('b', 8_000),
            ["c.pdf"] = new('c', 7_000),
            ["d.pdf"] = new('d', 6_000)
        };
        foreach (var (name, content) in documents)
            folder.AddFile(name, content);
        var mail = new MultipartMail { Limit = 36_000, ReportedLimit = 36_000, FailureStage = failureStage };

        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        Assert.Equal(2, mail.Sent.Count);
        var history = FolderStore.GetHistory(folder.Root);
        var parent = Assert.Single(history, run => run.ParentBatchId is null);
        Assert.Equal(RunStatus.Archived, parent.Status);
        var parts = history.Where(run => run.ParentBatchId == parent.Id).OrderBy(run => run.PartNumber).ToArray();
        Assert.Equal(2, parts.Length);
        Assert.Equal(parts.Select(part => part.Id), parent.PartIds);
        Assert.DoesNotContain(mail.Sent, sent => sent.Message.BatchId == parent.Id);
        Assert.All(parts, part =>
        {
            Assert.Equal(RunStatus.Archived, part.Status);
            Assert.Equal(2, part.PartCount);
            Assert.Equal(parent.Subject + $" [Part {part.PartNumber}/2]", part.Subject);
            Assert.Empty(part.CoveredOccurrences);
            Assert.Null(part.ScheduleId);
            Assert.Null(part.OccurrenceUtc);
        });
        Assert.Equal(documents.Keys.Order(), mail.Sent.SelectMany(sent => sent.Message.Attachments).Select(attachment => attachment.Name).Order());
        Assert.All(parts, part => Assert.Equal(15_000L, part.Attachments.Sum(attachment => attachment.Length)));
        foreach (var (name, content) in documents)
        {
            var archived = Assert.Single(Directory.GetFiles(Path.Combine(folder.Root, "Sent"), name, SearchOption.AllDirectories));
            Assert.Equal(content, File.ReadAllText(archived));
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(folder.Root, "To send")));
        Assert.Empty(Directory.GetFiles(Path.Combine(folder.Root, ".automail", "active")));
        Assert.False(File.Exists(Path.Combine(folder.Root, ".automail", "runs", parent.Id, "submission-000001.json")));
    }

    [Fact]
    public async Task UnknownLimitRaisesPartCountAndPreflightsEveryFinalPartBeforeAnySubmission()
    {
        using var folder = new TestFolder();
        for (var index = 0; index < 6; index++)
            folder.AddFile($"{index}.pdf", new string((char)('a' + index), 6_000));
        var mail = new MultipartMail { Limit = 32_000 };
        mail.BeforeSend = message =>
        {
            if (mail.Sent.Count != 0)
                return;
            var parent = Assert.Single(FolderStore.GetHistory(folder.Root), run => run.ParentBatchId is null);
            Assert.True(File.Exists(Path.Combine(folder.Root, ".automail", "runs", parent.Id, "split.json")));
            var plan = FolderStore.LoadSplitPlan(folder.Root, parent.Id);
            Assert.Equal(3, plan.Parts.Count);
            Assert.All(plan.Parts, part => Assert.Contains(mail.Events, entry => entry.Operation == "attached" && entry.BatchId == part.Id));
            Assert.DoesNotContain(mail.Events, entry => entry.Operation == "send");
        };

        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        Assert.Equal(3, mail.Sent.Count);
        Assert.Contains(mail.Created, message => message.Subject.EndsWith("[Part 1/2]", StringComparison.Ordinal));
        Assert.All(mail.Sent, sent => Assert.EndsWith($"[Part {mail.Sent.IndexOf(sent) + 1}/3]", sent.Message.Subject));
        Assert.All(mail.Sent, sent => Assert.Equal(2, sent.Message.Attachments.Count));
        Assert.All(mail.Created.Where(message => message.Subject.EndsWith("/2]", StringComparison.Ordinal)),
            message => Assert.DoesNotContain(mail.Sent, sent => sent.Message.BatchId == message.BatchId));
        Assert.Equal(6, mail.Sent.SelectMany(sent => sent.Message.Attachments).Select(attachment => attachment.Name).Distinct().Count());
    }

    [Fact]
    public async Task RestartReconcilesQueuedPartWithoutResendingAndCompletesScheduleOnlyAfterEveryPartArchives()
    {
        using var folder = new TestFolder();
        for (var index = 0; index < 6; index++)
            folder.AddFile($"{index}.pdf", new string((char)('a' + index), 6_000));
        var mail = new MultipartMail { Limit = 32_000, ReportedLimit = 32_000, StatusAfterSend = number => number == 2 ? MailSubmissionStatus.Queued : MailSubmissionStatus.Sent };

        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        var history = FolderStore.GetHistory(folder.Root);
        var parent = Assert.Single(history, run => run.ParentBatchId is null);
        Assert.Equal(RunStatus.Split, parent.Status);
        Assert.Equal(2, mail.Sent.Count);
        Assert.Single(history, run => run.ParentBatchId == parent.Id && run.Status == RunStatus.Archived);
        var queued = Assert.Single(history, run => run.ParentBatchId == parent.Id && run.Status == RunStatus.Queued);
        Assert.Empty(FolderStore.LoadState(folder.Root, FolderStore.LoadConfiguration(folder.Root)).CompletedThrough);
        var planBytes = File.ReadAllBytes(Path.Combine(folder.Root, ".automail", "runs", parent.Id, "split.json"));

        await folder.Engine(mail).RunNowAsync(folder.Root, default);
        Assert.Equal(2, mail.Sent.Count);
        mail.Statuses[queued.Id] = MailSubmissionStatus.Sent;
        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        Assert.Equal(3, mail.Sent.Count);
        Assert.Equal(3, mail.Sent.Select(sent => sent.Message.BatchId).Distinct().Count());
        history = FolderStore.GetHistory(folder.Root);
        Assert.All(history, run => Assert.Equal(RunStatus.Archived, run.Status));
        Assert.Equal(3, history.Count(run => run.ParentBatchId == parent.Id));
        Assert.Equal(parent.CoveredOccurrences, FolderStore.LoadState(folder.Root, FolderStore.LoadConfiguration(folder.Root)).CompletedThrough);
        Assert.Equal(planBytes, File.ReadAllBytes(Path.Combine(folder.Root, ".automail", "runs", parent.Id, "split.json")));
        Assert.Equal(6, Directory.GetFiles(Path.Combine(folder.Root, "Sent"), "*.pdf", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task RemainingPartsKeepOriginalAccountRecipientsAndMessageAfterConfigurationChanges()
    {
        using var folder = new TestFolder();
        folder.Configuration.Subject = "Original {date:yyyy-MM-dd}";
        folder.Configuration.Body = "Prepared on {date}.";
        folder.Configuration.Cc = ["copy@example.com"];
        folder.Save();
        for (var index = 0; index < 4; index++)
            folder.AddFile($"{index}.pdf", new string('a', 7_000));
        var mail = new MultipartMail { Limit = 35_000, ReportedLimit = 35_000, StatusAfterSend = number => number == 1 ? MailSubmissionStatus.Queued : MailSubmissionStatus.Sent };
        await folder.Engine(mail).RunNowAsync(folder.Root, default);
        var queued = Assert.Single(FolderStore.GetHistory(folder.Root), run => run.Status == RunStatus.Queued);
        var originalAccount = mail.Account;
        folder.Clock.Advance(TimeSpan.FromDays(1));
        folder.Configuration.Subject = "Changed {date}";
        folder.Configuration.Body = "Changed body";
        folder.Configuration.To = ["changed@example.com"];
        folder.Configuration.Cc = [];
        folder.Save();
        mail.Account = new("backup@example.com", "backup@example.com", "Backup");
        mail.Statuses[queued.Id] = MailSubmissionStatus.Sent;

        await folder.Engine(mail).RunNowAsync(folder.Root, default);
        Assert.Single(mail.Sent);
        mail.Account = originalAccount;
        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        Assert.Equal(2, mail.Sent.Count);
        Assert.All(mail.Sent, sent =>
        {
            Assert.Equal(originalAccount.Id, sent.AccountId);
            Assert.StartsWith("Original 2030-01-03 [Part ", sent.Message.Subject);
            Assert.Equal("Prepared on 03/01/2030.", sent.Message.Body);
            Assert.Equal("recipient@example.com", Assert.Single(sent.Message.To));
            Assert.Equal("copy@example.com", Assert.Single(sent.Message.Cc));
        });
        Assert.All(FolderStore.GetHistory(folder.Root), run => Assert.Equal(RunStatus.Archived, run.Status));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleOversizedDocumentRemainsIntactAndRequiresExplicitConfirmation(bool reportLimit)
    {
        using var folder = new TestFolder();
        var content = new string('x', 20_000);
        folder.AddFile("oversized.pdf", content);
        var mail = new MultipartMail { Limit = 20_000, ReportedLimit = reportLimit ? 20_000 : null };
        var engine = folder.Engine(mail);

        await engine.RunNowAsync(folder.Root, default);

        Assert.Empty(mail.Sent);
        var blocked = Assert.Single(FolderStore.GetHistory(folder.Root));
        Assert.Equal(RunStatus.SizeBlocked, blocked.Status);
        Assert.Empty(blocked.PartIds);
        var staged = Path.Combine(folder.Root, ".automail", "runs", blocked.Id, "attachments", "oversized.pdf");
        Assert.Equal(content, File.ReadAllText(staged));
        await engine.CheckSchedulesAsync(default);
        Assert.Empty(mail.Sent);
        Assert.Single(mail.Created);
        await engine.ConfirmSentAsync(folder.Root, blocked.Id, default);

        Assert.Empty(mail.Sent);
        Assert.Equal(RunStatus.Archived, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
        Assert.Equal(content, File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(folder.Root, "Sent"), "oversized.pdf", SearchOption.AllDirectories))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneOversizedDocumentBlocksTheWholeBatchBeforeAnySmallerDocumentIsSubmitted(bool reportLimit)
    {
        using var folder = new TestFolder();
        folder.AddFile("oversized.pdf", new string('x', 25_000));
        for (var index = 0; index < 3; index++)
            folder.AddFile($"{index}.pdf", new string('a', 2_000));
        var mail = new MultipartMail { Limit = 25_000, ReportedLimit = reportLimit ? 25_000 : null };

        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        var blocked = Assert.Single(FolderStore.GetHistory(folder.Root));
        Assert.Equal(RunStatus.SizeBlocked, blocked.Status);
        Assert.Empty(mail.Sent);
        Assert.Empty(blocked.PartIds);
        Assert.Equal(4, Directory.GetFiles(Path.Combine(folder.Root, ".automail", "runs", blocked.Id, "attachments")).Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(folder.Root, "Sent"), "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(folder.Root, ".automail", "runs", blocked.Id, "split.json")));
    }

    [Fact]
    public async Task MissingMutableParentProgressCannotRestoreOriginalSubmissionAfterSplit()
    {
        using var folder = new TestFolder();
        for (var index = 0; index < 4; index++)
            folder.AddFile($"{index}.pdf", new string('a', 7_000));
        var mail = new MultipartMail { Limit = 35_000, ReportedLimit = 35_000, StatusAfterSend = number => number == 1 ? MailSubmissionStatus.Queued : MailSubmissionStatus.Sent };
        await folder.Engine(mail).RunNowAsync(folder.Root, default);
        var parent = Assert.Single(FolderStore.GetHistory(folder.Root), run => run.ParentBatchId is null);
        var directory = Path.Combine(folder.Root, ".automail", "runs", parent.Id);
        var plan = File.ReadAllBytes(Path.Combine(directory, "split.json"));
        File.Delete(Path.Combine(directory, "run.json"));

        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        Assert.Single(mail.Sent);
        Assert.Equal(RunStatus.Split, FolderStore.LoadRun(folder.Root, parent.Id).Status);
        Assert.False(File.Exists(Path.Combine(directory, "submission-000001.json")));
        Assert.Equal(plan, File.ReadAllBytes(Path.Combine(directory, "split.json")));
        var stale = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(Path.Combine(directory, "batch.json")), TestFolder.JsonOptions)!;
        Assert.Throws<InvalidOperationException>(() => FolderStore.SaveRun(folder.Root, stale));
        Assert.False(FolderStore.TryBeginSubmission(folder.Root, stale));
        Assert.DoesNotContain(mail.Sent, sent => sent.Message.BatchId == parent.Id);
    }

    [Fact]
    public async Task RestartReconstructsChildManifestsAfterInterruptionImmediatelyAfterPlanPublication()
    {
        using var folder = new TestFolder();
        for (var index = 0; index < 4; index++)
            folder.AddFile($"{index}.pdf", new string('a', 7_000));
        using var cancellation = new CancellationTokenSource();
        var mail = new MultipartMail { Limit = 35_000, ReportedLimit = 35_000 };
        mail.AfterAttachmentAdded = message =>
        {
            if (message.Subject.EndsWith("[Part 2/2]", StringComparison.Ordinal))
                cancellation.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => folder.Engine(mail).RunNowAsync(folder.Root, cancellation.Token));

        var parent = Assert.Single(FolderStore.GetHistory(folder.Root));
        Assert.Equal(RunStatus.Split, parent.Status);
        Assert.Equal(2, parent.PartIds.Count);
        Assert.All(parent.PartIds, id => Assert.False(Directory.Exists(Path.Combine(folder.Root, ".automail", "runs", id))));
        Assert.Empty(mail.Sent);
        mail.AfterAttachmentAdded = null;
        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        Assert.Equal(2, mail.Sent.Count);
        Assert.Equal(parent.PartIds.Order(), mail.Sent.Select(sent => sent.Message.BatchId).Order());
        Assert.All(FolderStore.GetHistory(folder.Root), run => Assert.Equal(RunStatus.Archived, run.Status));
        Assert.Equal(3, mail.Created.Count);
    }

    [Fact]
    public async Task SplitParentCannotBeManuallyConfirmedWhileAChildIsQueued()
    {
        using var folder = new TestFolder();
        for (var index = 0; index < 4; index++)
            folder.AddFile($"{index}.pdf", new string('a', 7_000));
        var mail = new MultipartMail { Limit = 35_000, ReportedLimit = 35_000, StatusAfterSend = _ => MailSubmissionStatus.Queued };
        var engine = folder.Engine(mail);
        await engine.RunNowAsync(folder.Root, default);
        var parent = Assert.Single(FolderStore.GetHistory(folder.Root), run => run.ParentBatchId is null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ConfirmSentAsync(folder.Root, parent.Id, default));

        Assert.Equal(RunStatus.Split, FolderStore.LoadRun(folder.Root, parent.Id).Status);
        Assert.False(File.Exists(Path.Combine(folder.Root, ".automail", "runs", parent.Id, "sent.json")));
        Assert.Single(mail.Sent);
    }

    [Theory]
    [InlineData(MailSubmissionStatus.Queued)]
    [InlineData(MailSubmissionStatus.Sent)]
    public async Task SubmittedTrialAfterInterruptedPreflightPreventsSendingTheOriginalEvenIfItsLimitIncreases(MailSubmissionStatus submittedStatus)
    {
        using var folder = new TestFolder();
        for (var index = 0; index < 4; index++)
            folder.AddFile($"{index}.pdf", new string('a', 7_000));
        using var cancellation = new CancellationTokenSource();
        var mail = new MultipartMail { Limit = 35_000, ReportedLimit = 35_000 };
        mail.AfterAttachmentAdded = message =>
        {
            if (message.Subject.EndsWith("[Part 1/2]", StringComparison.Ordinal))
                cancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => folder.Engine(mail).RunNowAsync(folder.Root, cancellation.Token));
        var parent = Assert.Single(FolderStore.GetHistory(folder.Root));
        Assert.Empty(parent.PartIds);
        var trial = Assert.Single(mail.Created, message => message.Subject.EndsWith("[Part 1/2]", StringComparison.Ordinal));
        mail.Statuses[trial.BatchId] = submittedStatus;
        mail.Limit = 100_000;
        mail.ReportedLimit = 100_000;
        mail.AfterAttachmentAdded = null;
        var createdCount = mail.Created.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => folder.Engine(mail).RunNowAsync(folder.Root, default));

        Assert.Empty(mail.Sent);
        Assert.Equal(createdCount, mail.Created.Count);
        Assert.Equal(RunStatus.NeedsReview, FolderStore.LoadRun(folder.Root, parent.Id).Status);
        Assert.False(FolderStore.TryBeginSubmission(folder.Root, FolderStore.LoadRun(folder.Root, parent.Id)));
        Assert.False(File.Exists(Path.Combine(folder.Root, ".automail", "runs", parent.Id, "submission-000001.json")));
        Assert.Equal(submittedStatus, mail.Statuses[trial.BatchId]);
        await folder.Engine(mail).RunNowAsync(folder.Root, default);
        Assert.Contains(trial.BatchId, FolderStore.LoadRun(folder.Root, parent.Id).Detail);
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task InterruptedPreflightRequiresTheOriginalAccountBeforeResumingOrRebuildingTrials()
    {
        using var folder = new TestFolder();
        for (var index = 0; index < 4; index++)
            folder.AddFile($"{index}.pdf", new string('a', 7_000));
        using var cancellation = new CancellationTokenSource();
        var mail = new MultipartMail { Limit = 35_000, ReportedLimit = 35_000 };
        mail.AfterAttachmentAdded = message =>
        {
            if (message.Subject.EndsWith("[Part 1/2]", StringComparison.Ordinal))
                cancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => folder.Engine(mail).RunNowAsync(folder.Root, cancellation.Token));
        var parent = Assert.Single(FolderStore.GetHistory(folder.Root));
        var originalAccount = mail.Account;
        mail.Account = new("backup@example.com", "backup@example.com", "Backup");
        mail.AfterAttachmentAdded = null;
        var createdCount = mail.Created.Count;

        await Assert.ThrowsAsync<OutlookMailException>(() => folder.Engine(mail).RunNowAsync(folder.Root, default));

        Assert.Empty(mail.Sent);
        Assert.Equal(createdCount, mail.Created.Count);
        Assert.Equal(originalAccount.Id, FolderStore.LoadRun(folder.Root, parent.Id).AccountId);
        mail.Account = originalAccount;
        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        Assert.Equal(2, mail.Sent.Count);
        Assert.All(mail.Sent, sent => Assert.Equal(originalAccount.Id, sent.AccountId));
        Assert.All(FolderStore.GetHistory(folder.Root), run => Assert.Equal(RunStatus.Archived, run.Status));
    }

    [Fact]
    public async Task ProviderLimitShrinkBlocksLaterPartWithoutRenumberingOrResendingEarlierParts()
    {
        using var folder = new TestFolder();
        for (var index = 0; index < 4; index++)
            folder.AddFile($"{index}.pdf", new string('a', 7_000));
        var mail = new MultipartMail { Limit = 35_000, ReportedLimit = 35_000 };
        mail.BeforeSend = _ => { mail.Limit = 25_000; mail.ReportedLimit = 25_000; };
        var engine = folder.Engine(mail);

        await engine.RunNowAsync(folder.Root, default);

        var history = FolderStore.GetHistory(folder.Root);
        var parent = Assert.Single(history, run => run.ParentBatchId is null);
        var blocked = Assert.Single(history, run => run.Status == RunStatus.SizeBlocked);
        Assert.Equal(2, blocked.PartNumber);
        Assert.Equal(2, blocked.PartCount);
        Assert.EndsWith("[Part 2/2]", blocked.Subject);
        Assert.Equal(RunStatus.Split, parent.Status);
        Assert.Single(history, run => run.ParentBatchId == parent.Id && run.Status == RunStatus.Archived);
        Assert.Single(mail.Sent);
        var plan = File.ReadAllBytes(Path.Combine(folder.Root, ".automail", "runs", parent.Id, "split.json"));
        await engine.CheckSchedulesAsync(default);
        Assert.Single(mail.Sent);
        await engine.ConfirmSentAsync(folder.Root, blocked.Id, default);
        Assert.Equal(RunStatus.Split, FolderStore.LoadRun(folder.Root, parent.Id).Status);
        Assert.False(File.Exists(Path.Combine(folder.Root, ".automail", "runs", parent.Id, "sent.json")));
        await engine.RunNowAsync(folder.Root, default);

        Assert.Single(mail.Sent);
        Assert.Equal(plan, File.ReadAllBytes(Path.Combine(folder.Root, ".automail", "runs", parent.Id, "split.json")));
        Assert.All(FolderStore.GetHistory(folder.Root), run => Assert.Equal(RunStatus.Archived, run.Status));
        Assert.Equal(4, Directory.GetFiles(Path.Combine(folder.Root, "Sent"), "*.pdf", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task MultipartSchemaFenceSurvivesSavingAnOlderInMemoryConfiguration()
    {
        using var folder = new TestFolder();
        for (var index = 0; index < 4; index++)
            folder.AddFile($"{index}.pdf", new string('a', 7_000));
        var mail = new MultipartMail { Limit = 35_000, ReportedLimit = 35_000 };

        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        Assert.Equal(1, folder.Configuration.SchemaVersion);
        Assert.Equal(2, FolderStore.LoadConfiguration(folder.Root).SchemaVersion);
        folder.Configuration.Body = "Saved from the open configuration editor";
        folder.Save();
        Assert.Equal(2, FolderStore.LoadConfiguration(folder.Root).SchemaVersion);
        Assert.Equal(2, folder.Configuration.SchemaVersion);
    }

    [Fact]
    public async Task RejectedOriginalRetryFreezesEditedRecipientsAndMessageWhenPreparationThenRequiresParts()
    {
        using var folder = new TestFolder();
        folder.Configuration.Subject = "Original subject";
        folder.Configuration.Body = "Original body";
        folder.Save();
        for (var index = 0; index < 4; index++)
            folder.AddFile($"{index}.pdf", new string('a', 7_000));
        var mail = new MultipartMail { SendError = new MailSubmissionRejectedException("UnresolvedRecipients", "Correct the recipient before sending") };
        await Assert.ThrowsAsync<MailSubmissionRejectedException>(() => folder.Engine(mail).RunNowAsync(folder.Root, default));
        var rejected = Assert.Single(FolderStore.GetHistory(folder.Root));
        Assert.Equal(RunStatus.Rejected, rejected.Status);
        folder.Clock.Advance(TimeSpan.FromDays(1));
        folder.Configuration.Subject = "Corrected {date:yyyy-MM-dd}";
        folder.Configuration.Body = "Corrected on {date}.";
        folder.Configuration.To = ["corrected@example.com"];
        folder.Configuration.Cc = ["corrected-copy@example.com"];
        folder.Save();
        mail.SendError = null;
        mail.Limit = 35_000;
        mail.ReportedLimit = 35_000;

        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        Assert.Equal(3, mail.Sent.Count);
        var parts = mail.Sent.Skip(1).ToArray();
        Assert.Equal(2, parts.Length);
        Assert.All(parts, sent =>
        {
            Assert.NotEqual(rejected.Id, sent.Message.BatchId);
            Assert.StartsWith("Corrected 2030-01-03 [Part ", sent.Message.Subject);
            Assert.Equal("Corrected on 03/01/2030.", sent.Message.Body);
            Assert.Equal("corrected@example.com", Assert.Single(sent.Message.To));
            Assert.Equal("corrected-copy@example.com", Assert.Single(sent.Message.Cc));
        });
        var parent = FolderStore.LoadRun(folder.Root, rejected.Id);
        Assert.Equal(RunStatus.Archived, parent.Status);
        Assert.Equal(2, parent.PartIds.Count);
        Assert.False(File.Exists(Path.Combine(folder.Root, ".automail", "runs", rejected.Id, "submission-000002.json")));
    }

    [Fact]
    public async Task UnknownSendExceptionRequiresReviewAndNeverTriggersMultipartOrAutomaticResubmission()
    {
        using var folder = new TestFolder();
        for (var index = 0; index < 4; index++)
            folder.AddFile($"{index}.pdf", new string('a', 7_000));
        var mail = new MultipartMail { SendError = new IOException("The connection disappeared after Send") };
        var engine = folder.Engine(mail);

        await Assert.ThrowsAsync<IOException>(() => engine.RunNowAsync(folder.Root, default));
        mail.SendError = null;
        mail.Limit = 1;
        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        var run = Assert.Single(FolderStore.GetHistory(folder.Root));
        Assert.Equal(RunStatus.NeedsReview, run.Status);
        Assert.Empty(run.PartIds);
        Assert.Single(mail.Sent);
        Assert.Single(mail.Created);
        Assert.False(File.Exists(Path.Combine(folder.Root, ".automail", "runs", run.Id, "split.json")));
        Assert.Equal(1, FolderStore.LoadConfiguration(folder.Root).SchemaVersion);
    }

    [Fact]
    public async Task QueuedOriginalIsNeverSplitWhenTheProviderLimitLaterDecreases()
    {
        using var folder = new TestFolder();
        for (var index = 0; index < 4; index++)
            folder.AddFile($"{index}.pdf", new string('a', 7_000));
        var mail = new MultipartMail { StatusAfterSend = _ => MailSubmissionStatus.Queued };
        await folder.Engine(mail).RunNowAsync(folder.Root, default);
        mail.Limit = 1;

        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        var run = Assert.Single(FolderStore.GetHistory(folder.Root));
        Assert.Equal(RunStatus.Queued, run.Status);
        Assert.Empty(run.PartIds);
        Assert.Single(mail.Created);
        Assert.Single(mail.Sent);
        Assert.False(File.Exists(Path.Combine(folder.Root, ".automail", "runs", run.Id, "split.json")));
    }

    [Fact]
    public async Task UncertainChildSendIsNeverAutomaticallyRetriedOrRepartitioned()
    {
        using var folder = new TestFolder();
        for (var index = 0; index < 4; index++)
            folder.AddFile($"{index}.pdf", new string('a', 7_000));
        var mail = new MultipartMail { Limit = 35_000, ReportedLimit = 35_000 };
        mail.BeforeSend = _ =>
        {
            if (mail.Sent.Count == 1)
                mail.SendError = new IOException("The result of sending part two was lost");
        };
        await Assert.ThrowsAsync<IOException>(() => folder.Engine(mail).RunNowAsync(folder.Root, default));
        var history = FolderStore.GetHistory(folder.Root);
        var parent = Assert.Single(history, run => run.ParentBatchId is null);
        var uncertain = Assert.Single(history, run => run.Status == RunStatus.NeedsReview);
        Assert.Equal(2, uncertain.PartNumber);
        Assert.Single(history, run => run.ParentBatchId == parent.Id && run.Status == RunStatus.Archived);
        mail.SendError = null;
        mail.BeforeSend = null;
        mail.Limit = 1;

        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        Assert.Equal(2, mail.Sent.Count);
        Assert.Equal(2, parent.PartIds.Count);
        Assert.Equal(RunStatus.NeedsReview, FolderStore.LoadRun(folder.Root, uncertain.Id).Status);
        Assert.Empty(Directory.GetFiles(Path.Combine(folder.Root, ".automail", "runs", uncertain.Id), "preflight-*.json"));
        Assert.False(File.Exists(Path.Combine(folder.Root, ".automail", "runs", uncertain.Id, "split.json")));
        Assert.Equal(RunStatus.Split, FolderStore.LoadRun(folder.Root, parent.Id).Status);
    }

    [Fact]
    public async Task EachPartRepeatsOnlyReferencedFrozenPhotosAlongsideItsOwnDocuments()
    {
        using var folder = new TestFolder();
        var photo = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
        using var imageStream = new MemoryStream(photo);
        var image = FolderStore.ImportInlineImage(folder.Root, folder.Configuration, imageStream);
        folder.Configuration.InlineImages.Add(image);
        folder.Configuration.BodyIsHtml = true;
        folder.Configuration.Body = $"<p>Documents</p><img src=\"cid:{image.Id}\">";
        folder.Save();
        for (var index = 0; index < 4; index++)
            folder.AddFile($"{index}.pdf", new string('a', 7_000));
        var mail = new MultipartMail { Limit = 36_000, ReportedLimit = 36_000 };

        await folder.Engine(mail).RunNowAsync(folder.Root, default);

        Assert.Equal(2, mail.Sent.Count);
        Assert.All(mail.Sent, sent =>
        {
            var repeated = Assert.Single(sent.Message.Attachments, attachment => attachment.ContentId is not null);
            Assert.Equal(image.Id, repeated.ContentId);
            Assert.Equal("image/png", repeated.ContentType);
            Assert.Equal(photo, File.ReadAllBytes(repeated.Path));
            Assert.Equal(2, sent.Message.Attachments.Count(attachment => attachment.ContentId is null));
            Assert.True(sent.Message.IsHtml);
            Assert.Equal(folder.Configuration.Body, sent.Message.Body);
        });
        Assert.Equal(4, Directory.GetFiles(Path.Combine(folder.Root, "Sent"), "*.pdf", SearchOption.AllDirectories).Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(folder.Root, "Sent"), "*.png", SearchOption.AllDirectories));
    }

    private sealed class MultipartMail : IMailClient
    {
        public MailAccount Account { get; set; } = new("sender@example.com", "sender@example.com", "Sender");
        public long? Limit { get; set; }
        public long? ReportedLimit { get; set; }
        public string FailureStage { get; set; } = "attachments";
        public Exception? SendError { get; set; }
        public Action<MailMessage>? BeforeSend { get; set; }
        public Action<MailMessage>? AfterAttachmentAdded { get; set; }
        public Func<int, MailSubmissionStatus> StatusAfterSend { get; set; } = _ => MailSubmissionStatus.Sent;
        public Dictionary<string, MailSubmissionStatus> Statuses { get; } = [];
        public Dictionary<string, MailMessage> Drafts { get; } = [];
        public List<MailMessage> Created { get; } = [];
        public List<(MailMessage Message, string AccountId)> Sent { get; } = [];
        public List<(string Operation, string BatchId)> Events { get; } = [];

        public Task<MailAccount?> GetAccountAsync(CancellationToken cancellationToken) => Task.FromResult<MailAccount?>(Account);

        public Task<string> CreateDraftAsync(MailMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(Drafts.ContainsKey(message.BatchId));
            Drafts.Add(message.BatchId, message);
            Statuses[message.BatchId] = MailSubmissionStatus.Draft;
            Created.Add(message);
            Events.Add(("create", message.BatchId));
            if (FailureStage == "create" && Limit is { } limit && message.EstimatedEncodedBytes > limit)
                throw new MailSizeLimitException(ReportedLimit, "Outlook rejected the estimated encoded message size.", "CreateDraft");
            return Task.FromResult(message.BatchId);
        }

        public Task AddAttachmentsAsync(string draftId, IReadOnlyList<MailAttachment> attachments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = Drafts[draftId] with { Attachments = attachments.ToArray() };
            Drafts[draftId] = message;
            foreach (var attachment in attachments)
            {
                Assert.Equal(attachment.Length, new FileInfo(attachment.Path).Length);
                using var source = File.OpenRead(attachment.Path);
                Assert.Equal(attachment.Length, source.Length);
                _ = SHA256.HashData(source);
            }
            Events.Add(("attach", draftId));
            if (FailureStage == "attachments" && Limit is { } limit && message.EstimatedEncodedBytes > limit)
                throw new MailSizeLimitException(ReportedLimit, "Outlook rejected the encoded message while attaching files.", "AddAttachments");
            Events.Add(("attached", draftId));
            AfterAttachmentAdded?.Invoke(message);
            return Task.CompletedTask;
        }

        public Task DeleteDraftAsync(string draftId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(MailSubmissionStatus.Draft, Statuses[draftId]);
            Drafts.Remove(draftId);
            Statuses[draftId] = MailSubmissionStatus.Missing;
            Events.Add(("delete", draftId));
            return Task.CompletedTask;
        }

        public Task SendDraftAsync(string draftId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = Drafts[draftId];
            BeforeSend?.Invoke(message);
            Events.Add(("send", draftId));
            Sent.Add((message, Account.Id));
            if (SendError is not null)
                throw SendError;
            Statuses[draftId] = StatusAfterSend(Sent.Count);
            return Task.CompletedTask;
        }

        public Task<MailSubmissionStatus> GetSubmissionStatusAsync(string draftId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Statuses.GetValueOrDefault(draftId, MailSubmissionStatus.Missing));
        }
    }
}
