using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 螢幕鏡像的控制訊息(以 <see cref="ConnectionService"/> 的訊框協定、型別 2 傳送),對應 Android 端 ScreenMessages.kt。
///
/// 角色:「分享端」(sharer)是畫面被擷取、也可能被遠端操作的那一台;「觀看端」(viewer)是看畫面、送出操作事件的那一台。
/// 任一方都可以發起:
///   觀看端想看:viewer --screen-request--> sharer,sharer 的使用者同意後開始擷取並回 screen-info(等同接受),
///              不同意回 screen-reject。
///   分享端想分享:sharer --screen-offer--> viewer,viewer 回 screen-accept 後 sharer 開始擷取並送 screen-info;
///              不要的話回 screen-reject。
/// 進行中:sharer 送畫面訊框(型別 3:[4 bytes big-endian 序號][JPEG])給 viewer,viewer 每解完一張回 screen-ack,
/// sharer 最多只允許 <see cref="MaxFramesInFlight"/> 張還沒被確認,避免畫面在 TCP 緩衝區裡越積越多、延遲越來越大。
/// 畫面尺寸或是否允許操控改變時 sharer 會再送一次 screen-info。允許操控時 viewer 可以送 screen-input。
/// 任一方送 screen-stop 結束。同一時間只會有一個螢幕鏡像工作階段。
/// 聲音(選用):分享端在 screen-info 帶上 <see cref="AudioSampleRate"/>/<see cref="AudioChannels"/> 代表有分享聲音,
/// 之後送聲音訊框(型別 4:16-bit little-endian PCM,交錯排列);不帶(null)代表沒有分享聲音。
/// 聲音不受畫面的 ack 流量控制,沒聲音(全部是 0)的片段不送。
/// 舊版(不支援螢幕鏡像)收到不認得的訊框型別會直接忽略,所以發起端要自己逾時。
/// </summary>
public sealed class ScreenMessage
{
    public const string Request = "screen-request";
    public const string Offer = "screen-offer";
    public const string Accept = "screen-accept";
    public const string Reject = "screen-reject";
    public const string Info = "screen-info";
    public const string Ack = "screen-ack";
    public const string Input = "screen-input";
    public const string Stop = "screen-stop";

    // 遙控器(不用開螢幕鏡像,只送按鍵,見 RemoteControlController):控制端送 remote-request,受控端同意回
    // remote-accept(帶 Platform),不同意回 remote-reject;之後控制端用 screen-input(key-down/key-up,
    // SessionId 是遙控工作階段的)送按鍵,任一方送 remote-stop 結束。同一時間只有一個遙控工作階段。
    public const string RemoteRequest = "remote-request";
    public const string RemoteAccept = "remote-accept";
    public const string RemoteReject = "remote-reject";
    public const string RemoteStop = "remote-stop";

    /// <summary>同時允許幾張畫面還沒收到 screen-ack。</summary>
    public const int MaxFramesInFlight = 4;

    /// <summary>畫面訊框開頭的序號長度。</summary>
    public const int FrameHeaderSize = 4;

    /// <summary>不寫出值為 null 的欄位,訊息比較小(Android 端兩種寫法都能解析)。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public required string Type { get; init; }

    public string? SessionId { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>"android" | "windows",觀看端依此決定要顯示哪些按鈕。</summary>
    public string? Platform { get; init; }

    public bool? AllowControl { get; init; }

    public int? Seq { get; init; }

    /// <summary>screen-input 用,見 <see cref="ScreenInputAction"/>。</summary>
    public string? Action { get; init; }

    /// <summary>0~1 的相對座標(以畫面寬高為基準),兩端解析度不同也不用換算。</summary>
    public double? X { get; init; }

    public double? Y { get; init; }

    /// <summary>0 = 左鍵(觸控), 1 = 右鍵, 2 = 中鍵。</summary>
    public int? Button { get; init; }

    /// <summary>滾輪格數,正值 = 往上捲(對應 MouseWheelEventArgs.Delta &gt; 0),1.0 = 一格(120)。</summary>
    public double? DeltaY { get; init; }

    /// <summary>按鍵名稱,採用網頁 KeyboardEvent.key 的命名("Enter"、"Backspace"、"ArrowLeft"、"a"…);nav 動作則是 "back"/"home"/"recents"。</summary>
    public string? Key { get; init; }

    public string? Text { get; init; }

    /// <summary>screen-info:分享端有分享聲音時的取樣率(例如 48000),沒有分享聲音時為 null。</summary>
    public int? AudioSampleRate { get; init; }

    /// <summary>screen-info:聲音的聲道數(目前一律是 2,立體聲)。</summary>
    public int? AudioChannels { get; init; }

    /// <summary>screen-reject 的原因,見 <see cref="ScreenRejectReason"/>。</summary>
    public string? Reason { get; init; }

    public byte[] ToUtf8Json() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    public static ScreenMessage? Parse(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonSerializer.Deserialize<ScreenMessage>(utf8Json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public static class ScreenInputAction
{
    public const string PointerDown = "pointer-down";
    public const string PointerMove = "pointer-move";
    public const string PointerUp = "pointer-up";
    public const string Wheel = "wheel";
    public const string KeyDown = "key-down";
    public const string KeyUp = "key-up";
    public const string Text = "text";
    public const string Nav = "nav";
}

/// <summary>
/// 遙控器與螢幕鏡像快捷鍵列用到的按鍵名稱(網頁 KeyboardEvent.key 的標準命名),對應 Android 端 RemoteKeys。
/// 媒體鍵與音量鍵在 Android 受控端不需要無障礙服務(用 AudioManager 送出)。
/// </summary>
public static class RemoteKeys
{
    public const string PlayPause = "MediaPlayPause";
    public const string Next = "MediaTrackNext";
    public const string Previous = "MediaTrackPrevious";
    public const string Stop = "MediaStop";
    public const string VolumeUp = "AudioVolumeUp";
    public const string VolumeDown = "AudioVolumeDown";
    public const string VolumeMute = "AudioVolumeMute";

    public static bool IsMediaKey(string? key) =>
        key is PlayPause or Next or Previous or Stop or VolumeUp or VolumeDown or VolumeMute;
}

public static class ScreenRejectReason
{
    /// <summary>使用者按了拒絕。</summary>
    public const string Declined = "declined";

    /// <summary>已經有進行中的螢幕鏡像。</summary>
    public const string Busy = "busy";

    /// <summary>這台裝置不支援擷取螢幕。</summary>
    public const string Unsupported = "unsupported";

    /// <summary>使用者在系統的擷取確認畫面按了取消(Android)。</summary>
    public const string PermissionDenied = "permission-denied";
}
