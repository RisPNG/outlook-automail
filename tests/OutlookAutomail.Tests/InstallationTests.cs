using Velopack.Windows;
using Xunit;

namespace OutlookAutomail.Tests;

public sealed class InstallationTests
{
    [Fact]
    public void InstalledFolderOffersOneShortcutToTheNativeUninstallerAfterInstallAndUpdate()
    {
        using var fixture = new UpdateFixture();
        var locator = new RecordingUpdateLocator(fixture.Root, fixture.Process);

        Program.CreateUninstallShortcut(locator);
        Program.CreateUninstallShortcut(locator);

        var path = Assert.Single(Directory.EnumerateFiles(fixture.Root, "*.lnk"));
        using var shortcut = new ShellLink(path);
        Assert.Equal("Uninstall Outlook Automail.lnk", Path.GetFileName(path));
        Assert.Equal(locator.UpdateExePath, shortcut.Target);
        Assert.Equal("uninstall", shortcut.Arguments);
        Assert.Equal(locator.RootAppDir, shortcut.WorkingDirectory);
        Assert.Equal(Path.Combine(locator.AppContentDir!, "OutlookAutomail.exe"), shortcut.IconPath);
        Assert.Empty(fixture.Process.Starts);
    }

    [Fact]
    public void SharedFoldersCannotOverlapTheInstallationOrAppDataThatUninstallRemoves()
    {
        var configuration = new FolderConfiguration { To = ["recipient@example.com"] };
        foreach (var owned in new[] { SettingsStore.RootDirectory, AppContext.BaseDirectory })
        {
            foreach (var path in new[] { owned, Path.Combine(owned, "Shared documents"), Directory.GetParent(Path.TrimEndingDirectorySeparator(owned))!.FullName })
            {
                Assert.Throws<InvalidDataException>(() => FolderStore.ValidateConfiguration(path, configuration));
                Assert.Throws<InvalidDataException>(() => FolderStore.ResolveInlineImage(path, configuration,
                    new InlineImageConfiguration { Path = Path.Combine(FolderStore.MessageAssetsFolder, "signature.png") }));
            }
        }
    }

    [Fact]
    public void AnExternalSharedFolderKeepsItsConfigurationPhotosAndHistory()
    {
        using var folder = new TestFolder();
        var history = Path.Combine(folder.Root, FolderStore.MetadataFolder, "state.json");
        var photo = Path.Combine(folder.Root, FolderStore.MessageAssetsFolder, "signature.png");
        Directory.CreateDirectory(Path.GetDirectoryName(photo)!);
        File.WriteAllText(history, "shared history");
        File.WriteAllText(photo, "shared photo");
        folder.AddFile("document.pdf", "document");

        FolderStore.ValidateConfiguration(folder.Root, folder.Configuration);

        Assert.Equal("shared history", File.ReadAllText(history));
        Assert.Equal("shared photo", File.ReadAllText(photo));
        Assert.Equal("document", File.ReadAllText(Path.Combine(folder.Root, "To send", "document.pdf")));
        Assert.Equal(folder.Configuration.Id, FolderStore.LoadConfiguration(folder.Root).Id);
    }
}
