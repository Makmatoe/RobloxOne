using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class RuntimeSecurityPolicyTests
{
    [Fact]
    public void IsSupported_StandardInteractiveUser_ReturnsTrue()
    {
        var supported = RuntimeSecurityPolicy.IsSupported(
            new RuntimeSecurityContext(false, 1, "S-1-5-21-1-2-3-1001"),
            out var reason);

        Assert.True(supported);
        Assert.Empty(reason);
    }

    [Fact]
    public void IsSupported_ElevatedToken_ReturnsFalse()
    {
        var supported = RuntimeSecurityPolicy.IsSupported(
            new RuntimeSecurityContext(true, 1, "S-1-5-21-1-2-3-1001"),
            out _);

        Assert.False(supported);
    }

    [Theory]
    [InlineData(0, "S-1-5-21-1-2-3-1001")]
    [InlineData(1, "S-1-5-18")]
    [InlineData(1, "S-1-5-19")]
    [InlineData(1, "S-1-5-20")]
    [InlineData(1, null)]
    public void IsSupported_NonInteractiveOrServiceContext_ReturnsFalse(
        int sessionId,
        string? userSid)
    {
        var supported = RuntimeSecurityPolicy.IsSupported(
            new RuntimeSecurityContext(false, sessionId, userSid),
            out _);

        Assert.False(supported);
    }
}
