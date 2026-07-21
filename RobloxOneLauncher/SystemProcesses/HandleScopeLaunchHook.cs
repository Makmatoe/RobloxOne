using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RobloxOneLauncher.SystemProcesses;

public sealed class HandleScopeLaunchHook : ILaunchHook
{
    private const string SessionIdToken = "{SESSION_ID}";
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions RequestJsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly HandleScopeConfigurationLoader _configurationLoader;
    private readonly HandleScopeConnectionLoader _connectionLoader;
    private readonly HttpClient _client;
    private readonly HandleScopeApiBootstrapper _apiBootstrapper;
    private readonly object _queueLock = new();
    private Task _operationTail = Task.CompletedTask;
    private bool _disposed;

    public HandleScopeLaunchHook()
        : this(new HandleScopeConfigurationLoader(), new HandleScopeConnectionLoader())
    {
    }

    public HandleScopeLaunchHook(
        HandleScopeConfigurationLoader configurationLoader,
        string connectionPath)
        : this(
            configurationLoader,
            new HandleScopeConnectionLoader(connectionPath))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionPath);
    }

    internal HandleScopeLaunchHook(
        HandleScopeConfigurationLoader configurationLoader,
        HandleScopeConnectionLoader connectionLoader)
    {
        ArgumentNullException.ThrowIfNull(configurationLoader);
        ArgumentNullException.ThrowIfNull(connectionLoader);
        _configurationLoader = configurationLoader;
        _connectionLoader = connectionLoader;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = RequestTimeout,
            UseProxy = false
        };
        _client = new HttpClient(handler)
        {
            Timeout = RequestTimeout
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxOne/1.0");
        _apiBootstrapper = new HandleScopeApiBootstrapper(
            _connectionLoader,
            _client);
    }

    public Task NotifyLaunchAsync(
        LaunchHookEvent launchEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchEvent);
        lock (_queueLock)
        {
            if (_disposed)
                return Task.CompletedTask;
            _operationTail = RunQueuedAsync(
                _operationTail,
                launchEvent,
                cancellationToken);
            return _operationTail;
        }
    }

    public void Dispose()
    {
        lock (_queueLock)
            _disposed = true;
        _client.Dispose();
    }

    private async Task RunQueuedAsync(
        Task previousOperation,
        LaunchHookEvent launchEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await previousOperation.ConfigureAwait(false);
        }
        catch
        {
            // A prior optional operation cannot poison the serialized queue.
        }

        cancellationToken.ThrowIfCancellationRequested();
        await NotifyCoreAsync(launchEvent, cancellationToken);
    }

    private async Task NotifyCoreAsync(
        LaunchHookEvent launchEvent,
        CancellationToken cancellationToken)
    {
        var configuration = _configurationLoader.LoadEnabled();
        if (configuration is null)
            return;
        if (launchEvent.ProcessId <= 0)
        {
            Trace.WriteLine("HandleScope operation skipped: launched PID is unavailable.");
            return;
        }

        try
        {
            configuration = ResolveSessionSelector(
                configuration,
                launchEvent.ProcessId);
            if (configuration is null)
                return;

            var connection = await _apiBootstrapper.GetExistingAsync(
                cancellationToken);
            if (connection is null)
                return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(
                configuration.RetryTimeoutSeconds));
            var closed = await RetryPidCloseAsync(
                launchEvent.ProcessId,
                configuration,
                connection,
                timeout.Token);
            if (closed && configuration.AllProcesses)
            {
                await SweepAllProcessesAsync(
                    configuration,
                    connection,
                    timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Trace.WriteLine("HandleScope PID retry window expired.");
        }
        catch (OperationCanceledException)
        {
            // Application shutdown cancellation is expected.
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"HandleScope integration failed: {ex.GetType().Name}.");
        }
    }

    private static HandleScopeConfiguration? ResolveSessionSelector(
        HandleScopeConfiguration configuration,
        int processId)
    {
        if (!configuration.HandleName.Contains(
                SessionIdToken,
                StringComparison.OrdinalIgnoreCase))
        {
            return configuration;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return new HandleScopeConfiguration
            {
                Enabled = configuration.Enabled,
                ProcessName = configuration.ProcessName,
                HandleName = configuration.HandleName.Replace(
                    SessionIdToken,
                    process.SessionId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.OrdinalIgnoreCase),
                HandleType = configuration.HandleType,
                Access = configuration.Access,
                Match = configuration.Match,
                CloseAll = configuration.CloseAll,
                AllProcesses = configuration.AllProcesses,
                RetryTimeoutSeconds = configuration.RetryTimeoutSeconds,
                RetryIntervalMilliseconds =
                    configuration.RetryIntervalMilliseconds
            };
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException)
        {
            Trace.WriteLine(
                $"HandleScope session selector could not be resolved: {ex.GetType().Name}.");
            return null;
        }
    }

    private async Task<bool> RetryPidCloseAsync(
        int processId,
        HandleScopeConfiguration configuration,
        HandleScopeConnection connection,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (HasProcessExited(processId))
                return false;

            var dryRun = await SendCloseAsync(
                CreatePidRequest(processId, configuration, dryRun: true),
                connection,
                processId,
                cancellationToken);
            if (dryRun.Kind == CloseOutcomeKind.Failure)
                return false;

            if (dryRun.Kind == CloseOutcomeKind.Completed)
            {
                var close = await SendCloseAsync(
                    CreatePidRequest(processId, configuration, dryRun: false),
                    connection,
                    processId,
                    cancellationToken);
                if (close.ClosedExpectedProcess)
                    return true;
                if (close.Kind == CloseOutcomeKind.Failure)
                    return false;
            }

            await Task.Delay(
                configuration.RetryIntervalMilliseconds,
                cancellationToken);
        }

        return false;
    }

    private async Task SweepAllProcessesAsync(
        HandleScopeConfiguration configuration,
        HandleScopeConnection connection,
        CancellationToken cancellationToken)
    {
        var dryRun = await SendCloseAsync(
            CreateAllProcessesRequest(configuration, dryRun: true),
            connection,
            expectedProcessId: null,
            cancellationToken);
        if (dryRun.Kind != CloseOutcomeKind.Completed)
            return;

        await SendCloseAsync(
            CreateAllProcessesRequest(configuration, dryRun: false),
            connection,
            expectedProcessId: null,
            cancellationToken);
    }

    private async Task<CloseOutcome> SendCloseAsync(
        CloseRequest payload,
        HandleScopeConnection connection,
        int? expectedProcessId,
        CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = new Uri(
                connection.BaseUrl.AbsoluteUri.TrimEnd('/') +
                "/v1/handles/close");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(
                    payload,
                    options: RequestJsonOptions)
            };
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", connection.Token);

            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return CloseOutcome.NoMatch;
            if (response.StatusCode is not HttpStatusCode.OK and
                not HttpStatusCode.MultiStatus)
            {
                Trace.WriteLine(
                    $"HandleScope returned HTTP {(int)response.StatusCode}.");
                return CloseOutcome.Failure;
            }

            return await ParseResponseAsync(
                response,
                expectedProcessId,
                cancellationToken);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or JsonException or InvalidDataException)
        {
            Trace.WriteLine(
                $"HandleScope request failed: {ex.GetType().Name}.");
            return CloseOutcome.Failure;
        }
    }

    private static async Task<CloseOutcome> ParseResponseAsync(
        HttpResponseMessage response,
        int? expectedProcessId,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("HandleScope response was too large.");
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        using var document = await JsonDocument.ParseAsync(
            buffer,
            cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !HasOperationShape(root))
        {
            Trace.WriteLine("HandleScope returned a malformed operation response.");
            return CloseOutcome.Failure;
        }

        var closedExpectedProcess =
            FindPositiveNumber(root, "closedCount") ||
            expectedProcessId is int pid && ClosedCollectionContainsPid(root, pid);
        return new CloseOutcome(
            CloseOutcomeKind.Completed,
            closedExpectedProcess);
    }

    private static CloseRequest CreatePidRequest(
        int processId,
        HandleScopeConfiguration configuration,
        bool dryRun) =>
        new(
            new ProcessSelector(processId, null),
            CreateHandleSelector(configuration),
            configuration.CloseAll,
            dryRun,
            false);

    private static CloseRequest CreateAllProcessesRequest(
        HandleScopeConfiguration configuration,
        bool dryRun) =>
        new(
            new ProcessSelector(null, configuration.ProcessName),
            CreateHandleSelector(configuration),
            configuration.CloseAll,
            dryRun,
            true);

    private static HandleSelector CreateHandleSelector(
        HandleScopeConfiguration configuration) =>
        new(
            configuration.HandleName,
            configuration.Match,
            configuration.HandleType,
            configuration.Access);

    private static bool HasProcessExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasOperationShape(JsonElement element) =>
        EnumerateProperties(element).Any(property =>
            property.Name.Equals("closedCount", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Equals("closed", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Equals("matchedCount", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Equals("matches", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Equals("results", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Equals("processCount", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Equals("skipped", StringComparison.OrdinalIgnoreCase));

    private static bool FindPositiveNumber(JsonElement element, string propertyName)
    {
        foreach (var property in EnumerateProperties(element))
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetInt64(out var value) &&
                value > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ClosedCollectionContainsPid(JsonElement element, int processId)
    {
        foreach (var property in EnumerateProperties(element))
        {
            if (!property.Name.Equals("closed", StringComparison.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in property.Value.EnumerateArray())
            {
                if (ContainsPid(item, processId))
                    return true;
            }
        }

        return false;
    }

    private static bool ContainsPid(JsonElement element, int processId)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("pid", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt32(out var pid) &&
                    pid == processId)
                {
                    return true;
                }

                if (ContainsPid(property.Value, processId))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(
                item => ContainsPid(item, processId));
        }

        return false;
    }

    private static IEnumerable<JsonProperty> EnumerateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property;
                foreach (var nested in EnumerateProperties(property.Value))
                    yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumerateProperties(item))
                    yield return nested;
            }
        }
    }

    private sealed record CloseRequest(
        ProcessSelector Process,
        HandleSelector Handle,
        bool CloseAll,
        bool DryRun,
        bool AllProcesses);

    private sealed record ProcessSelector(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? Pid,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Name);

    private sealed record HandleSelector(
        string Name,
        string Match,
        string? Type,
        string? Access);

    private enum CloseOutcomeKind
    {
        Completed,
        NoMatch,
        Failure
    }

    private sealed record CloseOutcome(
        CloseOutcomeKind Kind,
        bool ClosedExpectedProcess)
    {
        public static CloseOutcome NoMatch { get; } =
            new(CloseOutcomeKind.NoMatch, false);
        public static CloseOutcome Failure { get; } =
            new(CloseOutcomeKind.Failure, false);
    }
}
