using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 「快速傳送」的檔案總管整合(使用者層級,不需要系統管理員權限):
/// <list type="bullet">
/// <item>右鍵選單「用 ConnectIt 傳送」:HKCU\Software\Classes\*\shell 與 Directory\shell 底下的靜態動詞
/// (Windows 11 在「顯示更多選項」裡)。一次選多個檔案時檔案總管會對每個檔案各啟動一次,
/// 由已經在執行的 ConnectIt 把短時間內收到的路徑合併成一批(見 <see cref="SendRequestServer"/>)。</item>
/// <item>「傳送到」選單的捷徑(%AppData%\Microsoft\Windows\SendTo\ConnectIt.lnk),一次就會帶上所有選取的檔案。</item>
/// </list>
/// 兩者都執行 <c>ConnectIt.Wpf.exe --send 路徑…</c>。程式資料夾可能因為自我更新或搬移而改變,
/// 所以開啟時(<see cref="RefreshIfEnabled"/>)會用目前的 exe 路徑重寫一次。
/// </summary>
public sealed class ShellIntegrationService
{
    public const string SendArg = "--send";

    private const string VerbName = "ConnectIt.Send";
    private static readonly string[] VerbParents = [@"Software\Classes\*\shell", @"Software\Classes\Directory\shell"];

    private readonly string _settingsFilePath;
    private readonly string _registryRoot;
    private readonly string _sendToFolder;

    public ShellIntegrationService()
        : this(
            Path.Combine(AppContext.BaseDirectory, "shell-integration.json"),
            registryRoot: string.Empty,
            Environment.GetFolderPath(Environment.SpecialFolder.SendTo))
    {
    }

    /// <summary>
    /// 測試用:<paramref name="registryRoot"/> 會加在每個登錄機碼前面(例如 <c>Software\ConnectItTests\xxx\</c>),
    /// 避免測試動到真正的右鍵選單;<paramref name="sendToFolder"/> 是放捷徑的資料夾。
    /// </summary>
    public ShellIntegrationService(string settingsFilePath, string registryRoot, string sendToFolder)
    {
        _settingsFilePath = settingsFilePath;
        _registryRoot = registryRoot;
        _sendToFolder = sendToFolder;
        Enabled = LoadEnabled();
    }

    public bool Enabled { get; private set; }

    public string SendToShortcutPath => Path.Combine(_sendToFolder, "ConnectIt.lnk");

    public void SetEnabled(bool enabled, string exePath)
    {
        Enabled = enabled;
        SaveEnabled(enabled);
        if (enabled)
        {
            Register(exePath);
        }
        else
        {
            Unregister();
        }
    }

    /// <summary>啟動時呼叫:開著的話用目前的 exe 路徑重寫一次(程式資料夾可能已經換了)。</summary>
    public void RefreshIfEnabled(string exePath)
    {
        if (Enabled)
        {
            Register(exePath);
        }
    }

    private void Register(string exePath)
    {
        foreach (var parent in VerbParents)
        {
            try
            {
                using var verb = Registry.CurrentUser.CreateSubKey($@"{_registryRoot}{parent}\{VerbName}", writable: true);
                verb.SetValue("MUIVerb", "用 ConnectIt 傳送");
                verb.SetValue("Icon", $"\"{exePath}\",0");
                verb.SetValue("MultiSelectModel", "Player");
                using var command = verb.CreateSubKey("command", writable: true);
                command.SetValue(string.Empty, $"\"{exePath}\" {SendArg} \"%1\"");
            }
            catch
            {
                // 寫不進登錄檔(權限被鎖)就只剩「傳送到」可以用。
            }
        }

        CreateSendToShortcut(exePath);
    }

    private void Unregister()
    {
        foreach (var parent in VerbParents)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"{_registryRoot}{parent}\{VerbName}", throwOnMissingSubKey: false);
            }
            catch
            {
                // 刪不掉就留著,點下去也只是開啟 ConnectIt。
            }
        }

        try
        {
            File.Delete(SendToShortcutPath);
        }
        catch
        {
            // 同上。
        }
    }

    /// <summary>用 WScript.Shell 建立 .lnk(.NET 沒有內建建立捷徑的 API)。</summary>
    private void CreateSendToShortcut(string exePath)
    {
        try
        {
            Directory.CreateDirectory(_sendToFolder);
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(SendToShortcutPath);
            shortcut.TargetPath = exePath;
            shortcut.Arguments = SendArg;
            shortcut.IconLocation = $"{exePath},0";
            shortcut.Description = "用 ConnectIt 傳送到其他裝置";
            shortcut.Save();
        }
        catch
        {
            // 建不了捷徑就只剩右鍵選單可以用。
        }
    }

    /// <summary>登錄檔裡目前寫的指令(<paramref name="parentIndex"/>:0 = 檔案、1 = 資料夾),沒有註冊時是 null。</summary>
    public string? ReadCommand(int parentIndex) =>
        Registry.CurrentUser.OpenSubKey($@"{_registryRoot}{VerbParents[parentIndex]}\{VerbName}\command")?.GetValue(string.Empty) as string;

    /// <summary>
    /// 從命令列參數取出要傳送的路徑:<c>--send</c> 後面的參數都是路徑(「傳送到」會一次帶上所有選取的項目)。
    /// 沒有 <c>--send</c> 回傳 null;只有 <c>--send</c> 沒有路徑回傳空清單(例如從系統匣選單手動開啟)。
    /// </summary>
    public static IReadOnlyList<string>? ParseSendArgs(IReadOnlyList<string> args)
    {
        var index = -1;
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], SendArg, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return null;
        }

        return args.Skip(index + 1).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim('"')).ToList();
    }

    private bool LoadEnabled()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                return JsonSerializer.Deserialize<ShellIntegrationData>(File.ReadAllText(_settingsFilePath))?.Enabled == true;
            }
        }
        catch
        {
            // 設定檔損毀就當作沒開。
        }

        return false;
    }

    private void SaveEnabled(bool enabled)
    {
        try
        {
            File.WriteAllText(_settingsFilePath, JsonSerializer.Serialize(new ShellIntegrationData { Enabled = enabled }));
        }
        catch
        {
            // 存不起來只是下次啟動會退回沒開。
        }
    }

    private sealed class ShellIntegrationData
    {
        public bool Enabled { get; set; }
    }
}
