using SessionDock.Services;
using Velopack.Sources;

namespace SessionDock.Tests;

public sealed class BoundedUpdateDownloaderTests
{
    [Fact]
    public async Task DownloadString_LongSourceTimeout_IsBounded()
    {
        var inner = new RecordingDownloader();
        var downloader = new BoundedUpdateDownloader(inner);

        await downloader.DownloadString(
            "https://api.github.com/releases",
            new Dictionary<string, string>(),
            timeout: 30);

        Assert.Equal(BoundedUpdateDownloader.MetadataTimeoutMinutes, inner.StringTimeout);
    }

    [Fact]
    public async Task DownloadBytes_ShortSourceTimeout_IsPreserved()
    {
        var inner = new RecordingDownloader();
        var downloader = new BoundedUpdateDownloader(inner);
        const double sourceTimeoutMinutes = 0.1;

        await downloader.DownloadBytes(
            "https://api.github.com/releases",
            new Dictionary<string, string>(),
            sourceTimeoutMinutes);

        Assert.Equal(sourceTimeoutMinutes, inner.BytesTimeout);
    }

    [Fact]
    public async Task DownloadFile_PackageTimeoutAndCancellation_ArePreserved()
    {
        var inner = new RecordingDownloader();
        var downloader = new BoundedUpdateDownloader(inner);
        using var cancellation = new CancellationTokenSource();

        await downloader.DownloadFile(
            "https://github.com/package.nupkg",
            "package.nupkg",
            _ => { },
            new Dictionary<string, string>(),
            timeout: 30,
            cancellation.Token);

        Assert.Equal(30, inner.FileTimeout);
        Assert.Equal(cancellation.Token, inner.FileCancellation);
    }

    private sealed class RecordingDownloader : IFileDownloader
    {
        public double? StringTimeout { get; private set; }

        public double? BytesTimeout { get; private set; }

        public double? FileTimeout { get; private set; }

        public CancellationToken FileCancellation { get; private set; }

        public Task DownloadFile(
            string url,
            string targetFile,
            Action<int> progress,
            IDictionary<string, string>? headers,
            double timeout,
            CancellationToken cancelToken)
        {
            FileTimeout = timeout;
            FileCancellation = cancelToken;
            return Task.CompletedTask;
        }

        public Task<byte[]> DownloadBytes(
            string url,
            IDictionary<string, string>? headers,
            double timeout)
        {
            BytesTimeout = timeout;
            return Task.FromResult(Array.Empty<byte>());
        }

        public Task<string> DownloadString(
            string url,
            IDictionary<string, string>? headers,
            double timeout)
        {
            StringTimeout = timeout;
            return Task.FromResult(string.Empty);
        }
    }
}
