using System.Text.RegularExpressions;
using RobloxOneLauncher.Models;

namespace RobloxOneLauncher.Services;

public static class DestinationParser
{
    private const int MaximumDestinationLength = 4096;
    private static readonly Regex GamePathPattern = new(
        @"/games/(?<id>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ShareCodePattern = new(
        @"^[A-Za-z0-9_-]{6,200}$",
        RegexOptions.Compiled);

    public static bool TryParse(
        string input,
        out LaunchTarget? target,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(input);
        target = null;
        error = "Enter a Place ID, Roblox private-server link, or the value after code=.";
        if (input.Length > MaximumDestinationLength)
        {
            error = "The destination is too long.";
            return false;
        }

        var value = input.Trim();
        if (long.TryParse(value, out var numericPlaceId) && numericPlaceId > 0)
        {
            target = new LaunchTarget(numericPlaceId, null, null);
            return true;
        }

        if (value.StartsWith("code=", StringComparison.OrdinalIgnoreCase))
            value = value[5..].Trim();

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            var trustedHttps = uri.Scheme == Uri.UriSchemeHttps &&
                               IsRobloxHost(uri.Host) &&
                               uri.IsDefaultPort &&
                               string.IsNullOrEmpty(uri.UserInfo);
            var trustedRobloxProtocol =
                uri.Scheme.Equals("roblox", StringComparison.OrdinalIgnoreCase);
            if (!trustedHttps && !trustedRobloxProtocol)
            {
                error = "Only official roblox.com links are accepted.";
                return false;
            }

            var query = ParseQuery(uri.Query);
            if (query.TryGetValue("code", out var shareCode))
            {
                if (!IsValidCode(shareCode))
                {
                    error = "That Roblox URL contains an invalid private-server code.";
                    return false;
                }

                target = new LaunchTarget(0, null, shareCode);
                return true;
            }

            var placeMatch = GamePathPattern.Match(uri.AbsolutePath);
            if (placeMatch.Success &&
                long.TryParse(placeMatch.Groups["id"].Value, out var placeId) &&
                placeId > 0)
            {
                query.TryGetValue("privateServerLinkCode", out var legacyLinkCode);
                if (string.IsNullOrWhiteSpace(legacyLinkCode))
                    query.TryGetValue("linkCode", out legacyLinkCode);
                if (legacyLinkCode is not null && !IsValidCode(legacyLinkCode))
                {
                    error = "That Roblox URL contains an invalid private-server code.";
                    return false;
                }

                target = new LaunchTarget(
                    placeId,
                    legacyLinkCode,
                    null);
                return true;
            }

            error = "That Roblox URL does not contain a Place ID or private-server code.";
            return false;
        }

        if (IsValidCode(value))
        {
            target = new LaunchTarget(0, null, value);
            return true;
        }

        return false;
    }

    private static bool IsRobloxHost(string host) =>
        host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidCode(string? value) =>
        value is not null && ShareCodePattern.IsMatch(value);

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in query.TrimStart('?').Split(
                     '&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace("+", " "));
            var value = pair.Length > 1
                ? Uri.UnescapeDataString(pair[1].Replace("+", " "))
                : string.Empty;
            values[key] = value;
        }

        return values;
    }
}
