using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RobloxOneLauncher.Models;

namespace RobloxOneLauncher.Services;

public sealed class RobloxWebSessionService : IDisposable
{
    private static readonly TimeSpan AccountTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LocaleTimeout = TimeSpan.FromSeconds(2);
    private WebView2? _browser;
    private int _generation;
    private bool _isReady;

    public event EventHandler? RobloxPageLoaded;

    public bool IsReady => _isReady && _browser?.CoreWebView2 is not null;

    public WebView2 BeginBrowserReplacement()
    {
        ReleaseBrowser();
        _browser = new WebView2();
        return _browser;
    }

    public void ReleaseBrowser()
    {
        _generation++;
        _isReady = false;
        _browser?.Dispose();
        _browser = null;
    }

    public async Task<bool> InitializeAsync(
        WebView2 browser,
        string userDataDirectory,
        bool showLogin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        var generation = _generation;
        cancellationToken.ThrowIfCancellationRequested();

        var environment = await CoreWebView2Environment.CreateAsync(
            userDataFolder: userDataDirectory);
        if (!IsCurrent(browser, generation) || cancellationToken.IsCancellationRequested)
            return false;

        await browser.EnsureCoreWebView2Async(environment);
        if (!IsCurrent(browser, generation) || cancellationToken.IsCancellationRequested)
            return false;

        Configure(browser.CoreWebView2);
        _isReady = true;
        browser.CoreWebView2.Navigate(showLogin
            ? "https://www.roblox.com/login"
            : "https://www.roblox.com/home");
        return true;
    }

    public void NavigateToLogin()
    {
        GetCore().Navigate("https://www.roblox.com/login");
    }

    public async Task<RobloxUser?> GetAuthenticatedUserAsync(
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var message = await RunMessageScriptAsync(
            requestId,
            RobloxWebScripts.GetAuthenticatedUser(requestId),
            AccountTimeout,
            cancellationToken);
        if (message is null ||
            !message.Value.TryGetProperty("user", out var user) ||
            user.ValueKind != JsonValueKind.Object ||
            !user.TryGetProperty("id", out var idElement) ||
            !idElement.TryGetInt64(out var id) ||
            id <= 0 ||
            !user.TryGetProperty("name", out var nameElement))
        {
            return null;
        }

        var name = nameElement.GetString();
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var displayName = user.TryGetProperty("displayName", out var displayNameElement)
            ? displayNameElement.GetString() ?? name
            : name;
        return new RobloxUser(id, name, displayName);
    }

    public async Task<LaunchTarget?> ResolvePrivateServerAsync(
        string shareCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shareCode);
        var requestId = Guid.NewGuid().ToString("N");
        var message = await RunMessageScriptAsync(
            requestId,
            RobloxWebScripts.ResolvePrivateServer(requestId, shareCode),
            ApiTimeout,
            cancellationToken);
        if (message is null ||
            !message.Value.TryGetProperty("placeId", out var placeIdElement) ||
            !placeIdElement.TryGetInt64(out var placeId) ||
            placeId <= 0 ||
            !message.Value.TryGetProperty("linkCode", out var linkCodeElement))
        {
            return null;
        }

        var linkCode = linkCodeElement.GetString();
        return string.IsNullOrWhiteSpace(linkCode)
            ? null
            : new LaunchTarget(placeId, linkCode, null);
    }

    public async Task<string?> GetAuthenticationTicketAsync(
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var message = await RunMessageScriptAsync(
            requestId,
            RobloxWebScripts.GetAuthenticationTicket(requestId),
            ApiTimeout,
            cancellationToken);
        if (message is null ||
            !message.Value.TryGetProperty("ticket", out var ticketElement))
        {
            return null;
        }

        return ticketElement.GetString();
    }

    public async Task<string?> GetUserLocaleAsync(
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var message = await RunMessageScriptAsync(
            requestId,
            RobloxWebScripts.GetUserLocale(requestId),
            LocaleTimeout,
            cancellationToken);
        if (message is null ||
            !message.Value.TryGetProperty("locale", out var localeElement))
        {
            return null;
        }

        return localeElement.GetString();
    }

    public async Task<string?> GetExperienceNameAsync(
        long placeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(placeId);
        var requestId = Guid.NewGuid().ToString("N");
        var message = await RunMessageScriptAsync(
            requestId,
            RobloxWebScripts.GetExperienceName(requestId, placeId),
            ApiTimeout,
            cancellationToken);
        if (message is null ||
            !message.Value.TryGetProperty("name", out var nameElement))
        {
            return null;
        }

        var name = nameElement.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(name) || name.Length > 200 ? null : name;
    }

    public async Task<bool> ClearProfileAsync()
    {
        if (!IsReady)
            return false;

        try
        {
            await GetCore().Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.AllProfile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        ReleaseBrowser();
    }

    private void Configure(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Profile.IsPasswordAutosaveEnabled = false;
        core.Profile.IsGeneralAutofillEnabled = false;
        core.Profile.PreferredTrackingPreventionLevel =
            CoreWebView2TrackingPreventionLevel.Balanced;
        core.NewWindowRequested += Core_NewWindowRequested;
        core.NavigationStarting += Core_NavigationStarting;
        core.NavigationCompleted += Core_NavigationCompleted;
        core.DownloadStarting += (_, args) =>
        {
            args.Cancel = true;
            args.Handled = true;
        };
        core.PermissionRequested += (_, args) =>
        {
            args.State = CoreWebView2PermissionState.Deny;
            args.SavesInProfile = false;
        };
    }

    private async Task<JsonElement?> RunMessageScriptAsync(
        string requestId,
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var core = GetCore();
        var completion = new TaskCompletionSource<JsonElement?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<CoreWebView2WebMessageReceivedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("requestId", out var idElement) ||
                    idElement.GetString() != requestId)
                {
                    return;
                }

                completion.TrySetResult(root.Clone());
            }
            catch
            {
                // Ignore unrelated or malformed browser messages.
            }
        };

        core.WebMessageReceived += handler;
        try
        {
            await core.ExecuteScriptAsync(script);
            var delay = Task.Delay(timeout, cancellationToken);
            var finished = await Task.WhenAny(completion.Task, delay);
            cancellationToken.ThrowIfCancellationRequested();
            return finished == completion.Task
                ? await completion.Task
                : null;
        }
        finally
        {
            core.WebMessageReceived -= handler;
        }
    }

    private void Core_NewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (sender is CoreWebView2 core && IsTrustedBrowserLocation(args.Uri))
            core.Navigate(args.Uri);
    }

    private static void Core_NavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsTrustedBrowserLocation(args.Uri))
            args.Cancel = true;
    }

    private void Core_NavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        var browser = _browser;
        if (!args.IsSuccess ||
            browser is null ||
            sender != browser.CoreWebView2 ||
            browser.Source is not { } source ||
            !IsRobloxHost(source.Host))
        {
            return;
        }

        RobloxPageLoaded?.Invoke(this, EventArgs.Empty);
    }

    private bool IsCurrent(WebView2 browser, int generation) =>
        generation == _generation && ReferenceEquals(browser, _browser);

    private CoreWebView2 GetCore()
    {
        if (!IsReady)
            throw new InvalidOperationException("The Roblox web session is not ready.");
        return _browser!.CoreWebView2;
    }

    private static bool IsTrustedBrowserLocation(string location)
    {
        if (location.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            return true;
        return Uri.TryCreate(location, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               IsRobloxHost(uri.Host);
    }

    private static bool IsRobloxHost(string host) =>
        host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase);
}
