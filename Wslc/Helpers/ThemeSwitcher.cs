using System;
using System.Windows;
using System.Windows.Media;

namespace WslManager.Wslc.Helpers
{
    /// <summary>
    /// 容器模块配色切换（v2.1）。
    ///
    /// 背景：容器页是从 WSLC 容器管理器整页并入的，自带一套浅色调色板，宿主切深色时它不变。
    /// 做法：把调色板从 Styles.xaml 里拆成 Palette.Light.xaml / Palette.Dark.xaml 两份，
    /// 六个界面（WslcView + 5 个对话框）各自合并一份；切换时只替换这一份字典的 Source，
    /// 其余引用全部是 DynamicResource，会自己重新解析 —— 不重建控件树、不丢状态。
    ///
    /// 窗口类的 Loaded 用类处理器统一接管，所以后打开的对话框也会自动套用当前主题。
    /// </summary>
    public static class ThemeSwitcher
    {
        private const string LightPalette = "pack://application:,,,/Wslc/Themes/Palette.Light.xaml";
        private const string DarkPalette = "pack://application:,,,/Wslc/Themes/Palette.Dark.xaml";

        private static bool _dark;
        private static bool _hooked;

        /// <summary>宿主主题变化时调用：切换所有已存在的容器模块界面。</summary>
        public static void Apply(bool dark)
        {
            _dark = dark;
            EnsureHook();
            Application app = Application.Current;
            if (app == null)
            {
                return;
            }
            foreach (Window w in app.Windows)
            {
                if (w == null)
                {
                    continue;
                }
                if (IsWslcWindow(w))
                {
                    Swap(w, dark);
                }
                WslcView view = FindView(w);
                if (view != null)
                {
                    Swap(view, dark);
                }
            }
        }

        /// <summary>单个界面切换：容器页懒加载创建后补调一次（那时可能还没进视觉树）。</summary>
        public static void ApplyTo(FrameworkElement surface, bool dark)
        {
            _dark = dark;
            Swap(surface, dark);
        }

        private static void EnsureHook()
        {
            if (_hooked)
            {
                return;
            }
            _hooked = true;
            // 类处理器：任何窗口加载完都会经过这里，容器模块的对话框自动套用当前主题，
            // 不必在 5 个对话框的构造函数里各写一行。
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnAnyWindowLoaded));
        }

        private static void OnAnyWindowLoaded(object sender, RoutedEventArgs e)
        {
            Window w = sender as Window;
            if (w != null && IsWslcWindow(w))
            {
                Swap(w, _dark);
            }
        }

        private static bool IsWslcWindow(Window w)
        {
            string ns = w.GetType().Namespace ?? string.Empty;
            return ns.StartsWith("WslManager.Wslc", StringComparison.Ordinal);
        }

        private static WslcView FindView(DependencyObject root)
        {
            if (root == null)
            {
                return null;
            }
            WslcView direct = root as WslcView;
            if (direct != null)
            {
                return direct;
            }
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                WslcView hit = FindView(VisualTreeHelper.GetChild(root, i));
                if (hit != null)
                {
                    return hit;
                }
            }
            return null;
        }

        /// <summary>把目标 Resources 里那份 Palette.*.xaml 换成目标配色的同键字典。</summary>
        private static void Swap(FrameworkElement target, bool dark)
        {
            if (target == null)
            {
                return;
            }
            var dicts = target.Resources.MergedDictionaries;
            for (int i = 0; i < dicts.Count; i++)
            {
                ResourceDictionary dict = dicts[i];
                string src = dict?.Source?.OriginalString ?? string.Empty;
                if (src.IndexOf("/Palette.", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                string want = dark ? DarkPalette : LightPalette;
                if (string.Equals(src, want, StringComparison.OrdinalIgnoreCase))
                {
                    return;     // 已经是目标配色
                }
                dicts[i] = new ResourceDictionary
                {
                    Source = new Uri(want, UriKind.Absolute)
                };
                return;
            }
        }
    }
}
