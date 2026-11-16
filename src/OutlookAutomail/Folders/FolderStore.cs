using System.Net.Mail;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Velopack.Locators;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.Converters;
using YamlDotNet.Serialization.NamingConventions;

namespace OutlookAutomail;

public static class FolderStore
{
    internal const string MetadataFolder = ".automail";
    internal const string CurrentTransport = "classicOutlook";
    public const string MessageAssetsFolder = "Message assets";
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly IDeserializer YamlReader = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithTypeConverter(new DateTimeOffsetConverter())
        .WithDuplicateKeyChecking()
        .Build();
    private static readonly ISerializer YamlWriter = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithTypeConverter(new DateTimeOffsetConverter())
        .Build();

    public static FolderConfiguration LoadConfiguration(string root)
    {
        var path = Path.Combine(Path.GetFullPath(root), "config.yml");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        var yaml = new YamlStream();
        yaml.Load(new StringReader(content));
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode mapping
            || !mapping.Children.ContainsKey(new YamlScalarNode("id"))
            || !mapping.Children.ContainsKey(new YamlScalarNode("active_from_utc"))
            || !mapping.Children.TryGetValue(new YamlScalarNode("schedules"), out var scheduleNode)
            || scheduleNode is not YamlSequenceNode schedules
            || schedules.Children.Any(node => node is not YamlMappingNode schedule
                || !schedule.Children.ContainsKey(new YamlScalarNode("id"))))
            throw new InvalidDataException("The YAML must explicitly include a stable folder id, active_from_utc, schedules, and an id for each schedule.");
        var configuration = YamlReader.Deserialize<FolderConfiguration>(content)
            ?? throw new InvalidDataException("The folder configuration is empty.");
        ValidateConfiguration(root, configuration);
        return configuration;
    }

    public static void SaveConfiguration(string root, FolderConfiguration configuration)
    {
        ValidateConfiguration(root, configuration);
        using var ownership = AcquireOwnership(root);
        var path = Path.Combine(Path.GetFullPath(root), "config.yml");
        if (File.Exists(path))
        {
            var previous = LoadConfiguration(root);
            if (previous.Id != configuration.Id)
                throw new InvalidDataException("The existing folder ID cannot be changed.");
            configuration.ActiveFromUtc = previous.ActiveFromUtc;
        }
        Directory.CreateDirectory(ResolveFolder(root, configuration.PendingFolder));
        Directory.CreateDirectory(ResolveFolder(root, configuration.SentFolder));
        WriteDurableFile(path, YamlWriter.Serialize(configuration));
    }

    public static InlineImageConfiguration ImportInlineImage(string root, FolderConfiguration configuration, Stream source)
    {
        var image = new InlineImageConfiguration();
        image.Path = Path.Combine(MessageAssetsFolder, image.Id + ".png");
        _ = ResolveInlineImage(root, configuration, image);
        var contentType = ValidateInlineImage(source);
        var extension = contentType switch { "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/bmp" => ".bmp", _ => ".png" };
        image.Path = Path.Combine(MessageAssetsFolder, image.Id + extension);
        var target = ResolveInlineImage(root, configuration, image);
        using var ownership = AcquireOwnership(root);
        if (File.Exists(Path.Combine(root, "config.yml")))
            _ = ResolveInlineImage(root, LoadConfiguration(root), image);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
            source.Position = 0;
            source.CopyTo(output);
            output.Flush(true);
        }
        catch
        {
            if (File.Exists(target))
                File.Delete(target);
            throw;
        }
        return image;
    }

    public static string ResolveInlineImage(string root, FolderConfiguration configuration, InlineImageConfiguration image)
    {
        if (!Regex.IsMatch(image.Id, "^[A-Za-z0-9][A-Za-z0-9._@-]{0,199}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            throw new InvalidDataException("Each embedded photo needs a unique content ID using letters, digits, '.', '_', '@', or '-'.");
        var assets = ValidateFolderLayout(root, configuration);
        var resolved = ResolveFolder(root, image.Path);
        if (!resolved.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Embedded photos must be in Message assets, separate from the pending and sent folders.");
        if (File.Exists(resolved) && File.GetAttributes(resolved).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Embedded photos cannot use file links.");
        return resolved;
    }

    public static IReadOnlyList<string> ValidateInlineImageReferences(string body, bool isHtml, IEnumerable<string> contentIds)
    {
        var references = Regex.Matches(WebUtility.HtmlDecode(body), "\\bcid:([^\\\"'\\s<>)]*)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
        if (references.Count > 0 && !isHtml)
            throw new InvalidDataException("Embedded photos require an HTML message body.");
        var available = contentIds.ToHashSet(StringComparer.Ordinal);
        foreach (var id in references)
            if (!available.Contains(id))
                throw new InvalidDataException($"The message references embedded photo '{id}' without a matching image. Add the photo or remove its cid reference. Existing batches can only use their original frozen photos.");
        return references;
    }

    public static string ValidateInlineImage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ValidateInlineImage(stream);
    }

    internal static string ValidateInlineImage(Stream stream)
    {
        Span<byte> header = stackalloc byte[32];
        stream.Position = 0;
        var count = stream.ReadAtLeast(header, header.Length, false);
        if (count >= 24 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && header[12..16].SequenceEqual("IHDR"u8))
            return "image/png";
        if (count >= 4 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff)
            return "image/jpeg";
        if (count >= 13 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8)))
            return "image/gif";
        if (count >= 26 && header[..2].SequenceEqual("BM"u8))
            return "image/bmp";
        throw new InvalidDataException("Choose a PNG, JPEG, GIF, or BMP photo with a valid image header.");
    }

    public static void RegisterWorker(string root, string installationId, string? machineName = null)
    {
        if (!Guid.TryParseExact(installationId, "N", out _))
            throw new InvalidDataException("The installation ID is invalid.");
        using var ownership = AcquireOwnership(root);
        var configuration = LoadConfiguration(root);
        var state = LoadState(root, configuration);
        if (state.Workers.All(worker => worker.InstallationId != installationId))
        {
            state.Workers.Add(new WorkerRegistration
            {
                InstallationId = installationId,
                MachineName = machineName ?? Environment.MachineName,
                RegisteredUtc = DateTimeOffset.UtcNow
            });
            SaveState(root, state);
        }
    }

    public static IReadOnlyList<RunRecord> GetHistory(string root, int maximum = 200)
    {
        var directory = Path.Combine(Path.GetFullPath(root), MetadataFolder, "runs");
        if (!Directory.Exists(directory))
            return [];
        var history = new List<RunRecord>();
        foreach (var batch in new DirectoryInfo(directory).EnumerateDirectories()
                     .OrderByDescending(batch => batch.LastWriteTimeUtc).Take(maximum))
        {
            if (!Guid.TryParseExact(batch.Name, "N", out _) || batch.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("The execution history contains an invalid batch directory.");
            if (!File.Exists(Path.Combine(batch.FullName, "batch.json")))
                continue;
            history.Add(LoadRun(root, batch.Name));
        }
        return history.OrderByDescending(run => run.CreatedUtc).ToList();
    }

    internal static FileStream AcquireOwnership(string root)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The configured shared folder is unavailable.");
        Directory.CreateDirectory(Path.Combine(root, MetadataFolder));
        try
        {
            return new FileStream(Path.Combine(root, MetadataFolder, "worker.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
        {
            throw new FolderBusyException("Another worker is using this folder.", exception);
        }
    }

    internal static void ValidateConfiguration(string root, FolderConfiguration configuration)
    {
        if (configuration.SchemaVersion != 1)
            throw new InvalidDataException("This configuration version is not supported.");
        if (!Guid.TryParseExact(configuration.Id, "N", out _))
            throw new InvalidDataException("The folder ID is invalid.");
        if (string.IsNullOrWhiteSpace(configuration.Name))
            throw new InvalidDataException("Enter a folder name.");
        _ = ValidateFolderLayout(root, configuration);
        if (configuration.To.Count == 0)
            throw new InvalidDataException("Enter at least one To recipient.");
        foreach (var address in configuration.To.Concat(configuration.Cc))
            if (!MailAddress.TryCreate(address, out var parsed) || parsed.Address != address)
                throw new InvalidDataException($"The email address '{address}' is invalid.");
        if (string.IsNullOrWhiteSpace(configuration.Subject))
            throw new InvalidDataException("Enter an email subject.");
        _ = MessageTemplate.Render(configuration, DateTimeOffset.UnixEpoch);
        if (configuration.InlineImages.Select(image => image.Id).Distinct(StringComparer.Ordinal).Count() != configuration.InlineImages.Count)
            throw new InvalidDataException("Each embedded photo must have a unique content ID.");
        foreach (var image in configuration.InlineImages)
            _ = ResolveInlineImage(root, configuration, image);
        _ = ValidateInlineImageReferences(configuration.Body, configuration.BodyIsHtml, configuration.InlineImages.Select(image => image.Id));
        if (configuration.FileReadySeconds < 0 || configuration.FileReadySeconds > 86400)
            throw new InvalidDataException("The file readiness delay must be between 0 and 86400 seconds.");
        if (configuration.MaximumMessageSizeMb is < 1 or > 150)
            throw new InvalidDataException("The message size limit must be between 1 and 150 MB.");
        if (configuration.Schedules.Select(schedule => schedule.Id).Distinct().Count() != configuration.Schedules.Count)
            throw new InvalidDataException("Each schedule must have a unique ID.");
        foreach (var schedule in configuration.Schedules)
        {
            if (!Guid.TryParseExact(schedule.Id, "N", out _)
                || !TimeOnly.TryParseExact(schedule.Time, "HH:mm", out _)
                || schedule.Days.Count == 0 || schedule.Days.Any(day => !Enum.IsDefined(day)))
                throw new InvalidDataException("Each schedule needs a valid ID, time in HH:mm, and at least one day.");
            _ = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        }
    }

    private static string ValidateFolderLayout(string root, FolderConfiguration configuration)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var installationDirectory = VelopackLocator.IsCurrentSet && VelopackLocator.Current.CurrentlyInstalledVersion is not null
            ? VelopackLocator.Current.RootAppDir!
            : AppContext.BaseDirectory;
        foreach (var directory in new[] { SettingsStore.RootDirectory, installationDirectory })
        {
            var owned = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (root.Equals(owned, StringComparison.OrdinalIgnoreCase)
                || root.StartsWith(owned + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || owned.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Choose a shared folder separate from the app installation and local app data folders.");
        }
        var pending = ResolveFolder(root, configuration.PendingFolder);
        var sent = ResolveFolder(root, configuration.SentFolder);
        var assets = ResolveFolder(root, MessageAssetsFolder);
        if (pending.Equals(sent, StringComparison.OrdinalIgnoreCase)
            || pending.StartsWith(sent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || sent.StartsWith(pending + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The pending and sent folders must be separate and cannot contain each other.");
        if (new[] { pending, sent }.Any(folder => assets.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || assets.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || folder.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Message assets is reserved for reusable photos and must remain separate from the pending and sent folders.");
        return assets;
    }

    internal static string ResolveFolder(string root, string relativePath)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)
            || relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part == ".." || part.Equals(MetadataFolder, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Folder paths must stay inside the configured root and outside .automail.");
        var resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, relativePath)));
        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Folder paths must stay inside the configured root.");
        var current = new DirectoryInfo(resolved);
        while (current.FullName.Length > root.Length)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Pending and sent folders cannot use directory links.");
            current = current.Parent ?? throw new InvalidDataException("The folder path is invalid.");
        }
        return resolved;
    }

    internal static FolderState LoadState(string root, FolderConfiguration configuration)
    {
        var path = Path.Combine(root, MetadataFolder, "state.json");
        FolderState state;
        if (File.Exists(path))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            state = JsonSerializer.Deserialize<FolderState>(stream, JsonOptions)
                ?? throw new InvalidDataException("The shared execution state is empty.");
        }
        else
        {
            if (Directory.Exists(Path.Combine(root, MetadataFolder, "runs"))
                && Directory.EnumerateDirectories(Path.Combine(root, MetadataFolder, "runs")).Any())
                throw new InvalidDataException("The shared execution state is missing. Restore it before processing existing batches.");
            state = new FolderState { FolderId = configuration.Id };
        }
        if (state.FolderId != configuration.Id)
            throw new InvalidDataException("The configuration does not match this folder's execution history.");
        if (state.Workers.Any(worker => !Guid.TryParseExact(worker.InstallationId, "N", out _))
            || state.Workers.Select(worker => worker.InstallationId).Distinct().Count() != state.Workers.Count)
            throw new InvalidDataException("The shared worker registry contains invalid installation IDs.");
        return state;
    }

    internal static RunRecord LoadRun(string root, string runId)
    {
        if (!Guid.TryParseExact(runId, "N", out _))
            throw new InvalidDataException("The execution ID is invalid.");
        var directory = Path.Combine(root, MetadataFolder, "runs", runId);
        var progressPath = Path.Combine(directory, "run.json");
        using var stream = new FileStream(File.Exists(progressPath) ? progressPath : Path.Combine(directory, "batch.json"), FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var progressDocument = JsonDocument.Parse(stream);
        if (!progressDocument.RootElement.TryGetProperty("transport", out var progressTransport)
            || progressTransport.GetString() != CurrentTransport)
            throw new InvalidDataException("This batch belongs to a previous mail connector. Review and finish it with the previous version before using this folder.");
        var run = progressDocument.Deserialize<RunRecord>(JsonOptions)
            ?? throw new InvalidDataException("An execution record is empty.");
        var mutable = run;
        using (var manifestStream = new FileStream(Path.Combine(directory, "batch.json"), FileMode.Open,
                   FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            using var manifestDocument = JsonDocument.Parse(manifestStream);
            if (!manifestDocument.RootElement.TryGetProperty("transport", out var manifestTransport)
                || manifestTransport.GetString() != CurrentTransport)
                throw new InvalidDataException("This batch belongs to a previous mail connector. Review and finish it with the previous version before using this folder.");
            var manifest = manifestDocument.Deserialize<RunRecord>(JsonOptions)
                ?? throw new InvalidDataException("The batch manifest is empty.");
            if (manifest.Id != runId)
                throw new InvalidDataException("The batch manifest has an invalid ID.");
            run.Attachments = manifest.Attachments;
            run.PendingFolder = manifest.PendingFolder;
            run.SentFolder = manifest.SentFolder;
            run.ScheduleId = manifest.ScheduleId;
            run.OccurrenceUtc = manifest.OccurrenceUtc;
            run.CoveredOccurrences = manifest.CoveredOccurrences;
        }
        var submissionPath = Directory.EnumerateFiles(directory, "submission-*.json")
            .OrderDescending(StringComparer.Ordinal).FirstOrDefault();
        var markerNames = new List<string>();
        if (submissionPath is not null)
        {
            markerNames.Add(Path.GetFileName(submissionPath));
            var rejection = Path.GetFileName(submissionPath).Replace("submission-", "rejected-", StringComparison.Ordinal);
            if (File.Exists(Path.Combine(directory, rejection)))
                markerNames.Add(rejection);
        }
        markerNames.Add("skipped.json");
        markerNames.Add("queued.json");
        markerNames.Add("sent.json");
        markerNames.Add("archived.json");
        foreach (var markerName in markerNames)
        {
            var path = Path.Combine(directory, markerName);
            if (!File.Exists(path))
                continue;
            using var marker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var markerDocument = JsonDocument.Parse(marker);
            if (!markerDocument.RootElement.TryGetProperty("transport", out var markerTransport)
                || markerTransport.GetString() != CurrentTransport)
                throw new InvalidDataException("This batch belongs to a previous mail connector. Review and finish it with the previous version before using this folder.");
            var authoritative = markerDocument.Deserialize<RunRecord>(JsonOptions)
                ?? throw new InvalidDataException("A submission marker is empty. Review this batch before continuing.");
            var expected = markerName.StartsWith("submission-", StringComparison.Ordinal) ? RunStatus.Submitting
                : markerName.StartsWith("rejected-", StringComparison.Ordinal) ? RunStatus.Rejected
                : markerName == "archived.json" ? RunStatus.Archived
                : markerName == "skipped.json" ? RunStatus.SkippedEmpty
                : markerName == "queued.json" ? RunStatus.Queued : RunStatus.Sent;
            if (authoritative.Id != runId
                || (expected == RunStatus.SkippedEmpty ? authoritative.Attachments.Count != 0 : authoritative.DraftId.Length == 0)
                || authoritative.SubmissionAttempt < (expected is RunStatus.Queued or RunStatus.Sent or RunStatus.Archived or RunStatus.SkippedEmpty ? 0 : 1)
                || authoritative.Status != expected)
                throw new InvalidDataException("A submission marker is invalid. Review this batch before continuing.");
            if (expected is RunStatus.Submitting or RunStatus.Queued && mutable.Status == RunStatus.NeedsReview
                && mutable.AccountId == authoritative.AccountId && mutable.DraftId == authoritative.DraftId
                && mutable.SubmissionAttempt == authoritative.SubmissionAttempt)
            {
                authoritative.Status = RunStatus.NeedsReview;
                authoritative.Detail = mutable.Detail;
            }
            if (expected == RunStatus.Sent && mutable.Status == RunStatus.Archived
                && mutable.AccountId == authoritative.AccountId && mutable.DraftId == authoritative.DraftId
                && mutable.SubmissionAttempt == authoritative.SubmissionAttempt)
                authoritative.Status = RunStatus.Archived;
            run = authoritative;
        }
        if (run.Id != runId)
            throw new InvalidDataException("An execution record has an invalid batch ID.");
        return run;
    }

    internal static IReadOnlyList<RunRecord> GetActiveRuns(string root)
    {
        var directory = Path.Combine(root, MetadataFolder, "active");
        if (!Directory.Exists(directory))
            return [];
        var runs = new List<RunRecord>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (JsonSerializer.Deserialize<string>(stream) != id)
                throw new InvalidDataException("An active batch claim is invalid.");
            runs.Add(LoadRun(root, id));
        }
        return runs.OrderBy(run => run.CreatedUtc).ToList();
    }

    internal static void ActivateBatch(string root, RunRecord run)
    {
        if ((run.Status == RunStatus.Archived && File.Exists(Path.Combine(root, MetadataFolder, "runs", run.Id, "archived.json")))
            || (run.Status == RunStatus.SkippedEmpty && File.Exists(Path.Combine(root, MetadataFolder, "runs", run.Id, "skipped.json"))))
            return;
        var directory = Path.Combine(root, MetadataFolder, "active");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, run.Id + ".json");
        try { WriteDurableFile(path, JsonSerializer.Serialize(run.Id), false); }
        catch (IOException) when (File.Exists(path)) { }
    }

    internal static void RetireBatch(string root, RunRecord run)
    {
        var proof = LoadRun(root, run.Id);
        if (proof.Status is not (RunStatus.Archived or RunStatus.SkippedEmpty)
            || !File.Exists(Path.Combine(root, MetadataFolder, "runs", run.Id,
                proof.Status == RunStatus.Archived ? "archived.json" : "skipped.json")))
            throw new InvalidOperationException("Only a durably completed batch can leave the active queue.");
        File.Delete(Path.Combine(root, MetadataFolder, "active", run.Id + ".json"));
    }

    internal static void SaveState(string root, FolderState state)
    {
        WriteDurableFile(Path.Combine(root, MetadataFolder, "state.json"), JsonSerializer.Serialize(state, JsonOptions));
    }

    internal static RunRecord CreateBatch(string root, RunRecord run, IReadOnlyDictionary<string, Stream>? inlineImages = null)
    {
        var directory = Path.Combine(root, MetadataFolder, "runs", run.Id);
        if (Directory.Exists(directory))
            return LoadRun(root, run.Id);
        var temporary = Path.Combine(root, MetadataFolder, "preparing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            if (inlineImages is { Count: > 0 })
            {
                Directory.CreateDirectory(Path.Combine(temporary, "images"));
                foreach (var (name, source) in inlineImages)
                {
                    if (name != Path.GetFileName(name) || string.IsNullOrWhiteSpace(name))
                        throw new InvalidDataException("The image snapshot contains an invalid file name.");
                    using var output = new FileStream(Path.Combine(temporary, "images", name), FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
                    source.Position = 0;
                    source.CopyTo(output);
                    output.Flush(true);
                }
            }
            var content = JsonSerializer.Serialize(run, JsonOptions);
            WriteDurableFile(Path.Combine(temporary, "batch.json"), content, false);
            WriteDurableFile(Path.Combine(temporary, "run.json"), content, false);
            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            try { Directory.Move(temporary, directory); }
            catch (IOException) when (Directory.Exists(directory)) { return LoadRun(root, run.Id); }
            return run;
        }
        finally
        {
            if (Directory.Exists(temporary))
                Directory.Delete(temporary, true);
        }
    }

    internal static void SaveRun(string root, RunRecord run)
    {
        if (!Guid.TryParseExact(run.Id, "N", out _))
            throw new InvalidDataException("The execution ID is invalid.");
        var directory = Path.Combine(root, MetadataFolder, "runs", run.Id);
        if (!File.Exists(Path.Combine(directory, "batch.json")))
            throw new InvalidDataException("The batch manifest is missing. Restore it before continuing.");
        if (Directory.EnumerateFiles(directory, "submission-*.json").Any()
            || File.Exists(Path.Combine(directory, "queued.json"))
            || File.Exists(Path.Combine(directory, "sent.json"))
            || File.Exists(Path.Combine(directory, "archived.json"))
            || File.Exists(Path.Combine(directory, "skipped.json")))
        {
            var authoritative = LoadRun(root, run.Id);
            var canPrepareAgain = authoritative.Status == RunStatus.Rejected
                && run.SubmissionAttempt == authoritative.SubmissionAttempt;
            if (!canPrepareAgain && (run.Status is RunStatus.Preparing or RunStatus.DraftReady
                || run.SubmissionAttempt != authoritative.SubmissionAttempt
                || run.AccountId != authoritative.AccountId || run.DraftId != authoritative.DraftId
                || (authoritative.Status is RunStatus.Sent or RunStatus.Archived
                    && run.Status is not (RunStatus.Sent or RunStatus.Archived))))
                throw new InvalidOperationException("Another worker has already claimed this batch's submission.");
        }
        WriteDurableFile(Path.Combine(directory, "run.json"), JsonSerializer.Serialize(run, JsonOptions));
    }

    internal static bool TryBeginSubmission(string root, RunRecord run)
    {
        if (File.Exists(Path.Combine(root, MetadataFolder, "runs", run.Id, "queued.json"))
            || File.Exists(Path.Combine(root, MetadataFolder, "runs", run.Id, "sent.json"))
            || File.Exists(Path.Combine(root, MetadataFolder, "runs", run.Id, "skipped.json")))
            return false;
        run.SubmissionAttempt++;
        var path = Path.Combine(root, MetadataFolder, "runs", run.Id, $"submission-{run.SubmissionAttempt:D6}.json");
        FileStream stream;
        try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough); }
        catch (IOException) when (File.Exists(path)) { return false; }
        using (stream)
        {
            JsonSerializer.Serialize(stream, run, JsonOptions);
            stream.Flush(true);
        }
        return true;
    }

    internal static void RecordRejection(string root, RunRecord run)
    {
        var path = Path.Combine(root, MetadataFolder, "runs", run.Id, $"rejected-{run.SubmissionAttempt:D6}.json");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, run, JsonOptions);
        stream.Flush(true);
    }

    internal static void RecordOutcome(string root, RunRecord run)
    {
        var name = run.Status switch
        {
            RunStatus.Queued => "queued.json",
            RunStatus.Sent => "sent.json",
            RunStatus.Archived => "archived.json",
            RunStatus.SkippedEmpty => "skipped.json",
            _ => throw new InvalidOperationException("Only queued, sent, archived, or skipped outcomes can be recorded.")
        };
        var path = Path.Combine(root, MetadataFolder, "runs", run.Id, name);
        FileStream stream;
        try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough); }
        catch (IOException) when (File.Exists(path))
        {
            var recorded = LoadRun(root, run.Id);
            if (recorded.Status is not (RunStatus.Queued or RunStatus.NeedsReview or RunStatus.Sent or RunStatus.Archived or RunStatus.SkippedEmpty)
                || recorded.AccountId != run.AccountId || recorded.DraftId != run.DraftId)
                throw new InvalidDataException("The recorded mail outcome does not match this batch.");
            return;
        }
        using (stream)
        {
            JsonSerializer.Serialize(stream, run, JsonOptions);
            stream.Flush(true);
        }
    }

    internal static void WriteHeartbeat(string root, WorkerHeartbeat heartbeat)
    {
        if (!Guid.TryParseExact(heartbeat.InstallationId, "N", out _))
            throw new InvalidDataException("The installation ID is invalid.");
        var directory = Path.Combine(root, MetadataFolder, "workers");
        Directory.CreateDirectory(directory);
        WriteDurableFile(Path.Combine(directory, heartbeat.InstallationId + ".json"), JsonSerializer.Serialize(heartbeat, JsonOptions));
    }

    internal static void WriteDurableFile(string path, string content, bool overwrite = true)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                var data = Encoding.UTF8.GetBytes(content);
                stream.Write(data);
                stream.Flush(true);
            }
            File.Move(temporaryPath, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

public sealed class FolderBusyException(string message, Exception? innerException = null) : IOException(message, innerException);

public enum RunStatus { Preparing, DraftReady, Submitting, Queued, Sent, Archived, SkippedEmpty, NeedsReview, Rejected }

public sealed class RunRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Transport { get; set; } = "";
    public string? ScheduleId { get; set; }
    public DateTimeOffset? OccurrenceUtc { get; set; }
    public Dictionary<string, DateTimeOffset> CoveredOccurrences { get; set; } = [];
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public RunStatus Status { get; set; }
    public string Detail { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string AccountAddress { get; set; } = "";
    public string WorkerName { get; set; } = "";
    public string DraftId { get; set; } = "";
    public int SubmissionAttempt { get; set; }
    public string PendingFolder { get; set; } = "";
    public string SentFolder { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public bool BodyIsHtml { get; set; }
    public List<string> To { get; set; } = [];
    public List<string> Cc { get; set; } = [];
    public List<RunAttachment> Attachments { get; set; } = [];
}

public sealed class RunAttachment
{
    public string Name { get; set; } = "";
    public long Length { get; set; }
    public DateTimeOffset LastWriteUtc { get; set; }
    public string Sha256 { get; set; } = "";
    public string? ContentId { get; set; }
    public string? ContentType { get; set; }
    public string? SourcePath { get; set; }
}

internal sealed class FolderState
{
    public string FolderId { get; set; } = "";
    public DateTimeOffset? LastRunUtc { get; set; }
    public List<WorkerRegistration> Workers { get; set; } = [];
    public Dictionary<string, DateTimeOffset> CompletedThrough { get; set; } = [];
}

public sealed class WorkerRegistration
{
    public string InstallationId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public DateTimeOffset RegisteredUtc { get; set; }
}

internal sealed class WorkerHeartbeat
{
    public string InstallationId { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; }
    public bool Ready { get; set; }
}
