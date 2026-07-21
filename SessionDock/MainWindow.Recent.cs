using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private RecentTypeFilter _recentTypeFilter;
    private long _recentAccountFilter;
    private bool _updatingRecentFilters;

    private void LaunchTabButton_Click(object sender, RoutedEventArgs e) =>
        ShowLauncherTab();

    private void RecentTabButton_Click(object sender, RoutedEventArgs e)
    {
        LaunchTabPanel.Visibility = Visibility.Collapsed;
        RecentTabPanel.Visibility = Visibility.Visible;
        LaunchTabButton.Background = Brushes.Transparent;
        LaunchTabButton.Foreground = CreateBrush("#98A3B8");
        RecentTabButton.Background = CreateBrush("#2A3142");
        RecentTabButton.Foreground = Brushes.White;
        AutomationProperties.SetItemStatus(LaunchTabButton, "Not selected");
        AutomationProperties.SetItemStatus(RecentTabButton, "Selected");
        RenderRecentExperiences();
    }

    private void ShowLauncherTab()
    {
        LaunchTabPanel.Visibility = Visibility.Visible;
        RecentTabPanel.Visibility = Visibility.Collapsed;
        LaunchTabButton.Background = CreateBrush("#2A3142");
        LaunchTabButton.Foreground = Brushes.White;
        RecentTabButton.Background = Brushes.Transparent;
        RecentTabButton.Foreground = CreateBrush("#98A3B8");
        AutomationProperties.SetItemStatus(LaunchTabButton, "Selected");
        AutomationProperties.SetItemStatus(RecentTabButton, "Not selected");
    }

    private void RenderRecentExperiences()
    {
        PopulateAccountFilter();
        UpdateClearHistoryButton();
        RecentExperiencesList.Children.Clear();
        var filtered = _settings.RecentExperiences
            .Where(MatchesRecentFilters)
            .OrderByDescending(item => item.IsPinned)
            .ThenByDescending(item => item.LastLaunchedAt)
            .ToList();

        if (filtered.Count == 0)
        {
            RecentExperiencesList.Children.Add(new TextBlock
            {
                Text = _settings.RecentExperiences.Count == 0
                    ? "Experiences you successfully launch will appear here."
                    : "No saved experiences match these filters.",
                Foreground = CreateBrush("#7F8BA0"),
                FontSize = 13,
                Margin = new Thickness(2, 18, 0, 0)
            });
            return;
        }

        var favorites = filtered.Where(item => item.IsPinned).ToList();
        var recent = filtered.Where(item => !item.IsPinned).ToList();
        AddRecentSection("FAVORITES", favorites);
        AddRecentSection("RECENT", recent);
    }

    private void AddRecentSection(string title, IReadOnlyList<RecentExperience> items)
    {
        if (items.Count == 0)
            return;

        RecentExperiencesList.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = CreateBrush("#68748A"),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(2, 4, 0, 8)
        });
        foreach (var item in items)
            RecentExperiencesList.Children.Add(CreateRecentExperienceCard(item));
    }

    private Border CreateRecentExperienceCard(RecentExperience recent)
    {
        var title = recent.CustomName
            ?? recent.Name
            ?? $"Place {recent.PlaceId}";
        var timestamp = recent.LastLaunchedAt.ToLocalTime().ToString("g");
        var type = recent.IsPrivateServer ? "Private server" : "Public";
        var account = string.IsNullOrWhiteSpace(recent.AccountUsername)
            ? "Unknown account"
            : $"@{recent.AccountUsername}";

        var card = new Border
        {
            Background = CreateBrush("#131720"),
            BorderBrush = CreateBrush(recent.IsPinned ? "#5D4BB1" : "#272E3D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0, 0, 0, 9),
            Padding = new Thickness(8)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var useButton = new Button
        {
            Tag = recent,
            Background = Brushes.Transparent,
            Padding = new Thickness(6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        var labels = new StackPanel();
        labels.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        labels.Children.Add(new TextBlock
        {
            Text = $"{type}  •  {account}  •  {timestamp}",
            Foreground = CreateBrush("#7F8BA0"),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (recent.ServerJobId is { Length: > 8 } serverJobId)
        {
            labels.Children.Add(new TextBlock
            {
                Text = $"Tracked server {serverJobId[..8]}…",
                ToolTip = $"Roblox server JobId\n{serverJobId}",
                Foreground = CreateBrush("#68748A"),
                FontSize = 10,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        useButton.Content = labels;
        useButton.ToolTip = recent.ServerJobId is null
            ? $"Use this destination\n{recent.Destination}"
            : $"Use this destination\n{recent.Destination}\n\nServer JobId\n{recent.ServerJobId}";
        useButton.Click += RecentExperienceButton_Click;
        grid.Children.Add(useButton);

        var actionColumn = 1;
        if (Guid.TryParse(recent.ServerJobId, out _))
        {
            var serverButton = CreateHistoryActionButton(
                "IconServerJoin",
                "Use this tracked Server JobId",
                recent,
                UseRecentServerButton_Click);
            Grid.SetColumn(serverButton, actionColumn++);
            grid.Children.Add(serverButton);
        }

        var pinButton = CreateHistoryActionButton(
            "IconStar",
            recent.IsPinned ? "Remove from Favorites" : "Add to Favorites",
            recent,
            PinRecentButton_Click,
            recent.IsPinned);
        Grid.SetColumn(pinButton, actionColumn++);
        grid.Children.Add(pinButton);

        var renameButton = CreateHistoryActionButton(
            "IconEdit",
            "Set a local custom name",
            recent,
            RenameRecentButton_Click);
        Grid.SetColumn(renameButton, actionColumn++);
        grid.Children.Add(renameButton);

        var removeButton = CreateHistoryActionButton(
            "IconTrash",
            "Remove from history",
            recent,
            RemoveRecentButton_Click);
        Grid.SetColumn(removeButton, actionColumn);
        grid.Children.Add(removeButton);
        card.Child = grid;
        return card;
    }

    private static Button CreateHistoryActionButton(
        string iconResourceKey,
        string tooltip,
        RecentExperience recent,
        RoutedEventHandler handler,
        bool fillIcon = false)
    {
        var icon = new Path
        {
            Data = (Geometry)Application.Current.FindResource(iconResourceKey),
            Style = (Style)Application.Current.FindResource("ButtonIcon")
        };
        if (fillIcon)
        {
            icon.Fill = CreateBrush("#B8A8FF");
            icon.Stroke = CreateBrush("#B8A8FF");
        }

        var button = new Button
        {
            Tag = recent,
            Content = icon,
            ToolTip = tooltip,
            Width = 38,
            MinHeight = 38,
            Margin = new Thickness(5, 0, 0, 0),
            Padding = new Thickness(4),
            Background = CreateBrush("#252C3B"),
            Foreground = CreateBrush("#C4CCDA")
        };
        AutomationProperties.SetName(button, tooltip);
        if (iconResourceKey == "IconStar")
        {
            AutomationProperties.SetItemStatus(
                button,
                recent.IsPinned ? "Selected" : "Not selected");
        }
        button.Click += handler;
        return button;
    }

    private void RecentExperienceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecentExperience recent })
            return;

        PlaceIdBox.Text = recent.Destination;
        TrackDestinationForActiveProfile();
        _settingsService.Save(_settings);
        ShowLauncherTab();
        PlaceIdBox.Focus();
        ResetDestinationViewport();
    }

    private void UseRecentServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecentExperience recent } ||
            !Guid.TryParse(recent.ServerJobId, out var serverJobId))
        {
            return;
        }

        PlaceIdBox.Text = serverJobId.ToString("D");
        TrackDestinationForActiveProfile();
        _settingsService.Save(_settings);
        ShowLauncherTab();
        PlaceIdBox.Focus();
        ResetDestinationViewport();
        SetStatus(
            "Tracked server selected",
            $"Launch will try server {serverJobId.ToString("D")[..8]}… for Place {recent.PlaceId}.",
            "SERVER READY");
    }

    private void PinRecentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecentExperience recent })
            return;
        if (!recent.IsPinned &&
            _settings.RecentExperiences.Count(item => item.IsPinned) >= 50)
        {
            SetStatus(
                "Favorites limit reached",
                "Remove one Favorite before pinning another. Recent history is unchanged.",
                "FAVORITES FULL");
            return;
        }
        recent.IsPinned = !recent.IsPinned;
        SaveRecentMetadata();
    }

    private void RenameRecentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecentExperience recent })
            return;

        var dialog = new TextPromptDialog(
            "Rename saved experience",
            "This local name applies to this destination for every account. Leave it blank to use Roblox's name.",
            recent.CustomName)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
            return;

        foreach (var matchingEntry in _settings.RecentExperiences.Where(item =>
                     RecentDestinationIdentity.Matches(item, recent)))
        {
            matchingEntry.CustomName = dialog.Value;
        }
        SaveRecentMetadata();
    }

    private void RemoveRecentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecentExperience recent })
            return;
        var result = MessageBox.Show(
            $"Remove “{recent.CustomName ?? recent.Name ?? $"Place {recent.PlaceId}"}” from saved experiences?",
            "Remove saved experience",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;
        _settings.RecentExperiences.Remove(recent);
        SaveRecentMetadata();
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var removableCount = _settings.RecentExperiences.Count(
            MatchesClearHistoryScope);
        if (removableCount == 0)
            return;

        var scope = _recentTypeFilter switch
        {
            RecentTypeFilter.Public => "public-server history",
            RecentTypeFilter.Private => "private-server history",
            _ => "history"
        };
        var entryText = removableCount == 1
            ? "1 non-favorite entry"
            : $"{removableCount} non-favorite entries";
        var accountScope = GetRecentAccountScopeDescription();
        var result = MessageBox.Show(
            $"Clear {scope} {accountScope}? This removes {entryText}. Favorites will stay saved.",
            $"Clear {scope}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        _settings.RecentExperiences.RemoveAll(MatchesClearHistoryScope);
        SaveRecentMetadata();
    }

    private void SaveRecentExperience(RecentExperience recent)
    {
        var matchingDestinationEntries = _settings.RecentExperiences
            .Where(item => RecentDestinationIdentity.Matches(item, recent))
            .ToList();
        var sharedCustomName = matchingDestinationEntries
            .Where(item => item.CustomName is not null)
            .OrderByDescending(item => item.LastLaunchedAt)
            .Select(item => item.CustomName)
            .FirstOrDefault();
        var existing = _settings.RecentExperiences.FirstOrDefault(item =>
            item.AccountUserId == recent.AccountUserId &&
            RecentDestinationIdentity.Matches(item, recent));
        if (existing is not null)
        {
            recent.IsPinned = existing.IsPinned;
            _settings.RecentExperiences.Remove(existing);
        }
        recent.CustomName = sharedCustomName;

        _settings.RecentExperiences.Insert(0, recent);
        if (_settings.RecentExperiences.Count(item => !item.IsPinned) > 50)
        {
            var removable = _settings.RecentExperiences
                .Where(item => !item.IsPinned)
                .OrderBy(item => item.LastLaunchedAt)
                .FirstOrDefault();
            if (removable is not null)
                _settings.RecentExperiences.Remove(removable);
        }
        SaveRecentMetadata(showError: false);
    }

    private void SaveRecentMetadata(bool showError = true)
    {
        try
        {
            _settingsService.Save(_settings);
        }
        catch (Exception ex)
        {
            if (showError)
                SetStatus("Local metadata could not be saved", ex.Message, "HISTORY ERROR");
        }
        RenderRecentExperiences();
    }

    private bool MatchesRecentFilters(RecentExperience item)
    {
        return MatchesRecentType(item) &&
               (_recentAccountFilter == 0 ||
                item.AccountUserId == _recentAccountFilter);
    }

    private bool MatchesRecentType(RecentExperience item) =>
        RecentHistoryScope.MatchesType(
            item,
            _recentTypeFilter switch
            {
                RecentTypeFilter.Public => RecentServerType.Public,
                RecentTypeFilter.Private => RecentServerType.Private,
                _ => RecentServerType.All
            });

    private bool MatchesClearHistoryScope(RecentExperience item) =>
        RecentHistoryScope.CanClear(
            item,
            _recentTypeFilter switch
            {
                RecentTypeFilter.Public => RecentServerType.Public,
                RecentTypeFilter.Private => RecentServerType.Private,
                _ => RecentServerType.All
            },
            _recentAccountFilter);

    private string GetRecentAccountScopeDescription()
    {
        if (_recentAccountFilter == 0)
            return "across all accounts";

        var username = _settings.RecentExperiences
            .Where(item => item.AccountUserId == _recentAccountFilter)
            .Select(item => item.AccountUsername)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return username is null
            ? $"for account {_recentAccountFilter}"
            : $"for @{username}";
    }

    private void UpdateClearHistoryButton()
    {
        var accessibleName = _recentTypeFilter switch
        {
            RecentTypeFilter.Public =>
                "Clear public history",
            RecentTypeFilter.Private =>
                "Clear private history",
            _ => "Clear all history"
        };
        var accountScope = GetRecentAccountScopeDescription();
        AutomationProperties.SetName(
            ClearHistoryButton,
            $"{accessibleName} {accountScope}");
        ClearHistoryButton.ToolTip =
            $"{accessibleName} {accountScope}; Favorites stay saved";
        ClearHistoryButton.IsEnabled =
            !_operationBusy &&
            _settings.RecentExperiences.Any(MatchesClearHistoryScope);
    }

    private void PopulateAccountFilter()
    {
        var accountIds = _settings.RecentExperiences
            .Where(item => item.AccountUserId > 0)
            .GroupBy(item => item.AccountUserId)
            .Select(group => new
            {
                UserId = group.Key,
                Username = group.Select(item => item.AccountUsername)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                    ?? group.Key.ToString()
            })
            .OrderBy(account => account.Username)
            .ToList();
        if (_recentAccountFilter != 0 &&
            accountIds.All(account => account.UserId != _recentAccountFilter))
        {
            _recentAccountFilter = 0;
        }

        _updatingRecentFilters = true;
        AccountFilterCombo.Items.Clear();
        AccountFilterCombo.Items.Add(new ComboBoxItem
        {
            Content = "All accounts",
            Tag = 0L
        });
        foreach (var account in accountIds)
        {
            AccountFilterCombo.Items.Add(new ComboBoxItem
            {
                Content = $"@{account.Username}",
                Tag = account.UserId
            });
        }

        AccountFilterCombo.SelectedItem = AccountFilterCombo.Items
            .Cast<ComboBoxItem>()
            .First(item => (long)item.Tag == _recentAccountFilter);
        _updatingRecentFilters = false;
    }

    private void AccountFilterCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_updatingRecentFilters ||
            AccountFilterCombo.SelectedItem is not ComboBoxItem { Tag: long userId })
        {
            return;
        }

        _recentAccountFilter = userId;
        RenderRecentExperiences();
    }

    private void AllTypeFilterButton_Click(object sender, RoutedEventArgs e) =>
        SetRecentTypeFilter(RecentTypeFilter.All);

    private void PublicFilterButton_Click(object sender, RoutedEventArgs e) =>
        SetRecentTypeFilter(RecentTypeFilter.Public);

    private void PrivateFilterButton_Click(object sender, RoutedEventArgs e) =>
        SetRecentTypeFilter(RecentTypeFilter.Private);

    private void SetRecentTypeFilter(RecentTypeFilter filter)
    {
        _recentTypeFilter = filter;
        SetFilterButtonState(AllTypeFilterButton, filter == RecentTypeFilter.All);
        SetFilterButtonState(PublicFilterButton, filter == RecentTypeFilter.Public);
        SetFilterButtonState(PrivateFilterButton, filter == RecentTypeFilter.Private);
        RenderRecentExperiences();
    }

    private static void SetFilterButtonState(Button button, bool active)
    {
        button.Background = active ? CreateBrush("#2A3142") : Brushes.Transparent;
        button.Foreground = active ? Brushes.White : CreateBrush("#98A3B8");
        AutomationProperties.SetItemStatus(button, active ? "Selected" : "Not selected");
    }

    private static SolidColorBrush CreateBrush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private enum RecentTypeFilter
    {
        All,
        Public,
        Private
    }
}
