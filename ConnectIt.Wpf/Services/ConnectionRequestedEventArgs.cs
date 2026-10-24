using System.Net;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 有裝置想要連進來時觸發。UI 顯示確認對話框後,呼叫 <see cref="Respond"/> 回覆是否接受。
/// </summary>
public sealed class ConnectionRequestedEventArgs : EventArgs
{
    public required string RequesterName { get; init; }

    public required IPAddress RequesterAddress { get; init; }

    /// <summary>對方的穩定裝置識別碼(舊版沒有時為 null)。</summary>
    public string? RequesterDeviceId { get; init; }

    /// <summary>對方 TLS 憑證的指紋,信任清單靠這個比對(不比對名稱,名稱可以被冒用)。</summary>
    public required string RequesterFingerprint { get; init; }

    /// <summary>這次連線的 6 位數配對碼,兩邊畫面都會顯示,讓使用者核對。</summary>
    public required string PairingCode { get; init; }

    public required Action<bool> Respond { get; init; }
}

public enum ConnectionRole
{
    Initiator,
    Acceptor,
}

/// <summary>雙方交握完成、正式建立連線時觸發。</summary>
public sealed class ConnectedEventArgs : EventArgs
{
    public required PeerSession Session { get; init; }

    public required string RemoteName { get; init; }

    public required IPAddress RemoteAddress { get; init; }

    public required ConnectionRole Role { get; init; }
}

/// <summary>主動送出連線請求的結果,UI 依此顯示對應的說明(而不是一律「對方未接受」)。</summary>
public enum ConnectionRequestOutcome
{
    /// <summary>對方按下接受,連線已建立。</summary>
    Accepted,

    /// <summary>對方按下拒絕(或直接關閉了連線)。</summary>
    Rejected,

    /// <summary>已送出請求,但對方在時限內沒有回應。</summary>
    Timeout,

    /// <summary>根本連不上對方(離線、位址不可達、被防火牆擋下)。</summary>
    Unreachable,

    /// <summary>使用者自己取消了等待。</summary>
    Cancelled,

    /// <summary>連得上但建立不了加密連線,通常是對方使用不支援加密的舊版 ConnectIt。</summary>
    IncompatibleVersion,
}
