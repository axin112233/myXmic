using System.IO;
using System.Windows;

namespace myXmic;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 全局异常捕获：任何崩溃都弹窗 + 落盘日志
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
            Report(ev.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, ev) =>
        {
            Report(ev.Exception);
            ev.Handled = true;
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ev) =>
            Report(ev.Exception);

        base.OnStartup(e);
    }

    private static void Report(Exception? ex)
    {
        if (ex == null) return;
        var msg = ex.ToString();
        try { File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "myxmic-error.txt"), msg); } catch { }
        System.Windows.MessageBox.Show(
            "程序异常（已保存到 %TEMP%\\myxmic-error.txt）：\n\n" + msg,
            "myXmic 错误", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
