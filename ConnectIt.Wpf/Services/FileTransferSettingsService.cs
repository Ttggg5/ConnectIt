using System.IO;
using System.Text.Json;

namespace ConnectIt.Wpf.Services;

/// <summary>管理檔案傳輸的設定(目前只有接收檔案要儲存的資料夾),並把設定儲存在本機供下次啟動使用。</summary>
public sealed class FileTransferSettingsService
{
    private readonly string _settingsFilePath;

    public string DownloadFolder { get; private set; }

    public FileTransferSettingsService()
    {
        _settingsFilePath = Path.Combine(AppContext.BaseDirectory, "filetransfer-settings.json");
        DownloadFolder = LoadSavedFolder();
    }

    /// <summary>變更接收檔案要儲存的資料夾,並儲存供下次啟動使用。</summary>
    public void SetDownloadFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder == DownloadFolder)
        {
            return;
        }

        DownloadFolder = folder;
        SaveFolder(folder);
    }

    private static string GetDefaultDownloadFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private string LoadSavedFolder()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var data = JsonSerializer.Deserialize<FileTransferSettingsData>(json);
                if (!string.IsNullOrWhiteSpace(data?.DownloadFolder))
                {
                    return data.DownloadFolder;
                }
            }
        }
        catch
        {
            // 設定檔損毀或無法讀取時,退回預設值。
        }

        return GetDefaultDownloadFolder();
    }

    private void SaveFolder(string folder)
    {
        try
        {
            var json = JsonSerializer.Serialize(new FileTransferSettingsData { DownloadFolder = folder });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // 儲存失敗不影響這次的變更,只是下次啟動會退回預設值。
        }
    }

    private sealed class FileTransferSettingsData
    {
        public string DownloadFolder { get; set; } = string.Empty;
    }
}
