using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace RobloxOneLauncher.SystemProcesses;

internal sealed class HandleScopeConnectionLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly string _connectionPath;

    public HandleScopeConnectionLoader(string? connectionPath = null)
    {
        _connectionPath = connectionPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HandleScope",
            "connection.json");
    }

    public HandleScopeConnection? Load()
    {
        if (!File.Exists(_connectionPath))
        {
            Trace.WriteLine("HandleScope connection is unavailable.");
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize<ConnectionDocument>(
                File.ReadAllText(_connectionPath),
                JsonOptions);
            if (document is null ||
                !TryValidateBaseUrl(document.BaseUrl, out var baseUrl) ||
                string.IsNullOrWhiteSpace(document.Token) ||
                document.Token.Length > 4096 ||
                (!string.IsNullOrWhiteSpace(document.ApiVersion) &&
                 !document.ApiVersion.Equals("v1", StringComparison.OrdinalIgnoreCase)))
            {
                Trace.WriteLine("HandleScope connection was rejected.");
                return null;
            }

            return new HandleScopeConnection(
                baseUrl!,
                document.Token,
                document.ApiVersion ?? "v1",
                document.ProcessId ?? document.ApiProcessId);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.WriteLine(
                $"HandleScope connection could not be read: {ex.GetType().Name}.");
            return null;
        }
    }

    private static bool TryValidateBaseUrl(string? value, out Uri? baseUrl)
    {
        baseUrl = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp &&
             parsed.Scheme != Uri.UriSchemeHttps) ||
            !parsed.IsLoopback ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        baseUrl = parsed;
        return true;
    }

    private sealed class ConnectionDocument
    {
        public string? BaseUrl { get; set; }
        public string? Token { get; set; }
        public string? ApiVersion { get; set; }
        public int? ProcessId { get; set; }
        public int? ApiProcessId { get; set; }
    }
}

internal sealed record HandleScopeConnection(
    Uri BaseUrl,
    string Token,
    string ApiVersion,
    int? ApiProcessId);
