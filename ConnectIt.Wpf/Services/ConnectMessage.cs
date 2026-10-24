namespace ConnectIt.Wpf.Services;

/// <summary>連線交握用的簡單訊息(以換行分隔的 JSON 傳送)。</summary>
public sealed class ConnectMessage
{
    public required string Type { get; init; } // "request" | "accept" | "reject"

    public string? DeviceName { get; init; }

    /// <summary>送出方的穩定裝置識別碼(request 與 accept 都會帶),同一台裝置重新連線時用來取代舊連線。</summary>
    public string? DeviceId { get; init; }

    /// <summary>協定版本(見 <see cref="ConnectionService.ProtocolVersion"/>);舊版沒有這個欄位。</summary>
    public int? ProtocolVersion { get; init; }

    /// <summary>reject 的原因:舊版發起端被拒絕時是 <see cref="ConnectionService.UpgradeRequiredReason"/>。</summary>
    public string? Reason { get; init; }
}
