using System.Text.Json;
using SessionDock.ReleaseTrust;

namespace SessionDock.Services;

internal static class UpdateFeedReader
{
    private const string InvalidFeedMessage =
        "GitHub returned an invalid update feed. The installed version was left unchanged.";

    public static async Task<T> ReadAsync<T>(Func<Task<T>> readAsync)
    {
        ArgumentNullException.ThrowIfNull(readAsync);
        try
        {
            return await readAsync();
        }
        catch (JsonException exception)
        {
            throw new ReleaseTrustException(InvalidFeedMessage, exception);
        }
        catch (ArgumentException exception) when (
            exception.GetType() == typeof(ArgumentException))
        {
            throw new ReleaseTrustException(InvalidFeedMessage, exception);
        }
    }
}
