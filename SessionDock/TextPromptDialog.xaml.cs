using System.Windows;

namespace SessionDock;

public partial class TextPromptDialog : Window
{
    public string? Value { get; private set; }

    public TextPromptDialog(string title, string detail, string? currentValue)
    {
        InitializeComponent();
        Services.WindowLayoutService.FitToWorkArea(this);
        Title = title;
        PromptTitle.Text = title;
        PromptDetail.Text = detail;
        ValueBox.Text = currentValue ?? string.Empty;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Value = string.IsNullOrWhiteSpace(ValueBox.Text)
            ? null
            : ValueBox.Text.Trim();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;
}
