namespace OutlookAutomail;

public sealed class AppSettings
{
    public string InstallationId { get; set; } = Guid.NewGuid().ToString("N");
    public string AccountId { get; set; } = "";
    public bool StartAtSignIn { get; set; }
    public List<FolderSubscription> Folders { get; set; } = [];
}

public sealed class FolderSubscription
{
    public string RootPath { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class FolderConfiguration
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Documents";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset ActiveFromUtc { get; set; } = DateTimeOffset.UtcNow;
    public string PendingFolder { get; set; } = "To send";
    public string SentFolder { get; set; } = "Sent";
    public List<string> To { get; set; } = [];
    public List<string> Cc { get; set; } = [];
    public string Subject { get; set; } = "Documents";
    public string Body { get; set; } = "Please find the attached documents.";
    public bool BodyIsHtml { get; set; }
    public List<InlineImageConfiguration> InlineImages { get; set; } = [];
    public string DateFormat { get; set; } = "dd/MM/yyyy";
    public string DateTimeZoneId { get; set; } = "UTC";
    public bool SendWhenEmpty { get; set; }
    public bool WaitForFiles { get; set; }
    public List<ScheduleConfiguration> Schedules { get; set; } = [new()];
}

public sealed class InlineImageConfiguration
{
    public string Id { get; set; } = "image-" + Guid.NewGuid().ToString("N") + "@outlookautomail";
    public string Path { get; set; } = "";
}

public sealed class ScheduleConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public string Time { get; set; } = "06:00";
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public List<DayOfWeek> Days { get; set; } = Enum.GetValues<DayOfWeek>().ToList();
}

public sealed record MailAccount(string Id, string Address, string DisplayName);
public sealed record MailAttachment(string Name, string Path, long Length, string? ContentId = null, string? ContentType = null);
public sealed record MailMessage(string BatchId, string Subject, string Body, bool IsHtml, IReadOnlyList<string> To, IReadOnlyList<string> Cc, IReadOnlyList<MailAttachment> Attachments)
{
    public long EstimatedEncodedBytes => 8192 + System.Text.Encoding.UTF8.GetByteCount(Subject) * 2L
        + System.Text.Encoding.UTF8.GetByteCount(Body) * 2L
        + To.Concat(Cc).Sum(address => System.Text.Encoding.UTF8.GetByteCount(address) + 128L)
        + Attachments.Sum(attachment => ((attachment.Length + 2) / 3) * 4
            + ((((attachment.Length + 2) / 3) * 4 + 75) / 76) * 2 + 2048);
}
public enum MailSubmissionStatus { Draft, Queued, Sent, Missing }

public class MailSubmissionRejectedException(string errorCode, string message) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
}

public sealed class MailSizeLimitException(long? maximumMessageBytes, string evidence, string stage)
    : MailSubmissionRejectedException("MessageTooLarge", $"The message exceeds an Outlook size limit during {stage}. {evidence}")
{
    public long? MaximumMessageBytes { get; } = maximumMessageBytes;
    public string Evidence { get; } = evidence;
    public string Stage { get; } = stage;
}

public interface IMailClient
{
    Task<MailAccount?> GetAccountAsync(CancellationToken cancellationToken);
    Task<string> CreateDraftAsync(MailMessage message, CancellationToken cancellationToken);
    Task AddAttachmentsAsync(string draftId, IReadOnlyList<MailAttachment> attachments, CancellationToken cancellationToken);
    Task DeleteDraftAsync(string draftId, CancellationToken cancellationToken);
    Task SendDraftAsync(string draftId, CancellationToken cancellationToken);
    Task<MailSubmissionStatus> GetSubmissionStatusAsync(string draftId, CancellationToken cancellationToken);
}

public sealed record FolderStatus(string RootPath, string Name, string State, string Detail, DateTimeOffset? LastRunUtc = null);
