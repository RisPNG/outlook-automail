using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace OutlookAutomail.Tests;

public sealed class OutlookMailClientTests
{
    [Fact]
    public async Task StartupWithoutAnAccountDoesNotContactOutlook()
    {
        var bridge = new RecordingOutlookSession();
        using var client = new OutlookMailClient(new AppSettings(), () => bridge, TimeSpan.FromSeconds(5));

        Assert.Null(await client.GetAccountAsync(default));
        Assert.Empty(bridge.Calls);
    }

    [Fact]
    public async Task BackgroundDiscoveryOnlyAttachesToRunningOutlook()
    {
        var bridge = new RecordingOutlookSession { Running = false };
        using var client = new OutlookMailClient(new AppSettings { AccountId = "employee@example.com" }, () => bridge, TimeSpan.FromSeconds(5));

        Assert.Null(await client.GetAccountAsync(default));

        Assert.False(Assert.Single(bridge.Calls).AllowLaunch);
    }

    [Fact]
    public async Task ExplicitConnectionCanStartOutlookAndReturnsConfiguredAccounts()
    {
        var bridge = new RecordingOutlookSession { Running = false };
        using var client = new OutlookMailClient(new AppSettings(), () => bridge, TimeSpan.FromSeconds(5));

        var accounts = await client.ConnectAsync(default);

        Assert.Equal(bridge.Accounts, accounts);
        Assert.True(Assert.Single(bridge.Calls).AllowLaunch);
    }

    [Fact]
    public async Task MissingSelectionNeverFallsBackToTheDefaultAccount()
    {
        var bridge = new RecordingOutlookSession();
        using var client = new OutlookMailClient(new AppSettings { AccountId = "missing@example.com" }, () => bridge, TimeSpan.FromSeconds(5));

        Assert.Null(await client.GetAccountAsync(default));
    }

    [Fact]
    public async Task EveryOutlookOperationUsesTheSameStaThread()
    {
        var bridge = new RecordingOutlookSession();
        var settings = new AppSettings { AccountId = "employee@example.com" };
        using (var client = new OutlookMailClient(settings, () => bridge, TimeSpan.FromSeconds(5)))
        {
            await client.ConnectAsync(default);
            var account = await client.GetAccountAsync(default);
            Assert.Equal(settings.AccountId, account!.Id);
            var batch = Guid.NewGuid().ToString("N");
            var message = new MailMessage(batch, "Documents", "Attached.", false, ["recipient@example.com"], [], []);
            Assert.Equal(batch, await client.CreateDraftAsync(message, default));
            await client.AddAttachmentsAsync(batch, [], default);
            await client.DeleteDraftAsync(batch, default);
            await client.CreateDraftAsync(message, default);
            await client.SendDraftAsync(batch, default);
            Assert.Equal(MailSubmissionStatus.Queued, await client.GetSubmissionStatusAsync(batch, default));
            bridge.Status = MailSubmissionStatus.Sent;
            Assert.Equal(MailSubmissionStatus.Sent, await client.GetSubmissionStatusAsync(batch, default));
            await client.DisconnectAsync(default);
        }

        Assert.All(bridge.Calls, call => Assert.Equal(ApartmentState.STA, call.Apartment));
        Assert.Single(bridge.Calls.Select(call => call.ThreadId).Distinct());
        Assert.All(bridge.Calls.Where(call => call.AccountId is not null), call => Assert.Equal(settings.AccountId, call.AccountId));
        Assert.True(bridge.Disposed.IsSet);
    }

    [Fact]
    public async Task QueuedOperationKeepsTheAccountSelectedWhenItWasRequested()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new RecordingOutlookSession
        {
            BeforeOperation = operation =>
            {
                if (operation == "Connect")
                {
                    entered.TrySetResult();
                    release.Wait(TimeSpan.FromSeconds(5));
                }
            }
        };
        var settings = new AppSettings { AccountId = "employee@example.com" };
        using var client = new OutlookMailClient(settings, () => bridge, TimeSpan.FromSeconds(5));
        var first = client.ConnectAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var send = client.SendDraftAsync(Guid.NewGuid().ToString("N"), default);
            settings.AccountId = "another@example.com";
            release.Set();
            await first;
            await send;
            Assert.Equal("employee@example.com", Assert.Single(bridge.Calls, call => call.Operation == "Send").AccountId);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task CancellationBeforeDispatchNeverSubmitsAnEmail()
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new RecordingOutlookSession
        {
            BeforeOperation = operation =>
            {
                if (operation == "Connect")
                {
                    entered.TrySetResult();
                    release.Wait(TimeSpan.FromSeconds(5));
                }
            }
        };
        using var client = new OutlookMailClient(new AppSettings { AccountId = "employee@example.com" }, () => bridge, TimeSpan.FromSeconds(5));
        var first = client.ConnectAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var send = client.SendDraftAsync(Guid.NewGuid().ToString("N"), cancellation.Token);
            cancellation.Cancel();
            var exception = await Assert.ThrowsAsync<MailSubmissionRejectedException>(() => send);
            Assert.Equal("CanceledBeforeSubmission", exception.ErrorCode);
            release.Set();
            await first;
            await client.GetSubmissionStatusAsync(Guid.NewGuid().ToString("N"), default);
            Assert.DoesNotContain(bridge.Calls, call => call.Operation == "Send");
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task ABlockedSubmissionRemainsUncertainAndDoesNotStartAnotherOperation()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new RecordingOutlookSession
        {
            BeforeOperation = operation =>
            {
                if (operation == "Send")
                {
                    entered.TrySetResult();
                    release.Wait(TimeSpan.FromSeconds(5));
                    returned.TrySetResult();
                }
            }
        };
        using var client = new OutlookMailClient(new AppSettings { AccountId = "employee@example.com" }, () => bridge, TimeSpan.FromMilliseconds(150));
        try
        {
            var send = client.SendDraftAsync(Guid.NewGuid().ToString("N"), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<OutlookMailException>(() => send);
            Assert.True(client.IsBusy);
            await Assert.ThrowsAsync<OutlookMailException>(() => client.ConnectAsync(default));
            var notDispatched = await Assert.ThrowsAsync<MailSubmissionRejectedException>(() => client.SendDraftAsync(Guid.NewGuid().ToString("N"), default));
            Assert.Equal("OutlookBusyBeforeSubmission", notDispatched.ErrorCode);
            Assert.Single(bridge.Calls);
            release.Set();
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var attempt = 0; ; attempt++)
            {
                try { await client.ConnectAsync(default); break; }
                catch (OutlookMailException) when (attempt < 20) { await Task.Delay(10); }
            }
            Assert.Equal(1, bridge.MaximumConcurrentOperations);
            Assert.Single(bridge.Calls.Select(call => call.ThreadId).Distinct());
            Assert.Single(bridge.Calls, call => call.Operation == "Send");
            for (var attempt = 0; client.IsBusy && attempt < 50; attempt++)
                await Task.Delay(10);
            Assert.False(client.IsBusy);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task AComFailureAfterSendStartsDoesNotBecomeASafeRetry()
    {
        var bridge = new RecordingOutlookSession
        {
            BeforeOperation = operation =>
            {
                if (operation == "Send")
                    throw new COMException("Private Outlook details.", unchecked((int)0x80004004));
            }
        };
        using var client = new OutlookMailClient(new AppSettings { AccountId = "employee@example.com" }, () => bridge, TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsAsync<OutlookMailException>(() => client.SendDraftAsync(Guid.NewGuid().ToString("N"), default));

        Assert.DoesNotContain("Private", exception.Message);
        Assert.Contains("0x80004004", exception.Message);
        Assert.Single(bridge.Calls);
    }

    [Fact]
    public async Task ProvenPreflightRejectionRemainsExplicitlyRetryable()
    {
        var bridge = new RecordingOutlookSession
        {
            BeforeOperation = operation =>
            {
                if (operation == "Send")
                    throw new MailSubmissionRejectedException("UnresolvedRecipients", "Correct the recipients.");
            }
        };
        using var client = new OutlookMailClient(new AppSettings { AccountId = "employee@example.com" }, () => bridge, TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsAsync<MailSubmissionRejectedException>(() => client.SendDraftAsync(Guid.NewGuid().ToString("N"), default));

        Assert.Equal("UnresolvedRecipients", exception.ErrorCode);
        Assert.Single(bridge.Calls);
    }

    [Fact]
    public async Task DisposalBeforeTheWorkerIsReadyStillStopsThatWorker()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new RecordingOutlookSession();
        var client = new OutlookMailClient(new AppSettings(), () =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
            return bridge;
        }, TimeSpan.FromSeconds(5));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var disposal = Task.Run(client.Dispose);
            await Task.Delay(30);
            release.Set();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(bridge.Disposed.IsSet);
            Assert.Equal("Dispose", Assert.Single(bridge.Calls).Operation);
        }
        finally { release.Set(); client.Dispose(); }
    }

    [Fact]
    public async Task DisposalIsBoundedWhileOutlookIsBlocked()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new RecordingOutlookSession
        {
            BeforeOperation = operation =>
            {
                if (operation == "Send")
                {
                    entered.TrySetResult();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
            }
        };
        var client = new OutlookMailClient(new AppSettings { AccountId = "employee@example.com" }, () => bridge, TimeSpan.FromMilliseconds(150));
        try
        {
            var send = client.SendDraftAsync(Guid.NewGuid().ToString("N"), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<OutlookMailException>(() => send);
            var stopwatch = Stopwatch.StartNew();
            await Task.Run(client.Dispose).WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4));
            Assert.False(bridge.Disposed.IsSet);
            release.Set();
            Assert.True(bridge.Disposed.Wait(TimeSpan.FromSeconds(5)));
            Assert.Single(bridge.Calls, call => call.Operation == "Send");
        }
        finally { release.Set(); client.Dispose(); }
    }

    private sealed record OutlookCall(string Operation, string? AccountId, int ThreadId, ApartmentState Apartment, bool AllowLaunch = false);

    private sealed class RecordingOutlookSession : IOutlookSessionBridge
    {
        private int concurrent;
        public ConcurrentQueue<OutlookCall> Calls { get; } = new();
        public ManualResetEventSlim Disposed { get; } = new();
        public IReadOnlyList<MailAccount> Accounts { get; } = [new("employee@example.com", "employee@example.com", "Employee"), new("another@example.com", "another@example.com", "Another employee")];
        public Action<string>? BeforeOperation { get; init; }
        public bool Running { get; set; } = true;
        public MailSubmissionStatus Status { get; set; } = MailSubmissionStatus.Draft;
        public int MaximumConcurrentOperations { get; private set; }

        public IReadOnlyList<MailAccount> Connect(bool allowLaunch)
        {
            Record("Connect", null, allowLaunch);
            if (allowLaunch)
                Running = true;
            return Running ? Accounts : [];
        }

        public string CreateDraft(string accountId, MailMessage message)
        {
            Record("Create", accountId);
            Status = MailSubmissionStatus.Draft;
            return message.BatchId;
        }

        public void AddAttachments(string accountId, string batchId, IReadOnlyList<MailAttachment> attachments, CancellationToken cancellationToken) => Record("Attachments", accountId);
        public void DeleteDraft(string accountId, string batchId) => Record("Delete", accountId);
        public void SendDraft(string accountId, string batchId, CancellationToken cancellationToken)
        {
            Record("Send", accountId);
            Status = MailSubmissionStatus.Queued;
        }

        public MailSubmissionStatus GetSubmissionStatus(string accountId, string batchId)
        {
            Record("Status", accountId);
            return Status;
        }

        public void Disconnect() => Record("Disconnect", null);

        public void Dispose()
        {
            Record("Dispose", null);
            Disposed.Set();
        }

        private void Record(string operation, string? accountId, bool allowLaunch = false)
        {
            Calls.Enqueue(new(operation, accountId, Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState(), allowLaunch));
            MaximumConcurrentOperations = Math.Max(MaximumConcurrentOperations, Interlocked.Increment(ref concurrent));
            try { BeforeOperation?.Invoke(operation); }
            finally { Interlocked.Decrement(ref concurrent); }
        }
    }
}
