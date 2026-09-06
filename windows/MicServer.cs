using System.Net;
using System.Net.Sockets;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace myXmic;

public enum Transport { Tcp, Udp, Bluetooth }

/// <summary>
/// 只启动用户选定的一种通道接收 48kHz(或16kHz)/16bit/mono PCM → WASAPI → CABLE Input。
/// 声道切换时 Android 端会以数据帧头协商，这里按首包自适配采样率。
/// </summary>
public class MicServer : IDisposable
{
    public event Action<string>? OnLog;
    public event Action<string>? OnClientChanged;
    public event Action<float>? OnLevel;

    private CancellationTokenSource? _cts;
    private MMDevice? _renderDevice;
    private WasapiOut? _player;
    private BufferedWaveProvider? _provider;
    private Task? _listenerTask;
    private volatile float _gain = 1.0f;
    private volatile int _sampleRate = 48000;

    /// <summary>音量增益 0.5 ~ 3.0，热生效。</summary>
    public float Gain
    {
        get => _gain;
        set => _gain = Math.Clamp(value, 0.1f, 3.5f);
    }

    public MMDevice? FindCable()
    {
        var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                 .FirstOrDefault(d => d.FriendlyName.Contains("CABLE Input",
                     StringComparison.OrdinalIgnoreCase));
    }

    private void EnsurePlayer()
    {
        if (_player != null && _sampleRate == _provider!.WaveFormat.SampleRate) return;
        try { _player?.Stop(); _player?.Dispose(); } catch { }
        var format = new WaveFormat(_sampleRate, 16, 1);
        _provider = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromMilliseconds(300),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        _player = new WasapiOut(_renderDevice!, AudioClientShareMode.Shared, false, 30);
        _player.Init(_provider);
        _player.Play();
    }

    public bool Start(Transport mode, int port)
    {
        _cts = new CancellationTokenSource();
        _renderDevice = FindCable();
        if (_renderDevice == null)
        {
            OnLog?.Invoke("未找到 CABLE Input，请先安装 VB-CABLE");
            return false;
        }
        EnsurePlayer();

        _listenerTask = mode switch
        {
            Transport.Tcp => Task.Run(() => TcpLoop(port, _cts.Token)),
            Transport.Udp => Task.Run(() => UdpLoop(port, _cts.Token)),
            Transport.Bluetooth => Task.Run(() => BtLoop(_cts.Token)),
            _ => Task.CompletedTask
        };
        return true;
    }

    // ---------- 接收循环（每个 DataChunk: [4B采样率标志?] + PCM） ----------

    private void Feed(byte[] buf, int n)
    {
        // 协议约定：前 4 字节为小端采样率，之后为 PCM；手机每帧都带头
        if (n >= 4)
        {
            int sr = BitConverter.ToInt32(buf, 0);
            if (sr is 48000 or 16000 or 44100)
            {
                if (sr != _sampleRate)
                {
                    _sampleRate = sr;
                    System.Windows.Application.Current?.Dispatcher.Invoke((Action)EnsurePlayer);
                }
                Feed(buf, 4, n - 4);
                return;
            }
        }
        Feed(buf, 0, n);
    }

    private void Feed(byte[] buf, int off, int n)
    {
        if (_provider == null) return;
        if (Math.Abs(_gain - 1f) > 0.01f)
        {
            for (int i = off; i + 1 < off + n; i += 2)
            {
                int s = BitConverter.ToInt16(buf, i);
                s = Math.Clamp((int)(s * _gain), short.MinValue, short.MaxValue);
                buf[i] = (byte)s; buf[i + 1] = (byte)(s >> 8);
            }
        }
        _provider.AddSamples(buf, off, n);
        if (_provider.BufferedDuration.TotalMilliseconds > 250) _provider.ClearBuffer();

        short max = 0;
        for (int i = off; i + 1 < off + n; i += 2)
        {
            var v = Math.Abs(BitConverter.ToInt16(buf, i));
            if (v > max) max = (short)v;
        }
        OnLevel?.Invoke(max / 32768f);
    }

    private async Task TcpLoop(int port, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch { break; }
                var ep = client.Client.RemoteEndPoint?.ToString() ?? "?";
                OnClientChanged?.Invoke($"TCP: {ep}");
                try
                {
                    client.NoDelay = true;
                    using var ns = client.GetStream();
                    var buf = new byte[8192];
                    int rd;
                    while (!ct.IsCancellationRequested && (rd = await ns.ReadAsync(buf, ct)) > 0)
                        Feed(buf, rd);
                }
                catch { }
                client.Dispose();
                OnClientChanged?.Invoke("未连接");
                OnLevel?.Invoke(0);
            }
        }
        finally { listener.Stop(); }
    }

    private async Task UdpLoop(int port, CancellationToken ct)
    {
        using var udp = new UdpClient(port);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await udp.ReceiveAsync(ct);
                OnClientChanged?.Invoke($"UDP: {r.RemoteEndPoint}");
                Feed(r.Buffer, r.Buffer.Length);
            }
            catch { break; }
        }
        OnLevel?.Invoke(0);
    }

    private async Task BtLoop(CancellationToken ct)
    {
        InTheHand.Net.Sockets.BluetoothListener? listener = null;
        try
        {
            listener = new InTheHand.Net.Sockets.BluetoothListener(
                InTheHand.Net.Bluetooth.BluetoothService.SerialPort);
            listener.Start();
        }
        catch (Exception ex)
        {
            OnLog?.Invoke("蓝牙不可用，请在系统设置里打开蓝牙并确认配对: " + ex.Message);
            return;
        }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await Task.Run(() => listener.AcceptBluetoothClient(), ct);
                OnClientChanged?.Invoke($"蓝牙: {client.RemoteMachineName}");
                using var ns = client.GetStream();
                var buf = new byte[8192];
                int rd;
                while (!ct.IsCancellationRequested && (rd = await ns.ReadAsync(buf, ct)) > 0)
                    Feed(buf, rd);
                client.Dispose();
                OnClientChanged?.Invoke("未连接");
                OnLevel?.Invoke(0);
            }
            catch { break; }
        }
        listener.Stop();
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _player?.Stop(); _player?.Dispose(); } catch { }
        _player = null;
        try { _renderDevice?.Dispose(); } catch { }
        _renderDevice = null;
        OnLevel?.Invoke(0);
    }

    public void Dispose() => Stop();
}
