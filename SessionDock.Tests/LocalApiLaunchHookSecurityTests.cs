using System.Diagnostics;
using System.Text.Json;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class LocalApiLaunchHookSecurityTests
{
    [Fact]
    public void CaptureAndScrubConfiguration_CurrentUrlWithoutCurrentTokenFailsClosed()
    {
        var environment = CreateLegacyEnvironment();
        environment[LocalApiLaunchHook.UrlEnvironmentVariable] =
            "https://127.0.0.1:4314/current";

        var captured = CaptureAndScrub(environment);

        Assert.Equal(
            "https://127.0.0.1:4314/current",
            captured.Endpoint);
        Assert.Null(captured.Token);
        AssertRejected(captured);
        AssertHookEnvironmentWasScrubbed(environment);
    }

    [Fact]
    public void CaptureAndScrubConfiguration_CurrentTokenWithoutCurrentUrlFailsClosed()
    {
        var environment = CreateLegacyEnvironment();
        environment[LocalApiLaunchHook.TokenEnvironmentVariable] =
            "current-secret";

        var captured = CaptureAndScrub(environment);

        Assert.Null(captured.Endpoint);
        Assert.Equal("current-secret", captured.Token);
        AssertRejected(captured);
        AssertHookEnvironmentWasScrubbed(environment);
    }

    [Fact]
    public void CaptureAndScrubConfiguration_CompleteCurrentPairWinsAndRemainsReusable()
    {
        var environment = CreateLegacyEnvironment();
        environment[LocalApiLaunchHook.UrlEnvironmentVariable] =
            "https://127.0.0.1:4314/current";
        environment[LocalApiLaunchHook.TokenEnvironmentVariable] =
            "current-secret";

        var captured = CaptureAndScrub(environment);

        Assert.Equal(
            "https://127.0.0.1:4314/current",
            captured.Endpoint);
        Assert.Equal("current-secret", captured.Token);
        AssertHookEnvironmentWasScrubbed(environment);
        using var first = new LocalApiLaunchHook(captured);
        using var second = new LocalApiLaunchHook(captured);
        Assert.True(first.IsConfigured);
        Assert.True(second.IsConfigured);
    }

    [Fact]
    public void CaptureAndScrubConfiguration_NoCurrentValuesUsesCoherentLegacyPair()
    {
        var environment = CreateLegacyEnvironment();

        var captured = CaptureAndScrub(environment);

        Assert.Equal(
            "https://127.0.0.1:4313/legacy",
            captured.Endpoint);
        Assert.Equal("legacy-secret", captured.Token);
        AssertHookEnvironmentWasScrubbed(environment);
        using var hook = new LocalApiLaunchHook(captured);
        Assert.True(hook.IsConfigured);
    }

    [Theory]
    [InlineData("http://127.0.0.1:4312/launch")]
    [InlineData("https://192.0.2.10:4312/launch")]
    [InlineData("https://localhost:4312/launch")]
    [InlineData("https://user@127.0.0.1:4312/launch")]
    [InlineData("https://127.0.0.1:4312/launch?mode=full")]
    [InlineData("https://127.0.0.1:4312/launch#fragment")]
    public void TryCreateAuthenticatedRequest_InsecureEndpointCreatesNoPayload(
        string endpoint)
    {
        var created = LocalApiLaunchHook.TryCreateAuthenticatedRequest(
            CreateEvent(),
            endpoint,
            "test-secret",
            out var request);

        Assert.False(created);
        Assert.Null(request);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("line\nbreak")]
    public void TryCreateAuthenticatedRequest_MissingOrInvalidTokenCreatesNoPayload(
        string? token)
    {
        var created = LocalApiLaunchHook.TryCreateAuthenticatedRequest(
            CreateEvent(),
            "https://127.0.0.1:4312/launch",
            token,
            out var request);

        Assert.False(created);
        Assert.Null(request);
    }

    [Theory]
    [InlineData("https://127.0.0.1:4312/launch")]
    [InlineData("https://[::1]:4312/launch")]
    public async Task TryCreateAuthenticatedRequest_TrustedShapeBindsBearerAndAccountPayload(
        string endpoint)
    {
        var created = LocalApiLaunchHook.TryCreateAuthenticatedRequest(
            CreateEvent(),
            endpoint,
            "test-secret",
            out var request);

        Assert.True(created);
        var authenticatedRequest = Assert.IsType<HttpRequestMessage>(request);
        using (authenticatedRequest)
        {
            Assert.Equal(HttpMethod.Post, authenticatedRequest.Method);
            Assert.Equal(
                Uri.UriSchemeHttps,
                authenticatedRequest.RequestUri!.Scheme);
            Assert.Equal(
                "Bearer",
                authenticatedRequest.Headers.Authorization!.Scheme);
            Assert.Equal(
                "test-secret",
                authenticatedRequest.Headers.Authorization.Parameter);
            Assert.Equal(
                "application/json",
                authenticatedRequest.Content!.Headers.ContentType!.MediaType);

            var json = await authenticatedRequest.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(789, document.RootElement
                .GetProperty("accountUserId")
                .GetInt64());
            Assert.Equal("builder", document.RootElement
                .GetProperty("accountUsername")
                .GetString());
            Assert.Equal("Main", document.RootElement
                .GetProperty("accountLabel")
                .GetString());
        }
    }

    [Fact]
    public void RemoveConfigurationFromChildEnvironment_RemovesCurrentAndLegacySecretsOnly()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "child.exe",
            UseShellExecute = false
        };
        startInfo.Environment[LocalApiLaunchHook.UrlEnvironmentVariable] =
            "https://127.0.0.1:4312/launch";
        startInfo.Environment[LocalApiLaunchHook.TokenEnvironmentVariable] =
            "current-secret";
        startInfo.Environment[LocalApiLaunchHook.LegacyUrlEnvironmentVariable] =
            "https://127.0.0.1:4313/launch";
        startInfo.Environment[LocalApiLaunchHook.LegacyTokenEnvironmentVariable] =
            "legacy-secret";
        startInfo.Environment["SESSIONDOCK_ORDINARY_TEST_VALUE"] =
            "preserve-me";

        LocalApiLaunchHook.RemoveConfigurationFromChildEnvironment(startInfo);

        Assert.DoesNotContain(
            LocalApiLaunchHook.UrlEnvironmentVariable,
            startInfo.Environment.Keys,
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            LocalApiLaunchHook.TokenEnvironmentVariable,
            startInfo.Environment.Keys,
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            LocalApiLaunchHook.LegacyUrlEnvironmentVariable,
            startInfo.Environment.Keys,
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            LocalApiLaunchHook.LegacyTokenEnvironmentVariable,
            startInfo.Environment.Keys,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            "preserve-me",
            startInfo.Environment["SESSIONDOCK_ORDINARY_TEST_VALUE"]);
    }

    private static LaunchHookEvent CreateEvent() =>
        new(
            "event-id",
            DateTimeOffset.UnixEpoch,
            123,
            456,
            "Experience",
            true,
            789,
            "builder",
            "Main");

    private static Dictionary<string, string?> CreateLegacyEnvironment() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [LocalApiLaunchHook.LegacyUrlEnvironmentVariable] =
                "https://127.0.0.1:4313/legacy",
            [LocalApiLaunchHook.LegacyTokenEnvironmentVariable] =
                "legacy-secret",
            ["SESSIONDOCK_ORDINARY_TEST_VALUE"] = "preserve-me"
        };

    private static LocalApiLaunchHook.CapturedConfiguration CaptureAndScrub(
        Dictionary<string, string?> environment) =>
        LocalApiLaunchHook.CaptureAndScrubConfiguration(
            name => environment.TryGetValue(name, out var value)
                ? value
                : null,
            (name, value) =>
            {
                if (value is null)
                    environment.Remove(name);
                else
                    environment[name] = value;
            });

    private static void AssertRejected(
        LocalApiLaunchHook.CapturedConfiguration captured)
    {
        var created = LocalApiLaunchHook.TryCreateAuthenticatedRequest(
            CreateEvent(),
            captured.Endpoint,
            captured.Token,
            out var request);

        Assert.False(created);
        Assert.Null(request);
    }

    private static void AssertHookEnvironmentWasScrubbed(
        IReadOnlyDictionary<string, string?> environment)
    {
        foreach (var variableName in new[]
        {
            LocalApiLaunchHook.UrlEnvironmentVariable,
            LocalApiLaunchHook.TokenEnvironmentVariable,
            LocalApiLaunchHook.LegacyUrlEnvironmentVariable,
            LocalApiLaunchHook.LegacyTokenEnvironmentVariable
        })
        {
            Assert.DoesNotContain(
                variableName,
                environment.Keys,
                StringComparer.OrdinalIgnoreCase);
        }

        Assert.Equal(
            "preserve-me",
            environment["SESSIONDOCK_ORDINARY_TEST_VALUE"]);
    }
}
