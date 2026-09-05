using System.Diagnostics;

namespace myXmic;

/// <summary>
/// 通过 PowerShell 启用/禁用音频端点。
/// 禁用后的设备会从所有应用的设备列表消失（等效"拔掉"），启用即恢复。
/// </summary>
public static class EndpointManager
{
    /// <summary>找到 "CABLE Output"(录音/"采集端) 和 "CABLE Input"(播放端) 的 PNP InstanceId。</summary>
    public static (string? captureId, string? renderId) FindCableIds()
    {
        var ps =
            "Get-PnpDevice -Class AudioEndpoint -ErrorAction SilentlyContinue " +
            "| Where-Object { $_.FriendlyName -match 'CABLE (Input|Output)' } " +
            "| ForEach-Object { $_.FriendlyName + '|' + $_.InstanceId }";
        var (code, stdout) = RunPs(ps);
        if (code != 0) return (null, null);

        string? cap = null, ren = null;
        foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length != 2) continue;
            if (parts[0].Contains("Output", StringComparison.OrdinalIgnoreCase)) cap = parts[1].Trim();
            if (parts[0].Contains("Input", StringComparison.OrdinalIgnoreCase)) ren = parts[1].Trim();
        }
        return (cap, ren);
    }

    public static bool SetEnabled(string instanceId, bool enable)
    {
        var verb = enable ? "Enable" : "Disable";
        var (code, _) = RunPs($"{verb}-PnpDevice -InstanceId '{instanceId}' -Confirm:$false");
        return code == 0;
    }

    /// <summary>是否已安装 VB-CABLE。</summary>
    public static bool IsCableInstalled() => FindCableIds().captureId != null;

    public static (int code, string stdout) RunPs(string script, int timeoutMs = 15000)
    {
        var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -NonInteractive -Command \"{script}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        p.Start();
        var outStr = p.StandardOutput.ReadToEnd();
        p.WaitForExit(timeoutMs);
        return (p.ExitCode, outStr);
    }

    /// <summary>adb reverse 端口转发（USB 模式）。</summary>
    public static bool AdbReverse(int port)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("adb", $"reverse tcp:{port} tcp:{port}")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            });
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }
}
