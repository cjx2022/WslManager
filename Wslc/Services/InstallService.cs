using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WslManager.Wslc.Services;

/// <summary>WSL / wslc 环境体检结果。</summary>
public sealed class WslEnvStatus
{
    public bool WslFound { get; set; }
    public string WslVersion { get; set; } = "";
    public Version WslVersionParsed { get; set; }
    public string KernelVersion { get; set; } = "";
    public string DefaultDistro { get; set; } = "";
    public string WslPath { get; set; } = "";

    /// <summary>默认发行版的 ext4.vhdx 所在目录（注册表 Lxss\BasePath），读不到时为空。</summary>
    public string WslDiskPath { get; set; } = "";

    public bool WslcFound { get; set; }
    public string WslcPath { get; set; } = "";
    public string WslcVersion { get; set; } = "";

    /// <summary>
    /// wslc 数据目录 —— 由 `wslc info` 的「设置文件: …\settings.yaml」取父目录得到（真实值）。
    /// 不硬编码 %LOCALAPPDATA%\wslc：settings.yaml 可以改到别处，硬编码只是"大概率对"。
    /// </summary>
    public string WslcDataDir { get; set; } = "";

    /// <summary>wslc 容器的 storage.vhdx 根目录（= 数据目录\sessions，随数据目录走）。</summary>
    public string WslcDiskPath { get; set; } = "";

    /// <summary>本程序运行日志（%LOCALAPPDATA%\WslManager\run.log）—— 反馈问题时最常被索要的路径。</summary>
    public string LogPath { get; set; } = "";

    /// <summary>默认发行版在资源管理器里的共享路径（\\wsl$\&lt;发行版&gt;），没有默认发行版时为空。</summary>
    public string DefaultSharePath { get; set; } = "";

    public string RawWslVersion { get; set; } = "";
    public string RawWslcHelp { get; set; } = "";

    /// <summary>wslc 要求 WSL >= 2.9.3。</summary>
    public static readonly Version MinWslForWslc = new(2, 9, 3);

    public bool SupportsWslc => WslVersionParsed != null && WslVersionParsed >= MinWslForWslc;

    public bool Ready => WslcFound;

    public string Summary
    {
        get
        {
            if (!WslFound) return "未检测到 WSL";
            if (WslcFound) return $"就绪 · WSL {WslVersion} · wslc 已安装";
            if (!SupportsWslc) return $"WSL {WslVersion} 版本过低（需 ≥ 2.9.3）";
            return $"WSL {WslVersion} · 待安装 wslc";
        }
    }
}

/// <summary>
/// 环境安装与升级。
/// ⚠ 设计原则：只走「官方渠道」——
///   1) wsl --update（官方自更新）
///   2) 微软 Store 页面
///   3) 用户自备的官方 msi 静默安装
/// 不内置、不硬编码任何第三方**安装源** —— 避免把私人通道固化进安装流程。
/// （与「镜像加速前缀」无关：那个只作用于 `wslc pull`，不参与任何安装动作。）
/// </summary>
public static class InstallService
{
    public static Task<WslEnvStatus> DetectAsync(CancellationToken ct = default)
        => Task.Run(() => Detect(ct), ct);

    public static WslEnvStatus Detect(CancellationToken ct = default)
    {
        var st = new WslEnvStatus();

        var wsl = ProcRunner.ResolveExecutable("wsl.exe");
        st.WslFound = wsl != null;
        st.WslPath = wsl ?? "";
        st.WslcDataDir = ResolveWslcDataDir();
        st.WslcDiskPath = Path.Combine(st.WslcDataDir, "sessions");
        st.LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WslManager", "run.log");

        if (st.WslFound)
        {
            var r = ProcRunner.Run(wsl, new[] { "--version" }, 25000, ct);
            st.RawWslVersion = r.Display;
            ParseWslVersion(st.RawWslVersion, st);

            var st2 = ProcRunner.Run(wsl, new[] { "--status" }, 25000, ct);
            ParseDefaultDistro(st2.Display, st);
            st.WslDiskPath = FindWslDiskPath(st.DefaultDistro);
            if (!string.IsNullOrEmpty(st.DefaultDistro))
                st.DefaultSharePath = @"\\wsl$\" + st.DefaultDistro;
        }

        var wslc = ProcRunner.ResolveExecutable("wslc.exe");
        st.WslcFound = wslc != null;
        st.WslcPath = wslc ?? "";

        if (st.WslcFound)
        {
            var h = ProcRunner.Run(wslc, new[] { "--version" }, 20000, ct);
            st.WslcVersion = ExtractVersion(h.Display);
            if (string.IsNullOrEmpty(st.WslcVersion))
                st.WslcVersion = h.Display.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
            var help = ProcRunner.Run(wslc, new[] { "--help" }, 20000, ct);
            st.RawWslcHelp = help.Display;
        }

        return st;
    }

    /// <summary>
    /// wslc 数据目录：`wslc info` 的「设置文件: …\settings.yaml」→ 父目录。
    /// 取不到（wslc 未装 / info 失败）才回退默认位置。GetStorageDir 内部有缓存，
    /// 且它自己会做目录存在性校验，所以这里不再判断命令行输出格式。
    /// </summary>
    private static string ResolveWslcDataDir()
    {
        try
        {
            var dir = WslcService.GetStorageDir();
            if (!string.IsNullOrWhiteSpace(dir)) return dir;
        }
        catch
        {
            // 单取路径失败不该影响整份体检结果
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wslc");
    }

    private static void ParseWslVersion(string text, WslEnvStatus st)
    {
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var lower = line.ToLowerInvariant();

            if (st.WslVersion.Length == 0 && (lower.StartsWith("wsl version") || lower.StartsWith("wsl 版本")))
            {
                st.WslVersion = ExtractVersion(line) ?? line;
                st.WslVersionParsed = TryVersion(ExtractVersion(line));
            }
            else if (st.KernelVersion.Length == 0 && (lower.Contains("kernel") || lower.Contains("内核")))
            {
                st.KernelVersion = ExtractVersion(line) ?? "";
            }
        }

        if (st.WslVersionParsed == null)
        {
            st.WslVersionParsed = TryVersion(ExtractVersion(text));
            if (string.IsNullOrEmpty(st.WslVersion)) st.WslVersion = ExtractVersion(text) ?? "";
        }
    }

    private static void ParseDefaultDistro(string text, WslEnvStatus st)
    {
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            var lower = line.ToLowerInvariant();
            if (lower.Contains("default distribution") || lower.Contains("默认分发"))
            {
                var i = line.IndexOf(':');
                if (i < 0) i = line.IndexOf('：');
                if (i >= 0) st.DefaultDistro = line.Substring(i + 1).Trim();
            }
        }
    }

    /// <summary>
    /// 默认发行版 ext4.vhdx 所在目录。
    ///
    /// 数据源只有注册表一处：<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss\{guid}</c>
    /// 下的 <c>BasePath</c>（Store 版是 …\Packages\Canonical…\LocalState，import --location
    /// 导入的则是用户自选目录）。`wsl --status` / `wsl -l -v` 都**不**输出磁盘位置，
    /// 所以这里读注册表；读不到（权限、条目为 WSL1 无 BasePath）就返回空，由界面显示「—」。
    /// </summary>
    private static string FindWslDiskPath(string defaultDistro)
    {
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (root == null) return "";

            string fallback = "";
            foreach (var name in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(name);
                if (sub == null) continue;
                var basePath = sub.GetValue("BasePath") as string;
                if (string.IsNullOrWhiteSpace(basePath)) continue;

                if (fallback.Length == 0) fallback = basePath;
                var distro = sub.GetValue("DistributionName") as string;
                if (!string.IsNullOrEmpty(defaultDistro) &&
                    string.Equals(distro, defaultDistro, StringComparison.OrdinalIgnoreCase))
                    return basePath;
            }
            return fallback;
        }
        catch
        {
            return "";
        }
    }

    private static string ExtractVersion(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = Regex.Match(text, @"\d+\.\d+\.\d+(?:\.\d+)?");
        return m.Success ? m.Value : null;
    }

    private static Version TryVersion(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return Version.TryParse(s, out var v) ? v : null;
    }

    // ─────────────────────────────────────────────────────────────
    // 安装动作（全部走官方渠道）
    // ─────────────────────────────────────────────────────────────

    /// <summary>官方自更新：wsl --update [--pre-release]。这是微软推荐方式。</summary>
    public static string[] OfficialUpdateArgs(bool preRelease)
        => preRelease ? new[] { "--update", "--pre-release" } : new[] { "--update" };

    /// <summary>
    /// 用用户自备的官方安装包静默安装（msiexec）。
    /// 提权由 ShellExecute("runas") 交给系统 UAC，工具本身不申请常驻管理员权限。
    /// </summary>
    public static bool IsInstallerPackage(string path)
    {
        var ext = Path.GetExtension(path ?? "").ToLowerInvariant();
        return ext is ".msi" or ".msix" or ".msixbundle" or ".appx" or ".appxbundle";
    }

    /// <summary>启动提权安装（不等待）。返回是否成功拉起。</summary>
    public static bool StartElevatedInstall(string packagePath, out string message)
    {
        message = "";
        try
        {
            if (!File.Exists(packagePath)) { message = "安装包不存在"; return false; }
            var ext = Path.GetExtension(packagePath).ToLowerInvariant();

            // ⚠ 实测坑：msiexec 提权后由 Installer 服务（SYSTEM 会话）执行，
            // 非系统盘（如 D:\）上的安装包会导致 msiexec 立即返回错误码 2 而失败。
            // 因此一律先复制到本机临时目录再安装。
            var staging = Path.Combine(Path.GetTempPath(), "WSLCManagerSetup");
            Directory.CreateDirectory(staging);
            var staged = Path.Combine(staging, Path.GetFileName(packagePath));

            var srcFull = Path.GetFullPath(packagePath);
            var dstFull = Path.GetFullPath(staged);
            if (!string.Equals(srcFull, dstFull, StringComparison.OrdinalIgnoreCase))
            {
                message = "正在复制安装包到临时目录…";
                File.Copy(srcFull, dstFull, true);
            }

            ProcessStartInfo psi;
            if (ext == ".msi")
            {
                var logPath = Path.Combine(staging, "install.log");
                psi = new ProcessStartInfo
                {
                    FileName = "msiexec.exe",
                    Arguments = $"/i \"{staged}\" /passive /norestart /l*v \"{logPath}\"",
                    UseShellExecute = true,
                    Verb = "runas"
                };
            }
            else
            {
                // msix / appx 走 PowerShell 的 Add-AppxPackage（需要管理员/信任）
                psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"Add-AppxPackage -Path '{staged}'\"",
                    UseShellExecute = true,
                    Verb = "runas"
                };
            }

            Process.Start(psi);
            message = "已请求管理员权限，请在系统弹窗中确认（安装完成后点击「环境检查」）";
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            message = "用户取消了 UAC 提权";
            return false;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    /// <summary>安装日志路径（供排障展示）。</summary>
    public static string LastInstallLogPath =>
        Path.Combine(Path.GetTempPath(), "WSLCManagerSetup", "install.log");

    /// <summary>打开微软官方 WSL 产品页（Microsoft Store）。</summary>
    public static void OpenStorePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-windows-store://pdp/?productid=9P9TQF7MRM4R",
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>打开 WSL 官方发布页，供用户自行取用官方安装包。</summary>
    public static void OpenReleasePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/microsoft/WSL/releases",
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>检查 WSL 是否注册了 Microsoft Store 版本（可被 Store 直接升级）。</summary>
    public static bool IsStorePackage()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var p = Path.Combine(local, "Microsoft", "WindowsApps");
        try
        {
            return Directory.Exists(p) &&
                   Directory.EnumerateDirectories(p, "MicrosoftCorporationII.WindowsSubsystemForLinux*").Any();
        }
        catch { return false; }
    }
}
