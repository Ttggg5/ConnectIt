using ConnectIt.Wpf.Services;

namespace ConnectIt.Tests;

public class TransferRateTrackerTests
{
    private double _now;

    private TransferRateTracker CreateTracker(long total)
    {
        var tracker = new TransferRateTracker(() => _now);
        tracker.Reset(total);
        return tracker;
    }

    [Fact]
    public void Update_ComputesPercentSpeedAndRemainingTime()
    {
        var tracker = CreateTracker(10_000_000);

        _now = 1;
        var snapshot = tracker.Update(1_000_000, 10_000_000);

        Assert.Equal(10, snapshot.Percent);
        Assert.Equal(1_000_000, snapshot.BytesPerSecond!.Value, precision: 0);
        Assert.Equal(9, snapshot.Remaining!.Value.TotalSeconds, precision: 3);
    }

    [Fact]
    public void Update_SmoothsSpeedInsteadOfJumpingToTheLatestSample()
    {
        var tracker = CreateTracker(100_000_000);

        _now = 1;
        tracker.Update(1_000_000, 100_000_000); // 1 MB/s
        _now = 2;
        var snapshot = tracker.Update(11_000_000, 100_000_000); // 瞬間 10 MB/s

        Assert.InRange(snapshot.BytesPerSecond!.Value, 1_000_001, 9_999_999);
    }

    [Fact]
    public void Update_BeforeFirstSample_HasNoSpeedYet()
    {
        var tracker = CreateTracker(1000);

        _now = 0.1;
        var snapshot = tracker.Update(100, 1000);

        Assert.Null(snapshot.BytesPerSecond);
        Assert.Null(snapshot.Remaining);
        Assert.Equal("計算速度中…", snapshot.RateText);
    }

    [Theory]
    [InlineData(8, "8 秒")]
    [InlineData(185, "3 分 05 秒")]
    [InlineData(3720, "1 小時 02 分")]
    public void FormatDuration_UsesReadableChineseUnits(int seconds, string expected)
    {
        Assert.Equal(expected, TransferRateSnapshot.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }
}
