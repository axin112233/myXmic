using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Windows;

namespace myXmic;

public partial class MainWindow : Window
{
    private readonly MicServer _server = new();
    private bool _running;
    private bool _reallyExit;
    private string? _captureId, _renderId;
    private System.Windows.Forms.NotifyIcon? _tray;
    private CancellationTokenSource? _discoveryCts;
    private bool _zh = true;

    private static readonly (string zh, string en)[] ModeName =
        { ("USB (ADB)", "USB (ADB)"), ("Wi-Fi (TCP)", "Wi-Fi (TCP)"),
          ("Wi-Fi (UDP)", "Wi-Fi (UDP)"), ("蓝牙 Bluetooth", "Bluetooth") };

    public MainWindow()
    {
        InitializeComponent();
        InitTray();

        _server.OnLog += Log;
        _server.OnClientChanged += s => Dispatcher.Invoke(() => TxtStatus.Text = s);
        _server.OnLevel += v => Dispatcher.Invoke(() => Level.Value = v * 100);
        _server.OnBitrate += b => Dispatcher.Invoke(() =>
            TxtStats.Text = $"{(_zh ? "码率" : "Bitrate")}: {b} kbps");

        TxtMyIps.Text = (_zh ? "本机 IP: " : "My IPs: ") + string.Join("  ", GetLocalIPv4());

        Task.Run(() =>
        {
            (_captureId, _renderId) = EndpointManager.FindCableIds();
            if (_captureId == null)
            {
                Dispatcher.Invoke(() =>
                {
                    TxtDevice.Text = S("虚拟声卡: ❌ 未安装，点右下「一键安装」",
                                       "Cable: ❌ not installed, use Install button");
                    BtnToggle.IsEnabled = false;
                });
                return;
            }
            EndpointManager.SetEnabled(_captureId, true);
            if (_renderId != null) EndpointManager.SetEnabled(_renderId, true);
            Dispatcher.Invoke(() =>
            {
                TxtDevice.Text = S("虚拟声卡: ✅ 就绪（退出自动隐藏）",
                                   "Cable: ✅ ready (hidden on exit)");
                BtnToggle.IsEnabled = true;
            });
        });

        Log("提示：其他软件(Zoom/OBS等)里选 “CABLE Output” 作为麦克风。\n" +
            "侦听=本机扬声器回放，可随时开关，不影响输出到其他软件。");
    }

    private string S(string zh, string en) => _zh ? zh : en;

    // ---------- 托盘 ----------
    private void InitTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "myXmic",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(S("显示窗口", "Show"), null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(S("退出", "Exit"), null, (_, _) => RealExit());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }
    private void MinimizeToTray() { Hide(); WindowState = WindowState.Normal; }
    private void RestoreFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized) MinimizeToTray();
        base.OnStateChanged(e);
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyExit) { e.Cancel = true; MinimizeToTray(); return; }
        CleanupAll();
        base.OnClosing(e);
    }
    private void RealExit() { _reallyExit = true; Close(); }

    private void CleanupAll()
    {
        _discoveryCts?.Cancel();
        try { _server.Dispose(); } catch { }
        try
        {
            if (_captureId != null) EndpointManager.SetEnabled(_captureId, false);
            if (_renderId != null) EndpointManager.SetEnabled(_renderId, false);
        }
        catch { }
        try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
    }

    // ---------- 自动发现应答（Wi-Fi 用） ----------
    private async Task DiscoveryLoop(CancellationToken ct)
    {
        using var udp = new UdpClient(8124) { EnableBroadcast = true };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await udp.ReceiveAsync(ct);
                if (Encoding.UTF8.GetString(r.Buffer) == "MYXMIC_DISCOVER")
                    await udp.SendAsync(Encoding.UTF8.GetBytes("MYXMIC_HERE"),
                        new IPEndPoint(r.RemoteEndPoint.Address, 8124), ct);
            }
            catch { break; }
        }
    }

    // ---------- 服务开关 ----------
    private async void BtnToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_running) { Stop(); return; }

        var port = int.TryParse(TxtPort.Text, out var p) ? p : 8125;
        var mode = CmbMode.SelectedIndex switch
        {
            0 => Transport.Usb,
            1 => Transport.Tcp,
            2 => Transport.Udp,
            _ => Transport.Bluetooth
        };

        // USB: 启动时主动“自寻” adb；Wi-Fi: 启动发现应答
        TxtUsb.Text = "";
        if (mode == Transport.Tcp || mode == Transport.Udp)
        {
            _discoveryCts = new CancellationTokenSource();
            _ = Task.Run(() => DiscoveryLoop(_discoveryCts.Token));
        }
        if (mode == Transport.Usb)
        {
            var ok = EndpointManager.AdbReverse(port);
            TxtUsb.Text = ok
                ? S("USB: ✅ adb 已就绪，等待手机推流…", "USB: ✅ adb ready, waiting phone…")
                : S("USB: ⚠ 未见到 adb 设备（插上线并开 USB 调试后点启动）",
                    "USB: ⚠ no adb device");
        }

        _server.Gain = (float)GainSlider.Value;
        _server.MonitorGain = (float)MonGain.Value;
        _server.MonitorEnabled = ChkMonitor.IsChecked == true;

        if (!_server.Start(mode, port))
        {
            Log(S("启动失败：虚拟声卡未就绪", "Start failed: virtual cable missing"));
            return;
        }

        _running = true;
        BtnToggle.Content = S("停 止", "STOP");
        CmbMode.IsEnabled = TxtPort.IsEnabled = false;
        TxtStatus.Text = S("已启动，等待手机…", "Running, waiting phone…");
    }

    private void Stop()
    {
        _discoveryCts?.Cancel();
        _server.Stop();
        _running = false;
        BtnToggle.Content = S("启 动", "START");
        CmbMode.IsEnabled = TxtPort.IsEnabled = true;
        TxtStatus.Text = S("已停止", "Stopped");
        TxtStats.Text = S("码率: - kbps", "Bitrate: - kbps");
        Level.Value = 0;
        TxtUsb.Text = "";
    }

    // ---------- 侦听（不影响虚拟声卡输出） ----------
    private void Monitor_Changed(object sender, RoutedEventArgs e) =>
        _server.MonitorEnabled = ChkMonitor.IsChecked == true;

    private void MonGain_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _server.MonitorGain = (float)e.NewValue;
        TxtMonGain.Text = $"{(int)(e.NewValue * 100)}%";
    }

    private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _server.Gain = (float)e.NewValue;
        TxtGain.Text = $"{(int)(e.NewValue * 100)}%";
    }

    // ---------- 语言 ----------
    private void BtnLang_Click(object sender, RoutedEventArgs e)
    {
        _zh = !_zh;
        BtnLang.Content = _zh ? "EN" : "中文";
        LblConn.Text = S("连接方式", "Connection");
        LblPort.Text = S("端口", "Port");
        LblGain.Text = S("增益", "Gain");
        LblMonGain.Text = S("侦听音量", "Monitor gain");
        ChkMonitor.Content = S("🎧 侦听我的声音（不影响虚拟声卡输出）",
                               "🎧 Listen to my voice (does not affect cable output)");
        ChkHideMic.Content = S("退出时隐藏虚拟麦克风", "Hide virtual mic on exit");
        BtnInstallDriver.Content = S("一键安装虚拟声卡", "Install Virtual Cable");
        BtnToggle.Content = _running ? S("停 止", "STOP") : S("启 动", "START");
        Title = "myXmic";
    }

    // ---------- 一键装驱动 ----------
    private async void BtnInstallDriver_Click(object sender, RoutedEventArgs e)
    {
        BtnInstallDriver.IsEnabled = false;
        var progress = new Progress<int>(v => BtnInstallDriver.Content = v < 100 ? $"下载 {v}%" : "安装中…");
        var err = await DriverInstaller.InstallAsync(progress);
        if (err != null)
        {
            BtnInstallDriver.Content = S("一键安装虚拟声卡", "Install Virtual Cable");
            BtnInstallDriver.IsEnabled = true;
            System.Windows.MessageBox.Show(err, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        (_captureId, _renderId) = await Task.Run(EndpointManager.FindCableIds);
        var ok = _captureId != null;
        TxtDevice.Text = ok ? S("虚拟声卡: ✅ 就绪（退出自动隐藏）", "Cable: ✅ ready")
                            : S("虚拟声卡: 未检测到", "Cable: not found");
        BtnToggle.IsEnabled = ok;
        BtnInstallDriver.Content = ok ? "✔" : S("一键安装虚拟声卡", "Install Virtual Cable");
        BtnInstallDriver.IsEnabled = !ok;
    }

    private void Log(string msg) => Dispatcher.Invoke(() =>
    {
        TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        TxtLog.ScrollToEnd();
    });

    private static IEnumerable<string> GetLocalIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(ua.Address))
                    yield return ua.Address.ToString();
        }
    }
}
