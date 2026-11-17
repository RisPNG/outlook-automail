using Xunit;

namespace OutlookAutomail.Tests;

public sealed class MachineVariableTests
{
    private const string FirstInstallation = "11111111111111111111111111111111";
    private const string SecondInstallation = "22222222222222222222222222222222";

    [Theory]
    [InlineData(FirstInstallation, "Jason")]
    [InlineData(SecondInstallation, "Lucia")]
    public void EachInstallationUsesItsOwnValuesEvenWhenMachineNamesMatch(string installationId, string name)
    {
        var configuration = new FolderConfiguration
        {
            Subject = "Documents from {name} - {date:yyyy-MM-dd}",
            Body = "Best Regards,\n{name}",
            MachineVariables =
            [
                new() { InstallationId = FirstInstallation, MachineName = "Office PC", Values = new() { ["name"] = "Jason" } },
                new() { InstallationId = SecondInstallation, MachineName = "Office PC", Values = new() { ["name"] = "Lucia" } }
            ]
        };

        var rendered = MessageTemplate.Render(configuration, DateTimeOffset.Parse("2030-01-03T08:00:00Z"), installationId);

        Assert.Equal($"Documents from {name} - 2030-01-03", rendered.Subject);
        Assert.Equal($"Best Regards,\n{name}", rendered.Body);
    }

    [Fact]
    public void ValuesAreLiteralTextAndHtmlIsEncodedOnceWhileEscapesAndOtherBracesSurvive()
    {
        var configuration = new FolderConfiguration
        {
            Subject = "{name} / {{name}} / {date:yyyy}",
            Body = "<p>{name} / {{name}} / {date:yyyy} / {unknown} / {Name}</p><style>.notice { color: red; }</style>{\"amount\": 1}",
            BodyIsHtml = true,
            MachineVariables = [new() { InstallationId = FirstInstallation, Values = new() { ["name"] = "Jason <&> {date} {other}", ["other"] = "recursive" } }]
        };

        var rendered = MessageTemplate.Render(configuration, DateTimeOffset.Parse("2030-01-03T08:00:00Z"), FirstInstallation);

        Assert.Equal("Jason <&> {date} {other} / {name} / 2030", rendered.Subject);
        Assert.Equal("<p>Jason &lt;&amp;&gt; {date} {other} / {name} / 2030 / {unknown} / {Name}</p><style>.notice { color: red; }</style>{\"amount\": 1}", rendered.Body);
    }

    [Fact]
    public void HtmlEditorRoundTripPreservesCustomPlaceholdersAlongsideFormattedDates()
    {
        const string body = "<p>Best Regards,<br>{name} / {{name}} / {date:'<Document &>' yyyy}</p>";
        var configuration = new FolderConfiguration
        {
            Body = MessageTemplate.ReadBodyFromEditor(MessageTemplate.PrepareBodyForEditor(body, true)),
            BodyIsHtml = true,
            MachineVariables = [new() { InstallationId = FirstInstallation, Values = new() { ["name"] = "Jason & Lucia" } }]
        };

        Assert.Equal(body, configuration.Body);
        Assert.Equal("<p>Best Regards,<br>Jason &amp; Lucia / {name} / &lt;Document &amp;&gt; 2030</p>",
            MessageTemplate.Render(configuration, DateTimeOffset.Parse("2030-01-03T08:00:00Z"), FirstInstallation).Body);
    }

    [Fact]
    public async Task MissingMachineValueBlocksDraftCreationBeforeFilesAreClaimed()
    {
        using var folder = new TestFolder();
        folder.Configuration.Body = "Best Regards,\n{name}";
        folder.Configuration.MachineVariables = [new() { InstallationId = FirstInstallation, Values = new() { ["name"] = "Jason" } }];
        folder.Save();
        folder.AddFile("document.pdf", "document");
        var settings = folder.Settings();
        settings.InstallationId = SecondInstallation;
        var mail = new FakeMail();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new AutomationEngine(settings, mail, folder.Clock).RunNowAsync(folder.Root, default));

        Assert.Contains("body", error.Message);
        Assert.Contains("{name}", error.Message);
        Assert.Equal(0, mail.CreateCount);
        Assert.Empty(FolderStore.GetHistory(folder.Root));
        Assert.True(File.Exists(Path.Combine(folder.Root, "To send", "document.pdf")));
    }

    [Fact]
    public void SharedConfigurationLoadsWithoutRequiringValuesForTheReadingComputer()
    {
        using var folder = new TestFolder();
        folder.Configuration.Body = "Best Regards,\n{name}";
        folder.Configuration.MachineVariables = [new() { InstallationId = FirstInstallation, MachineName = "Office", Values = new() { ["name"] = "Jason", ["phone"] = "" } }];
        folder.Save();

        var loaded = FolderStore.LoadConfiguration(folder.Root);

        Assert.Equal("Office", Assert.Single(loaded.MachineVariables).MachineName);
        Assert.Equal("Jason", loaded.MachineVariables[0].Values["name"]);
        Assert.Equal("", loaded.MachineVariables[0].Values["phone"]);
        Assert.Equal("Best Regards,\nJason", MessageTemplate.Render(loaded, folder.Clock.GetUtcNow(), FirstInstallation).Body);
        Assert.Throws<InvalidDataException>(() => MessageTemplate.Render(loaded, folder.Clock.GetUtcNow(), SecondInstallation));
    }

    [Theory]
    [InlineData("", "Jason")]
    [InlineData("date", "Jason")]
    [InlineData("1name", "Jason")]
    [InlineData("full name", "Jason")]
    [InlineData("name", "Jason\r\nBcc: other@example.com")]
    public void InvalidVariableDefinitionsCannotBeSaved(string name, string value)
    {
        using var folder = new TestFolder();
        folder.Configuration.MachineVariables = [new() { InstallationId = FirstInstallation, Values = new() { [name] = value } }];

        Assert.Throws<InvalidDataException>(folder.Save);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData(FirstInstallation)]
    public void InvalidOrDuplicateInstallationIdentitiesCannotBeSaved(string installationId)
    {
        using var folder = new TestFolder();
        folder.Configuration.MachineVariables = [new() { InstallationId = FirstInstallation }, new() { InstallationId = installationId }];

        Assert.Throws<InvalidDataException>(folder.Save);
    }

    [Fact]
    public void StaleEditorsPreserveOtherComputersLatestValuesAndCanRemoveTheirOwnProfile()
    {
        using var folder = new TestFolder();
        folder.Configuration.MachineVariables =
        [
            new() { InstallationId = FirstInstallation, Values = new() { ["name"] = "Jason" } },
            new() { InstallationId = SecondInstallation, Values = new() { ["name"] = "Lucia" } }
        ];
        folder.Save();
        var firstEditor = FolderStore.LoadConfiguration(folder.Root);
        var secondEditor = FolderStore.LoadConfiguration(folder.Root);
        firstEditor.MachineVariables[0].Values["name"] = "Jason Tan";
        secondEditor.MachineVariables[1].Values["name"] = "Lucia Lee";

        FolderStore.SaveConfiguration(folder.Root, firstEditor, FirstInstallation);
        FolderStore.SaveConfiguration(folder.Root, secondEditor, SecondInstallation);

        var saved = FolderStore.LoadConfiguration(folder.Root);
        Assert.Equal("Jason Tan", saved.MachineVariables.Single(machine => machine.InstallationId == FirstInstallation).Values["name"]);
        Assert.Equal("Lucia Lee", saved.MachineVariables.Single(machine => machine.InstallationId == SecondInstallation).Values["name"]);
        firstEditor.MachineVariables.RemoveAll(machine => machine.InstallationId == FirstInstallation);
        FolderStore.SaveConfiguration(folder.Root, firstEditor, FirstInstallation);
        Assert.Equal("Lucia Lee", Assert.Single(FolderStore.LoadConfiguration(folder.Root).MachineVariables).Values["name"]);
    }

    [Fact]
    public void SavingWithoutVariablesKeepsTheYamlCompatibleWithExistingClients()
    {
        using var folder = new TestFolder();
        folder.Configuration.Schedules.Clear();
        folder.Save();

        var yaml = File.ReadAllText(Path.Combine(folder.Root, "config.yml"));

        Assert.DoesNotContain("machine_variables:", yaml);
        Assert.Contains("schedules: []", yaml);
        Assert.Empty(FolderStore.LoadConfiguration(folder.Root).MachineVariables);
    }

    [Fact]
    public async Task BackupWithValuesCanTakeOverImmediatelyWhenPrimaryHasAStaleReadyHeartbeat()
    {
        using var folder = new TestFolder();
        folder.Configuration.WaitForFiles = true;
        folder.Save();
        var primarySettings = folder.Settings();
        primarySettings.InstallationId = FirstInstallation;
        var backupSettings = folder.Settings();
        backupSettings.InstallationId = SecondInstallation;
        FolderStore.RegisterWorker(folder.Root, FirstInstallation, "Primary");
        FolderStore.RegisterWorker(folder.Root, SecondInstallation, "Backup");
        var primaryMail = new FakeMail();
        var backupMail = new FakeMail();
        var primary = new AutomationEngine(primarySettings, primaryMail, folder.Clock);
        await primary.CheckSchedulesAsync(default);
        folder.Configuration.Body = "Best Regards,\n{name}";
        folder.Configuration.MachineVariables = [new() { InstallationId = SecondInstallation, Values = new() { ["name"] = "Lucia" } }];
        folder.Save();
        folder.AddFile("document.pdf", "document");

        await new AutomationEngine(backupSettings, backupMail, folder.Clock).CheckSchedulesAsync(default);
        await primary.CheckSchedulesAsync(default);

        Assert.Equal(1, backupMail.SendCount);
        Assert.Equal("Best Regards,\nLucia", backupMail.LastMessage!.Body);
        Assert.Equal(0, primaryMail.SendCount);
    }

    [Fact]
    public async Task InterruptedBatchKeepsItsFrozenValuesWhenAnUnconfiguredBackupContinuesIt()
    {
        using var folder = new TestFolder();
        var originalSettings = folder.Settings();
        originalSettings.InstallationId = FirstInstallation;
        folder.Configuration.Subject = "Documents from {name}";
        folder.Configuration.Body = "Best Regards,\n{name}";
        folder.Configuration.MachineVariables = [new() { InstallationId = FirstInstallation, Values = new() { ["name"] = "Jason" } }];
        folder.Save();
        folder.AddFile("document.pdf", "document");
        var originalMail = new FakeMail { UploadError = new IOException("Interrupted preparation") };
        await Assert.ThrowsAsync<IOException>(() => new AutomationEngine(originalSettings, originalMail, folder.Clock).RunNowAsync(folder.Root, default));
        folder.Configuration.MachineVariables[0].Values["name"] = "Changed";
        folder.Save();
        var backupSettings = folder.Settings();
        backupSettings.InstallationId = SecondInstallation;
        var backupMail = new FakeMail { Account = new("backup@example.com", "backup@example.com", "Backup") };

        await new AutomationEngine(backupSettings, backupMail, folder.Clock).RunNowAsync(folder.Root, default);

        Assert.Equal("Documents from Jason", backupMail.LastMessage!.Subject);
        Assert.Equal("Best Regards,\nJason", backupMail.LastMessage.Body);
        Assert.Equal(RunStatus.Archived, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
    }

    [Fact]
    public async Task RejectedBatchUsesTheRetryingComputersValuesAndOriginalDate()
    {
        using var folder = new TestFolder();
        var firstSettings = folder.Settings();
        firstSettings.InstallationId = FirstInstallation;
        var secondSettings = folder.Settings();
        secondSettings.InstallationId = SecondInstallation;
        folder.Configuration.Subject = "Documents {date:yyyy-MM-dd} from {name}";
        folder.Configuration.MachineVariables =
        [
            new() { InstallationId = FirstInstallation, Values = new() { ["name"] = "Jason" } },
            new() { InstallationId = SecondInstallation, Values = new() { ["name"] = "Lucia" } }
        ];
        folder.Save();
        folder.AddFile("document.pdf", "document");
        var firstMail = new FakeMail { SendError = new MailSubmissionRejectedException("UnresolvedRecipients", "Recipient correction required.") };
        await Assert.ThrowsAsync<MailSubmissionRejectedException>(() => new AutomationEngine(firstSettings, firstMail, folder.Clock).RunNowAsync(folder.Root, default));
        folder.Clock.Advance(TimeSpan.FromDays(2));
        var secondMail = new FakeMail { Account = new("lucia@example.com", "lucia@example.com", "Lucia") };

        await new AutomationEngine(secondSettings, secondMail, folder.Clock).RunNowAsync(folder.Root, default);

        Assert.Equal("Documents 2030-01-03 from Lucia", secondMail.LastMessage!.Subject);
        Assert.Equal(RunStatus.Archived, Assert.Single(FolderStore.GetHistory(folder.Root)).Status);
    }
}
