using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace RobloxOneLauncher.SystemProcesses;

internal sealed class HandleScopeApiBootstrapper
{
    private const int MaximumHealthResponseBytes = 64 * 1024;
    private readonly HandleScopeConnectionLoader _connectionLoader;
    private readonly HttpClient _client;

    public HandleScopeApiBootstrapper(
        HandleScopeConnectionLoader connectionLoader,
        HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(connectionLoader);
        ArgumentNullException.ThrowIfNull(client);
        _connectionLoader = connectionLoader;
        _client = client;
    }

    public async Task<HandleScopeConnection?> GetExistingAsync(
        CancellationToken cancellationToken)
    {
        var existing = _connectionLoader.Load();
        if (existing is not null &&
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
                new Uri(
                    connection.BaseUrl.AbsoluteUri.TrimEnd('/') +
                    "/v1/health"));
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
            return document.RootElement.TryGetProperty("status", out var status) &&
                   status.ValueKind == JsonValueKind.String &&
                   status.GetString()?.Equals(
                       "ready",
                       StringComparison.OrdinalIgnoreCase) == true;
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
}
