using System.ComponentModel;
using System.Runtime.CompilerServices;
using ConnectIt.Wpf.Services;

namespace ConnectIt.Wpf.Models;

/// <summary>
/// 「目前連線」面板裡的一台已連線裝置。可以同時連線多台,面板一次顯示其中一台(選取中的那台),
/// 每台各自記住自己的傳輸狀態(傳輸卡片的內容),切換時直接換成那台的狀態。
/// 頂端的裝置切換列繫結 <see cref="Name"/> 與 <see cref="ChipStatus"/>。
/// </summary>
public sealed class ConnectedPeerItem(PeerSession session, string address) : INotifyPropertyChanged
{
    private string? _chipStatus;

    public PeerSession Session { get; } = session;

    public string PeerId => Session.PeerId;

    public string Name => Session.RemoteName;

    public string Address { get; } = address;

    /// <summary>裝置切換列上名稱下方的小字(例如「傳送中 42%」),沒有在傳輸時為 null。</summary>
    public string? ChipStatus
    {
        get => _chipStatus;
        set
        {
            if (_chipStatus != value)
            {
                _chipStatus = value;
                OnPropertyChanged();
            }
        }
    }

    // ---- 傳輸卡片的狀態(只有選取中的裝置會實際顯示) ----

    /// <summary>傳輸卡片是否顯示。</summary>
    public bool TransferVisible { get; set; }

    /// <summary>true = 傳輸中(顯示進度條與取消鈕);false = 已完成接收(顯示開啟按鈕)。</summary>
    public bool TransferInProgress { get; set; }

    public string TransferName { get; set; } = string.Empty;

    public string TransferStatus { get; set; } = string.Empty;

    /// <summary>0–100。</summary>
    public double TransferPercent { get; set; }

    public string TransferPercentText { get; set; } = string.Empty;

    /// <summary>最近一次「接收成功」的紀錄;傳輸卡片在完成狀態時的「開啟」就是作用在它上面。</summary>
    public TransferRecord? LastReceivedRecord { get; set; }

    public TransferRateTracker Rate { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
