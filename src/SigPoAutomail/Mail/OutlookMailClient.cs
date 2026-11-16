using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace SigPoAutomail;

public sealed class OutlookMailException(string message) : InvalidOperationException(message);

internal interface IOutlookSessionBridge : IDisposable
{
    IReadOnlyList<MailAccount> Connect(bool allowLaunch);
    string CreateDraft(string accountId, MailMessage message);
    void AddAttachments(string accountId, string batchId, IReadOnlyList<MailAttachment> attachments, CancellationToken cancellationToken);
    void DeleteDraft(string accountId, string batchId);
    void SendDraft(string accountId, string batchId, CancellationToken cancellationToken);
    MailSubmissionStatus GetSubmissionStatus(string accountId, string batchId);
    void Disconnect();
}

public sealed class OutlookMailClient : IMailClient, IDisposable
{
    private readonly AppSettings settings;
    private readonly Func<IOutlookSessionBridge> sessionFactory;
    private readonly TimeSpan operationTimeout;
    private readonly ConcurrentQueue<Action> commands = new();
    private readonly TaskCompletionSource<Control> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object operationGate = new();
    private readonly Thread thread;
    private IOutlookSessionBridge? session;
    private Control? dispatcher;
    private bool dispatching;
    private WorkState? blocked;
    private int disposed;

    public bool IsBusy => Volatile.Read(ref dispatching) || !commands.IsEmpty;

    public OutlookMailClient(AppSettings settings) : this(settings, () => new OutlookComSession(), TimeSpan.FromSeconds(60)) { }

    internal OutlookMailClient(AppSettings settings, Func<IOutlookSessionBridge> sessionFactory, TimeSpan operationTimeout)
    {
        this.settings = settings;
        this.sessionFactory = sessionFactory;
        this.operationTimeout = operationTimeout;
        thread = new Thread(RunSession) { IsBackground = true, Name = "Classic Outlook session" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public Task<IReadOnlyList<MailAccount>> ConnectAsync(CancellationToken cancellationToken) => InvokeAsync((session, _) => session.Connect(true), false, cancellationToken);

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await InvokeAsync((session, _) => { session.Disconnect(); return true; }, false, cancellationToken).ConfigureAwait(false);
    }

    public Task<MailAccount?> GetAccountAsync(CancellationToken cancellationToken)
    {
        var selected = settings.AccountId;
        if (string.IsNullOrWhiteSpace(selected))
            return Task.FromResult<MailAccount?>(null);
        return InvokeAsync((session, _) => session.Connect(false).SingleOrDefault(account => account.Id == selected), false, cancellationToken);
    }

    public Task<string> CreateDraftAsync(MailMessage message, CancellationToken cancellationToken)
    {
        var selected = settings.AccountId;
        return InvokeAsync((session, _) => session.CreateDraft(selected, message), false, cancellationToken);
    }

    public async Task AddAttachmentsAsync(string draftId, IReadOnlyList<MailAttachment> attachments, CancellationToken cancellationToken)
    {
        var selected = settings.AccountId;
        await InvokeAsync((session, token) => { session.AddAttachments(selected, draftId, attachments, token); return true; }, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDraftAsync(string draftId, CancellationToken cancellationToken)
    {
        var selected = settings.AccountId;
        await InvokeAsync((session, _) => { session.DeleteDraft(selected, draftId); return true; }, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendDraftAsync(string draftId, CancellationToken cancellationToken)
    {
        var selected = settings.AccountId;
        await InvokeAsync((session, token) => { session.SendDraft(selected, draftId, token); return true; }, true, cancellationToken).ConfigureAwait(false);
    }

    public Task<MailSubmissionStatus> GetSubmissionStatusAsync(string draftId, CancellationToken cancellationToken)
    {
        var selected = settings.AccountId;
        return InvokeAsync((session, _) => session.GetSubmissionStatus(selected, draftId), false, cancellationToken);
    }

    private async Task<T> InvokeAsync<T>(Func<IOutlookSessionBridge, CancellationToken, T> operation, bool submission, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        lock (operationGate)
            if (blocked is not null)
            {
                if (submission)
                    throw new MailSubmissionRejectedException("OutlookBusyBeforeSubmission", "This email was not submitted because Outlook is still completing an earlier operation. Check Outlook before retrying.");
                throw new OutlookMailException("Classic Outlook is still completing an earlier operation. Check Outlook for a security prompt or other dialog before continuing.");
            }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        timeout.CancelAfter(operationTimeout);
        var operationToken = timeout.Token;
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new WorkState();
        try
        {
            var window = await ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            lock (operationGate)
                if (blocked is not null)
                {
                    if (submission)
                        throw new MailSubmissionRejectedException("OutlookBusyBeforeSubmission", "This email was not submitted because Outlook is still completing an earlier operation. Check Outlook before retrying.");
                    throw new OutlookMailException("Classic Outlook is still completing an earlier operation. Check Outlook before continuing.");
                }
            commands.Enqueue(() =>
            {
                lock (operationGate)
                {
                    if (work.Abandoned || operationToken.IsCancellationRequested)
                    {
                        work.Finished = true;
                        completion.TrySetCanceled();
                        return;
                    }
                    work.Started = true;
                }
                try { completion.TrySetResult(operation(session!, operationToken)); }
                catch (COMException exception)
                {
                    completion.TrySetException(new OutlookMailException($"Classic Outlook could not complete the operation (0x{exception.HResult:X8}). Check Outlook for a security prompt or connection problem."));
                }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally
                {
                    lock (operationGate)
                    {
                        work.Finished = true;
                        if (ReferenceEquals(blocked, work))
                            blocked = null;
                    }
                }
            });
            window.BeginInvoke((Action)DispatchNext);
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            bool started;
            bool finished;
            lock (operationGate)
            {
                started = work.Started;
                finished = work.Finished;
                if (!started)
                    work.Abandoned = true;
                else if (!finished)
                    blocked = work;
            }
            if (started && finished && completion.Task.IsCompleted)
                return await completion.Task.ConfigureAwait(false);
            if (submission && !started)
                throw new MailSubmissionRejectedException("CanceledBeforeSubmission", "The email was not submitted because the Outlook operation was canceled before it started.");
            if (started)
                throw new OutlookMailException("The Outlook operation has not finished. Its result may be uncertain. Check Outlook for a security prompt or other dialog. The operation will not be repeated automatically.");
            cancellationToken.ThrowIfCancellationRequested();
            throw new OutlookMailException("Classic Outlook did not become ready in time. Try again after checking Outlook.");
        }
        catch (InvalidOperationException) when (!work.Started && Volatile.Read(ref disposed) != 0)
        {
            lock (operationGate)
                work.Abandoned = true;
            if (submission)
                throw new MailSubmissionRejectedException("CanceledBeforeSubmission", "The app closed before the Outlook submission started.");
            throw new ObjectDisposedException(nameof(OutlookMailClient));
        }
    }

    private void RunSession()
    {
        try
        {
            Application.OleRequired();
            using var window = new Control();
            _ = window.Handle;
            dispatcher = window;
            session = sessionFactory();
            ready.TrySetResult(window);
            if (!commands.IsEmpty)
                window.BeginInvoke((Action)DispatchNext);
            Application.Run();
        }
        catch (Exception exception)
        {
            ready.TrySetException(new OutlookMailException($"The Outlook connection thread could not start ({exception.GetType().Name})."));
        }
        finally
        {
            try { session?.Dispose(); }
            catch (COMException exception) { AppLog.Write($"Outlook connection cleanup failed (0x{exception.HResult:X8})."); }
            dispatcher = null;
        }
    }

    private void DispatchNext()
    {
        if (dispatching)
            return;
        dispatching = true;
        try
        {
            if (commands.TryDequeue(out var command))
                command();
        }
        finally
        {
            dispatching = false;
            if (!commands.IsEmpty && dispatcher is { IsDisposed: false } window)
                window.BeginInvoke((Action)DispatchNext);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        lifetime.Cancel();
        commands.Enqueue(() => Application.ExitThread());
        if (ready.Task.IsCompletedSuccessfully && ready.Task.Result is { IsDisposed: false } window)
            window.BeginInvoke((Action)DispatchNext);
        thread.Join(TimeSpan.FromSeconds(2));
        lifetime.Dispose();
    }

    private sealed class WorkState
    {
        public bool Started { get; set; }
        public bool Finished { get; set; }
        public bool Abandoned { get; set; }
    }
}
