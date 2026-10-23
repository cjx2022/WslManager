using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace WslManager.Wslc.Helpers;

/// <summary>
/// 极简配置：%APPDATA%\WSLCManager\settings.ini
/// 首次运行自动生成一份带注释的默认配置。
/// </summary>
public static class AppSettings
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WSLCManager");

    public static string Location => Path.Combine(Dir, "settings.ini");

    /// <summary>
    /// true = 软件渲染。**默认 false（走硬件渲染）** —— 软件渲染下 WPF 的 ClearType 会退化成灰度抗锯齿，
    /// 中文小字明显发虚，实测清晰度优先于省内存。
    /// </summary>
    public static bool SoftwareRendering { get; set; } = false;

    /// <summary>退出时回收工作集。</summary>
    public static bool TrimOnMinimize { get; set; } = true;

    /// <summary>镜像下载加速地址（registry mirror 前缀），空 = 不加速、直连官方源。</summary>
    public static string RegistryMirror { get; set; } = "";

    /// <summary>
    /// 内置的**第三方公开**镜像加速地址（逐个都是公开、免注册的社区/厂商公益加速站）。
    ///
    /// ⚠ 它们与本工具作者**无关**：不提供、不运营、不背书，可用性与速度一概不保证，
    ///   随时可能限流、变更或失效，请自行判断风险后再使用。想一个都不用就把
    ///   `MirrorPresets` 设成 `none`。
    /// </summary>
    public const string DefaultMirrorPresets =
        "docker.m.daocloud.io,docker.1ms.run,hub.rat.dev,docker.xuanyuan.me";

    /// <summary>
    /// 设置页「常用镜像源（点击填入）」按钮的预设清单，逗号分隔。
    /// 默认值是上面那批公开地址；设成 `none` / `-` 表示一个都不要。
    /// </summary>
    public static string MirrorPresets { get; set; } = DefaultMirrorPresets;

    /// <summary>「浏览器搜索」按钮打开的地址模板，`{q}` 会被替换成检索词。</summary>
    public static string WebSearchUrlTemplate { get; set; } =
        "https://hub.docker.com/search?q={q}";

    /// <summary>性能监控自动刷新周期（毫秒）。</summary>
    public static int StatsIntervalMs { get; set; } = 2000;

    /// <summary>
    /// 开机自启拉起容器后最小化到托盘（只影响自启那一次；是否开机自启见注册表，
    /// 不在这里 —— 注册表才是唯一事实，避免两处状态打架）。
    /// </summary>
    public static bool AutoStartMinimized { get; set; } = true;

    public static void Load()
    {
        try
        {
            if (!File.Exists(Location))
            {
                Save();
                return;
            }

            foreach (var raw in File.ReadAllLines(Location))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;

                var i = line.IndexOf('=');
                if (i <= 0) continue;

                var key = line.Substring(0, i).Trim().ToLowerInvariant();
                var val = line.Substring(i + 1).Trim();

                switch (key)
                {
                    case "rendermode":
                        SoftwareRendering = !val.Equals("hardware", StringComparison.OrdinalIgnoreCase)
                                            && !val.Equals("auto", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "trimonminimize":
                        TrimOnMinimize = val != "0" && !val.Equals("false", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "registrymirror":
                        RegistryMirror = val;
                        break;
                    case "mirrorpresets":
                        // ⚠ 空值 = **沿用内置公开预设**，不是「清空」。
                        //   存量配置文件里这个键就是空的（旧版本写出来的），若把空值当清空，
                        //   升级后预设按钮会整片消失 —— 那是升级把人弄丢功能，不是用户的意愿。
                        //   真要一个都不用，显式写 none 或 -。
                        var presets = (val ?? "").Trim();
                        if (presets.Length == 0) break;
                        MirrorPresets = presets.Equals("none", StringComparison.OrdinalIgnoreCase) || presets == "-"
                            ? ""
                            : presets;
                        break;
                    case "websearchurltemplate":
                        if (val.Contains("{q}", StringComparison.Ordinal)) WebSearchUrlTemplate = val;
                        break;
                    case "statsintervalms":
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                            StatsIntervalMs = Math.Clamp(ms, 500, 60000);
                        break;
                    case "autostartminimized":
                        AutoStartMinimized = val != "0" && !val.Equals("false", StringComparison.OrdinalIgnoreCase);
                        break;
                }
            }
        }
        catch { /* 读不了就用默认值 */ }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var text =
                "# WSLC 管理器配置\r\n" +
                "#\r\n" +
                "# RenderMode: Hardware = 硬件加速（默认；滚动/动画更顺，代价是加载显卡用户态驱动）\r\n" +
                "#             Software = 软件渲染（省约 150MB 显卡驱动内存，UI 动画略耗 CPU）\r\n" +
                "#             ※ 软件渲染下 WPF 的 ClearType 会退化成灰度抗锯齿，中文小字会发虚；\r\n" +
                "#               修改后需重启程序生效\r\n" +
                "RenderMode=" + (SoftwareRendering ? "Software" : "Hardware") + "\r\n" +
                "#\r\n" +
                "# TrimOnMinimize: 1 = 最小化时回收工作集（1/0）\r\n" +
                "TrimOnMinimize=" + (TrimOnMinimize ? "1" : "0") + "\r\n" +
                "#\r\n" +
                "# RegistryMirror: 容器镜像下载加速地址，纯主机名（可带端口），不要写 https:// 。\r\n" +
                "#                 例：RegistryMirror=mirror.example.com 。留空 = 直连官方源\r\n" +
                "RegistryMirror=" + RegistryMirror + "\r\n" +
                "#\r\n" +
                "# MirrorPresets: 设置页「常用镜像源」按钮的预设清单，逗号分隔。\r\n" +
                "#                下面这批是程序内置的**第三方公开**加速地址，与本工具作者无关：\r\n" +
                "#                不提供、不运营、不背书，可用性与速度不保证，随时可能限流或失效。\r\n" +
                "#                可自行增删；写 none（或 -）表示一个都不用。\r\n" +
                "#                例：MirrorPresets=docker.m.daocloud.io,hub.rat.dev\r\n" +
                "MirrorPresets=" + MirrorPresets + "\r\n" +
                "#\r\n" +
                "# WebSearchUrlTemplate: 「浏览器搜索」打开的地址模板，{q} 为检索词占位\r\n" +
                "WebSearchUrlTemplate=" + WebSearchUrlTemplate + "\r\n" +
                "#\r\n" +
                "# StatsIntervalMs: 性能监控自动刷新周期（毫秒，500~60000）\r\n" +
                "StatsIntervalMs=" + StatsIntervalMs + "\r\n" +
                "#\r\n" +
                "# AutoStartMinimized: 1 = 开机自启拉起容器后最小化到托盘（0 = 照常显示窗口）。\r\n" +
                "#                     只影响自启那一次。是否开机自启见注册表：\r\n" +
                "#                     HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run 下的 " + AutoStart.EntryName + " 项\r\n" +
                "AutoStartMinimized=" + (AutoStartMinimized ? "1" : "0") + "\r\n";
            File.WriteAllText(Location, text, new UTF8Encoding(false));
        }
        catch { }
    }
}
