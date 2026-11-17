using System.Runtime.ExceptionServices;
using OutlookAutomail.UI;
using Xunit;

namespace OutlookAutomail.Tests;

public sealed class FolderConfigurationFormTests
{
    [Theory]
    [InlineData(null, "Lucia", false)]
    [InlineData("Jason", "Jason Tan", false)]
    [InlineData("Jason", "", false)]
    [InlineData("Jason", "Jason Tan", true)]
    public async Task EditingThisComputersValueSavesItsProfileAndPreservesTheOtherComputer(string? original, string value, bool uppercaseIdentity)
    {
        using var folder = new TestFolder();
        const string installationId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string otherInstallationId = "22222222222222222222222222222222";
        folder.Configuration.Body = "Best Regards,\n{name}";
        folder.Configuration.MachineVariables = [new() { InstallationId = otherInstallationId, Values = new() { ["name"] = "Other sender" } }];
        if (original is not null)
            folder.Configuration.MachineVariables.Add(new() { InstallationId = uppercaseIdentity ? installationId.ToUpperInvariant() : installationId, Values = new() { ["name"] = original } });
        folder.Save();
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new FolderConfigurationForm(folder.Root, FolderStore.LoadConfiguration(folder.Root), installationId)
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-2400, -1600),
                    ShowInTaskbar = false
                };
                Exception? failure = null;
                form.Shown += (_, _) =>
                {
                    try
                    {
                        var controls = new List<Control> { form };
                        for (var index = 0; index < controls.Count; index++)
                            controls.AddRange(controls[index].Controls.Cast<Control>());
                        var tabs = controls.OfType<TabControl>().Single(control => control.TabPages.Cast<TabPage>().Any(page => page.Text == "Variables"));
                        tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Variables");
                        var grid = controls.OfType<DataGridView>().Single(control => control.Columns.Count == 2);
                        var row = grid.Rows.Cast<DataGridViewRow>().Single(item => Convert.ToString(item.Cells[0].Value) == "name");
                        Assert.Equal(original, row.Cells[1].Value);
                        grid.CurrentCell = row.Cells[1];
                        Assert.True(grid.BeginEdit(true));
                        var input = Assert.IsType<DataGridViewTextBoxEditingControl>(grid.EditingControl);
                        Assert.Equal(original ?? "", input.Text);
                        input.Text = value;
                        Assert.True(grid.EndEdit());
                        Assert.Equal(value, row.Cells[1].Value);
                        var save = Assert.IsType<Button>(form.AcceptButton);
                        Assert.True(save.Enabled);
                        save.PerformClick();
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                        form.Close();
                    }
                };
                var result = form.ShowDialog();
                if (failure is not null)
                    ExceptionDispatchInfo.Capture(failure).Throw();
                Assert.Equal(DialogResult.OK, result);
                finished.TrySetResult(null);
            }
            catch (Exception exception) { finished.TrySetResult(exception); }
        })
        { IsBackground = true, Name = "Folder variables editor test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var error = await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
        var saved = FolderStore.LoadConfiguration(folder.Root);
        Assert.Equal(value, saved.MachineVariables.Single(machine => machine.InstallationId == installationId).Values["name"]);
        Assert.Equal("Other sender", saved.MachineVariables.Single(machine => machine.InstallationId == otherInstallationId).Values["name"]);
        Assert.Equal("Best Regards,\n" + value, MessageTemplate.Render(saved, folder.Clock.GetUtcNow(), installationId).Body);
    }
}
