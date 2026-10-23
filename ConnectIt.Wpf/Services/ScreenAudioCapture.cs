using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ConnectIt.Wpf.Services;

/// <summary>
/// 螢幕鏡像分享端的聲音:用 WASAPI loopback 擷取「這台電腦正在播放的所有聲音」(預設輸出裝置的混音),
/// 轉成 16-bit 立體聲 PCM(取樣率維持裝置原本的,通常 48000)透過 <see cref="DataAvailable"/> 交給呼叫端送出。
///
/// 裝置的混音格式通常是 32-bit float、也可能是 5.1/7.1 多聲道,一律只取前兩個聲道(單聲道就左右複製)。
/// 沒在播任何聲音時 WASAPI 不會送資料(或送全 0),全 0 的片段直接略過,不佔頻寬。
/// </summary>
public sealed class ScreenAudioCapture : IDisposable
{
    public const int OutputChannels = 2;

    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT:WASAPI 混音格式(WAVE_FORMAT_EXTENSIBLE)裡代表 32-bit float 的子格式。</summary>
    private static readonly Guid KsDataFormatIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly WasapiLoopbackCapture _capture;
    private byte[] _converted = [];

    /// <summary>擷取到一段聲音(16-bit 立體聲 PCM)。陣列是共用緩衝區,事件處理完就不能再用,需要保留請自己複製。</summary>
    public event Action<byte[], int>? DataAvailable;

    public int SampleRate => _capture.WaveFormat.SampleRate;

    /// <summary>預設輸出裝置不存在(例如沒有任何音效裝置)時丟出例外。</summary>
    public ScreenAudioCapture()
    {
        _capture = new WasapiLoopbackCapture();
        _capture.DataAvailable += OnDataAvailable;
    }

    public void Start() => _capture.StartRecording();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        var length = ConvertToPcm16Stereo(e.Buffer, e.BytesRecorded, _capture.WaveFormat, ref _converted);
        if (length > 0 && !IsSilent(_converted.AsSpan(0, length)))
        {
            DataAvailable?.Invoke(_converted, length);
        }
    }

    /// <summary>把 WASAPI 混音格式(float32 / PCM16 / PCM24 / PCM32,任意聲道數)轉成 16-bit 立體聲,回傳有效長度。</summary>
    public static int ConvertToPcm16Stereo(byte[] source, int byteCount, WaveFormat format, ref byte[] destination)
    {
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0)
        {
            return 0;
        }

        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible extensible && extensible.SubFormat == KsDataFormatIeeeFloat);
        var frames = byteCount / (bytesPerSample * channels);
        var needed = frames * OutputChannels * 2;
        if (destination.Length < needed)
        {
            destination = new byte[needed];
        }

        var input = source.AsSpan(0, byteCount);
        var output = destination.AsSpan();
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * channels * bytesPerSample;
            var left = ReadSample(input, offset, bytesPerSample, isFloat);
            var right = channels > 1 ? ReadSample(input, offset + bytesPerSample, bytesPerSample, isFloat) : left;
            BinaryPrimitives.WriteInt16LittleEndian(output[(frame * 4)..], left);
            BinaryPrimitives.WriteInt16LittleEndian(output[(frame * 4 + 2)..], right);
        }

        return needed;
    }

    private static short ReadSample(ReadOnlySpan<byte> data, int offset, int bytesPerSample, bool isFloat)
    {
        if (isFloat && bytesPerSample == 4)
        {
            var value = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
            return (short)Math.Clamp((int)(value * short.MaxValue), short.MinValue, short.MaxValue);
        }

        return bytesPerSample switch
        {
            2 => BinaryPrimitives.ReadInt16LittleEndian(data[offset..]),
            3 => (short)((data[offset + 2] << 8) | data[offset + 1]), // 24-bit:取高 16 位元。
            4 => (short)(BinaryPrimitives.ReadInt32LittleEndian(data[offset..]) >> 16),
            _ => 0,
        };
    }

    public static bool IsSilent(ReadOnlySpan<byte> pcm) => pcm.IndexOfAnyExcept((byte)0) < 0;

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        try
        {
            _capture.StopRecording();
        }
        catch
        {
            // 已經停止或裝置已移除。
        }

        _capture.Dispose();
    }
}

/// <summary>
/// 螢幕鏡像觀看端播放對方的聲音(16-bit PCM)。用 WASAPI 共用模式、約 60ms 的輸出延遲播放,
/// 前面放一個小緩衝吸收網路抖動;緩衝累積超過 <see cref="MaxBufferedMs"/>(兩台裝置的時脈有微小差異,
/// 或網路卡了一下之後一次湧入)就清掉重來,聲音不會越拖越慢、跟畫面越差越多。
///
/// 靜音只調整這個串流自己的音量(<see cref="VolumeWaveProvider16"/>),絕對不能用 <c>WasapiOut.Volume</c>:
/// 那個屬性實際上設定的是整個喇叭裝置的系統主音量(AudioEndpointVolume),會把使用者電腦的音量拉到 100% 或 0%。
/// </summary>
public sealed class ScreenAudioPlayer : IDisposable
{
    private const int MaxBufferedMs = 200;

    private readonly BufferedWaveProvider _buffer;
    private readonly VolumeWaveProvider16 _volume;
    private readonly WasapiOut _output;

    public int SampleRate { get; }

    public int Channels { get; }

    public ScreenAudioPlayer(int sampleRate, int channels)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, channels))
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = true, // 沒有資料時輸出靜音,不要讓播放停掉。
        };
        _volume = new VolumeWaveProvider16(_buffer);
        _output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 60);
        _output.Init(_volume);
        _output.Play();
    }

    public bool Muted
    {
        get => _volume.Volume == 0;
        set => _volume.Volume = value ? 0f : 1f;
    }

    public void Add(byte[] pcm)
    {
        if (_buffer.BufferedDuration.TotalMilliseconds > MaxBufferedMs)
        {
            _buffer.ClearBuffer();
        }

        _buffer.AddSamples(pcm, 0, pcm.Length);
    }

    public void Dispose()
    {
        try
        {
            _output.Stop();
        }
        catch
        {
            // 裝置已移除。
        }

        _output.Dispose();
    }
}
