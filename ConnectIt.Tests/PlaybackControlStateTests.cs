using ConnectIt.Wpf.Services;

namespace ConnectIt.Tests;

public class PlaybackControlStateTests
{
    private sealed class FakeClock
    {
        public long NowMs { get; set; }

        public long Read() => NowMs;
    }

    [Fact]
    public void Snapshot_InitialState_IsDisabledWithNoVideo()
    {
        var state = new PlaybackControlState();

        var snapshot = state.Snapshot();

        Assert.False(snapshot.Enabled);
        Assert.Null(snapshot.VideoRelativePath);
        Assert.False(snapshot.IsPlaying);
        Assert.Equal(0, snapshot.PositionMs);
        Assert.Equal(1.0, snapshot.PlaybackRate);
        Assert.Equal(1.0, snapshot.Volume);
        Assert.False(snapshot.Muted);
    }

    [Fact]
    public void SetPlaybackRate_ClampsToSupportedRange()
    {
        var state = new PlaybackControlState();

        state.SetPlaybackRate(10.0);
        Assert.Equal(4.0, state.Snapshot().PlaybackRate);

        state.SetPlaybackRate(0.01);
        Assert.Equal(0.25, state.Snapshot().PlaybackRate);
    }

    [Fact]
    public void SetVolume_ClampsToZeroOneRange()
    {
        var state = new PlaybackControlState();

        state.SetVolume(1.5);
        Assert.Equal(1.0, state.Snapshot().Volume);

        state.SetVolume(-0.5);
        Assert.Equal(0.0, state.Snapshot().Volume);
    }

    [Fact]
    public void SetMuted_TogglesIndependentlyOfVolume()
    {
        var state = new PlaybackControlState();

        state.SetMuted(true);
        var snapshot = state.Snapshot();
        Assert.True(snapshot.Muted);
        Assert.Equal(1.0, snapshot.Volume);
    }

    [Fact]
    public void Reset_RestoresPlaybackRateVolumeAndMutedToDefaults()
    {
        var state = new PlaybackControlState();
        state.SetPlaybackRate(2.0);
        state.SetVolume(0.3);
        state.SetMuted(true);

        state.Reset();

        var snapshot = state.Snapshot();
        Assert.Equal(1.0, snapshot.PlaybackRate);
        Assert.Equal(1.0, snapshot.Volume);
        Assert.False(snapshot.Muted);
    }

    [Fact]
    public void SetVideo_DefaultsToAutoplayFromGivenPosition()
    {
        var state = new PlaybackControlState();

        state.SetVideo("movies/a.mp4", startPositionMs: 5_000);

        var snapshot = state.Snapshot();
        Assert.Equal("movies/a.mp4", snapshot.VideoRelativePath);
        Assert.True(snapshot.IsPlaying);
        Assert.Equal(5_000, snapshot.PositionMs);
    }

    [Fact]
    public void Snapshot_WhilePlaying_ExtrapolatesPositionByElapsedTime()
    {
        var clock = new FakeClock { NowMs = 0 };
        var state = new PlaybackControlState(clock.Read);

        state.SetVideo("movies/a.mp4", startPositionMs: 1_000);
        clock.NowMs = 3_500;

        Assert.Equal(1_000 + 3_500, state.Snapshot().PositionMs);
    }

    [Fact]
    public void Snapshot_WhilePaused_DoesNotAdvancePosition()
    {
        var clock = new FakeClock { NowMs = 0 };
        var state = new PlaybackControlState(clock.Read);

        state.SetVideo("movies/a.mp4", startPositionMs: 1_000, autoplay: false);
        clock.NowMs = 10_000;

        Assert.Equal(1_000, state.Snapshot().PositionMs);
    }

    [Fact]
    public void SetPlaying_False_FreezesPositionAtCurrentExtrapolatedValue()
    {
        var clock = new FakeClock { NowMs = 0 };
        var state = new PlaybackControlState(clock.Read);

        state.SetVideo("movies/a.mp4", startPositionMs: 0);
        clock.NowMs = 2_000;
        state.SetPlaying(false);
        clock.NowMs = 10_000;

        Assert.Equal(2_000, state.Snapshot().PositionMs);
    }

    [Fact]
    public void SetPlaying_True_ResumesExtrapolatingFromFrozenPosition()
    {
        var clock = new FakeClock { NowMs = 0 };
        var state = new PlaybackControlState(clock.Read);

        state.SetVideo("movies/a.mp4", startPositionMs: 0);
        clock.NowMs = 2_000;
        state.SetPlaying(false);
        clock.NowMs = 5_000;
        state.SetPlaying(true);
        clock.NowMs = 6_000;

        Assert.Equal(2_000 + 1_000, state.Snapshot().PositionMs);
    }

    [Fact]
    public void Seek_SetsPositionRegardlessOfPlayingState()
    {
        var clock = new FakeClock { NowMs = 0 };
        var state = new PlaybackControlState(clock.Read);

        state.SetVideo("movies/a.mp4", startPositionMs: 0);
        state.Seek(42_000);

        Assert.Equal(42_000, state.Snapshot().PositionMs);
    }

    [Fact]
    public void Seek_NegativeValue_ClampsToZero()
    {
        var state = new PlaybackControlState();

        state.Seek(-500);

        Assert.Equal(0, state.Snapshot().PositionMs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetEnabled_NoOpWhenAlreadyAtThatValue_DoesNotBumpVersion(bool enabled)
    {
        var state = new PlaybackControlState();
        state.SetEnabled(enabled);
        var versionAfterFirstSet = state.Snapshot().Version;

        state.SetEnabled(enabled);

        Assert.Equal(versionAfterFirstSet, state.Snapshot().Version);
    }

    [Fact]
    public void Version_IncrementsOnEveryMutation()
    {
        var state = new PlaybackControlState();
        var v0 = state.Snapshot().Version;

        state.SetEnabled(true);
        var v1 = state.Snapshot().Version;
        state.SetVideo("a.mp4");
        var v2 = state.Snapshot().Version;
        state.SetPlaying(false);
        var v3 = state.Snapshot().Version;
        state.Seek(1000);
        var v4 = state.Snapshot().Version;

        Assert.True(v1 > v0);
        Assert.True(v2 > v1);
        Assert.True(v3 > v2);
        Assert.True(v4 > v3);
    }

    [Fact]
    public void Reset_ClearsEnabledVideoAndPlayingState()
    {
        var state = new PlaybackControlState();
        state.SetEnabled(true);
        state.SetVideo("a.mp4", startPositionMs: 1_000);

        state.Reset();

        var snapshot = state.Snapshot();
        Assert.False(snapshot.Enabled);
        Assert.Null(snapshot.VideoRelativePath);
        Assert.False(snapshot.IsPlaying);
        Assert.Equal(0, snapshot.PositionMs);
    }
}
