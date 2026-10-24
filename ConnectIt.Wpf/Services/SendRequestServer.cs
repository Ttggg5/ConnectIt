using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 把「快速傳送」的路徑從新啟動的 ConnectIt 轉交給已經在執行的那一個(同一台電腦只允許一個實例,見 App.OnStartup)。
/// 新實例用具名管道送出一行 JSON 路徑陣列就結束;執行中的實例收集路徑,停頓 <see cref="BatchWindow"/> 沒有新的路徑
/// 才當成一批交給 <see cref="BatchReady"/>——檔案總管的右鍵選單對每個選取的檔案各啟動一次,要合併起來才不會跳出很多次選擇視窗。
/// </summary>
public sealed class SendRequestServer : IDisposable
{
    public static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(600);

    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly List<string> _pending = [];
    private Timer? _flushTimer;

    public SendRequestServer(string pipeName)
    {
        _pipeName = pipeName;
    }

    /// <summary>收集好一批要傳送的路徑(背景執行緒觸發)。空清單代表「開啟傳送」但還沒選檔案。</summary>
    public event EventHandler<IReadOnlyList<string>>? BatchReady;

    public void Start() => _ = ListenLoopAsync(_cts.Token);

    /// <summary>本機(第一個實例自己的命令列)也走同一套合併邏輯。</summary>
    public void Enqueue(IReadOnlyList<string> paths)
    {
        lock (_lock)
        {
            _pending.AddRange(paths);
            _flushTimer?.Dispose();
            _flushTimer = new Timer(_ => Flush(), null, BatchWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        List<string> batch;
        lock (_lock)
        {
            batch = _pending.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _pending.Clear();
            _flushTimer?.Dispose();
            _flushTimer = null;
        }

        BatchReady?.Invoke(this, batch);
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line != null && JsonSerializer.Deserialize<List<string>>(line) is { } paths)
                {
                    Enqueue(paths);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 單一連線出錯不影響之後的請求。
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>新實例呼叫:把路徑交給執行中的實例,成功回傳 true。</summary>
    public static bool TrySend(string pipeName, IReadOnlyList<string> paths, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.WriteLine(JsonSerializer.Serialize(paths));
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_lock)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
    }
}
