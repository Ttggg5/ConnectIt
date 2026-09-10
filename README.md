# ConnectIt

一個 WPF 桌面範例應用程式,示範如何在區域網路內用標準 **mDNS/DNS-SD**(RFC 6762/6763)自動探索裝置,並透過 TCP 交握流程(送出連線請求 → 對方確認接受/拒絕)建立一對一連線。

因為探索與交握都採用標準協定,理論上可以與其他平台(例如 Android 端用 `NsdManager` 註冊相同的 service type)互相探索、互相連線。

## 功能特色

- **自動廣播與探索**:啟動後自動用 mDNS 廣播自己,並持續搜尋區網內其他執行中的裝置。
- **連線確認交握**:發起連線的一方送出請求後,需等待對方在畫面上按下「接受」或「拒絕」才會建立連線。
- **檔案傳輸**:連線後可以按下「傳送檔案」或直接把檔案拖曳到視窗來傳送,對方會跳出確認對話框,接受後會顯示進度並可隨時按下取消。接收的檔案預設存到 `Downloads` 資料夾,可以在設定頁更改。
- **斷線偵測**:任一方關閉連線都會通知另一方,並自動返回裝置搜尋畫面。
- **Material Design UI**:使用 [MaterialDesignThemes](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit) 打造的介面,含裝置清單、連線確認對話框、狀態日誌。
- **導覽列與設定頁**:左側導覽列可在「裝置」與「設定」頁面間切換;設定頁可選擇淺色、深色或跟隨系統的顏色主題,選擇會儲存在本機,下次啟動時套用。

## 專案結構

| 專案 | 說明 |
| --- | --- |
| `ConnectIt.Wpf` | 主要的 WPF 應用程式 |
| `ConnectIt.Wpf/Services/MdnsDiscoveryService.cs` | 負責 mDNS 裝置廣播與探索 |
| `ConnectIt.Wpf/Services/ConnectionService.cs` | 負責 TCP 連線交握(請求/接受/拒絕)、斷線偵測,以及連線建立後的檔案傳輸訊框協定 |
| `ConnectIt.Wpf/Services/ThemeService.cs` | 負責套用與儲存淺色/深色/跟隨系統的顏色主題 |
| `ConnectIt.Wpf/Services/FileTransferSettingsService.cs` | 負責儲存接收檔案要存放的資料夾設定 |
| `ConnectIt.Wpf/Models/DiscoveredDevice.cs` | 探索到的裝置資料模型 |
| `ConnectIt.Tests` | 使用 xUnit 撰寫的單元測試,涵蓋連線交握流程、裝置模型與檔案傳輸 |

## 需求環境

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/) (`net10.0-windows`)

## 建置與執行

```powershell
# 還原套件並建置
dotnet build

# 執行應用程式
dotnet run --project ConnectIt.Wpf
```

啟動兩個(或以上)執行個體(同一台機器上開兩個視窗,或在同一區網的兩台機器上各執行一份),即可在裝置清單中看到彼此,點選後送出連線請求。

## 執行測試

```powershell
dotnet test
```

## 運作原理簡述

1. 應用程式啟動時,`ConnectionService` 開始監聽一個 TCP 連接埠,`MdnsDiscoveryService` 將此連接埠與裝置名稱透過 mDNS 廣播出去,同時開始搜尋網路上其他裝置。
2. 使用者在裝置清單中點選欲連線的裝置後,發起端會建立 TCP 連線並送出 `request` 訊息。
3. 被連線的一方跳出確認對話框,使用者按下「接受」或「拒絕」後回傳 `accept`/`reject` 訊息。
4. 若接受,雙方各自進入已連線畫面,並開始監控連線狀態;任一方關閉連線都會通知另一方並返回裝置搜尋畫面。
5. 連線建立後,同一個 TCP socket 改用一個簡單的長度前綴訊框協定傳輸資料(控制訊息或檔案內容區塊),檔案傳輸就是在這個協定上跑的一套 `file-offer` → `file-accept`/`file-reject` → 分塊傳送 → `file-complete` 交握,任一方也可以隨時送出 `file-cancel` 取消。
