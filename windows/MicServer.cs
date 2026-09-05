using System.Net;
using System.Net.Sockets;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace myXmic;

/// <summary>
/// TCP 接收手机 PCM(48k/16/mono) → WASAPI → CABLE Input。
/// 事件全部抛给 UI 层。
/// </summary>
public class MicServer : IDisposable
{
    public event Action<string>? OnLog;
    public event Action<string>? OnClientChanged;   // 连接到设备的描述 / "已断开"
    public event Action<float>? OnLevel;            // 0..1 音量电平

    private const int SampleRate = 48000;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private MMDevice? _renderDevice;
    private WasapiOut? _player;
    private BufferedWaveProvider? _provider;

    public MMDevice? FindCable()
    {
        var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                 .FirstOrDefault(d => d.FriendlyName.Contains("CABLE Input",
                     StringComparison.OrdinalIgnoreCase));
    }

    public bool Start(int port)
    {
        _cts = new CancellationTokenSource();

        _renderDevice = FindCable();
        if (_renderDevice == null)
        {
            OnLog?.Invoke("未找到 CABLE Input，请先安装 VB-CABLE");
            return false;
        }

        var format = new WaveFormat(SampleRate, 16, 1);
        _provider = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromMilliseconds(300),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        _player = new WasapiOut(_renderDevice, AudioClientShareMode.Shared, false, 30);
        _player.Init(_provider);
        _player.Play();

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();

        _ = Task.Run(() => AcceptLoop(_cts.Token));
        return true;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => HandleClient(client, ct), ct);
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        var ep = client.Client.RemoteEndPoint?.ToString() ?? "?";
        OnClientChanged?.Invoke($"已连接 {ep}");
        try
        {
            client.NoDelay = true;
            using var ns = client.GetStream();
            var buf = new byte[8192];
            int n;
            while (!ct.IsCancellationRequested && (n = await ns.ReadAsync(buf, ct)) > 0)
            {
                _provider!.AddSamples(buf, 0, n);
                if (_provider.BufferedDuration.TotalMilliseconds > 250)
                    _provider.ClearBuffer();
                OnLevel?.Invoke(CalcLevel(buf, n));
            }
        }
        catch { }
        OnClientChanged?.Invoke("未连接");
        OnLevel?.Invoke(0);
        client.Dispose();
    }

    private static float CalcLevel(byte[] buf, int len)
    {
        // PCM16 峰值 / 32768
        short max = 0;
        for (int i = 0; i + 1 < len; i += 2)
        {
            var v = Math.Abs(BitConverter.ToInt16(buf, i));
            if (v > max) max = (short)v;
        }
        return max / 32768f;
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _player?.Stop(); } catch { }
        _player?.Dispose(); _player = null;
        _renderDevice?.Dispose(); _renderDevice = null;
        OnLevel?.Invoke(0);
    }

    public void Dispose() => Stop();
}
