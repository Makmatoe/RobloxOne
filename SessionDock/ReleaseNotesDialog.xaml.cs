using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SessionDock.Services;

namespace SessionDock;

public partial class ReleaseNotesDialog : Window
{
    private readonly bool _hasPreviousRelease;

    internal ReleaseNotesDialog(BundledReleaseNotes notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        InitializeComponent();
        WindowLayoutService.FitToWorkArea(this);

        CurrentVersionText.Text =
            $"SessionDock {notes.Current.Version.ToString(3)}";
        CurrentNotesBox.Text = notes.Current.DisplayText;
        AutomationProperties.SetName(
            CurrentReleaseButton,
            $"Current release, SessionDock {notes.Current.Version.ToString(3)}");
        AutomationProperties.SetName(
            CurrentNotesBox,
            $"Release notes for SessionDock {notes.Current.Version.ToString(3)}");

        _hasPreviousRelease = notes.Previous is not null;
        if (notes.Previous is { } previous)
        {
            PreviousVersionText.Text =
                $"SessionDock {previous.Version.ToString(3)}";
            PreviousNotesBox.Text = previous.DisplayText;
            AutomationProperties.SetName(
                PreviousReleaseButton,
                $"Previous release, SessionDock {previous.Version.ToString(3)}");
            AutomationProperties.SetName(
                PreviousNotesBox,
                $"Release notes for SessionDock {previous.Version.ToString(3)}");
        }
        else
        {
            PreviousVersionText.Text = "No earlier notes";
            PreviousNotesBox.Text =
                "No earlier bundled release notes are available.";
            PreviousReleaseButton.IsEnabled = false;
            AutomationProperties.SetName(
                PreviousReleaseButton,
                "No previous release notes available");
        }

        ShowRelease(current: true, moveFocus: false);
        Loaded += ReleaseNotesDialog_Loaded;
    }

    private void ReleaseNotesDialog_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= ReleaseNotesDialog_Loaded;
        CurrentReleaseButton.Focus();
    }

    private void CurrentReleaseButton_Click(object sender, RoutedEventArgs e) =>
        ShowRelease(current: true, moveFocus: false);

    private void PreviousReleaseButton_Click(object sender, RoutedEventArgs e) =>
        ShowRelease(current: false, moveFocus: false);

    private void ReleaseTabButton_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key == Key.Left)
        {
            ShowRelease(current: true, moveFocus: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && _hasPreviousRelease)
        {
            ShowRelease(current: false, moveFocus: true);
            e.Handled = true;
        }
    }

    private void ShowRelease(bool current, bool moveFocus)
    {
        if (!current && !_hasPreviousRelease)
            return;

        CurrentNotesPanel.Visibility = current
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviousNotesPanel.Visibility = current
            ? Visibility.Collapsed
            : Visibility.Visible;
        SetTabSelected(CurrentReleaseButton, current);
        SetTabSelected(PreviousReleaseButton, !current);
        AutomationProperties.SetItemStatus(
            CurrentReleaseButton,
            current ? "Selected" : "Not selected");
        AutomationProperties.SetItemStatus(
            PreviousReleaseButton,
            current ? "Not selected" : "Selected");

        if (moveFocus)
        {
            (current
                ? CurrentReleaseButton
                : PreviousReleaseButton).Focus();
        }
    }

    private void SetTabSelected(Button button, bool selected)
    {
        button.IsTabStop = selected;
        button.SetResourceReference(
            Control.BackgroundProperty,
            selected
                ? "ReleaseTabSelectedBrush"
                : "ReleaseTabIdleBrush");
        button.SetResourceReference(
            Control.ForegroundProperty,
            selected
                ? "ReleaseTabSelectedTextBrush"
                : "ReleaseTabIdleTextBrush");
    }
}
