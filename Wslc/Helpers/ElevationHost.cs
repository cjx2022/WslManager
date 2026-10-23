using System.Windows;

namespace WslManager.Wslc.Helpers;

/// <summary>
/// 「以管理员身份重启」相关的进程级状态。
///
/// 为什么单独放一个类：这几个标志原本挂在 <c>EnvCheckWindow</c> 上，v2.2 把环境检查
/// 内嵌进「环境与安装」页、窗口本身删掉了，但 <c>App</c>（退出时拉起提权实例）和
/// <c>MainWindow</c>（放行关闭拦截）仍然要读它们 —— 拎出来才不会让页面和进程生命周期互相拖住。
/// </summary>
public static class ElevationHost
{
    /// <summary>置位后，本进程退出时会拉起管理员实例。</summary>
    public static bool PendingElevate;

    /// <summary>
    /// 置位后，主窗口的「关闭 → 最小化到托盘」拦截会被放行，允许进程真正退出。
    /// 提权重启时必须置位，否则 Application.Shutdown 落不了地，新实例也等不到互斥量释放。
    /// </summary>
    public static bool IsElevateShutdown;

    /// <summary>
    /// 以管理员身份重新启动当前程序。<b>必须在应用退出流程中调用</b>——
    /// 本进程仍持有单实例互斥量时直接启动新进程，会被新进程判定为「已在运行」而自杀。
    /// </summary>
    public static void TryRelaunchElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                Log("提权失败：拿不到自身 exe 路径");
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                // 告诉新实例：你是提权重启来的，不要抢单实例锁（旧进程可能还没释放）
                Arguments = "--elevated",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            System.Diagnostics.Process.Start(psi);
            Log("已拉起管理员实例：" + exe + " --elevated");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 最常见：用户在 UAC 弹窗点了「否」(ERROR_CANCELLED = 1223)
            Log("提权被取消或失败：" + ex.Message);
            MessageBox.Show(
                "提权启动失败或被取消。\n\n你也可以关闭本程序，右键 →「以管理员身份运行」。",
                "提权失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log("提权失败：" + ex.Message);
            MessageBox.Show("提权失败：" + ex.Message, "提权失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void Log(string msg)
    {
        try { WslManager.Helpers.SimpleLog.Write(msg); } catch { }
    }
}
