using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace RobloxOneLauncher.Services;

public sealed class RobloxServerTracker
{
    private static readonly TimeSpan TrackingTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogTimestampTolerance = TimeSpan.FromSeconds(5);
    private static readonly Regex JoinPattern = new(
        @"Joining game '(?<job>[0-9a-fA-F-]{36})' place (?<place>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UserPattern = new(
        @"\buserid:(?<user>\d+),",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex TimestampPattern = new(
        @"^(?<timestamp>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string _logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Roblox",
        "logs");

    public async Task<string?> FindJoinedServerAsync(
        long expectedUserId,
        long expectedPlaceId,
        DateTimeOffset launchStartedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedUserId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedPlaceId);

        var deadline = DateTimeOffset.UtcNow + TrackingTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var serverJobId = TryFindJoinedServer(
                expectedUserId,
                expectedPlaceId,
                launchStartedAt);
            if (serverJobId is not null)
                return serverJobId;

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        return null;
    }

    private string? TryFindJoinedServer(
        long expectedUserId,
        long expectedPlaceId,
        DateTimeOffset launchStartedAt)
    {
        if (!Directory.Exists(_logDirectory))
            return null;

        IEnumerable<FileInfo> candidates;
        try
        {
            candidates = new DirectoryInfo(_logDirectory)
                .EnumerateFiles("*_Player_*.log", SearchOption.TopDirectoryOnly)
                .Where(file =>
                    file.LastWriteTimeUtc >=
                    launchStartedAt.UtcDateTime - LogTimestampTolerance)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(16)
                .ToArray();
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var file in candidates)
        {
            var serverJobId = TryReadJoinedServer(
                file.FullName,
                expectedUserId,
                expectedPlaceId,
                launchStartedAt - LogTimestampTolerance);
            if (serverJobId is not null)
                return serverJobId;
        }

        return null;
    }

    private static string? TryReadJoinedServer(
        string path,
        long expectedUserId,
        long expectedPlaceId,
        DateTimeOffset earliestTimestamp)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? pendingServerJobId = null;
            long pendingPlaceId = 0;

            while (reader.ReadLine() is { } line)
            {
                var joinMatch = JoinPattern.Match(line);
                if (joinMatch.Success &&
                    TryReadTimestamp(line, out var timestamp) &&
                    timestamp >= earliestTimestamp &&
                    long.TryParse(
                        joinMatch.Groups["place"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var placeId) &&
                    Guid.TryParse(joinMatch.Groups["job"].Value, out var jobId))
                {
                    pendingServerJobId = jobId.ToString("D");
                    pendingPlaceId = placeId;
                    continue;
                }

                if (pendingServerJobId is null || pendingPlaceId != expectedPlaceId)
                    continue;

                var userMatch = UserPattern.Match(line);
                if (userMatch.Success &&
                    long.TryParse(
                        userMatch.Groups["user"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var userId) &&
                    userId == expectedUserId)
                {
                    return pendingServerJobId;
                }
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            // Roblox can rotate or replace a log while it is being inspected.
        }

        return null;
    }

    private static bool TryReadTimestamp(
        string line,
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        var match = TimestampPattern.Match(line);
        return match.Success && DateTimeOffset.TryParseExact(
            match.Groups["timestamp"].Value,
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }
}
