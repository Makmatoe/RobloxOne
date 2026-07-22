using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SessionDock.Models;

namespace SessionDock.Services;

public sealed class RobloxWebSessionService : IDisposable
{
    private static readonly TimeSpan AccountTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LocaleTimeout = TimeSpan.FromSeconds(2);
    private const int MaximumWebMessageCharacters = 64 * 1024;
    private const int MaximumAuthenticationTicketCharacters = 8 * 1024;
    private static readonly Regex PrivateServerCodePattern = new(
        "^[A-Za-z0-9_-]{6,200}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private WebView2? _browser;
    private int _generation;
    private bool _isReady;
    private WebSessionToken? _currentToken;
    private int _failedGeneration = -1;

    internal event EventHandler<WebSessionEventArgs>? RobloxPageLoaded;
    internal event EventHandler<WebSessionUnavailableEventArgs>? SessionUnavailable;

    public bool IsReady => _isReady &&
        _currentToken is not null &&
        _browser?.CoreWebView2 is not null;

    internal WebSessionBrowser BeginBrowserReplacement(string accountKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        ReleaseBrowser();
        _browser = new WebView2 { AllowExternalDrop = false };
        var token = new WebSessionToken(_generation, accountKey);
        _currentToken = token;
        return new WebSessionBrowser(_browser, token);
    }

    public void ReleaseBrowser()
    {
        var browser = _browser;
        _generation++;
        _isReady = false;
        _currentToken = null;
        _browser = null;
        try
        {
            browser?.Dispose();
        }
        catch (Exception exception) when (
            IsExpectedRuntimeTeardownFailure(exception))
        {
            System.Diagnostics.Trace.WriteLine(
                $"WebView2 teardown failed safely: {exception.GetType().Name}.");
        }
    }

    internal async Task<bool> InitializeAsync(
        WebSessionBrowser session,
        string userDataDirectory,
        bool showLogin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        var browser = session.Browser;
        var token = session.Token;
        cancellationToken.ThrowIfCancellationRequested();

        CoreWebView2Environment environment;
        try
        {
            environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: userDataDirectory);
        }
        catch (WebView2RuntimeNotFoundException exception)
        {
            EnsureCurrent(token);
            throw new WebSessionUnavailableException(
                WebSessionUnavailableReason.MissingRuntime,
                "The Microsoft Edge WebView2 Runtime is not installed or could not be found.",
                exception);
        }
        catch (COMException exception) when (
            IsExpectedInitializationHResult(exception.HResult))
        {
            EnsureCurrent(token);
            throw new WebSessionUnavailableException(
                WebSessionUnavailableReason.RuntimeStartFailed,
                "The Roblox sign-in runtime could not be started. Restart SessionDock and check the WebView2 installation.",
                exception);
        }
        if (!IsCurrent(session) || cancellationToken.IsCancellationRequested)
            return false;

        try
        {
            await browser.EnsureCoreWebView2Async(environment);
        }
        catch (COMException exception) when (
            IsExpectedInitializationHResult(exception.HResult))
        {
            EnsureCurrent(token);
            throw new WebSessionUnavailableException(
                WebSessionUnavailableReason.RuntimeStartFailed,
                "The Roblox sign-in runtime could not be started. Restart SessionDock and check the WebView2 installation.",
                exception);
        }
        catch (Exception exception) when (
            IsCorrelatedRuntimeFailure(
                exception,
                token,
                exactRuntimeCall: true))
        {
            throw CreateRuntimeUnavailableException(exception, token);
        }
        if (!IsCurrent(session) || cancellationToken.IsCancellationRequested)
            return false;

        try
        {
            Configure(browser.CoreWebView2);
            _isReady = true;
            _failedGeneration = -1;
            browser.CoreWebView2.Navigate(showLogin
                ? "https://www.roblox.com/login"
                : "https://www.roblox.com/home");
        }
        catch (Exception exception) when (
            IsCorrelatedRuntimeFailure(
                exception,
                token,
                exactRuntimeCall: true))
        {
            _isReady = false;
            throw CreateRuntimeUnavailableException(exception, token);
        }
        return true;
    }

    internal void NavigateToLogin(WebSessionToken token)
    {
        try
        {
            GetCore(token).Navigate("https://www.roblox.com/login");
        }
        catch (Exception exception) when (
            IsCorrelatedRuntimeFailure(exception, token, exactRuntimeCall: true))
        {
            throw CreateRuntimeUnavailableException(exception, token);
        }
    }

    internal async Task<RobloxUser?> GetAuthenticatedUserAsync(
        WebSessionToken token,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var message = await RunMessageScriptAsync(
            requestId,
            RobloxWebScripts.GetAuthenticatedUser(requestId),
            AccountTimeout,
            token,
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
        if (!IsBoundedDisplayText(name, 50))
            return null;
        var safeName = name!;
        var displayName = user.TryGetProperty("displayName", out var displayNameElement)
            ? displayNameElement.GetString() ?? safeName
            : safeName;
        if (!IsBoundedDisplayText(displayName, 200))
            displayName = safeName;
        return new RobloxUser(id, safeName, displayName!);
    }

    internal async Task<LaunchTarget?> ResolvePrivateServerAsync(
        string shareCode,
        WebSessionToken token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shareCode);
        var requestId = Guid.NewGuid().ToString("N");
        var message = await RunMessageScriptAsync(
            requestId,
            RobloxWebScripts.ResolvePrivateServer(requestId, shareCode),
            ApiTimeout,
            token,
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
        return linkCode is null || !PrivateServerCodePattern.IsMatch(linkCode)
            ? null
            : new LaunchTarget(placeId, linkCode, null);
    }

    internal async Task<string?> GetAuthenticationTicketAsync(
        WebSessionToken token,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var message = await RunMessageScriptAsync(
            requestId,
            RobloxWebScripts.GetAuthenticationTicket(requestId),
            ApiTimeout,
            token,
            cancellationToken);
        if (message is null ||
            !message.Value.TryGetProperty("ticket", out var ticketElement))
        {
            return null;
        }

        var ticket = ticketElement.GetString();
        return ticket is { Length: > 0 and <= MaximumAuthenticationTicketCharacters } &&
               !ticket.Any(char.IsControl)
            ? ticket
            : null;
    }

    internal async Task<string?> GetUserLocaleAsync(
        WebSessionToken token,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var message = await RunMessageScriptAsync(
            requestId,
            RobloxWebScripts.GetUserLocale(requestId),
            LocaleTimeout,
            token,
            cancellationToken);
        if (message is null ||
            !message.Value.TryGetProperty("locale", out var localeElement))
        {
            return null;
        }

        var locale = localeElement.GetString();
        return locale is { Length: > 0 and <= 32 } &&
               !locale.Any(char.IsControl)
            ? locale
            : null;
    }

    internal async Task<string?> GetExperienceNameAsync(
        long placeId,
        WebSessionToken token,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(placeId);
        var requestId = Guid.NewGuid().ToString("N");
        var message = await RunMessageScriptAsync(
            requestId,
            RobloxWebScripts.GetExperienceName(requestId, placeId),
            ApiTimeout,
            token,
            cancellationToken);
        if (message is null ||
            !message.Value.TryGetProperty("name", out var nameElement))
        {
            return null;
        }

        var name = nameElement.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(name) || name.Length > 200 ? null : name;
    }

    internal async Task<bool> ClearProfileAsync(
        WebSessionToken token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCurrent(token);
        if (!IsReady)
            return false;

        try
        {
            await GetCore(token).Profile.ClearBrowsingDataAsync(
                    CoreWebView2BrowsingDataKinds.AllProfile)
                .WaitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            IsCorrelatedRuntimeFailure(exception, token, exactRuntimeCall: true))
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
        core.Settings.AreHostObjectsAllowed = false;
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
        core.ProcessFailed += Core_ProcessFailed;
        core.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
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
        WebSessionToken token,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var core = GetCore(token);
        var completion = new TaskCompletionSource<JsonElement?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<CoreWebView2WebMessageReceivedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            try
            {
                if (!IsTrustedScriptOrigin(args.Source))
                    return;
                var message = args.WebMessageAsJson;
                if (message.Length > MaximumWebMessageCharacters)
                    return;
                using var document = JsonDocument.Parse(
                    message,
                    new JsonDocumentOptions { MaxDepth = 8 });
                var root = document.RootElement;
                if (!root.TryGetProperty("requestId", out var idElement) ||
                    idElement.GetString() != requestId)
                {
                    return;
                }

                completion.TrySetResult(root.Clone());
            }
            catch (Exception exception) when (
                exception is JsonException or ArgumentException or
                    InvalidOperationException ||
                IsCorrelatedRuntimeFailure(
                    exception,
                    token,
                    exactRuntimeCall: true))
            {
                // Ignore unrelated or malformed browser messages.
            }
        };

        core.WebMessageReceived += handler;
        try
        {
            try
            {
                await core.ExecuteScriptAsync(script);
            }
            catch (Exception exception) when (
                IsCorrelatedRuntimeFailure(
                    exception,
                    token,
                    exactRuntimeCall: true))
            {
                throw CreateRuntimeUnavailableException(exception, token);
            }
            EnsureCurrent(token);
            var delay = Task.Delay(timeout, cancellationToken);
            var finished = await Task.WhenAny(completion.Task, delay);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCurrent(token);
            return finished == completion.Task
                ? await completion.Task
                : null;
        }
        finally
        {
            try
            {
                core.WebMessageReceived -= handler;
            }
            catch (Exception exception) when (
                IsCorrelatedRuntimeFailure(
                    exception,
                    token,
                    exactRuntimeCall: true))
            {
                System.Diagnostics.Trace.WriteLine(
                    $"Superseded WebView2 handler cleanup failed safely: {exception.GetType().Name}.");
            }
        }
    }

    private void Core_NewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (sender is CoreWebView2 core && IsTrustedBrowserLocation(args.Uri))
        {
            try
            {
                core.Navigate(args.Uri);
            }
            catch (Exception exception) when (
                _currentToken is { } token &&
                IsCorrelatedRuntimeFailure(
                    exception,
                    token,
                    exactRuntimeCall: true))
            {
                System.Diagnostics.Trace.WriteLine(
                    $"Superseded WebView2 navigation failed safely: {exception.GetType().Name}.");
            }
        }
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
        var token = _currentToken;
        if (browser is null || token is null)
            return;

        try
        {
            if (!args.IsSuccess ||
                sender != browser.CoreWebView2 ||
                browser.Source is not { } source ||
                !IsTrustedScriptHost(source.Host))
            {
                return;
            }
        }
        catch (Exception exception) when (
            IsCurrent(token.Value) &&
            IsCorrelatedRuntimeFailure(
                exception,
                token.Value,
                exactRuntimeCall: true))
        {
            MarkSessionUnavailable(token.Value);
            return;
        }

        if (IsCurrent(token.Value))
            RobloxPageLoaded?.Invoke(this, new WebSessionEventArgs(token.Value));
    }

    private void Core_ProcessFailed(
        object? sender,
        CoreWebView2ProcessFailedEventArgs args)
    {
        if (_currentToken is not { } token ||
            sender is not CoreWebView2 core ||
            !ReferenceEquals(core, _browser?.CoreWebView2))
        {
            return;
        }

        if (args.ProcessFailedKind is not (
                CoreWebView2ProcessFailedKind.BrowserProcessExited or
                CoreWebView2ProcessFailedKind.RenderProcessExited or
                CoreWebView2ProcessFailedKind.RenderProcessUnresponsive))
        {
            System.Diagnostics.Trace.WriteLine(
                $"WebView2 subprocess reported {args.ProcessFailedKind} and was left to runtime recovery.");
            return;
        }

        MarkSessionUnavailable(token);
    }

    private void MarkSessionUnavailable(WebSessionToken token)
    {
        if (!IsCurrent(token) ||
            !_isReady && _failedGeneration == token.Generation)
        {
            return;
        }

        _isReady = false;
        _failedGeneration = token.Generation;
        SessionUnavailable?.Invoke(
            this,
            new WebSessionUnavailableEventArgs(
                token,
                WebSessionUnavailableReason.ProcessExited));
    }

    private bool IsCurrent(WebSessionBrowser session) =>
        IsCurrent(session.Token) && ReferenceEquals(session.Browser, _browser);

    internal bool IsCurrent(WebSessionToken token) =>
        _currentToken == token && token.Generation == _generation;

    private CoreWebView2 GetCore(WebSessionToken token)
    {
        EnsureCurrent(token);
        if (!IsReady)
        {
            throw new WebSessionUnavailableException(
                _failedGeneration == token.Generation
                    ? WebSessionUnavailableReason.ProcessExited
                    : WebSessionUnavailableReason.Closed,
                "The Roblox web session is no longer available.");
        }
        return _browser!.CoreWebView2;
    }

    private void EnsureCurrent(WebSessionToken token)
    {
        if (!IsCurrent(token))
        {
            throw new WebSessionUnavailableException(
                WebSessionUnavailableReason.Superseded,
                "A newer Roblox account session replaced this operation.");
        }
    }

    private bool IsCorrelatedRuntimeFailure(
        Exception exception,
        WebSessionToken token,
        bool exactRuntimeCall = false)
    {
        if (exception is WebSessionUnavailableException)
            return true;
        var correlated = exactRuntimeCall ||
            !IsCurrent(token) ||
            _failedGeneration == token.Generation;
        return correlated &&
            (exception is ObjectDisposedException or InvalidOperationException ||
             exception is COMException comException &&
             IsClosedRuntimeHResult(comException.HResult));
    }

    private WebSessionUnavailableException CreateRuntimeUnavailableException(
        Exception exception,
        WebSessionToken token) =>
        exception as WebSessionUnavailableException ??
        new WebSessionUnavailableException(
            IsCurrent(token)
                ? WebSessionUnavailableReason.ProcessExited
                : WebSessionUnavailableReason.Superseded,
            IsCurrent(token)
                ? "The Roblox web session process exited. Reconnect this account and try again."
                : "A newer Roblox account session replaced this operation.",
            exception);

    internal static bool IsExpectedInitializationHResult(int hResult) =>
        hResult is
            unchecked((int)0x80070032) or
            unchecked((int)0x8007139F) or
            unchecked((int)0x80070578) or
            unchecked((int)0x80070070) or
            unchecked((int)0x8007064E) or
            unchecked((int)0x80070002) or
            unchecked((int)0x80070050) or
            unchecked((int)0x80070005) or
            unchecked((int)0x80004005);

    private static bool IsClosedRuntimeHResult(int hResult) =>
        hResult is
            unchecked((int)0x80010108) or
            unchecked((int)0x800401FD) or
            unchecked((int)0x80000013);

    private static bool IsExpectedRuntimeTeardownFailure(
        Exception exception) =>
        exception is ObjectDisposedException or InvalidOperationException ||
        exception is COMException comException &&
        IsClosedRuntimeHResult(comException.HResult);

    private static bool IsTrustedBrowserLocation(string location)
    {
        if (location.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            return true;
        return Uri.TryCreate(location, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               uri.IsDefaultPort &&
               string.IsNullOrEmpty(uri.UserInfo) &&
               IsTrustedScriptHost(uri.Host);
    }

    private static bool IsTrustedScriptOrigin(string location) =>
        Uri.TryCreate(location, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        IsTrustedScriptHost(uri.Host);

    private static bool IsTrustedScriptHost(string host) =>
        host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("www.roblox.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsBoundedDisplayText(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl);
}
