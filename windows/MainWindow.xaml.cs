using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;

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

    // 简易双语词典 zh / en
    private static readonly Dictionary<string, (string zh, string en)> T = new()
    {
        ["mode"] = ("连接方式:", "Mode:"),
        ["port"] = ("端口:", "Port:"),
        ["gain"] = ("音量:", "Gain:"),
        ["hide"] = ("退出时隐藏虚拟麦克风", "Hide virtual mic on exit"),
        ["start"] = ("启 动 服 务", "START SERVER"),
        ["stop"] = ("停 止 服 务", "STOP SERVER"),
        ["install"] = ("一键安装虚拟声卡", "Install Virtual Cable"),
        ["status"] = ("状态", "Status"),
        ["phone_idle"] = ("手机: 未连接", "Phone: idle"),
    };

    public MainWindow()
    {
        InitializeComponent();
        InitTray();
        _server.OnLog += Log;
        _server.OnClientChanged += s => Dispatcher.Invoke(() => TxtConn.Text = $"手机: {s}");
        _server.OnLevel += v => Dispatcher.Invoke(() => Level.Value = v * 100);
        TxtMyIps.Text = "本机 IP: " + string.Join("  ", GetLocalIPv4());

        Task.Run(() =>
        {
            (_captureId, _renderId) = EndpointManager.FindCableIds();
            if (_captureId == null)
            {
                Dispatcher.Invoke(() =>
                {
                    TxtDevice.Text = S("虚拟声卡: ❌ 未安装，请点一键安装", "Cable: ❌ not installed, use one-click install");
                    BtnToggle.IsEnabled = false;
                });
                return;
            }
            EndpointManager.SetEnabled(_captureId, true);
            if (_renderId != null) EndpointManager.SetEnabled(_renderId, true);
            Dispatcher.Invoke(() =>
            {
                TxtDevice.Text = S("虚拟声卡: ✅ 已就绪（退出时将自动隐藏）", "Cable: ✅ ready (hidden on exit)");
                BtnToggle.IsEnabled = true;
            });
        });
    }

    private string S(string zh, string en) => _zh ? zh : en;

    private void BtnLang_Click(object sender, RoutedEventArgs e)
    {
        _zh = !_zh;
        BtnLang.Content = _zh ? "EN" : "中文";
        LblMode.Text = T["mode"].ItemOn(_zh);
        LblPort.Text = T["port"].ItemOn(_zh);
        LblGain.Text = T["gain"].ItemOn(_zh);
        ChkHideMic.Content = T["hide"].ItemOn(_zh);
        GrpStatus.Header = T["status"].ItemOn(_zh);
        BtnToggle.Content = (_running ? T["stop"] : T["start"]).ItemOn(_zh);
        BtnInstallDriver.Content = T["install"].ItemOn(_zh);
        Title = _zh ? "myXmic 服务器" : "myXmic Server";
    }

    private void InitTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "myXmic",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => RealExit());
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

    // ---------- 自动发现：手机对广播域喊话，我们回自己的 IP ----------
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

    private void BtnToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_running) { StopService(); return; }
        var port = int.TryParse(TxtPort.Text, out var p) ? p : 8125;
        var mode = CmbMode.SelectedIndex switch
        {
            1 => Transport.Udp,
            2 => Transport.Bluetooth,
            _ => Transport.Tcp
        };

        // TCP 模式顺便尝试 adb reverse（USB 场景）
        TxtUsb.Text = mode == Transport.Tcp && EndpointManager.AdbReverse(port)
            ? S("USB(adb): ✅ 已转发，手机填 127.0.0.1", "USB(adb): ✅ forwarded, phone IP = 127.0.0.1")
            : S("USB(adb): ⚠ 未就绪（Wi-Fi 不受影响）", "USB(adb): ⚠ not ready (Wi-Fi unaffected)");

        if (!_server.Start(mode, port))
        {
            Log(S("启动失败：请装虚拟声卡并用管理员运行", "Failed: install virtual cable & run as admin"));
            return;
        }

        _server.Gain = (float)GainSlider.Value;
        _discoveryCts = new CancellationTokenSource();
        _ = Task.Run(() => DiscoveryLoop(_discoveryCts.Token)); // 仅 Wi-Fi 用得到，开销极低

        _running = true;
        BtnToggle.Content = S("停 止 服 务", "STOP SERVER");
        CmbMode.IsEnabled = TxtPort.IsEnabled = false;
        Log($"已启动 [{CmbMode.Text}] 端口 {port}");
    }

    private void StopService()
    {
        _discoveryCts?.Cancel();
        _server.Stop();
        _running = false;
        BtnToggle.Content = S("启 动 服 务", "START SERVER");
        CmbMode.IsEnabled = TxtPort.IsEnabled = true;
        TxtConn.Text = S("手机: 未连接", "Phone: idle");
        Level.Value = 0;
        Log("已停止");
    }

    private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _server.Gain = (float)e.NewValue;
        if (TxtGain != null) TxtGain.Text = $"{(int)(e.NewValue * 100)}%";
    }

    private async void BtnInstallDriver_Click(object sender, RoutedEventArgs e)
    {
        BtnInstallDriver.IsEnabled = false;
        var progress = new Progress<int>(v => BtnInstallDriver.Content = v < 100 ? $"下载中 {v}%" : "安装中…");
        var err = await DriverInstaller.InstallAsync(progress);
        if (err != null)
        {
            BtnInstallDriver.Content = T["install"].ItemOn(_zh);
            BtnInstallDriver.IsEnabled = true;
            System.Windows.MessageBox.Show(err, "安装失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        (_captureId, _renderId) = await Task.Run(EndpointManager.FindCableIds);
        var ok = _captureId != null;
        TxtDevice.Text = ok ? S("虚拟声卡: ✅ 已就绪（退出时将自动隐藏）", "Cable: ✅ ready (hidden on exit)")
                            : S("虚拟声卡: 仍未检测到", "Cable: still not found");
        BtnToggle.IsEnabled = ok;
        BtnInstallDriver.Content = ok ? "✔" : T["install"].ItemOn(_zh);
        BtnInstallDriver.IsEnabled = !ok;
    }

    private void Log(string msg) => Dispatcher.Invoke(() => TxtLog.Text = msg);

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

// 双语取值小工具
internal static class TupleExt
{
    public static string ItemOn(this (string zh, string en) t, bool zh) => zh ? t.zh : t.en;
}
