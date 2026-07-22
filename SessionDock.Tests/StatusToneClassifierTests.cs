using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class StatusToneClassifierTests
{
    [Theory]
    [InlineData("BATCH COMPLETE", StatusTone.Success)]
    [InlineData("SETTINGS SAVED", StatusTone.Success)]
    [InlineData("BATCH PARTIAL", StatusTone.Warning)]
    [InlineData("DUPLICATE ACCOUNT", StatusTone.Warning)]
    [InlineData("BATCH CANCELLED", StatusTone.Warning)]
    [InlineData("UPDATE BUSY", StatusTone.Warning)]
    [InlineData("SESSION ERROR", StatusTone.Error)]
    [InlineData("UPDATE REJECTED", StatusTone.Error)]
    [InlineData("ACCESS DENIED", StatusTone.Error)]
    [InlineData("NETWORK TIMEOUT", StatusTone.Error)]
    [InlineData("CHECKING SESSION", StatusTone.Neutral)]
    public void Classify_ReturnsExpectedTone(string badge, StatusTone expected)
    {
        Assert.Equal(expected, StatusToneClassifier.Classify(badge));
    }
}
