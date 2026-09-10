using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Sockets;
using System.Windows;
using ConnectIt.Wpf.Models;
using ConnectIt.Wpf.Services;

namespace ConnectIt.Wpf;

public partial class MainWindow : Window
{
    private readonly MdnsDiscoveryService _discovery = new();
    private readonly ConnectionService _connection = new();
    private readonly ObservableCollection<DiscoveredDevice> _devices = new();

    private bool _isAdvertising;

    public MainWindow()
    {
        InitializeComponent();

        DevicesListView.ItemsSource = _devices;
        DeviceNameTextBox.Text = Environment.MachineName;

        _discovery.DeviceDiscovered += OnDeviceDiscovered;
        _discovery.DeviceRemoved += OnDeviceRemoved;
        _discovery.StatusChanged += OnServiceStatusChanged;
        _connection.StatusChanged += OnServiceStatusChanged;
        _connection.ClientConnected += OnClientConnected;

        Loaded += (_, _) =>
        {
            _discovery.Start();
            _discovery.Refresh();
        };

        Closed += (_, _) =>
        {
            _discovery.Dispose();
            _connection.Dispose();
        };
    }

    private void ToggleAdvertiseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isAdvertising)
        {
            var deviceName = string.IsNullOrWhiteSpace(DeviceNameTextBox.Text)
                ? Environment.MachineName
                : DeviceNameTextBox.Text.Trim();

            var port = _connection.StartListening();
            _discovery.Advertise(deviceName, port);

            _isAdvertising = true;
            ToggleAdvertiseButton.Content = "停止廣播";
            AdvertiseStatusText.Text = $"廣播中,連接埠 {port}";
            DeviceNameTextBox.IsEnabled = false;
        }
        else
        {
            _discovery.StopAdvertising();
            _connection.StopListening();

            _isAdvertising = false;
            ToggleAdvertiseButton.Content = "開始廣播並監聽";
            AdvertiseStatusText.Text = string.Empty;
            DeviceNameTextBox.IsEnabled = true;
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _devices.Clear();
        _discovery.Refresh();
    }

    private void DevicesListView_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ConnectButton.IsEnabled = DevicesListView.SelectedItem is DiscoveredDevice;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesListView.SelectedItem is not DiscoveredDevice device)
        {
            return;
        }

        ConnectButton.IsEnabled = false;
        try
        {
            AppendLog($"正在連線到 {device.DisplayName} ({device.Address}:{device.Port}) ...");
            using var client = await _connection.ConnectAsync(device.Address, device.Port);
            AppendLog($"已成功連線到 {device.DisplayName}。");
            MessageBox.Show(this, $"已成功連線到 {device.DisplayName}\n({device.Address}:{device.Port})",
                "連線成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (SocketException ex)
        {
            AppendLog($"連線失敗:{ex.Message}");
            MessageBox.Show(this, $"連線失敗:{ex.Message}", "連線錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ConnectButton.IsEnabled = DevicesListView.SelectedItem is DiscoveredDevice;
        }
    }

    private void OnDeviceDiscovered(object? sender, DiscoveredDevice device)
    {
        Dispatcher.Invoke(() =>
        {
            var existing = _devices.FirstOrDefault(d => d.Key == device.Key);
            if (existing != null)
            {
                var index = _devices.IndexOf(existing);
                _devices[index] = device;
            }
            else
            {
                _devices.Add(device);
                AppendLog($"找到裝置:{device.DisplayName} ({device.Address}:{device.Port})");
            }
        });
    }

    private void OnDeviceRemoved(object? sender, string instanceName)
    {
        Dispatcher.Invoke(() =>
        {
            var existing = _devices.FirstOrDefault(d => d.Key == instanceName);
            if (existing != null)
            {
                _devices.Remove(existing);
                AppendLog($"裝置已離線:{existing.DisplayName}");
            }
        });
    }

    private void OnClientConnected(object? sender, TcpClient client)
    {
        Dispatcher.Invoke(() => AppendLog($"接受來自 {client.Client.RemoteEndPoint} 的連入連線。"));
    }

    private void OnServiceStatusChanged(object? sender, string message)
    {
        Dispatcher.Invoke(() => AppendLog(message));
    }

    private void AppendLog(string message)
    {
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
    }
}
