using System;
using System.IO;

namespace WslManager.Wslc.Services;

/// <summary>
/// 探测 `wsl --update` 正在下载的官方安装包。
///
/// 实测（2026-10-09，WSL 2.9.9.0 → 3.0.1.0）：wsl --update 把安装包下到
/// <c>%TEMP%\wsl.&lt;版本&gt;.x64.msi</c>（本次 350.6 MB），而且**下载期间命令行不打印任何进度**
/// —— 用户在界面上只看到「正在检查更新」然后长时间静默，误以为卡死。
/// 因此这里改从文件系统侧旁路取数：盯住该临时包的大小变化即可算出速度。
/// </summary>
public static class DownloadProbe
{
    /// <summary>在临时目录里找出「不算太旧」的 wsl 安装包，多个则取最后写入的那个。</summary>
    public static FileInfo FindActivePackage(DateTime notOlderThan)
    {
        try
        {
            var dir = Path.GetTempPath();
            FileInfo best = null;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
            {
                var n = f.Name.ToLowerInvariant();
                if (!n.StartsWith("wsl")) continue;
                if (!(n.EndsWith(".msi") || n.EndsWith(".msix") ||
                      n.EndsWith(".msixbundle") || n.EndsWith(".appx") || n.EndsWith(".appxbundle")))
                    continue;
                if (f.LastWriteTime < notOlderThan) continue;
                if (best == null || f.LastWriteTime > best.LastWriteTime) best = f;
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    public static string Human(long bytes)
        => bytes >= 1048576
            ? (bytes / 1048576.0).ToString("F1") + " MB"
            : (bytes / 1024.0).ToString("F0") + " KB";
}
