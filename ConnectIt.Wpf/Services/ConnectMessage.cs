namespace ConnectIt.Wpf.Services;

/// <summary>連線交握用的簡單訊息(以換行分隔的 JSON 傳送)。</summary>
public sealed class ConnectMessage
{
    public required string Type { get; init; } // "request" | "accept" | "reject"

    public string? DeviceName { get; init; }
}
