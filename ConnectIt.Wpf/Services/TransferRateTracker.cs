namespace ConnectIt.Wpf.Services;

/// <summary>
/// 根據傳輸進度回報計算目前的百分比、傳輸速度與預計剩餘時間,給傳輸卡片顯示用
/// (例如「45%・3.2 MB/s・約剩 12 秒」)。
/// 速度用指數移動平均平滑:進度事件很頻繁、每次間隔的位元組數起伏很大,直接相除會讓數字一直跳。
/// 時間來源可注入,方便測試。
/// </summary>
public sealed class TransferRateTracker
{
    /// <summary>兩次取樣至少間隔這麼久才更新速度,避免極短間隔造成的雜訊。</summary>
    private const double MinSampleIntervalSeconds = 0.25;

    /// <summary>平滑係數:越大越貼近最新的瞬間速度,越小越平穩。</summary>
    private const double SmoothingFactor = 0.3;

    private readonly Func<double> _nowSeconds;
    private long _totalBytes;
    private long _lastBytes;
    private double _lastSampleAt;
    private double? _smoothedBytesPerSecond;

    public TransferRateTracker()
        : this(() => Environment.TickCount64 / 1000.0)
    {
    }

    /// <summary>測試用:指定時間來源(秒)。</summary>
    public TransferRateTracker(Func<double> nowSeconds)
    {
        _nowSeconds = nowSeconds;
    }

    /// <summary>開始追蹤一個新的傳輸(總大小可能稍後才知道,可以先傳 0)。</summary>
    public void Reset(long totalBytes = 0)
    {
        _totalBytes = Math.Max(0, totalBytes);
        _lastBytes = 0;
        _lastSampleAt = _nowSeconds();
        _smoothedBytesPerSecond = null;
    }

    public TransferRateSnapshot Update(long bytesTransferred, long totalBytes)
    {
        if (totalBytes > 0)
        {
            _totalBytes = totalBytes;
        }

        var now = _nowSeconds();
        var elapsed = now - _lastSampleAt;

        if (bytesTransferred < _lastBytes)
        {
            // 進度倒退(例如換成下一個資料夾項目但回報的是單檔進度),重新起算。
            _lastBytes = bytesTransferred;
            _lastSampleAt = now;
        }
        else if (elapsed >= MinSampleIntervalSeconds)
        {
            var instant = (bytesTransferred - _lastBytes) / elapsed;
            _smoothedBytesPerSecond = _smoothedBytesPerSecond is { } previous
                ? previous + SmoothingFactor * (instant - previous)
                : instant;
            _lastBytes = bytesTransferred;
            _lastSampleAt = now;
        }

        var fraction = _totalBytes > 0 ? Math.Clamp((double)bytesTransferred / _totalBytes, 0, 1) : 0;
        TimeSpan? remaining = null;
        if (_smoothedBytesPerSecond is > 1 && _totalBytes > 0)
        {
            remaining = TimeSpan.FromSeconds(Math.Max(0, _totalBytes - bytesTransferred) / _smoothedBytesPerSecond.Value);
        }

        return new TransferRateSnapshot(fraction, bytesTransferred, _totalBytes, _smoothedBytesPerSecond, remaining);
    }
}

/// <summary>某一刻的傳輸狀態。速度/剩餘時間在還沒有足夠樣本前為 null。</summary>
public readonly record struct TransferRateSnapshot(
    double Fraction, long BytesTransferred, long TotalBytes, double? BytesPerSecond, TimeSpan? Remaining)
{
    public int Percent => (int)Math.Floor(Fraction * 100);

    /// <summary>例如「3.2 MB/s・約剩 12 秒」;還在估算時回傳「計算速度中…」。</summary>
    public string RateText
    {
        get
        {
            if (BytesPerSecond is not { } speed)
            {
                return "計算速度中…";
            }

            var text = $"{FormatBytes((long)speed)}/s";
            if (Remaining is { } remaining)
            {
                text += $"・約剩 {FormatDuration(remaining)}";
            }

            return text;
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:0} {units[unitIndex]}" : $"{value:0.#} {units[unitIndex]}";
    }

    /// <summary>「8 秒」「3 分 05 秒」「1 小時 02 分」。</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        var totalSeconds = (long)Math.Ceiling(duration.TotalSeconds);
        if (totalSeconds < 60)
        {
            return $"{Math.Max(1, totalSeconds)} 秒";
        }

        if (totalSeconds < 3600)
        {
            return $"{totalSeconds / 60} 分 {totalSeconds % 60:00} 秒";
        }

        return $"{totalSeconds / 3600} 小時 {totalSeconds % 3600 / 60:00} 分";
    }
}
