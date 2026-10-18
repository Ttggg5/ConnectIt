namespace ConnectIt.Wpf.Services;

/// <summary>
/// 設定頁數字輸入框的即時驗證:輸入當下就判斷是否合法,合法才儲存,不合法就在輸入框下方顯示原因,
/// 而不是像以前一樣失去焦點時默默把值夾到範圍內(使用者不知道自己的輸入被改掉了)。
/// </summary>
public static class SettingsValidation
{
    public readonly record struct Result(bool IsValid, int Value, string? Error)
    {
        public static Result Ok(int value) => new(true, value, null);

        public static Result Fail(string error) => new(false, 0, error);
    }

    public static Result ValidateSearchDuration(string? text) =>
        ValidateRange(text, DiscoverySettingsService.MinSearchDurationSeconds, DiscoverySettingsService.MaxSearchDurationSeconds, "秒");

    public static Result ValidateConnectTimeout(string? text) =>
        ValidateRange(text, ConnectionSettingsService.MinConnectTimeoutSeconds, ConnectionSettingsService.MaxConnectTimeoutSeconds, "秒");

    public static Result ValidateAutoplayCountdown(string? text) =>
        ValidateRange(text, VideoServerSettingsService.MinAutoplayCountdownSeconds, VideoServerSettingsService.MaxAutoplayCountdownSeconds, "秒");

    /// <summary>留空代表交由系統自動指派(回傳 0);否則必須是 1024-65535(避開需要系統管理員權限的特權連接埠)。</summary>
    public static Result ValidatePort(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Result.Ok(0);
        }

        return int.TryParse(text.Trim(), out var port) && port is >= 1024 and <= 65535
            ? Result.Ok(port)
            : Result.Fail("請輸入 1024–65535 之間的連接埠,或留空讓系統自動指派");
    }

    private static Result ValidateRange(string? text, int min, int max, string unit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Result.Fail($"請輸入 {min}–{max} {unit}");
        }

        return int.TryParse(text.Trim(), out var value) && value >= min && value <= max
            ? Result.Ok(value)
            : Result.Fail($"請輸入 {min}–{max} 之間的數字({unit})");
    }
}
