namespace ConnectIt.Wpf.Services;

/// <summary>遠端控制模式的播放狀態快照,見 <see cref="PlaybackControlState.Snapshot"/>。</summary>
public sealed class PlaybackStateSnapshot
{
    public required bool Enabled { get; init; }

    public string? VideoRelativePath { get; init; }

    public required bool IsPlaying { get; init; }

    public required long PositionMs { get; init; }

    public required double PlaybackRate { get; init; }

    public required double Volume { get; init; }

    public required bool Muted { get; init; }

    public required long Version { get; init; }

    public required long UpdatedAtUtcMs { get; init; }
}

/// <summary>
/// 遠端控制模式的播放狀態,對應 Android 端 video/PlaybackControlState.kt——用「碼表」模型記錄
/// 上次更新時的位置、當下是否在播放、跟一個 monotonic 時間戳,每次被問到時用經過的時間做外插
/// 算出「現在」的位置,觀眾端(HTTP 輪詢 `/control/state`)不需要自己處理時鐘校正。
///
/// 主機端不一定真的在播放影片(遙控器介面可以只是選片/下指令),所以這裡完全不持有/依賴任何
/// 播放器實例,純粹是一個被動的資料模型,由 <see cref="VideoStreamingService"/> 持有單一實例,
/// 同時餵給 HTTP 伺服器(讀,回應輪詢)跟遙控面板(寫,下指令)。
/// </summary>
public sealed class PlaybackControlState
{
    private readonly Lock _gate = new();
    private readonly Func<long> _clockMs;

    private bool _enabled;
    private string? _videoRelativePath;
    private bool _isPlaying;
    private long _positionAtUpdate;
    private long _updatedAt;
    private long _updatedAtUtcMs;
    private double _playbackRate = 1.0;
    private double _volume = 1.0;
    private bool _muted;
    private long _version;

    /// <summary><paramref name="clockMs"/> 預設用 <see cref="Environment.TickCount64"/>(不受系統時間
    /// 校正/時區影響的 monotonic 時鐘),測試時可注入假時鐘。</summary>
    public PlaybackControlState(Func<long>? clockMs = null)
    {
        _clockMs = clockMs ?? (() => Environment.TickCount64);
        _updatedAt = _clockMs();
        _updatedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
            _version++;
        }
    }

    /// <summary>切換主機目前播放的影片。<paramref name="autoplay"/> 預設 true,符合「換片就接著播」的直覺行為。</summary>
    public void SetVideo(string relativePath, long startPositionMs = 0, bool autoplay = true)
    {
        lock (_gate)
        {
            _videoRelativePath = relativePath;
            _positionAtUpdate = Math.Max(0, startPositionMs);
            _updatedAt = _clockMs();
            _updatedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _isPlaying = autoplay;
            _version++;
        }
    }

    public void SetPlaying(bool isPlaying)
    {
        lock (_gate)
        {
            if (_isPlaying == isPlaying)
            {
                return;
            }

            // 切換播放狀態前,先把目前(外插後)的位置定住,否則從 playing 切成 paused 那一瞬間,
            // 下一次 Snapshot 會少算/多算這段時間。
            _positionAtUpdate = CurrentPositionLocked();
            _updatedAt = _clockMs();
            _updatedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _isPlaying = isPlaying;
            _version++;
        }
    }

    public void Seek(long positionMs)
    {
        lock (_gate)
        {
            _positionAtUpdate = Math.Max(0, positionMs);
            _updatedAt = _clockMs();
            _updatedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _version++;
        }
    }

    public void SetPlaybackRate(double rate)
    {
        lock (_gate)
        {
            var clamped = Math.Clamp(rate, 0.25, 4.0);
            if (_playbackRate == clamped)
            {
                return;
            }

            _playbackRate = clamped;
            _version++;
        }
    }

    public void SetVolume(double volume)
    {
        lock (_gate)
        {
            var clamped = Math.Clamp(volume, 0.0, 1.0);
            if (_volume == clamped)
            {
                return;
            }

            _volume = clamped;
            _version++;
        }
    }

    public void SetMuted(bool muted)
    {
        lock (_gate)
        {
            if (_muted == muted)
            {
                return;
            }

            _muted = muted;
            _version++;
        }
    }

    /// <summary>重新分享/停止分享時呼叫——遠端控制狀態不跨 session 保留,一律回到「關閉、沒選片」。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _enabled = false;
            _videoRelativePath = null;
            _isPlaying = false;
            _positionAtUpdate = 0;
            _updatedAt = _clockMs();
            _updatedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _playbackRate = 1.0;
            _volume = 1.0;
            _muted = false;
            _version++;
        }
    }

    public PlaybackStateSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new PlaybackStateSnapshot
            {
                Enabled = _enabled,
                VideoRelativePath = _videoRelativePath,
                IsPlaying = _isPlaying,
                PositionMs = CurrentPositionLocked(),
                PlaybackRate = _playbackRate,
                Volume = _volume,
                Muted = _muted,
                Version = _version,
                UpdatedAtUtcMs = _updatedAtUtcMs,
            };
        }
    }

    /// <summary>呼叫端必須已持有 <see cref="_gate"/>。播放中的話用經過的時間外插目前位置,暫停中就是定住的那個值。</summary>
    private long CurrentPositionLocked() =>
        _isPlaying ? _positionAtUpdate + Math.Max(0, _clockMs() - _updatedAt) : _positionAtUpdate;
}
