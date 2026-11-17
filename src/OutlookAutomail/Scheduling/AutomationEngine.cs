using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OutlookAutomail;

public sealed class AutomationEngine(AppSettings settings, IMailClient mailClient, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> folderGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object activityGate = new();
    private int activeOperations;
    private bool paused;
    public event Action<FolderStatus>? StatusChanged;

    public bool TryPause()
    {
        lock (activityGate)
        {
            if (activeOperations != 0)
                return false;
            paused = true;
            return true;
        }
    }

    public void Resume()
    {
        lock (activityGate)
            paused = false;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(RunScheduleLoopAsync(cancellationToken), RunHeartbeatLoopAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            foreach (var subscription in settings.Folders.ToArray())
            {
                try
                {
                    FolderStore.WriteHeartbeat(subscription.RootPath, new WorkerHeartbeat
                    {
                        InstallationId = settings.InstallationId,
                        UpdatedUtc = clock.GetUtcNow(),
                        Ready = false
                    });
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public async Task RunNowAsync(string root, CancellationToken cancellationToken)
    {
        await ProcessFolderAsync(root, true, false, cancellationToken);
    }

    public async Task RetryArchiveOrReconcileAsync(string root, CancellationToken cancellationToken)
    {
        await ProcessFolderAsync(root, true, true, cancellationToken);
    }

    public async Task CheckSchedulesAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(settings.Folders.ToArray().Where(subscription => subscription.Enabled)
            .Select(subscription => ProcessScheduledFolderAsync(subscription.RootPath, cancellationToken)));
    }

    public async Task ConfirmSentAsync(string root, string runId, CancellationToken cancellationToken)
    {
        root = Path.GetFullPath(root);
        var gate = folderGates.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        var entered = false;
        try
        {
            lock (activityGate)
            {
                if (paused)
                    throw new InvalidOperationException("Processing is paused while the application prepares an update.");
                activeOperations++;
                entered = true;
            }
            using var ownership = FolderStore.AcquireOwnership(root);
            var configuration = FolderStore.LoadConfiguration(root);
            var state = FolderStore.LoadState(root, configuration);
            var run = FolderStore.LoadRun(root, runId);
            if (run.PartIds.Count != 0 || run.Status is not (RunStatus.Submitting or RunStatus.Queued or RunStatus.NeedsReview or RunStatus.SizeBlocked))
                throw new InvalidOperationException("Only a queued or uncertain submission can be manually confirmed as sent.");
            run.Status = RunStatus.Sent;
            run.Detail = "The user manually confirmed that this message was sent.";
            run.UpdatedUtc = clock.GetUtcNow();
            FolderStore.RecordOutcome(root, run);
            FolderStore.SaveRun(root, run);
            CompleteBatch(root, state, run);
            StatusChanged?.Invoke(new(root, configuration.Name, run.ParentBatchId is null ? "Archived" : "Part archived",
                run.ParentBatchId is null ? run.Detail : "The selected part was confirmed and archived. The split batch remains active until every part completes.", run.UpdatedUtc));
        }
        finally
        {
            if (entered)
                lock (activityGate) activeOperations--;
            gate.Release();
        }
    }

    private async Task RunScheduleLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await CheckSchedulesAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(20), clock, cancellationToken);
        }
    }

    private async Task ProcessScheduledFolderAsync(string root, CancellationToken cancellationToken)
    {
        try { await ProcessFolderAsync(root, false, false, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (FolderBusyException)
        {
            StatusChanged?.Invoke(new(root, Path.GetFileName(Path.TrimEndingDirectorySeparator(root)), "Busy", "Another worker is processing this folder."));
        }
        catch (Exception exception)
        {
            AppLog.Write($"Folder processing failed for {root}.", exception);
            StatusChanged?.Invoke(new(root, Path.GetFileName(Path.TrimEndingDirectorySeparator(root)), "Error", exception.Message));
        }
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            MailAccount? account = null;
            try
            {
                if (!Volatile.Read(ref paused))
                    account = await mailClient.GetAccountAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                foreach (var subscription in settings.Folders.ToArray())
                    StatusChanged?.Invoke(new(subscription.RootPath, Path.GetFileName(subscription.RootPath), "Outlook unavailable", exception.Message));
            }
            foreach (var subscription in settings.Folders.ToArray())
            {
                try
                {
                    var configuration = FolderStore.LoadConfiguration(subscription.RootPath);
                    var ready = !Volatile.Read(ref paused) && subscription.Enabled && configuration.Enabled && account is not null;
                    if (ready)
                    {
                        try { _ = MessageTemplate.Render(configuration, clock.GetUtcNow(), settings.InstallationId); }
                        catch (InvalidDataException exception)
                        {
                            ready = false;
                            StatusChanged?.Invoke(new(subscription.RootPath, configuration.Name, "Message needs attention", exception.Message));
                        }
                    }
                    FolderStore.WriteHeartbeat(subscription.RootPath, new WorkerHeartbeat
                    {
                        InstallationId = settings.InstallationId,
                        UpdatedUtc = clock.GetUtcNow(),
                        Ready = ready
                    });
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    StatusChanged?.Invoke(new(subscription.RootPath, Path.GetFileName(subscription.RootPath), "Unavailable", exception.Message));
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(20), clock, cancellationToken);
        }
    }

    private async Task ProcessFolderAsync(string root, bool manual, bool recoveryOnly, CancellationToken cancellationToken)
    {
        root = Path.GetFullPath(root);
        var gate = folderGates.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        var entered = false;
        try
        {
            lock (activityGate)
            {
                if (paused)
                {
                    if (manual)
                        throw new InvalidOperationException("Processing is paused while the application prepares an update.");
                    return;
                }
                activeOperations++;
                entered = true;
            }
            using var ownership = FolderStore.AcquireOwnership(root);
            var configuration = FolderStore.LoadConfiguration(root);
            var state = FolderStore.LoadState(root, configuration);
            if (state.Workers.All(worker => worker.InstallationId != settings.InstallationId))
            {
                state.Workers.Add(new WorkerRegistration
                {
                    InstallationId = settings.InstallationId,
                    MachineName = Environment.MachineName,
                    RegisteredUtc = clock.GetUtcNow()
                });
                FolderStore.SaveState(root, state);
            }
            var activeRuns = FolderStore.GetActiveRuns(root);
            foreach (var completed in activeRuns.Where(run => run.Status is RunStatus.Sent or RunStatus.Archived or RunStatus.SkippedEmpty))
                CompleteBatch(root, state, completed);
            var active = activeRuns.FirstOrDefault(run => run.Status is not (RunStatus.Sent or RunStatus.Archived or RunStatus.SkippedEmpty));
            if (!configuration.Enabled && !manual)
            {
                StatusChanged?.Invoke(new(root, configuration.Name, "Paused", "This shared configuration is disabled."));
                return;
            }
            var account = await mailClient.GetAccountAsync(cancellationToken);
            string? messageError = null;
            try { _ = MessageTemplate.Render(configuration, clock.GetUtcNow(), settings.InstallationId); }
            catch (InvalidDataException exception) { messageError = exception.Message; }
            FolderStore.WriteHeartbeat(root, new WorkerHeartbeat
            {
                InstallationId = settings.InstallationId,
                UpdatedUtc = clock.GetUtcNow(),
                Ready = !Volatile.Read(ref paused) && account is not null && configuration.Enabled && (active is not null || messageError is null)
            });
            if (account is null)
            {
                StatusChanged?.Invoke(new(root, configuration.Name, "Outlook unavailable", "Open classic Outlook and select an account to process this folder."));
                return;
            }
            var unresolved = activeRuns.FirstOrDefault(run => run.Status is RunStatus.Submitting or RunStatus.Queued or RunStatus.NeedsReview);
            if (unresolved is not null)
            {
                if (unresolved.AccountId != account.Id || unresolved.DraftId.Length == 0)
                {
                    StatusChanged?.Invoke(new(root, configuration.Name, "Needs review",
                        $"Select {unresolved.AccountAddress} in classic Outlook to check this submission. This batch will not be sent again automatically.", unresolved.CreatedUtc));
                    return;
                }
                var observation = await mailClient.GetSubmissionStatusAsync(unresolved.DraftId, cancellationToken);
                if (unresolved.Status == RunStatus.NeedsReview && observation is MailSubmissionStatus.Draft or MailSubmissionStatus.Missing
                    && Directory.EnumerateFiles(Path.Combine(root, FolderStore.MetadataFolder, "runs", unresolved.Id), "preflight-*.json").Any())
                {
                    StatusChanged?.Invoke(new(root, configuration.Name, "Needs review", unresolved.Detail, unresolved.UpdatedUtc));
                    return;
                }
                ObserveSubmission(root, unresolved, observation);
                if (unresolved.Status == RunStatus.Sent)
                {
                    CompleteBatch(root, state, unresolved);
                    active = activeRuns.FirstOrDefault(run => run.Id != unresolved.Id
                        && run.Status is not (RunStatus.Sent or RunStatus.Archived or RunStatus.SkippedEmpty));
                }
                else
                {
                    StatusChanged?.Invoke(new(root, configuration.Name,
                        unresolved.Status == RunStatus.Queued ? "Queued in Outlook" : "Needs review", unresolved.Detail, unresolved.CreatedUtc));
                    return;
                }
            }
            if (active?.Status == RunStatus.Split)
            {
                await ProcessSplitBatchAsync(root, state, active, account, configuration, manual, recoveryOnly, cancellationToken);
                return;
            }
            if (recoveryOnly)
            {
                StatusChanged?.Invoke(new(root, configuration.Name, "Ready", "Archive and submission recovery completed."));
                return;
            }
            if (active is null && messageError is not null)
                throw new InvalidDataException(messageError);
            if (!manual)
            {
                WorkerRegistration? preferred = null;
                foreach (var worker in state.Workers)
                {
                    var heartbeatPath = Path.Combine(root, FolderStore.MetadataFolder, "workers", worker.InstallationId + ".json");
                    if (!File.Exists(heartbeatPath))
                        continue;
                    using var heartbeatStream = new FileStream(heartbeatPath, FileMode.Open,
                        FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var heartbeat = JsonSerializer.Deserialize<WorkerHeartbeat>(heartbeatStream, FolderStore.JsonOptions)
                        ?? throw new InvalidDataException("A worker heartbeat is empty.");
                    if (heartbeat.Ready && heartbeat.UpdatedUtc >= clock.GetUtcNow().AddSeconds(-90)
                        && heartbeat.UpdatedUtc <= clock.GetUtcNow().AddSeconds(90))
                    {
                        if (active is null)
                        {
                            try { _ = MessageTemplate.Render(configuration, clock.GetUtcNow(), worker.InstallationId); }
                            catch (InvalidDataException) { continue; }
                        }
                        preferred = worker;
                        break;
                    }
                }
                if (preferred?.InstallationId != settings.InstallationId)
                {
                    StatusChanged?.Invoke(new(root, configuration.Name, "Standby", $"Waiting for {preferred?.MachineName ?? "an available worker"}."));
                    return;
                }
            }
            var run = active;
            if (run?.Status is RunStatus.Rejected or RunStatus.SizeBlocked)
            {
                if (!manual && (run.Status == RunStatus.SizeBlocked || !Directory.EnumerateFiles(
                        Path.Combine(root, FolderStore.MetadataFolder, "runs", run.Id), "preflight-*.json").Any()))
                {
                    StatusChanged?.Invoke(new(root, configuration.Name, run.Status == RunStatus.SizeBlocked ? "Size blocked" : "Rejected", run.Detail + " Correct the cause and use Run now to retry this batch.", run.UpdatedUtc));
                    return;
                }
            }
            if (run is null)
            {
                var due = SchedulePlanner.GetDueOccurrence(configuration, state.CompletedThrough, clock.GetUtcNow());
                if (!manual && due is null)
                {
                    StatusChanged?.Invoke(new(root, configuration.Name, "Ready", "Waiting for the next scheduled run.", state.LastRunUtc));
                    return;
                }
                var pending = FolderStore.ResolveFolder(root, configuration.PendingFolder);
                var files = Directory.EnumerateFiles(pending, "*", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path)).OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToList();
                if (files.Any(file => file.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                    throw new InvalidDataException("Attachment files cannot be file links.");
                if (files.Count == 0 && !configuration.SendWhenEmpty && configuration.WaitForFiles)
                {
                    StatusChanged?.Invoke(new(root, configuration.Name, "Waiting for files", "The due schedule will run when files become available."));
                    return;
                }
                var createdUtc = clock.GetUtcNow();
                var rendered = MessageTemplate.Render(configuration, createdUtc, settings.InstallationId);
                run = new RunRecord
                {
                    Transport = FolderStore.CurrentTransport,
                    Id = due is null ? Guid.NewGuid().ToString("N")
                        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{configuration.Id}:{due.ScheduleId}:{due.DueUtc:O}")))[..32].ToLowerInvariant(),
                    CreatedUtc = createdUtc,
                    UpdatedUtc = createdUtc,
                    Status = files.Count == 0 && !configuration.SendWhenEmpty ? RunStatus.SkippedEmpty : RunStatus.Preparing,
                    ScheduleId = due?.ScheduleId,
                    OccurrenceUtc = due?.DueUtc,
                    CoveredOccurrences = due is null ? [] : new(due.CoveredOccurrences),
                    WorkerName = Environment.MachineName,
                    AccountId = account.Id,
                    AccountAddress = account.Address,
                    PendingFolder = configuration.PendingFolder,
                    SentFolder = configuration.SentFolder,
                    Subject = rendered.Subject,
                    Body = rendered.Body,
                    BodyIsHtml = configuration.BodyIsHtml,
                    To = [.. configuration.To],
                    Cc = [.. configuration.Cc]
                };
                run.DraftId = run.Id;
                var imageStreams = new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var file in files)
                    {
                        try
                        {
                            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None);
                            run.Attachments.Add(new RunAttachment
                            {
                                Name = file.Name,
                                Length = stream.Length,
                                LastWriteUtc = file.LastWriteTimeUtc,
                                Sha256 = Convert.ToHexString(SHA256.HashData(stream))
                            });
                        }
                        catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
                        {
                            StatusChanged?.Invoke(new(root, configuration.Name, "Waiting for files", $"'{file.Name}' is still open in another application."));
                            return;
                        }
                    }
                    if (run.Status != RunStatus.SkippedEmpty)
                    {
                        var referenced = FolderStore.ValidateInlineImageReferences(run.Body, run.BodyIsHtml, configuration.InlineImages.Select(image => image.Id));
                        foreach (var image in configuration.InlineImages.Where(image => referenced.Contains(image.Id)))
                        {
                            var path = FolderStore.ResolveInlineImage(root, configuration, image);
                            var file = new FileInfo(path);
                            FileStream stream;
                            try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
                            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
                            {
                                StatusChanged?.Invoke(new(root, configuration.Name, "Waiting for files", "An embedded photo is still open in another application."));
                                return;
                            }
                            var attachment = new RunAttachment { ContentId = image.Id, SourcePath = image.Path };
                            try
                            {
                                attachment.ContentType = FolderStore.ValidateInlineImage(stream);
                                stream.Position = 0;
                                attachment.Sha256 = Convert.ToHexString(SHA256.HashData(stream));
                                attachment.Name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(image.Id)))
                                    + (attachment.ContentType switch { "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/bmp" => ".bmp", _ => ".png" });
                                imageStreams.Add(attachment.Name, stream);
                            }
                            catch
                            {
                                stream.Dispose();
                                throw;
                            }
                            attachment.Length = stream.Length;
                            attachment.LastWriteUtc = file.LastWriteTimeUtc;
                            run.Attachments.Add(attachment);
                        }
                    }
                    run = FolderStore.CreateBatch(root, run, imageStreams);
                }
                finally
                {
                    foreach (var stream in imageStreams.Values)
                        stream.Dispose();
                }
                FolderStore.ActivateBatch(root, run);
                if (run.Status == RunStatus.SkippedEmpty)
                {
                    run.Detail = "The folder was empty. This scheduled occurrence was skipped.";
                    CompleteBatch(root, state, run);
                    StatusChanged?.Invoke(new(root, configuration.Name, "Skipped empty", run.Detail, run.UpdatedUtc));
                    return;
                }
            }
            StatusChanged?.Invoke(new(root, configuration.Name, "Sending", "Preparing the attachment batch.", run.CreatedUtc));
            try
            {
                if (Directory.EnumerateFiles(Path.Combine(root, FolderStore.MetadataFolder, "runs", run.Id), "preflight-*.json").Any())
                    run = await PartitionBatchAsync(root, run, account, configuration,
                        new MailSizeLimitException(null, "Resuming a recorded size-recovery preflight. No new original submission is permitted.", "recorded draft preparation"), cancellationToken);
                else
                    run = await SubmitBatchAsync(root, run, account, configuration, cancellationToken);
            }
            catch (MailSizeLimitException exception)
            {
                AppLog.Write($"Batch {run.Id}: confirmed size failure before submission during {exception.Stage}.", exception);
                run = await PartitionBatchAsync(root, FolderStore.LoadRun(root, run.Id), account, configuration, exception, cancellationToken);
            }
            if (run.Status == RunStatus.Split)
            {
                await ProcessSplitBatchAsync(root, state, run, account, configuration, manual, false, cancellationToken);
                return;
            }
            if (run.Status is RunStatus.Sent or RunStatus.Archived)
            {
                CompleteBatch(root, state, run);
                StatusChanged?.Invoke(new(root, configuration.Name, "Archived", run.Detail, run.UpdatedUtc));
            }
            else
                StatusChanged?.Invoke(new(root, configuration.Name, run.Status == RunStatus.Queued ? "Queued in Outlook"
                    : run.Status == RunStatus.SizeBlocked ? "Size blocked" : run.Status.ToString(), run.Detail, run.UpdatedUtc));
        }
        finally
        {
            if (entered)
                lock (activityGate) activeOperations--;
            gate.Release();
        }
    }

    private async Task<RunRecord> SubmitBatchAsync(string root, RunRecord run, MailAccount account,
        FolderConfiguration configuration, CancellationToken cancellationToken)
    {
        run = FolderStore.LoadRun(root, run.Id);
        if (run.Status is not (RunStatus.Preparing or RunStatus.DraftReady or RunStatus.Rejected or RunStatus.SizeBlocked))
            return run;
        if (run.ParentBatchId is not null && run.AccountId != account.Id)
            throw new OutlookMailException($"Select the original account {run.AccountAddress} to continue this mail part.");
        var retryRejected = run.Status is RunStatus.Rejected or RunStatus.SizeBlocked;
        var editMessage = retryRejected && run.ParentBatchId is null;
        var rendered = editMessage ? MessageTemplate.Render(configuration, run.CreatedUtc, settings.InstallationId) : (run.Subject, run.Body);
        var imageReferences = FolderStore.ValidateInlineImageReferences(rendered.Body, editMessage ? configuration.BodyIsHtml : run.BodyIsHtml,
            run.Attachments.Where(attachment => attachment.ContentId is not null).Select(attachment => attachment.ContentId!));
        if (retryRejected)
        {
            run.Status = RunStatus.Preparing;
            run.Subject = rendered.Subject;
            run.Body = rendered.Body;
            if (editMessage)
            {
                run.BodyIsHtml = configuration.BodyIsHtml;
                run.To = [.. configuration.To];
                run.Cc = [.. configuration.Cc];
            }
        }
        var pending = FolderStore.ResolveFolder(root, run.PendingFolder);
        var directory = Path.Combine(root, FolderStore.MetadataFolder, "runs", run.Id, "attachments");
        Directory.CreateDirectory(directory);
        var streams = new List<FileStream>();
        try
        {
            foreach (var attachment in run.Attachments)
            {
                if (attachment.Name != Path.GetFileName(attachment.Name) || string.IsNullOrWhiteSpace(attachment.Name))
                    throw new InvalidDataException("The attachment manifest contains an invalid file name.");
                var staged = Path.Combine(root, FolderStore.MetadataFolder, "runs", run.Id,
                    attachment.ContentId is null ? "attachments" : "images", attachment.Name);
                if (attachment.ContentId is null && run.ParentBatchId is not null && File.Exists(staged)
                    && File.Exists(Path.Combine(root, FolderStore.MetadataFolder, "runs", run.ParentBatchId, "attachments", attachment.Name)))
                    throw new InvalidDataException($"Both parent and part contain '{attachment.Name}'. Review the frozen batch before continuing.");
                if (attachment.ContentId is null && !File.Exists(staged))
                {
                    var source = run.ParentBatchId is null ? Path.Combine(pending, attachment.Name)
                        : Path.Combine(root, FolderStore.MetadataFolder, "runs", run.ParentBatchId, "attachments", attachment.Name);
                    File.Move(source, staged);
                }
                var stream = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
                streams.Add(stream);
                if (stream.Length != attachment.Length || Convert.ToHexString(SHA256.HashData(stream)) != attachment.Sha256)
                    throw new InvalidDataException($"'{attachment.Name}' changed after the batch was recorded. Restore its original contents before retrying.");
            }
            if (run.AccountId != account.Id)
            {
                run.Status = RunStatus.Preparing;
            }
            run.AccountId = account.Id;
            run.AccountAddress = account.Address;
            run.WorkerName = Environment.MachineName;
            run.DraftId = run.Id;
            run.UpdatedUtc = clock.GetUtcNow();
            FolderStore.SaveRun(root, run);
            var draftStatus = await mailClient.GetSubmissionStatusAsync(run.DraftId, cancellationToken);
            if (draftStatus is MailSubmissionStatus.Sent or MailSubmissionStatus.Queued)
            {
                ObserveSubmission(root, run, draftStatus);
                return run;
            }
            if (draftStatus == MailSubmissionStatus.Missing)
                run.Status = RunStatus.Preparing;
            if (run.Status == RunStatus.Preparing)
            {
                if (retryRejected && draftStatus == MailSubmissionStatus.Draft)
                {
                    await mailClient.DeleteDraftAsync(run.DraftId, cancellationToken);
                    draftStatus = MailSubmissionStatus.Missing;
                }
                var message = new MailMessage(run.Id, run.Subject, run.Body, run.BodyIsHtml, run.To, run.Cc,
                    run.Attachments.Where(attachment => attachment.ContentId is null || imageReferences.Contains(attachment.ContentId)).Select(attachment => new MailAttachment(attachment.Name,
                        Path.Combine(root, FolderStore.MetadataFolder, "runs", run.Id, attachment.ContentId is null ? "attachments" : "images", attachment.Name),
                        attachment.Length, attachment.ContentId, attachment.ContentType)).ToList());
                if (draftStatus == MailSubmissionStatus.Missing)
                {
                    await mailClient.CreateDraftAsync(message, cancellationToken);
                    var createdStatus = await mailClient.GetSubmissionStatusAsync(run.DraftId, cancellationToken);
                    if (createdStatus is MailSubmissionStatus.Sent or MailSubmissionStatus.Queued)
                    {
                        ObserveSubmission(root, run, createdStatus);
                        return run;
                    }
                    if (createdStatus == MailSubmissionStatus.Missing)
                        throw new InvalidOperationException("Classic Outlook has not exposed the saved draft. The batch will be checked again before sending.");
                }
                await mailClient.AddAttachmentsAsync(run.DraftId, message.Attachments, cancellationToken);
                run.Status = RunStatus.DraftReady;
                run.UpdatedUtc = clock.GetUtcNow();
                FolderStore.SaveRun(root, run);
            }
            cancellationToken.ThrowIfCancellationRequested();
            run.Status = RunStatus.Submitting;
            run.Detail = "Submission started. An interrupted result requires reconciliation before any further send.";
            run.UpdatedUtc = clock.GetUtcNow();
            if (!FolderStore.TryBeginSubmission(root, run))
                return FolderStore.LoadRun(root, run.Id);
            FolderStore.SaveRun(root, run);
            try { await mailClient.SendDraftAsync(run.DraftId, cancellationToken); }
            catch (MailSubmissionRejectedException exception)
            {
                run.Status = RunStatus.Rejected;
                run.Detail = "Outlook did not submit the message: " + exception.Message;
                run.UpdatedUtc = clock.GetUtcNow();
                FolderStore.RecordRejection(root, run);
                FolderStore.SaveRun(root, run);
                throw;
            }
            catch (Exception exception)
            {
                run.Status = RunStatus.NeedsReview;
                run.Detail = "The send result is uncertain: " + exception.Message;
                run.UpdatedUtc = clock.GetUtcNow();
                FolderStore.SaveRun(root, run);
                throw;
            }
            ObserveSubmission(root, run, MailSubmissionStatus.Queued);
            if (await mailClient.GetSubmissionStatusAsync(run.DraftId, cancellationToken) == MailSubmissionStatus.Sent)
                ObserveSubmission(root, run, MailSubmissionStatus.Sent);
            return run;
        }
        finally
        {
            foreach (var stream in streams)
                stream.Dispose();
        }
    }

    private async Task<RunRecord> PartitionBatchAsync(string root, RunRecord run, MailAccount account, FolderConfiguration configuration,
        MailSizeLimitException rejection, CancellationToken cancellationToken)
    {
        if (account.Id != run.AccountId)
            throw new OutlookMailException($"Select the original account {run.AccountAddress} to resume size recovery for this batch.");
        FolderStore.EnableMultipart(root, configuration);
        var directory = Path.Combine(root, FolderStore.MetadataFolder, "runs", run.Id);
        var originalStatus = await mailClient.GetSubmissionStatusAsync(run.Id, cancellationToken);
        if (originalStatus is MailSubmissionStatus.Sent or MailSubmissionStatus.Queued)
        {
            ObserveSubmission(root, run, originalStatus);
            return run;
        }
        if (originalStatus == MailSubmissionStatus.Draft)
            await mailClient.DeleteDraftAsync(run.Id, cancellationToken);
        foreach (var path in Directory.EnumerateFiles(directory, "preflight-*.json"))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var previous = JsonSerializer.Deserialize<List<RunRecord>>(stream, FolderStore.JsonOptions)
                ?? throw new InvalidDataException("A mail part preflight record is empty.");
            await ClearTrialDraftsAsync(root, run, previous, cancellationToken);
        }
        var documents = run.Attachments.Where(attachment => attachment.ContentId is null)
            .OrderByDescending(attachment => attachment.Length).ThenBy(attachment => attachment.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var references = FolderStore.ValidateInlineImageReferences(run.Body, run.BodyIsHtml,
            run.Attachments.Where(attachment => attachment.ContentId is not null).Select(attachment => attachment.ContentId!));
        var images = run.Attachments.Where(attachment => attachment.ContentId is not null && references.Contains(attachment.ContentId)).ToList();
        var maximumBytes = rejection.MaximumMessageBytes;
        for (var count = 2; count <= documents.Count; count++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groups = Enumerable.Range(0, count).Select(_ => new List<RunAttachment>()).ToList();
            foreach (var document in documents)
                groups.OrderBy(group => group.Sum(attachment => attachment.Length)).First().Add(document);
            groups = groups.OrderByDescending(group => group.Sum(attachment => attachment.Length)).ToList();
            var parts = groups.Select((group, index) => new RunRecord
            {
                Transport = FolderStore.CurrentTransport,
                ParentBatchId = run.Id,
                PartNumber = index + 1,
                PartCount = count,
                CreatedUtc = run.CreatedUtc,
                UpdatedUtc = clock.GetUtcNow(),
                Status = RunStatus.Preparing,
                AccountId = run.AccountId,
                AccountAddress = run.AccountAddress,
                WorkerName = run.WorkerName,
                PendingFolder = run.PendingFolder,
                SentFolder = run.SentFolder,
                Subject = run.Subject + $" [Part {index + 1}/{count}]",
                Body = run.Body,
                BodyIsHtml = run.BodyIsHtml,
                To = [.. run.To],
                Cc = [.. run.Cc],
                Attachments = [.. group, .. images]
            }).ToList();
            foreach (var part in parts)
                part.DraftId = part.Id;
            var messages = parts.Select(part => new MailMessage(part.Id, part.Subject, part.Body, part.BodyIsHtml, part.To, part.Cc,
                part.Attachments.Select(attachment => new MailAttachment(attachment.Name,
                    Path.Combine(directory, attachment.ContentId is null ? "attachments" : "images", attachment.Name),
                    attachment.Length, attachment.ContentId, attachment.ContentType)).ToList())).ToList();
            if (maximumBytes is > 0 && messages.Any(message => message.EstimatedEncodedBytes > maximumBytes.Value))
                continue;
            FolderStore.WriteDurableFile(Path.Combine(directory, "preflight-" + Guid.NewGuid().ToString("N") + ".json"),
                JsonSerializer.Serialize(parts, FolderStore.JsonOptions), false);
            var tooLarge = false;
            try
            {
                foreach (var message in messages)
                {
                    await mailClient.CreateDraftAsync(message, cancellationToken);
                    await mailClient.AddAttachmentsAsync(message.BatchId, message.Attachments, cancellationToken);
                }
            }
            catch (MailSizeLimitException exception)
            {
                tooLarge = true;
                if (exception.MaximumMessageBytes is > 0)
                    maximumBytes = maximumBytes is > 0 ? Math.Min(maximumBytes.Value, exception.MaximumMessageBytes.Value) : exception.MaximumMessageBytes;
                AppLog.Write($"Batch {run.Id}: {count}-part draft preflight exceeded the provider size limit. No part was submitted.", exception);
            }
            if (tooLarge)
            {
                await ClearTrialDraftsAsync(root, run, parts, cancellationToken);
                continue;
            }
            FolderStore.PublishSplitPlan(root, new SplitBatchPlan { Parent = run, Parts = parts });
            AppLog.Write($"Batch {run.Id}: published {count} mail parts, original account {run.AccountId}, size evidence: {rejection.Evidence}");
            return FolderStore.LoadRun(root, run.Id);
        }
        run.Status = RunStatus.SizeBlocked;
        var repeatedSize = new MailMessage(run.Id, run.Subject + " [Part 1/1]", run.Body, run.BodyIsHtml, run.To, run.Cc,
            images.Select(image => new MailAttachment(image.Name, "", image.Length, image.ContentId, image.ContentType)).ToList()).EstimatedEncodedBytes;
        var blockingContent = documents.Count == 0 || maximumBytes is > 0 && repeatedSize >= maximumBytes.Value
            ? "The repeated message body/photos" : $"The document '{documents[0].Name}' with the repeated message/photos";
        run.Detail = blockingContent + " cannot fit the Outlook limit"
            + (maximumBytes is > 0 ? $" of {maximumBytes.Value} bytes" : " detected during preparation")
            + ". No email parts were submitted. Increase the provider limit or send/share the entire frozen batch separately, then confirm that exact batch as sent. Do not replace its staged files or delete .automail.";
        run.UpdatedUtc = clock.GetUtcNow();
        FolderStore.SaveRun(root, run);
        return run;
    }

    private async Task ClearTrialDraftsAsync(string root, RunRecord parent, IReadOnlyList<RunRecord> trials, CancellationToken cancellationToken)
    {
        foreach (var part in trials)
        {
            if (!Guid.TryParseExact(part.Id, "N", out _) || part.ParentBatchId != parent.Id || part.AccountId != parent.AccountId)
                throw new InvalidDataException("A mail part preflight record has invalid ownership.");
            var status = await mailClient.GetSubmissionStatusAsync(part.Id, cancellationToken);
            if (status is MailSubmissionStatus.Queued or MailSubmissionStatus.Sent)
            {
                parent.Status = RunStatus.NeedsReview;
                parent.Detail = $"Trial mail {part.Id} was submitted outside automatic processing. Review which documents were sent. Complete the entire frozen batch separately before confirming that batch as sent. No automatic resend is permitted.";
                parent.UpdatedUtc = clock.GetUtcNow();
                FolderStore.SaveRun(root, parent);
                throw new InvalidOperationException(parent.Detail);
            }
            if (status == MailSubmissionStatus.Draft)
                await mailClient.DeleteDraftAsync(part.Id, cancellationToken);
        }
    }

    private async Task ProcessSplitBatchAsync(string root, FolderState state, RunRecord parent, MailAccount account,
        FolderConfiguration configuration, bool manual, bool recoveryOnly, CancellationToken cancellationToken)
    {
        var plan = FolderStore.LoadSplitPlan(root, parent.Id);
        if (account.Id != parent.AccountId)
        {
            StatusChanged?.Invoke(new(root, configuration.Name, "Needs review", $"Select the original account {parent.AccountAddress} to continue this split batch."));
            return;
        }
        var archived = 0;
        foreach (var recorded in plan.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var imageStreams = new Dictionary<string, Stream>();
            RunRecord part;
            try
            {
                foreach (var image in recorded.Attachments.Where(attachment => attachment.ContentId is not null))
                    imageStreams.Add(image.Name, new FileStream(Path.Combine(root, FolderStore.MetadataFolder, "runs", parent.Id, "images", image.Name),
                        FileMode.Open, FileAccess.Read, FileShare.Read));
                part = FolderStore.CreateBatch(root, recorded, imageStreams);
            }
            finally
            {
                foreach (var stream in imageStreams.Values)
                    stream.Dispose();
            }
            if (part.Status is RunStatus.Submitting or RunStatus.Queued or RunStatus.NeedsReview)
                ObserveSubmission(root, part, await mailClient.GetSubmissionStatusAsync(part.Id, cancellationToken));
            if (part.Status is not (RunStatus.Sent or RunStatus.Archived)
                && !recoveryOnly && (manual || part.Status is not (RunStatus.Rejected or RunStatus.SizeBlocked)))
            {
                try { part = await SubmitBatchAsync(root, part, account, configuration, cancellationToken); }
                catch (MailSizeLimitException exception)
                {
                    part = FolderStore.LoadRun(root, recorded.Id);
                    part.Status = RunStatus.SizeBlocked;
                    part.Detail = $"Part {part.PartNumber}/{part.PartCount} no longer fits the provider limit. Earlier parts keep their numbers. Increase the provider limit or send/share this exact part separately and confirm this part as sent. {exception.Message}";
                    part.UpdatedUtc = clock.GetUtcNow();
                    FolderStore.SaveRun(root, part);
                }
            }
            if (part.Status is RunStatus.Sent or RunStatus.Archived)
            {
                CompleteBatch(root, state, part);
                archived++;
                continue;
            }
            parent.Detail = $"{archived} of {plan.Parts.Count} parts archived. Part {part.PartNumber}/{part.PartCount}: {part.Status}. {part.Detail}";
            parent.UpdatedUtc = clock.GetUtcNow();
            FolderStore.SaveRun(root, parent);
            StatusChanged?.Invoke(new(root, configuration.Name, "Sending in parts", parent.Detail, parent.UpdatedUtc));
            return;
        }
        CompleteBatch(root, state, parent);
        StatusChanged?.Invoke(new(root, configuration.Name, "Archived", parent.Detail, parent.UpdatedUtc));
    }

    private void ObserveSubmission(string root, RunRecord run, MailSubmissionStatus observation)
    {
        run.Status = observation switch
        {
            MailSubmissionStatus.Sent => RunStatus.Sent,
            MailSubmissionStatus.Queued => RunStatus.Queued,
            _ => RunStatus.NeedsReview
        };
        run.Detail = observation switch
        {
            MailSubmissionStatus.Sent => "Classic Outlook confirms the message is in Sent Items. This does not confirm recipient delivery.",
            MailSubmissionStatus.Queued => "Classic Outlook has queued this message in Outbox. Waiting for Sent Items confirmation before archiving or sending another batch.",
            _ => $"The original Outlook account has not confirmed this submission. Check Drafts, Outbox, and Sent Items on {run.WorkerName} before manually confirming it. This batch will not be sent again automatically."
        };
        run.UpdatedUtc = clock.GetUtcNow();
        if (run.Status is RunStatus.Sent or RunStatus.Queued)
            FolderStore.RecordOutcome(root, run);
        FolderStore.SaveRun(root, run);
    }

    private void CompleteBatch(string root, FolderState state, RunRecord run)
    {
        if (run.Status == RunStatus.SkippedEmpty)
        {
            FolderStore.RecordOutcome(root, run);
            FolderStore.SaveRun(root, run);
        }
        if (run.PartIds.Count != 0)
        {
            if (run.PartIds.Any(id => FolderStore.LoadRun(root, id).Status != RunStatus.Archived))
                throw new InvalidOperationException("All mail parts must finish archiving before their batch can complete.");
            var originalStaging = Path.Combine(root, FolderStore.MetadataFolder, "runs", run.Id, "attachments");
            if (Directory.Exists(originalStaging) && Directory.EnumerateFiles(originalStaging).Any())
                throw new InvalidDataException("The completed split batch still contains parent document copies. Review its files before completing the occurrence.");
            run.Status = RunStatus.Archived;
            run.Detail = $"All {run.PartIds.Count} mail parts are confirmed sent and archived. Recipient delivery remains subject to the mail server.";
            run.UpdatedUtc = clock.GetUtcNow();
            FolderStore.RecordOutcome(root, run);
            FolderStore.SaveRun(root, run);
        }
        else if (run.Status == RunStatus.Sent)
            ArchiveBatch(root, run);
        CompleteOccurrence(root, state, run);
        FolderStore.RetireBatch(root, run);
        state.LastRunUtc = clock.GetUtcNow();
        FolderStore.SaveState(root, state);
    }

    private void ArchiveBatch(string root, RunRecord run)
    {
        var sent = FolderStore.ResolveFolder(root, run.SentFolder);
        var destination = Path.Combine(sent, run.CreatedUtc.ToString("yyyy-MM-dd") + "_" + run.Id);
        var staged = Path.Combine(root, FolderStore.MetadataFolder, "runs", run.Id, "attachments");
        Directory.CreateDirectory(destination);
        foreach (var attachment in run.Attachments.Where(attachment => attachment.ContentId is null))
        {
            if (attachment.Name != Path.GetFileName(attachment.Name) || string.IsNullOrWhiteSpace(attachment.Name))
                throw new InvalidDataException("The attachment manifest contains an invalid file name.");
            var source = Path.Combine(staged, attachment.Name);
            var target = Path.Combine(destination, attachment.Name);
            if (!File.Exists(target))
                File.Move(source, target);
            using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != attachment.Length || Convert.ToHexString(SHA256.HashData(stream)) != attachment.Sha256)
                throw new InvalidDataException($"The archived file '{attachment.Name}' does not match its recorded batch.");
            if (File.Exists(source))
                throw new InvalidDataException($"Both staged and archived copies of '{attachment.Name}' exist. Review the batch before continuing.");
        }
        if (Directory.Exists(staged))
            Directory.Delete(staged);
        run.Status = RunStatus.Archived;
        run.UpdatedUtc = clock.GetUtcNow();
        FolderStore.RecordOutcome(root, run);
        FolderStore.SaveRun(root, run);
    }

    private static void CompleteOccurrence(string root, FolderState state, RunRecord run)
    {
        var changed = false;
        foreach (var occurrence in run.CoveredOccurrences)
        {
            if (!state.CompletedThrough.TryGetValue(occurrence.Key, out var completed) || completed < occurrence.Value)
            {
                state.CompletedThrough[occurrence.Key] = occurrence.Value;
                changed = true;
            }
        }
        if (changed)
            FolderStore.SaveState(root, state);
    }
}

public static class SchedulePlanner
{
    public static ScheduledOccurrence? GetDueOccurrence(FolderConfiguration configuration,
        IReadOnlyDictionary<string, DateTimeOffset> completedThrough, DateTimeOffset now)
    {
        var occurrences = new List<ScheduledOccurrence>();
        foreach (var schedule in configuration.Schedules.Where(schedule => schedule.Enabled))
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
            var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
            var time = TimeOnly.ParseExact(schedule.Time, "HH:mm");
            for (var daysBack = 0; daysBack < 8; daysBack++)
            {
                var date = DateOnly.FromDateTime(localNow.DateTime).AddDays(-daysBack);
                if (!schedule.Days.Contains(date.DayOfWeek))
                    continue;
                var local = date.ToDateTime(time, DateTimeKind.Unspecified);
                if (timeZone.IsInvalidTime(local))
                    continue;
                var offset = timeZone.IsAmbiguousTime(local)
                    ? timeZone.GetAmbiguousTimeOffsets(local).Max()
                    : timeZone.GetUtcOffset(local);
                var due = new DateTimeOffset(local, offset).ToUniversalTime();
                if (due > now)
                    continue;
                if (due >= configuration.ActiveFromUtc
                    && (!completedThrough.TryGetValue(schedule.Id, out var completed) || due > completed))
                    occurrences.Add(new ScheduledOccurrence(schedule.Id, due));
                break;
            }
        }
        var latest = occurrences.OrderByDescending(occurrence => occurrence.DueUtc).FirstOrDefault();
        return latest is null ? null : latest with
        {
            CoveredOccurrences = occurrences.ToDictionary(occurrence => occurrence.ScheduleId, occurrence => occurrence.DueUtc)
        };
    }
}

public sealed record ScheduledOccurrence(string ScheduleId, DateTimeOffset DueUtc)
{
    public IReadOnlyDictionary<string, DateTimeOffset> CoveredOccurrences { get; init; } = new Dictionary<string, DateTimeOffset>();
}
