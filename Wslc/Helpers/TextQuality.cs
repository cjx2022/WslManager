using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace WslManager.Wslc.Helpers;

/// <summary>
/// 文字排版模式（<see cref="TextFormattingMode"/>）的 DPI 自适应策略。
///
/// ── 为什么需要它（“字体发虚 / 边缘锯齿”的根因）────────────────────────────
/// WPF 的 TextFormattingMode 有两个取值，作用完全不同：
///   · Display —— 按 **GDI(96 DPI) 像素网格**排布字形，字宽四舍五入到整像素。
///                在 100% 缩放下最锐利（WinForms 那种观感），
///                但它是**为 96 DPI 设计的**：一旦系统缩放不是 100%，
///                这个像素网格就和设备的物理像素网格错位 ——
///                笔画落不到像素格上、粗细忽粗忽细、只剩一层很淡的灰边，就是“发虚 + 锯齿”。
///   · Ideal   —— 按字体本身的理想度量排布，缩放时自然。
///                在高 DPI（125% / 150%，如今的主流）下画质明显更好。
///
/// 本程序原先把 Display 写死在每个窗口的 XAML 上，而当时的机器缩放是 125%
/// （实测渲染缓冲 1550×1000 对应逻辑 1240×800 → 正好 1.25）→ 全窗口都在用
/// 错配的像素网格。这就是本次修复的对象。
///
/// 策略：按显示器 DPI 选 —— ≈100% 用 Display（最锐），&gt;100% 用 Ideal。
/// 程序声明了 PerMonitorV2，窗口拖到另一块缩放不同的屏上 DPI 会变，
/// 所以除了 Loaded，还要在 DpiChanged 时重算（见 <see cref="Install"/>）。
/// </summary>
internal static class TextQuality
{
    // ── 系统 DPI ─────────────────────────────────────────────────
    // 优先 Win10 1607+ 的 GetDpiForSystem；老系统回落到 GDI 的 GetDeviceCaps(LOGPIXELSX)。
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForSystem();

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    private const int LOGPIXELSX = 88;

    private static double _systemScale;

    /// <summary>命令行 --textmode=display|ideal：强制指定，仅用于验收 / 排查（正常运行不设）。</summary>
    internal static string Override = "";

    /// <summary>系统主屏缩放系数（1.0 = 100%）。取不到时按 1.0 处理，绝不抛。</summary>
    internal static double SystemScale
    {
        get
        {
            if (_systemScale > 0) return _systemScale;

            double s = 0;
            try
            {
                uint d = GetDpiForSystem();
                if (d > 0) s = d / 96.0;
            }
            catch { /* 老系统没有这个入口点 */ }

            if (s <= 0)
            {
                try
                {
                    var hdc = GetDC(IntPtr.Zero);
                    if (hdc != IntPtr.Zero)
                    {
                        int dpi = GetDeviceCaps(hdc, LOGPIXELSX);
                        ReleaseDC(IntPtr.Zero, hdc);
                        if (dpi > 0) s = dpi / 96.0;
                    }
                }
                catch { }
            }

            if (s <= 0 || double.IsNaN(s) || double.IsInfinity(s)) s = 1.0;
            _systemScale = s;
            return _systemScale;
        }
    }

    /// <summary>边界值：超过这个缩放就用 Ideal。留 5% 余量，避免 1.0001 之类的浮点噪声。</summary>
    internal const double IdealThreshold = 1.05;

    /// <summary>纯函数：按缩放系数选排版模式（便于离线自检）。</summary>
    internal static TextFormattingMode Pick(double scale)
    {
        if (string.Equals(Override, "display", StringComparison.OrdinalIgnoreCase))
            return TextFormattingMode.Display;
        if (string.Equals(Override, "ideal", StringComparison.OrdinalIgnoreCase))
            return TextFormattingMode.Ideal;
        return scale > IdealThreshold ? TextFormattingMode.Ideal : TextFormattingMode.Display;
    }

    /// <summary>取窗口所在显示器的真实 DPI；拿不到就退回系统 DPI。</summary>
    internal static double ScaleFor(Visual v)
    {
        try
        {
            var d = VisualTreeHelper.GetDpi(v);
            if (d.DpiScaleX > 0.01) return d.DpiScaleX;
        }
        catch { }
        return SystemScale;
    }

    /// <summary>把「按 DPI 选出的排版模式」应用到根元素（会沿视觉树继承给所有子元素）。</summary>
    internal static TextFormattingMode ApplyTo(FrameworkElement root)
    {
        if (root == null) return TextFormattingMode.Display;
        var mode = Pick(ScaleFor(root));
        TextOptions.SetTextFormattingMode(root, mode);
        // 抗锯齿方式固定为 ClearType（子像素）；Auto 会被系统级设置改掉，
        // 而这里的场景永远是「深色字 + 不透明浅色底」，ClearType 一定更清楚。
        TextOptions.SetTextRenderingMode(root, TextRenderingMode.ClearType);
        return mode;
    }

    /// <summary>
    /// 装到所有窗口上（App 启动时调一次）。
    ///
    /// 用路由事件的**类处理器**而不是逐个窗口写 OnSourceInitialized：
    ///   RegisterClassHandler(typeof(Window), ...) 对 Window 及其全部派生类生效，
    ///   包括以后新增的对话框 —— 新加窗口忘了设排版模式的坑就此封死。
    /// 时机选 Loaded 是安全的：Loaded 在首次呈现（ContentRendered）之前，
    /// 此时改排版模式只是让接下来的 Arrange 用新值量一遍，不会闪一帧错的。
    /// </summary>
    internal static void Install()
    {
        AttachClassHandler(typeof(Window));
        // ToolTip 的宿主是 WPF 内部的 Popup（分层窗口），不在 Window 的视觉树里，
        // 拿不到 Window 那一份设置，得单独挂一次。
        AttachClassHandler(typeof(System.Windows.Controls.ToolTip));

        // PerMonitorV2：窗口被拖到缩放不同的显示器上，DPI 会变，必须重算。
        try
        {
            EventManager.RegisterClassHandler(typeof(Window), Window.DpiChangedEvent,
                new DpiChangedEventHandler((s, _) =>
                {
                    if (s is FrameworkElement fe) ApplyTo(fe);
                }));
        }
        catch { /* 极老系统没有 DpiChangedEvent，Loaded 那一份已经够用 */ }
    }

    private static void AttachClassHandler(Type t)
    {
        try
        {
            EventManager.RegisterClassHandler(t, FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, _) =>
                {
                    if (s is FrameworkElement fe) ApplyTo(fe);
                }));
        }
        catch { }
    }

    /// <summary>给启动日志 / 自检用的一句话说明。</summary>
    internal static string Describe(Visual v = null)
    {
        double s = v == null ? SystemScale : ScaleFor(v);
        var mode = Pick(s);
        return $"缩放 {s * 100:0}% → TextFormattingMode={mode}" +
               (Override.Length > 0 ? $"（被 --textmode={Override} 覆盖）" : "");
    }
}
