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
            if (run.Status is not (RunStatus.Submitting or RunStatus.Queued or RunStatus.NeedsReview))
                throw new InvalidOperationException("Only a queued or uncertain submission can be manually confirmed as sent.");
            run.Status = RunStatus.Sent;
            run.Detail = "The user manually confirmed that this message was sent.";
            run.UpdatedUtc = clock.GetUtcNow();
            FolderStore.RecordOutcome(root, run);
            FolderStore.SaveRun(root, run);
            CompleteBatch(root, state, run);
            StatusChanged?.Invoke(new(root, configuration.Name, "Archived", run.Detail, run.UpdatedUtc));
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
                    FolderStore.WriteHeartbeat(subscription.RootPath, new WorkerHeartbeat
                    {
                        InstallationId = settings.InstallationId,
                        UpdatedUtc = clock.GetUtcNow(),
                        Ready = !Volatile.Read(ref paused) && subscription.Enabled && configuration.Enabled && account is not null
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
            FolderStore.WriteHeartbeat(root, new WorkerHeartbeat
            {
                InstallationId = settings.InstallationId,
                UpdatedUtc = clock.GetUtcNow(),
                Ready = !Volatile.Read(ref paused) && account is not null && configuration.Enabled
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
                ObserveSubmission(root, unresolved,
                    await mailClient.GetSubmissionStatusAsync(unresolved.DraftId, cancellationToken));
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
            if (recoveryOnly)
            {
                StatusChanged?.Invoke(new(root, configuration.Name, "Ready", "Archive and submission recovery completed."));
                return;
            }
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
            if (run?.Status == RunStatus.Rejected)
            {
                if (!manual)
                {
                    StatusChanged?.Invoke(new(root, configuration.Name, "Rejected", "Outlook did not submit the message. Correct the cause and use Run now to retry this batch.", run.UpdatedUtc));
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
                if (files.Any(file => file.LastWriteTimeUtc > clock.GetUtcNow().UtcDateTime.AddSeconds(-configuration.FileReadySeconds)))
                {
                    StatusChanged?.Invoke(new(root, configuration.Name, "Waiting for files", "Some files are still changing or have not reached the readiness delay."));
                    return;
                }
                if (files.Count == 0 && !configuration.SendWhenEmpty && configuration.WaitForFiles)
                {
                    StatusChanged?.Invoke(new(root, configuration.Name, "Waiting for files", "The due schedule will run when files become available."));
                    return;
                }
                var createdUtc = clock.GetUtcNow();
                var rendered = MessageTemplate.Render(configuration, createdUtc);
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
                long estimatedSize = 8192 + Encoding.UTF8.GetByteCount(run.Subject) * 2L + Encoding.UTF8.GetByteCount(run.Body) * 2L;
                var imageStreams = new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var file in files)
                    {
                        try
                        {
                            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None);
                            var base64Size = ((stream.Length + 2) / 3) * 4;
                            estimatedSize += base64Size + ((base64Size + 75) / 76) * 2 + 2048;
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
                            if (file.LastWriteTimeUtc > clock.GetUtcNow().UtcDateTime.AddSeconds(-configuration.FileReadySeconds))
                            {
                                StatusChanged?.Invoke(new(root, configuration.Name, "Waiting for files", "An embedded photo has not reached the readiness delay."));
                                return;
                            }
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
                            var base64Size = ((attachment.Length + 2) / 3) * 4;
                            estimatedSize += base64Size + ((base64Size + 75) / 76) * 2 + 2048;
                            run.Attachments.Add(attachment);
                        }
                    }
                    if (estimatedSize > configuration.MaximumMessageSizeMb * 1024L * 1024)
                        throw new InvalidDataException($"The batch exceeds the configured {configuration.MaximumMessageSizeMb} MB message limit after email encoding. Remove files or increase the limit before retrying.");
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
            run = await SubmitBatchAsync(root, run, account, configuration, cancellationToken);
            if (run.Status is RunStatus.Sent or RunStatus.Archived)
            {
                CompleteBatch(root, state, run);
                StatusChanged?.Invoke(new(root, configuration.Name, "Archived", run.Detail, run.UpdatedUtc));
            }
            else
                StatusChanged?.Invoke(new(root, configuration.Name, run.Status == RunStatus.Queued ? "Queued in Outlook" : "Needs review",
                    run.Status == RunStatus.Queued ? run.Detail : "This batch already has a submission claim. Reconcile its result before continuing.", run.UpdatedUtc));
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
        if (run.Status is not (RunStatus.Preparing or RunStatus.DraftReady or RunStatus.Rejected))
            return run;
        var retryRejected = run.Status == RunStatus.Rejected;
        var rendered = retryRejected ? MessageTemplate.Render(configuration, run.CreatedUtc) : (run.Subject, run.Body);
        var imageReferences = FolderStore.ValidateInlineImageReferences(rendered.Body, retryRejected ? configuration.BodyIsHtml : run.BodyIsHtml,
            run.Attachments.Where(attachment => attachment.ContentId is not null).Select(attachment => attachment.ContentId!));
        long batchSize = 8192 + Encoding.UTF8.GetByteCount(rendered.Subject) * 2L + Encoding.UTF8.GetByteCount(rendered.Body) * 2L;
        foreach (var attachment in run.Attachments.Where(attachment => attachment.ContentId is null || imageReferences.Contains(attachment.ContentId)))
        {
            var base64Size = ((attachment.Length + 2) / 3) * 4;
            batchSize += base64Size + ((base64Size + 75) / 76) * 2 + 2048;
        }
        if (batchSize > configuration.MaximumMessageSizeMb * 1024L * 1024)
            throw new InvalidDataException("This recorded batch exceeds the current message size limit. Increase the limit before retrying.");
        if (retryRejected)
        {
            run.Status = RunStatus.Preparing;
            run.Subject = rendered.Subject;
            run.Body = rendered.Body;
            run.BodyIsHtml = configuration.BodyIsHtml;
            run.To = [.. configuration.To];
            run.Cc = [.. configuration.Cc];
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
                if (attachment.ContentId is null && !File.Exists(staged))
                    File.Move(Path.Combine(pending, attachment.Name), staged);
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
        CompleteOccurrence(root, state, run);
        if (run.Status == RunStatus.Sent)
            ArchiveBatch(root, run);
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
