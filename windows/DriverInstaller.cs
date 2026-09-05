using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace myXmic;

/// <summary>
/// 一键安装 VB-CABLE：从 VB-Audio 官网下载驱动包 → 解压出安装器 → 静默安装。
/// 无需用户手动准备任何文件。
/// </summary>
public static class DriverInstaller
{
    // 官方直链（版本更新时改这里即可）
    // https://vb-audio.com/Cable/ 页面上的 "Local package" 按钮指向此地址
    private const string ZipUrl =
        "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";

    /// <returns>null = 成功；否则为错误信息。</returns>
    public static async Task<string?> InstallAsync(IProgress<int>? progress = null)
    {
        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), "myxmic_vbcable");
            Directory.CreateDirectory(tmp);
            var zipPath = Path.Combine(tmp, "vbcable.zip");

            // 1. 下载
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using (var resp = await http.GetAsync(ZipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!resp.IsSuccessStatusCode)
                    return $"下载失败 HTTP {(int)resp.StatusCode}。可手动到 https://vb-audio.com/Cable/ 下载安装。";

                var total = resp.Content.Headers.ContentLength ?? -1;
                await using var net = await resp.Content.ReadAsStreamAsync();
                await using var fs = File.Create(zipPath);
                var buf = new byte[81920];
                long read = 0; int n;
                while ((n = await net.ReadAsync(buf)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n));
                    read += n;
                    if (total > 0) progress?.Report((int)(read * 100 / total));
                }
            }

            // 2. 解压出 64 位安装器
            string? setup = null;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var e in zip.Entries)
                {
                    if (e.Name.Equals("VBCABLE_Setup_x64.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        setup = Path.Combine(tmp, e.Name);
                        e.ExtractToFile(setup, overwrite: true);
                        break;
                    }
                }
            }
            if (setup == null)
                return "下载的包结构异常（官网可能更新了版本）。请到 https://vb-audio.com/Cable/ 手动安装。";

            // 3. 静默安装（管理员权限已在程序清单里保证）
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = setup,
                Arguments = "-i -h",
                UseShellExecute = true,
                Verb = "runas"
            });
            if (p == null) return "安装器启动失败";
            await p.WaitForExitAsync();
            return null;
        }
        catch (Exception ex)
        {
            return $"安装出错: {ex.Message}";
        }
    }
}
