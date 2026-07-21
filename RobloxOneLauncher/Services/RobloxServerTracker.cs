using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RobloxOneLauncher.Services;

public sealed class RobloxServerTracker
{
    private const int MaximumCandidateLogs = 8;
    private const int MaximumLogTailBytes = 512 * 1024;
    private const int MaximumLogLineCharacters = 16 * 1024;
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
        if (!Directory.Exists(_logDirectory) || IsReparsePoint(_logDirectory))
            return null;

        IEnumerable<FileInfo> candidates;
        try
        {
            candidates = new DirectoryInfo(_logDirectory)
                .EnumerateFiles("*_Player_*.log", SearchOption.TopDirectoryOnly)
                .Where(file => !IsReparsePoint(file.FullName))
                .Where(file =>
                    file.LastWriteTimeUtc >=
                    launchStartedAt.UtcDateTime - LogTimestampTolerance)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(MaximumCandidateLogs)
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
            var available = stream.Length;
            var offset = Math.Max(0, available - MaximumLogTailBytes);
            stream.Position = offset;
            var length = checked((int)Math.Min(
                MaximumLogTailBytes,
                available - offset));
            var bytes = new byte[length];
            var total = 0;
            while (total < length)
            {
                var read = stream.Read(bytes, total, length - total);
                if (read == 0)
                    break;
                total += read;
            }

            var text = Encoding.UTF8.GetString(bytes, 0, total);
            if (offset > 0)
            {
                var firstLineEnd = text.IndexOf('\n');
                if (firstLineEnd < 0)
                    return null;
                text = text[(firstLineEnd + 1)..];
            }

            using var reader = new StringReader(text);
            string? pendingServerJobId = null;
            long pendingPlaceId = 0;

            while (reader.ReadLine() is { } line)
            {
                if (line.Length > MaximumLogLineCharacters)
                {
                    pendingServerJobId = null;
                    pendingPlaceId = 0;
                    continue;
                }

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

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
