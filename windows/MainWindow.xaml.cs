using System.Drawing;
using System.Windows;

namespace myXmic;

public partial class MainWindow : Window
{
    private readonly MicServer _server = new();
    private bool _running;
    private bool _reallyExit;              // 托盘菜单点"退出"才为 true
    private string? _captureId, _renderId; // VB-CABLE 两个端点
    private System.Windows.Forms.NotifyIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();
        InitTray();

        _server.OnLog += Log;
        _server.OnClientChanged += s => Dispatcher.Invoke(() => TxtConn.Text = $"手机: {s}");
        _server.OnLevel += v => Dispatcher.Invoke(() => Level.Value = v * 100);

        // 程序一打开就"插入"虚拟麦克风；若未安装则提示一键安装
        Task.Run(async () =>
        {
            (_captureId, _renderId) = EndpointManager.FindCableIds();
            if (_captureId == null)
            {
                Dispatcher.Invoke(() =>
                {
                    TxtDevice.Text = "虚拟声卡: ❌ 未安装，请点下方「一键安装虚拟声卡」";
                    BtnToggle.IsEnabled = false;
                });
                return;
            }
            EndpointManager.SetEnabled(_captureId, true);
            if (_renderId != null) EndpointManager.SetEnabled(_renderId, true);
            Dispatcher.Invoke(() =>
            {
                TxtDevice.Text = "虚拟声卡: ✅ 已就绪（程序退出后将自动隐藏）";
                BtnToggle.IsEnabled = true;
            });
        });
    }

    // ---------- 托盘 ----------
    private void InitTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "myXmic Server",
            Icon = SystemIcons.Application,
            Visible = true
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => RealExit());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void MinimizeToTray()
    {
        Hide();
        WindowState = WindowState.Normal;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // 点右上角 X / 最小化 → 进托盘，不退出
    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized) MinimizeToTray();
        base.OnStateChanged(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;      // 拦截关闭，转托盘
            MinimizeToTray();
            return;
        }
        CleanupAll();
        base.OnClosing(e);
    }

    // ---------- 真正的退出：停服务 + 隐藏虚拟麦克风 + 释放一切 ----------
    private void RealExit()
    {
        _reallyExit = true;
        Close(); // 会走 OnClosing → CleanupAll
    }

    private void CleanupAll()
    {
        try { _server.Dispose(); } catch { }          // 停 TCP/WASAPI/线程
        try                                            // 从系统"拔掉"虚拟麦克风
        {
            if (_captureId != null) EndpointManager.SetEnabled(_captureId, false);
            if (_renderId != null) EndpointManager.SetEnabled(_renderId, false);
        }
        catch { }
        try
        {
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
        }
        catch { }
    }

    // ---------- 服务开关 ----------
    private void BtnToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_running) { StopService(); return; }
        var port = int.TryParse(TxtPort.Text, out var p) ? p : 8125;

        TxtUsb.Text = EndpointManager.AdbReverse(port)
            ? "USB(adb): ✅ 已建立转发，手机填 127.0.0.1"
            : "USB(adb): ⚠ adb 未就绪，仅 Wi-Fi 可用";

        if (!_server.Start(port))
        {
            Log("启动失败：请确认 VB-CABLE 已安装且本程序以管理员运行");
            return;
        }
        _running = true;
        BtnToggle.Content = "停 止 服 务";
        TxtPort.IsEnabled = false;
        Log($"服务已启动，监听 0.0.0.0:{port}");
    }

    private void StopService()
    {
        _server.Stop();
        _running = false;
        BtnToggle.Content = "启 动 服 务";
        TxtPort.IsEnabled = true;
        TxtConn.Text = "手机: 未连接";
        Log("已停止");
    }

    // ---------- 一键安装 VB-CABLE ----------
    private async void BtnInstallDriver_Click(object sender, RoutedEventArgs e)
    {
        BtnInstallDriver.IsEnabled = false;
        var progress = new Progress<int>(v => BtnInstallDriver.Content = v < 100 ? $"下载中 {v}%" : "安装中…请允许弹出的安装窗口");
        var err = await DriverInstaller.InstallAsync(progress);
        if (err != null)
        {
            BtnInstallDriver.Content = "一键安装虚拟声卡";
            BtnInstallDriver.IsEnabled = true;
            MessageBox.Show(err, "安装失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // 安装完重新探测
        (_captureId, _renderId) = await Task.Run(EndpointManager.FindCableIds);
        var ok = _captureId != null;
        TxtDevice.Text = ok ? "虚拟声卡: ✅ 已就绪（程序退出后将自动隐藏）" : "虚拟声卡: 仍未检测到";
        BtnToggle.IsEnabled = ok;
        BtnInstallDriver.Content = ok ? "✔ 已安装" : "一键安装虚拟声卡";
        BtnInstallDriver.IsEnabled = !ok;
        if (ok) Log("驱动安装完成。若列表未刷新，请重启本程序。");
    }

    private void Log(string msg) => Dispatcher.Invoke(() => TxtLog.Text = msg);
}
