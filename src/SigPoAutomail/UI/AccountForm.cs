namespace SigPoAutomail.UI;

internal sealed partial class AccountForm : Form
{
    public string SelectedAccountId => _accounts.SelectedRows.Count == 0 ? "" : (_accounts.SelectedRows[0].Tag as MailAccount)?.Id ?? "";

    public AccountForm(IReadOnlyList<MailAccount> accounts, string selectedAccountId)
    {
        InitializeComponent();
        _accounts.Rows.Add("No account (pause sending)", "");
        var selectedIndex = 0;
        foreach (var account in accounts)
        {
            var index = _accounts.Rows.Add(account.Address, account.DisplayName);
            _accounts.Rows[index].Tag = account;
            if (string.Equals(account.Id, selectedAccountId, StringComparison.Ordinal))
                selectedIndex = index;
        }
        _accounts.CurrentCell = _accounts.Rows[selectedIndex].Cells[0];
        if (accounts.Count == 0)
            _guidance.Text = "No accounts were found in the current classic Outlook profile. Add your business account in classic Outlook, then choose Connect to classic Outlook again.";
        _save.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
        };
    }
}
