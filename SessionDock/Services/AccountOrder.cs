using SessionDock.Models;

namespace SessionDock.Services;

internal static class AccountOrder
{
    internal static bool WouldMoveBefore(
        IReadOnlyList<AccountProfile> accounts,
        string sourceKey,
        string? beforeKey) =>
        TryResolveMove(accounts, sourceKey, beforeKey, out _, out _);

    internal static bool TryMoveBefore(
        List<AccountProfile> accounts,
        string sourceKey,
        string? beforeKey)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (!TryResolveMove(
                accounts,
                sourceKey,
                beforeKey,
                out var sourceIndex,
                out var targetIndex))
        {
            return false;
        }

        var account = accounts[sourceIndex];
        accounts.RemoveAt(sourceIndex);
        accounts.Insert(targetIndex, account);
        return true;
    }

    private static bool TryResolveMove(
        IReadOnlyList<AccountProfile> accounts,
        string sourceKey,
        string? beforeKey,
        out int sourceIndex,
        out int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        sourceIndex = FindIndex(accounts, sourceKey);
        targetIndex = -1;
        if (sourceIndex < 0 || accounts.Count < 2)
            return false;

        if (beforeKey is null)
        {
            targetIndex = accounts.Count - 1;
            return sourceIndex != targetIndex;
        }

        var beforeIndex = FindIndex(accounts, beforeKey);
        if (beforeIndex < 0 || beforeIndex == sourceIndex)
            return false;

        targetIndex = beforeIndex > sourceIndex
            ? beforeIndex - 1
            : beforeIndex;
        return sourceIndex != targetIndex;
    }

    private static int FindIndex(
        IReadOnlyList<AccountProfile> accounts,
        string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return -1;

        for (var index = 0; index < accounts.Count; index++)
        {
            if (accounts[index].Key.Equals(
                    key,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}
