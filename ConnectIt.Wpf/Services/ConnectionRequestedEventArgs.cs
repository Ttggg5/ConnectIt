using System.Net;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 有裝置想要連進來時觸發。UI 顯示確認對話框後,呼叫 <see cref="Respond"/> 回覆是否接受。
/// </summary>
public sealed class ConnectionRequestedEventArgs : EventArgs
{
    public required string RequesterName { get; init; }

    public required IPAddress RequesterAddress { get; init; }

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
    public required string RemoteName { get; init; }

    public required IPAddress RemoteAddress { get; init; }

    public required ConnectionRole Role { get; init; }
}
