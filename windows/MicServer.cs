using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace myXmic;

public enum Transport { Tcp, Udp, Usb, Bluetooth }

/// <summary>
/// 音频中枢：一条通道接收手机 PCM → 写入虚拟声卡(=其他应用可用的麦克风)
/// 侦听开启时，并行复制到默认扬声器（独立开关、互不影响）。
/// </summary>
public class MicServer : IDisposable
{
    public event Action<string>? OnLog;
    public event Action<string>? OnClientChanged;
    public event Action<float>? OnLevel;        // 0..1
    public event Action<int>? OnBitrate;        // kbps

    private CancellationTokenSource? _cts;
    private MMDeviceEnumerator? _enum;
    private MMDevice? _cableRender;             // CABLE Input (虚拟声卡播放端)
    private MMDevice? _defaultSpeaker;          // 默认扬声器（侦听用）
    private WasapiOut? _cablePlayer;
    private WasapiOut? _monitorPlayer;          // 侦听播放器（仅开启时存在）
    private BufferedWaveProvider? _cableBuf;
    private BufferedWaveProvider? _monitorBuf;
    private Task? _listenerTask;
    private Task? _usbRetryTask;

    private volatile float _gain = 1.0f;
    private volatile float _monitorGain = 1.0f;
    private volatile bool _monitorEnabled;
    private volatile int _sampleRate = 48000;
    private long _bytesWindow;
    private long _windowStart = Environment.TickCount64;

    public bool MonitorEnabled
    {
        get => _monitorEnabled;
        set
        {
            _monitorEnabled = value;
            if (value) StartMonitor(); else StopMonitor();
        }
    }

    public float Gain { get => _gain; set => _gain = Math.Clamp(value, 0.1f, 3.5f); }
    public float MonitorGain { get => _monitorGain; set => _monitorGain = Math.Clamp(value, 0f, 3f); }

    public string CurrentMode { get; private set; } = "-";
    public int CurrentPort { get; private set; }

    public MMDevice? FindCable() =>
        GetEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));

    private MMDeviceEnumerator GetEnumerator() => _enum ??= new MMDeviceEnumerator();

    // ---------- 通道 ----------

    public bool Start(Transport mode, int port, Action<string>? log = null)
    {
        _cts = new CancellationTokenSource();
        _cableRender = FindCable();
        if (_cableRender == null)
        {
            OnLog?.Invoke("未找到 CABLE Input，请先安装虚拟声卡");
            return false;
        }
        EnsureCablePlayer();

        CurrentMode = mode.ToString();
        CurrentPort = port;

        // 侦听默认扬声器只在用户开勾选时才拿
        if (_monitorEnabled) StartMonitor();

        _listenerTask = mode switch
        {
            Transport.Tcp => Task.Run(() => TcpLoop(IPAddress.Any, port, _cts.Token)),
            Transport.Udp => Task.Run(() => UdpLoop(port, _cts.Token)),
            Transport.Usb => Task.Run(() => UsbLoop(port, _cts.Token)),
            Transport.Bluetooth => Task.Run(() => BtLoop(_cts.Token)),
            _ => Task.CompletedTask
        };
        return true;
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { StopMonitor(); } catch { }
        try { _cablePlayer?.Stop(); _cablePlayer?.Dispose(); } catch { }
        _cablePlayer = null;
        try { _cableRender?.Dispose(); } catch { }
        _cableRender = null;
        _usbRetryTask = null;
        OnLevel?.Invoke(0);
        OnBitrate?.Invoke(0);
    }

    public void Dispose() { Stop(); try { _enum?.Dispose(); } catch { } }

    // ---------- 播放端构建 ----------

    private void EnsureCablePlayer()
    {
        if (_cableRender == null) return;
        if (_cablePlayer != null && _cableBuf!.WaveFormat.SampleRate == _sampleRate) return;
        try { _cablePlayer?.Stop(); _cablePlayer?.Dispose(); } catch { }
        _cableBuf = new BufferedWaveProvider(new WaveFormat(_sampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromMilliseconds(300),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        _cablePlayer = new WasapiOut(_cableRender, AudioClientShareMode.Shared, false, 30);
        _cablePlayer.Init(_cableBuf);
        _cablePlayer.Play();
    }

    private void StartMonitor()
    {
        if (_monitorPlayer != null) return;
        try
        {
            _defaultSpeaker = GetEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            _monitorBuf = new BufferedWaveProvider(new WaveFormat(_sampleRate, 16, 1))
            {
                BufferDuration = TimeSpan.FromMilliseconds(200),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            _monitorPlayer = new WasapiOut(_defaultSpeaker, AudioClientShareMode.Shared, false, 30);
            _monitorPlayer.Init(_monitorBuf);
            _monitorPlayer.Play();
        }
        catch (Exception ex) { OnLog?.Invoke("侦听启动失败: " + ex.Message); }
    }

    private void StopMonitor()
    {
        try { _monitorPlayer?.Stop(); _monitorPlayer?.Dispose(); } catch { }
        _monitorPlayer = null;
    }

    // ---------- 核心：一条进、两路出 ----------

    private void Feed(byte[] buf, int off, int n, float cableGain)
    {
        if (_cableBuf == null) return;

        _bytesWindow += n;
        var now = Environment.TickCount64;
        if (now - _windowStart >= 1000)
        {
            var kbps = (int)(_bytesWindow * 8 / (now - _windowStart)); // *1000/1024≈*1
            OnBitrate?.Invoke(kbps);
            _bytesWindow = 0; _windowStart = now;
        }

        // 主路：增益后写虚拟声卡（其他应用拿到的是这个）
        if (Math.Abs(cableGain - 1f) > 0.01f) ScaleInPlace(buf, off, n, cableGain);
        _cableBuf.AddSamples(buf, off, n);
        if (_cableBuf.BufferedDuration.TotalMilliseconds > 250) _cableBuf.ClearBuffer();

        // 侦听：复制一份原样数据到扬声器（独立于虚拟声卡）
        if (_monitorEnabled && _monitorBuf != null)
        {
            var copy = new byte[n];
            Array.Copy(buf, off, copy, 0, n);
            if (Math.Abs(_monitorGain - 1f) > 0.01f) ScaleInPlace(copy, 0, n, _monitorGain);
            _monitorBuf.AddSamples(copy, 0, n);
            if (_monitorBuf.BufferedDuration.TotalMilliseconds > 200) _monitorBuf.ClearBuffer();
        }

        short max = 0;
        for (int i = off; i + 1 < off + n; i += 2)
        {
            var v = Math.Abs(BitConverter.ToInt16(buf, i));
            if (v > max) max = (short)v;
        }
        OnLevel?.Invoke(max / 32768f);
    }

    private static void ScaleInPlace(byte[] buf, int off, int n, float g)
    {
        for (int i = off; i + 1 < off + n; i += 2)
        {
            int s = BitConverter.ToInt16(buf, i);
            int v = (int)(s * g);
            if (v > short.MaxValue) v = short.MaxValue; else if (v < short.MinValue) v = short.MinValue;
            buf[i] = (byte)v; buf[i + 1] = (byte)(v >> 8);
        }
    }

    // 自适应采样率：帧头 4 字节小端 sr
    private void FeedFrame(byte[] buf, int n)
    {
        if (n >= 4)
        {
            int sr = BitConverter.ToInt32(buf, 0);
            if (sr is 48000 or 16000 or 44100)
            {
                if (sr != _sampleRate)
                {
                    _sampleRate = sr;
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        EnsureCablePlayer();
                        if (_monitorEnabled) { StopMonitor(); StartMonitor(); }
                    });
                }
                Feed(buf, 4, n - 4, _gain);
                return;
            }
        }
        Feed(buf, 0, n, _gain);
    }

    // ---------- 三种监听循环 ----------

    private async Task TcpLoop(IPAddress bind, int port, CancellationToken ct)
    {
        var listener = new TcpListener(bind, port);
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
                        FeedFrame(buf, rd);
                }
                catch { }
                client.Dispose();
                OnClientChanged?.Invoke("未连接 / Idle");
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
                FeedFrame(r.Buffer, r.Buffer.Length);
            }
            catch { break; }
        }
        OnLevel?.Invoke(0);
    }

    /// <summary>USB 模式：自动循环 adb reverse 直到手机出现，再当 TCP 服务端。</summary>
    private async Task UsbLoop(int port, CancellationToken ct)
    {
        // 持续尝试 adb reverse（手机插上/授权后能自动通）
        _usbRetryTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var ok = EndpointManager.AdbReverse(port);
                OnClientChanged?.Invoke(ok ? "USB: adb 已转发，等待手机推流…" : "USB: 等待 adb 设备…");
                if (ok) break;
                await Task.Delay(1500, ct).ContinueWith(_ => { });
            }
        }, ct);
        await TcpLoop(IPAddress.Loopback, port, ct);
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
            OnLog?.Invoke("蓝牙不可用(在PC开蓝牙并与手机配对): " + ex.Message);
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
                    FeedFrame(buf, rd);
                client.Dispose();
                OnClientChanged?.Invoke("未连接 / Idle");
                OnLevel?.Invoke(0);
            }
            catch { break; }
        }
        listener.Stop();
    }
}
