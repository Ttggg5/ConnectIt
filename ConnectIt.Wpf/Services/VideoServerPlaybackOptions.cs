namespace ConnectIt.Wpf.Services;

/// <summary>啟動影片伺服器時套用的自訂選項快照(來自 <see cref="VideoServerSettingsService"/>)。
/// 獨立成一個型別而不是一長串參數,方便 <see cref="VideoStreamingService.Start"/> 的呼叫端閱讀。</summary>
public sealed record VideoServerPlaybackOptions
{
    public static readonly VideoServerPlaybackOptions Default = new();

    public string DefaultSort { get; init; } = VideoServerSettingsService.DefaultSortValue;

    public bool AutoplayNext { get; init; } = VideoServerSettingsService.DefaultAutoplayNext;

    public bool Shuffle { get; init; } = VideoServerSettingsService.DefaultShuffle;

    public int DefaultVolumePercent { get; init; } = VideoServerSettingsService.DefaultVolumePercent;

    public double DefaultSpeed { get; init; } = VideoServerSettingsService.DefaultPlaybackSpeed;

    public int AutoplayCountdownSeconds { get; init; } = VideoServerSettingsService.DefaultAutoplayCountdownSeconds;

    public IReadOnlyCollection<string> ExtraExtensions { get; init; } = [];
}
