using ConnectIt.Wpf.Services;
using MaterialDesignThemes.Wpf;

namespace ConnectIt.Wpf.Models;

/// <summary>遙控器/螢幕鏡像快捷鍵列上的一顆按鈕:依序按下 <see cref="Keys"/>,再反過來放開。</summary>
public sealed record RemoteShortcut(string Label, PackIconKind Icon, string[] Keys);

/// <summary>
/// 遙控器視窗與螢幕鏡像觀看視窗共用的按鈕清單。媒體鍵兩種受控端都能用;簡報與系統快捷鍵只有 Windows 受控端能用
/// (Android 不允許 App 注入任意按鍵)。
/// </summary>
public static class RemoteShortcuts
{
    public static readonly RemoteShortcut[] Media =
    [
        new("上一首", PackIconKind.SkipPrevious, [RemoteKeys.Previous]),
        new("播放/暫停", PackIconKind.PlayPause, [RemoteKeys.PlayPause]),
        new("下一首", PackIconKind.SkipNext, [RemoteKeys.Next]),
        new("停止", PackIconKind.Stop, [RemoteKeys.Stop]),
        new("音量−", PackIconKind.VolumeMinus, [RemoteKeys.VolumeDown]),
        new("靜音", PackIconKind.VolumeMute, [RemoteKeys.VolumeMute]),
        new("音量+", PackIconKind.VolumePlus, [RemoteKeys.VolumeUp]),
    ];

    public static readonly RemoteShortcut[] Presentation =
    [
        new("上一頁", PackIconKind.ChevronLeft, ["ArrowLeft"]),
        new("下一頁", PackIconKind.ChevronRight, ["ArrowRight"]),
        new("開始投影片(F5)", PackIconKind.Presentation, ["F5"]),
        new("黑畫面(B)", PackIconKind.MonitorOff, ["b"]),
        new("結束(Esc)", PackIconKind.ExitToApp, ["Escape"]),
    ];

    public static readonly RemoteShortcut[] System =
    [
        new("複製(Ctrl+C)", PackIconKind.ContentCopy, ["Control", "c"]),
        new("貼上(Ctrl+V)", PackIconKind.ContentPaste, ["Control", "v"]),
        new("復原(Ctrl+Z)", PackIconKind.Undo, ["Control", "z"]),
        new("切換視窗(Alt+Tab)", PackIconKind.SwapHorizontal, ["Alt", "Tab"]),
        new("開始(Win)", PackIconKind.MicrosoftWindows, ["Meta"]),
        new("顯示桌面(Win+D)", PackIconKind.MonitorDashboard, ["Meta", "d"]),
        new("關閉視窗(Alt+F4)", PackIconKind.WindowClose, ["Alt", "F4"]),
    ];
}
