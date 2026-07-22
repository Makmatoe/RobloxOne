using Velopack.Sources;

namespace SessionDock.Services;

internal sealed class BoundedUpdateDownloader : IFileDownloader
{
    internal const double MetadataTimeoutMinutes = 20d / 60d;
    private readonly IFileDownloader _inner;

    public BoundedUpdateDownloader()
        : this(new HttpClientFileDownloader())
    {
    }

    internal BoundedUpdateDownloader(IFileDownloader inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Task DownloadFile(
        string url,
        string targetFile,
        Action<int> progress,
        IDictionary<string, string>? headers = null,
        double timeout = 30,
        CancellationToken cancelToken = default) =>
        _inner.DownloadFile(
            url,
            targetFile,
            progress,
            headers,
            timeout,
            cancelToken);

    public Task<byte[]> DownloadBytes(
        string url,
        IDictionary<string, string>? headers = null,
        double timeout = 30) =>
        _inner.DownloadBytes(url, headers, BoundMetadataTimeout(timeout));

    public Task<string> DownloadString(
        string url,
        IDictionary<string, string>? headers = null,
        double timeout = 30) =>
        _inner.DownloadString(url, headers, BoundMetadataTimeout(timeout));

    private static double BoundMetadataTimeout(double timeout) =>
        double.IsFinite(timeout) && timeout > 0
            ? Math.Min(timeout, MetadataTimeoutMinutes)
            : MetadataTimeoutMinutes;
}
