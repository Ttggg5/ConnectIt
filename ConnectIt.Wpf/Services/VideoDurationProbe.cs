using System.Windows.Media;
using System.Windows.Threading;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 讀取影片檔案的總長度中繼資料,不需要真的解碼/播放——遙控面板的時間軸需要知道總長度才能畫,
/// 主機端本身平常完全不解析媒體檔案(見 <see cref="PlaybackControlState"/> 的說明),這裡是唯一
/// 的例外。用 WPF 內建的 <see cref="MediaPlayer"/>(底層是 Media Foundation)開檔讀
/// <c>NaturalDuration</c>,不需要額外掛 ffmpeg 之類的外部工具/相依套件,跟
/// <see cref="VideoThumbnailGenerator"/> 用殼層縮圖 API 的理由一樣,盡量用 Windows 本身就有的能力。
///
/// <see cref="MediaPlayer"/> 是 STA-only,而且要有跑起來的 Dispatcher 訊息迴圈才會真的觸發
/// MediaOpened/MediaFailed 事件,所以跟縮圖產生一樣另外開一條專用的 STA 執行緒、自己跑一個
/// <see cref="Dispatcher.Run"/>。只開檔讀中繼資料,不會呼叫 <c>Play()</c>,不會真的播放。
/// </summary>
public static class VideoDurationProbe
{
    private static readonly SemaphoreSlim ConcurrencyLimit = new(2);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>嘗試探測影片長度(毫秒);格式不支援/檔案有問題/逾時等情況回傳 null。</summary>
    public static async Task<long?> TryGetDurationMsAsync(string filePath)
    {
        await ConcurrencyLimit.WaitAsync().ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<long?>();

            var thread = new Thread(() => ProbeOnStaThread(filePath, tcs))
            {
                IsBackground = true,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            ConcurrencyLimit.Release();
        }
    }

    private static void ProbeOnStaThread(string filePath, TaskCompletionSource<long?> tcs)
    {
        MediaPlayer? player = null;
        DispatcherTimer? timeoutTimer = null;

        void Finish(long? result)
        {
            if (tcs.TrySetResult(result))
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }

        try
        {
            player = new MediaPlayer();
            player.MediaOpened += (_, _) =>
                Finish(player.NaturalDuration.HasTimeSpan ? (long)player.NaturalDuration.TimeSpan.TotalMilliseconds : null);
            player.MediaFailed += (_, _) => Finish(null);

            timeoutTimer = new DispatcherTimer { Interval = Timeout };
            timeoutTimer.Tick += (_, _) => Finish(null);
            timeoutTimer.Start();

            player.Open(new Uri(filePath));
            Dispatcher.Run();
        }
        catch (Exception)
        {
            tcs.TrySetResult(null);
        }
        finally
        {
            timeoutTimer?.Stop();
            player?.Close();
        }
    }
}
