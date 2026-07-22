using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace SessionDock.SystemProcesses;

internal sealed class HandleScopeApiBootstrapper
{
    internal const string RequiredPolicy = "roblox-singleton-event-v1";
    private const int MaximumHealthResponseBytes = 64 * 1024;
    private readonly HandleScopeConnectionLoader _connectionLoader;
    private readonly HttpClient _client;
    private readonly IHandleScopeProcessVerifier _processVerifier;

    public HandleScopeApiBootstrapper(
        HandleScopeConnectionLoader connectionLoader,
        HttpClient client)
        : this(
            connectionLoader,
            client,
            HandleScopeProcessVerifier.CreateDefault())
    {
    }

    internal HandleScopeApiBootstrapper(
        HandleScopeConnectionLoader connectionLoader,
        HttpClient client,
        IHandleScopeProcessVerifier processVerifier)
    {
        ArgumentNullException.ThrowIfNull(connectionLoader);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(processVerifier);
        _connectionLoader = connectionLoader;
        _client = client;
        _processVerifier = processVerifier;
    }

    public async Task<HandleScopeConnection?> GetExistingAsync(
        CancellationToken cancellationToken)
    {
        var existing = _connectionLoader.Load();
        if (existing is not null &&
            _processVerifier.IsExpected(existing) &&
            await IsReadyAsync(existing, cancellationToken))
        {
            return existing;
        }
        Trace.WriteLine(
            "HandleScope is enabled, but its installed local API is unavailable.");
        return null;
    }

    private async Task<bool> IsReadyAsync(
        HandleScopeConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(connection.BaseUrl, "/v1/health"));
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength > MaximumHealthResponseBytes)
            {
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(
                cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                    break;
                if (buffer.Length + read > MaximumHealthResponseBytes)
                    return false;
                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            using var document = await JsonDocument.ParseAsync(
                buffer,
                cancellationToken: cancellationToken);
            return IsValidHealthDocument(document.RootElement);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or JsonException or IOException or
            TaskCanceledException or OperationCanceledException)
        {
            if (ex is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return false;
        }
    }

    internal static bool IsValidHealthDocument(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        HasUniqueProperties(root) &&
        root.TryGetProperty("status", out var status) &&
        status.ValueKind == JsonValueKind.String &&
        status.GetString()?.Equals("ready", StringComparison.Ordinal) == true &&
        root.TryGetProperty("apiVersion", out var apiVersion) &&
        apiVersion.ValueKind == JsonValueKind.String &&
        apiVersion.GetString()?.Equals("v1", StringComparison.Ordinal) == true &&
        root.TryGetProperty("policy", out var policy) &&
        policy.ValueKind == JsonValueKind.String &&
        policy.GetString()?.Equals(RequiredPolicy, StringComparison.Ordinal) == true;

    private static bool HasUniqueProperties(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return root.EnumerateObject().All(property => names.Add(property.Name));
    }

}
