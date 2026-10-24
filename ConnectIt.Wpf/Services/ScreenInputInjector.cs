using System.Runtime.InteropServices;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 把觀看端送來的 screen-input 事件用 SendInput 注入成這台電腦的滑鼠/鍵盤操作(Windows 是分享端、
/// 而且使用者允許操控時才會被呼叫)。
///
/// 座標是 0~1 的相對位置,換算成 SendInput 的絕對座標(0~65535,不帶 MOUSEEVENTF_VIRTUALDESK
/// 時以主螢幕為範圍,跟 <see cref="ScreenCaptureService"/> 只擷取主螢幕一致)。
/// 記住目前被「按住」的按鍵/滑鼠鍵,工作階段結束時 <see cref="ReleaseAll"/> 一律放開,
/// 避免連線中斷時 Ctrl/Shift 或滑鼠左鍵卡在按下狀態。
///
/// 限制:Windows 的 UIPI 不允許一般權限的程式操作以系統管理員身分執行的視窗(例如工作管理員、UAC 提示)。
/// </summary>
public sealed class ScreenInputInjector
{
    private readonly HashSet<ushort> _pressedKeys = [];
    private readonly HashSet<int> _pressedButtons = [];
    private readonly object _sync = new();

    public void Handle(ScreenMessage message)
    {
        lock (_sync)
        {
            switch (message.Action)
            {
                case ScreenInputAction.PointerMove:
                    MoveTo(message.X, message.Y);
                    break;
                case ScreenInputAction.PointerDown:
                    MoveTo(message.X, message.Y);
                    SetButton(message.Button ?? 0, pressed: true);
                    break;
                case ScreenInputAction.PointerUp:
                    MoveTo(message.X, message.Y);
                    SetButton(message.Button ?? 0, pressed: false);
                    break;
                case ScreenInputAction.Wheel when message.DeltaY is { } delta:
                    MoveTo(message.X, message.Y);
                    SendMouse(MOUSEEVENTF_WHEEL, data: (int)Math.Round(Math.Clamp(delta, -10, 10) * WHEEL_DELTA));
                    break;
                case ScreenInputAction.KeyDown when MapKey(message.Key) is { } vk:
                    SendKey(vk, down: true);
                    break;
                case ScreenInputAction.KeyUp when MapKey(message.Key) is { } vk:
                    SendKey(vk, down: false);
                    break;
                case ScreenInputAction.Text when !string.IsNullOrEmpty(message.Text):
                    TypeText(message.Text);
                    break;
            }
        }
    }

    /// <summary>放開所有還被按住的按鍵與滑鼠鍵。</summary>
    public void ReleaseAll()
    {
        lock (_sync)
        {
            foreach (var vk in _pressedKeys.ToArray())
            {
                SendKey(vk, down: false);
            }

            foreach (var button in _pressedButtons.ToArray())
            {
                SetButton(button, pressed: false);
            }
        }
    }

    private void MoveTo(double? x, double? y)
    {
        if (x is null || y is null)
        {
            return;
        }

        var dx = (int)Math.Round(Math.Clamp(x.Value, 0, 1) * 65535);
        var dy = (int)Math.Round(Math.Clamp(y.Value, 0, 1) * 65535);
        SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, dx, dy);
    }

    private void SetButton(int button, bool pressed)
    {
        var flags = (button, pressed) switch
        {
            (0, true) => MOUSEEVENTF_LEFTDOWN,
            (0, false) => MOUSEEVENTF_LEFTUP,
            (1, true) => MOUSEEVENTF_RIGHTDOWN,
            (1, false) => MOUSEEVENTF_RIGHTUP,
            (2, true) => MOUSEEVENTF_MIDDLEDOWN,
            (2, false) => MOUSEEVENTF_MIDDLEUP,
            _ => 0u,
        };
        if (flags == 0)
        {
            return;
        }

        if (pressed)
        {
            _pressedButtons.Add(button);
        }
        else
        {
            _pressedButtons.Remove(button);
        }

        SendMouse(flags);
    }

    private void TypeText(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            if (ch == '\n' || ch == '\r')
            {
                if (ch == '\n')
                {
                    inputs.Add(KeyInput(VK_RETURN, 0, 0));
                    inputs.Add(KeyInput(VK_RETURN, 0, KEYEVENTF_KEYUP));
                }

                continue;
            }

            // KEYEVENTF_UNICODE:直接送出字元,不受目前鍵盤配置/輸入法影響,中文、emoji(代理對)都可以。
            inputs.Add(KeyInput(0, ch, KEYEVENTF_UNICODE));
            inputs.Add(KeyInput(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }

        if (inputs.Count > 0)
        {
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        }
    }

    private void SendKey(ushort vk, bool down)
    {
        if (down)
        {
            _pressedKeys.Add(vk);
        }
        else
        {
            _pressedKeys.Remove(vk);
        }

        var flags = (down ? 0u : KEYEVENTF_KEYUP) | (IsExtendedKey(vk) ? KEYEVENTF_EXTENDEDKEY : 0u);
        SendInput(1, [KeyInput(vk, 0, flags)], Marshal.SizeOf<INPUT>());
    }

    private static void SendMouse(uint flags, int dx = 0, int dy = 0, int data = 0)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = unchecked((uint)data), dwFlags = flags } },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyInput(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };

    /// <summary>網頁 KeyboardEvent.key 風格的按鍵名稱 → 虛擬鍵碼;不認得的回傳 null。</summary>
    public static ushort? MapKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (key.Length == 1)
        {
            var ch = char.ToUpperInvariant(key[0]);
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return ch;
            }

            return key[0] == ' ' ? (ushort)0x20 : null;
        }

        if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 12)
        {
            return (ushort)(0x70 + f - 1);
        }

        return key switch
        {
            "Enter" => VK_RETURN,
            "Backspace" => 0x08,
            "Tab" => 0x09,
            "Escape" => 0x1B,
            "Delete" => 0x2E,
            "Insert" => 0x2D,
            "Home" => 0x24,
            "End" => 0x23,
            "PageUp" => 0x21,
            "PageDown" => 0x22,
            "ArrowLeft" => 0x25,
            "ArrowUp" => 0x26,
            "ArrowRight" => 0x27,
            "ArrowDown" => 0x28,
            "Shift" => 0x10,
            "Control" => 0x11,
            "Alt" => 0x12,
            "Meta" => 0x5B,
            "CapsLock" => 0x14,
            "ContextMenu" => 0x5D,
            "PrintScreen" => 0x2C,
            RemoteKeys.VolumeMute => 0xAD,
            RemoteKeys.VolumeDown => 0xAE,
            RemoteKeys.VolumeUp => 0xAF,
            RemoteKeys.Next => 0xB0,
            RemoteKeys.Previous => 0xB1,
            RemoteKeys.Stop => 0xB2,
            RemoteKeys.PlayPause => 0xB3,
            _ => null,
        };
    }

    private static bool IsExtendedKey(ushort vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5D;

    // ===================== Win32 =====================

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_RETURN = 0x0D;
    private const int WHEEL_DELTA = 120;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
