using System.Runtime.InteropServices;

namespace OutlookAutomail;

internal sealed class OutlookComSession : IOutlookSessionBridge
{
    private const string BatchProperty = "http://schemas.microsoft.com/mapi/string/{00020329-0000-0000-C000-000000000046}/OutlookAutomailBatchId";
    private const string AccountProperty = "http://schemas.microsoft.com/mapi/string/{00020329-0000-0000-C000-000000000046}/OutlookAutomailAccountId";
    private object? application;

    public IReadOnlyList<MailAccount> Connect(bool allowLaunch)
    {
        if (application is null)
        {
            var type = Type.GetTypeFromProgID("Outlook.Application");
            if (type is null)
            {
                if (!allowLaunch)
                    return [];
                throw new OutlookMailException("Classic Outlook is not installed. New Outlook does not support this connection method.");
            }
            var classId = type.GUID;
            var result = GetActiveObject(ref classId, IntPtr.Zero, out var running);
            if (result >= 0)
                application = running;
            else if (result == unchecked((int)0x800401E3) && allowLaunch)
                application = Activator.CreateInstance(type);
            else if (result == unchecked((int)0x800401E3))
                return [];
            else
                Marshal.ThrowExceptionForHR(result);
        }

        object? session = null;
        object? accounts = null;
        try
        {
            session = ((dynamic)application!).Session;
            accounts = ((dynamic)session).Accounts;
            var result = new List<MailAccount>();
            var count = (int)((dynamic)accounts).Count;
            for (var index = 1; index <= count; index++)
            {
                object? account = null;
                try
                {
                    account = ((dynamic)accounts).Item(index);
                    var address = ((string)((dynamic)account).SmtpAddress).Trim();
                    if (address.Length == 0)
                        continue;
                    result.Add(new MailAccount(address.ToLowerInvariant(), address, (string)((dynamic)account).DisplayName));
                }
                finally { if (account is not null) Marshal.ReleaseComObject(account); }
            }
            if (result.GroupBy(account => account.Id).Any(group => group.Count() > 1))
                throw new OutlookMailException("The Outlook profile contains more than one account with the same email address. Resolve the duplicate account before connecting.");
            return result;
        }
        catch (COMException exception) when (exception.HResult is unchecked((int)0x80010108) or unchecked((int)0x800706BA))
        {
            Disconnect();
            throw new OutlookMailException("Classic Outlook has disconnected. Open Outlook and connect again.");
        }
        finally
        {
            if (accounts is not null) Marshal.ReleaseComObject(accounts);
            if (session is not null) Marshal.ReleaseComObject(session);
        }
    }

    public string CreateDraft(string accountId, MailMessage message)
    {
        var existing = FindMessage(accountId, message.BatchId, out _);
        if (existing is not null)
        {
            Marshal.ReleaseComObject(existing);
            return message.BatchId;
        }

        object? account = null;
        object? folder = null;
        object? items = null;
        object? mail = null;
        object? properties = null;
        object? recipients = null;
        object? sentFolder = null;
        object? store = null;
        object? storeProperties = null;
        try
        {
            account = FindAccount(accountId);
            long maximumBytes = 0;
            try
            {
                store = ((dynamic)account).DeliveryStore;
                if (store is not null)
                {
                    storeProperties = ((dynamic)store).PropertyAccessor;
                    maximumBytes = Convert.ToInt64(((dynamic)storeProperties).GetProperty("http://schemas.microsoft.com/mapi/proptag/0x666D0003")) * 1024L;
                }
            }
            catch (COMException) { }
            if (maximumBytes > 0 && message.EstimatedEncodedBytes > maximumBytes)
                throw new MailSizeLimitException(maximumBytes, $"The selected Outlook store reports a maximum submission size of {maximumBytes} bytes (0x666D0003, kilobytes converted to bytes). No Send call started.", "draft preflight");
            folder = OpenAccountFolder(accountId, 16);
            sentFolder = OpenAccountFolder(accountId, 5);
            items = ((dynamic)folder!).Items;
            mail = ((dynamic)items).Add("IPM.Note");
            ((dynamic)mail).SendUsingAccount = (dynamic)account;
            ((dynamic)mail).DeleteAfterSubmit = false;
            if ((int)((dynamic)account).AccountType != 1)
                ((dynamic)mail).SaveSentMessageFolder = (dynamic)sentFolder!;
            ((dynamic)mail).Subject = message.Subject;
            if (message.IsHtml)
                ((dynamic)mail).HTMLBody = message.Body;
            else
                ((dynamic)mail).Body = message.Body;
            recipients = ((dynamic)mail).Recipients;
            foreach (var address in message.To)
            {
                object? recipient = null;
                try
                {
                    recipient = ((dynamic)recipients).Add(address);
                    ((dynamic)recipient).Type = 1;
                }
                finally { if (recipient is not null) Marshal.ReleaseComObject(recipient); }
            }
            foreach (var address in message.Cc)
            {
                object? recipient = null;
                try
                {
                    recipient = ((dynamic)recipients).Add(address);
                    ((dynamic)recipient).Type = 2;
                }
                finally { if (recipient is not null) Marshal.ReleaseComObject(recipient); }
            }
            properties = ((dynamic)mail).PropertyAccessor;
            ((dynamic)properties).SetProperty(BatchProperty, message.BatchId);
            ((dynamic)properties).SetProperty(AccountProperty, accountId);
            ((dynamic)mail).Save();
            return message.BatchId;
        }
        finally
        {
            if (storeProperties is not null) Marshal.ReleaseComObject(storeProperties);
            if (store is not null) Marshal.ReleaseComObject(store);
            if (sentFolder is not null) Marshal.ReleaseComObject(sentFolder);
            if (recipients is not null) Marshal.ReleaseComObject(recipients);
            if (properties is not null) Marshal.ReleaseComObject(properties);
            if (mail is not null) Marshal.ReleaseComObject(mail);
            if (items is not null) Marshal.ReleaseComObject(items);
            if (folder is not null) Marshal.ReleaseComObject(folder);
            if (account is not null) Marshal.ReleaseComObject(account);
        }
    }

    public void AddAttachments(string accountId, string batchId, IReadOnlyList<MailAttachment> attachments, CancellationToken cancellationToken)
    {
        var mail = FindMessage(accountId, batchId, out var status);
        object? collection = null;
        try
        {
            if (mail is null || status != MailSubmissionStatus.Draft)
                throw new OutlookMailException("The application draft is missing or has already been submitted. Reconcile its state before continuing.");
            collection = ((dynamic)mail).Attachments;
            for (var index = (int)((dynamic)collection).Count; index >= 1; index--)
                ((dynamic)collection).Remove(index);
            foreach (var attachment in attachments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (new FileInfo(attachment.Path).Length != attachment.Length)
                    throw new IOException($"The attachment changed after preparation: {attachment.Name}");
                object? added = null;
                object? properties = null;
                try
                {
                    try { added = ((dynamic)collection).Add(attachment.Path, 1, Type.Missing, attachment.Name); }
                    catch (COMException exception) when (exception.HResult is unchecked((int)0x80040305) or unchecked((int)0x800700DF))
                    {
                        throw new MailSizeLimitException(null, $"Outlook rejected attachment '{attachment.Name}' while adding files (0x{exception.HResult:X8}). No Send call started.", "attachment preparation");
                    }
                    if (attachment.ContentId is not null)
                    {
                        if (attachment.ContentType is not ("image/png" or "image/jpeg" or "image/gif" or "image/bmp"))
                            throw new InvalidDataException("An embedded photo has an unsupported content type.");
                        properties = ((dynamic)added).PropertyAccessor;
                        foreach (var (schema, value) in new (string Schema, object Value)[]
                        {
                            ("http://schemas.microsoft.com/mapi/proptag/0x3712001F", attachment.ContentId),
                            ("http://schemas.microsoft.com/mapi/proptag/0x370E001F", attachment.ContentType),
                            ("http://schemas.microsoft.com/mapi/proptag/0x7FFE000B", true)
                        })
                        {
                            try { ((dynamic)properties).SetProperty(schema, value); }
                            catch (COMException exception)
                            {
                                throw new OutlookMailException($"Outlook could not prepare embedded photo metadata at {schema} (0x{exception.HResult:X8}). No Send call started. Check that this account supports embedded photos.");
                            }
                        }
                    }
                }
                finally
                {
                    if (properties is not null) Marshal.ReleaseComObject(properties);
                    if (added is not null) Marshal.ReleaseComObject(added);
                }
            }
            ((dynamic)mail).Save();
        }
        finally
        {
            if (collection is not null) Marshal.ReleaseComObject(collection);
            if (mail is not null) Marshal.ReleaseComObject(mail);
        }
    }

    public void DeleteDraft(string accountId, string batchId)
    {
        var mail = FindMessage(accountId, batchId, out var status);
        try
        {
            if (mail is null)
                return;
            if (status != MailSubmissionStatus.Draft)
                throw new OutlookMailException("An email in Outbox or Sent Items cannot be deleted as an unfinished draft.");
            ((dynamic)mail).Delete();
        }
        finally { if (mail is not null) Marshal.ReleaseComObject(mail); }
    }

    public void SendDraft(string accountId, string batchId, CancellationToken cancellationToken)
    {
        object? mail = null;
        object? account = null;
        object? recipients = null;
        object? sentFolder = null;
        try
        {
            try
            {
                mail = FindMessage(accountId, batchId, out var status);
                if (mail is null)
                    throw new MailSubmissionRejectedException("MissingDraft", "The application draft could not be found. Recreate the draft before retrying.");
                if (status != MailSubmissionStatus.Draft)
                    return;
                account = FindAccount(accountId);
                recipients = ((dynamic)mail).Recipients;
                if ((int)((dynamic)recipients).Count == 0 || !(bool)((dynamic)recipients).ResolveAll())
                    throw new MailSubmissionRejectedException("UnresolvedRecipients", "Outlook could not resolve every recipient. Correct the recipient list before retrying.");
                sentFolder = OpenAccountFolder(accountId, 5);
                ((dynamic)mail).SendUsingAccount = (dynamic)account;
                ((dynamic)mail).DeleteAfterSubmit = false;
                if ((int)((dynamic)account).AccountType != 1)
                    ((dynamic)mail).SaveSentMessageFolder = (dynamic)sentFolder!;
                ((dynamic)mail).Save();
                if (cancellationToken.IsCancellationRequested)
                    throw new MailSubmissionRejectedException("CanceledBeforeSubmission", "The operation was canceled before Outlook was asked to send the email.");
            }
            catch (COMException exception)
            {
                throw new MailSubmissionRejectedException("OutlookPreflightFailed", $"Outlook did not submit the email because preparation failed (0x{exception.HResult:X8}). Check Outlook before retrying.");
            }
            ((dynamic)mail).Send();
        }
        finally
        {
            if (sentFolder is not null) Marshal.ReleaseComObject(sentFolder);
            if (recipients is not null) Marshal.ReleaseComObject(recipients);
            if (account is not null) Marshal.ReleaseComObject(account);
            if (mail is not null) Marshal.ReleaseComObject(mail);
        }
    }

    public MailSubmissionStatus GetSubmissionStatus(string accountId, string batchId)
    {
        var mail = FindMessage(accountId, batchId, out var status);
        if (mail is not null)
            Marshal.ReleaseComObject(mail);
        return status;
    }

    private object? FindMessage(string accountId, string batchId, out MailSubmissionStatus status)
    {
        if (!Guid.TryParseExact(batchId, "N", out _))
            throw new OutlookMailException("The stored email correlation is invalid. Review the batch before continuing.");
        if (string.IsNullOrWhiteSpace(accountId))
            throw new OutlookMailException("Select an Outlook account before processing email.");
        if (application is null)
            throw new OutlookMailException("Classic Outlook is not connected. Open Outlook and connect an account.");
        var filter = $"@SQL=\"{BatchProperty}/0000001f\" = '{batchId}' AND \"{AccountProperty}/0000001f\" = '{accountId.Replace("'", "''", StringComparison.Ordinal)}'";
        foreach (var location in new[] { 5, 4, -4, 16 })
        {
            object? folder = null;
            object? session = null;
            object? items = null;
            object? found = null;
            object? duplicate = null;
            try
            {
                if (location == -4)
                {
                    session = ((dynamic)application).Session;
                    folder = ((dynamic)session).GetDefaultFolder(4);
                }
                else
                {
                    try { folder = OpenAccountFolder(accountId, location); }
                    catch (COMException exception) when (location == 4 && exception.HResult == unchecked((int)0x8004010F)) { continue; }
                }
                if (folder is null)
                {
                    if (location == 4)
                        continue;
                    throw new OutlookMailException("The Outlook profile does not provide the folder required to track this email safely.");
                }
                items = ((dynamic)folder).Items;
                found = ((dynamic)items).Find(filter);
                if (found is null)
                    continue;
                duplicate = ((dynamic)items).FindNext();
                if (duplicate is not null)
                    throw new OutlookMailException("More than one Outlook email matches this batch. Review the matching emails before continuing.");
                status = location == 5 && (bool)((dynamic)found).Sent ? MailSubmissionStatus.Sent
                    : location != 16 || (bool)((dynamic)found).Submitted ? MailSubmissionStatus.Queued : MailSubmissionStatus.Draft;
                var result = found;
                found = null;
                return result;
            }
            finally
            {
                if (duplicate is not null) Marshal.ReleaseComObject(duplicate);
                if (found is not null) Marshal.ReleaseComObject(found);
                if (items is not null) Marshal.ReleaseComObject(items);
                if (folder is not null) Marshal.ReleaseComObject(folder);
                if (session is not null) Marshal.ReleaseComObject(session);
            }
        }
        status = MailSubmissionStatus.Missing;
        return null;
    }

    private object FindAccount(string accountId)
    {
        if (application is null)
            throw new OutlookMailException("Classic Outlook is not connected. Open Outlook and connect an account.");
        object? session = null;
        object? accounts = null;
        object? selected = null;
        try
        {
            session = ((dynamic)application).Session;
            accounts = ((dynamic)session).Accounts;
            for (var index = 1; index <= (int)((dynamic)accounts).Count; index++)
            {
                object? account = null;
                try
                {
                    account = ((dynamic)accounts).Item(index);
                    if (((string)((dynamic)account).SmtpAddress).Trim().ToLowerInvariant() != accountId)
                        continue;
                    if (selected is not null)
                        throw new OutlookMailException("The selected email address appears more than once in the Outlook profile.");
                    selected = account;
                    account = null;
                }
                finally { if (account is not null) Marshal.ReleaseComObject(account); }
            }
            var result = selected ?? throw new OutlookMailException("The selected account is no longer available in the Outlook profile. Connect and select an account again.");
            selected = null;
            return result;
        }
        finally
        {
            if (selected is not null) Marshal.ReleaseComObject(selected);
            if (accounts is not null) Marshal.ReleaseComObject(accounts);
            if (session is not null) Marshal.ReleaseComObject(session);
        }
    }

    private object? OpenAccountFolder(string accountId, int folderType)
    {
        object? account = null;
        object? store = null;
        try
        {
            account = FindAccount(accountId);
            store = ((dynamic)account).DeliveryStore;
            if (store is null)
                throw new OutlookMailException("The selected Outlook account has no delivery store. Choose an account with Drafts and Sent Items folders.");
            object? folder = ((dynamic)store).GetDefaultFolder(folderType);
            if (folder is null && folderType != 4)
                throw new OutlookMailException("The selected Outlook account does not provide both Drafts and Sent Items. Choose an account with a supported delivery store.");
            return folder;
        }
        finally
        {
            if (store is not null) Marshal.ReleaseComObject(store);
            if (account is not null) Marshal.ReleaseComObject(account);
        }
    }

    public void Disconnect()
    {
        if (application is not null)
        {
            Marshal.ReleaseComObject(application);
            application = null;
        }
    }

    public void Dispose() => Disconnect();

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid classId, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
}
