using System;
using System.Net.NetworkInformation;

namespace WslManager.Wslc.Helpers;

/// <summary>
/// 网络速率采样。
/// 为什么需要它：`wslc pull` 在非终端环境下**完全不输出进度**（实测 stdout / stderr 都是 0 字节，
/// 进度条只在真实 tty 里渲染），拿不到任何百分比或速度，所以只能自己在外面量。
///
/// 取的是**所有非回环网卡的累计接收字节数**（.NET 自带 API，无需 P/Invoke），
/// 两次采样求差 = 这段时间的下载速率。口径是整机而非单进程，
/// 但镜像拉取时基本跑满带宽，作为进度参考足够准。
/// </summary>
public static class NetMeter
{
    /// <summary>累计接收字节数；-1 表示取不到。</summary>
    public static long TotalBytesReceived()
    {
        try
        {
            long total = 0;
            bool any = false;

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                try
                {
                    var st = ni.GetIPv4Statistics();
                    total += st.BytesReceived;
                    any = true;
                }
                catch { }
            }
            return any ? total : -1;
        }
        catch { return -1; }
    }

    /// <summary>把字节数格式化成易读形式。</summary>
    public static string Human(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("F1") + " MB";
        return (bytes / 1073741824.0).ToString("F2") + " GB";
    }
}
