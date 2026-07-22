namespace SessionDock.Tests;

public sealed class MainWindowLayoutTests
{
    [Theory]
    [InlineData(120, 200, 120, 80)]
    [InlineData(80, 200, -120, 120)]
    public void CalculateHorizontalWheelOffset_MovesInWheelDirection(
        double currentOffset,
        double scrollableWidth,
        int wheelDelta,
        double expectedOffset)
    {
        Assert.Equal(
            expectedOffset,
            MainWindow.CalculateHorizontalWheelOffset(
                currentOffset,
                scrollableWidth,
                wheelDelta));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void CalculateHorizontalWheelOffset_PreservesPrecisionWheelMovement(
        int wheelDelta)
    {
        const double currentOffset = 100;

        var result = MainWindow.CalculateHorizontalWheelOffset(
            currentOffset,
            scrollableWidth: 200,
            wheelDelta);

        Assert.NotEqual(currentOffset, result);
        Assert.Equal(
            Math.Abs(wheelDelta / 3d),
            Math.Abs(result - currentOffset),
            precision: 10);
    }

    [Theory]
    [InlineData(10, 200, 120, 0)]
    [InlineData(190, 200, -120, 200)]
    [InlineData(50, 0, -120, 0)]
    public void CalculateHorizontalWheelOffset_ClampsToAvailableRange(
        double currentOffset,
        double scrollableWidth,
        int wheelDelta,
        double expectedOffset)
    {
        Assert.Equal(
            expectedOffset,
            MainWindow.CalculateHorizontalWheelOffset(
                currentOffset,
                scrollableWidth,
                wheelDelta));
    }
}
