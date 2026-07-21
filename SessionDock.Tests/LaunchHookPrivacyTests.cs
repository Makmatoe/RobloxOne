using System.Text.Json;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class LaunchHookPrivacyTests
{
    [Fact]
    public void SerializedLaunchEvent_ContainsOnlyDocumentedMetadata()
    {
        var launchEvent = new LaunchHookEvent(
            "event-id",
            DateTimeOffset.UnixEpoch,
            123,
            456,
            "Experience",
            true,
            789,
            "builder",
            "Main");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(
            launchEvent,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var propertyNames = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "accountLabel",
                "accountUserId",
                "accountUsername",
                "eventId",
                "eventType",
                "experienceName",
                "isPrivateServer",
                "occurredAt",
                "placeId",
                "processId"
            },
            propertyNames);
    }
}
