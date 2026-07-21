using System.Windows;
using System.Windows.Controls;
using SessionDock.Models;

namespace SessionDock;

public partial class BatchLaunchDialog : Window
{
    private readonly IReadOnlyList<BatchLaunchAccountOption> _accounts;

    public IReadOnlyList<AccountProfile> SelectedAccounts { get; private set; } = [];
    public TimeSpan Delay { get; private set; } = TimeSpan.FromSeconds(8);

    public BatchLaunchDialog(IEnumerable<AccountProfile> accounts)
    {
        InitializeComponent();
        Services.WindowLayoutService.FitToWorkArea(this);
        _accounts = accounts
            .Select(account => new BatchLaunchAccountOption(account))
            .ToArray();
        AccountsList.ItemsSource = _accounts;
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e) =>
        SetAllSelected(true);

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e) =>
        SetAllSelected(false);

    private void SetAllSelected(bool selected)
    {
        foreach (var account in _accounts)
            account.IsSelected = selected;
        AccountsList.Items.Refresh();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _accounts
            .Where(account => account.IsSelected)
            .Select(account => account.Profile)
            .ToArray();
        if (selected.Length < 2)
        {
            ValidationText.Text = "Select at least two accounts for a batch launch.";
            return;
        }

        if (DelayComboBox.SelectedItem is not ComboBoxItem { Tag: string secondsText } ||
            !int.TryParse(secondsText, out var seconds))
        {
            ValidationText.Text = "Select a valid delay.";
            return;
        }

        SelectedAccounts = selected;
        Delay = TimeSpan.FromSeconds(seconds);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;

    private sealed class BatchLaunchAccountOption(AccountProfile profile)
    {
        public AccountProfile Profile { get; } = profile;
        public string DisplayName { get; } = profile.Label ?? $"@{profile.Username}";
        public string Identity { get; } = profile.Label is null
            ? $"User ID {profile.UserId}"
            : $"@{profile.Username}  •  User ID {profile.UserId}";
        public string ColorHex { get; } = profile.ColorHex ?? "#7C5CFC";
        public bool IsSelected { get; set; } = true;
    }
}
