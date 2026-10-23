using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualBasic.CompilerServices;

// System.Windows.Media 里也有同名字（WPF 的 PixelFormat），这里画的是 GDI+ 位图，
// 用别名钉死到 System.Drawing 那一套，避免 CS0104。
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace WslManager.Helpers;

[StandardModule]
public sealed class AppIcon
{
	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool DestroyIcon(nint hIcon);

	[DllImport("gdi32.dll", SetLastError = true)]
	private static extern bool DeleteObject(nint hObject);

	/// <summary>内嵌的正式 logo（bill 提供的几何企鹅，白底已转透明、裁掉白边）。见 Assets/logo.png。</summary>
	private const string LogoResourceName = "WslManager.Assets.logo.png";

	/// <summary>运行状态角标（托盘）：绿点 = 有发行版在跑。主体是设计稿原样，状态只靠这个小点。</summary>
	private static readonly System.Drawing.Color RunDot = System.Drawing.Color.FromArgb(46, 204, 113);

	private static byte[] _logoPng;

	/// <summary>读内嵌 logo（读一次缓存）。找不到直接抛错暴露问题，不悄悄回落。</summary>
	private static byte[] LogoPng()
	{
		if (_logoPng != null) return _logoPng;
		var asm = Assembly.GetExecutingAssembly();
		using var s = asm.GetManifestResourceStream(LogoResourceName)
			?? throw new FileNotFoundException("内嵌 logo 缺失：" + LogoResourceName
				+ "；程序集实际资源：" + string.Join(", ", asm.GetManifestResourceNames()));
		using var ms = new MemoryStream();
		s.CopyTo(ms);
		_logoPng = ms.ToArray();
		return _logoPng;
	}

	/// <summary>把内嵌 logo 解码成 GDI+ 位图（透明底，方形）。</summary>
	private static Bitmap LogoBitmap()
		=> new Bitmap(new MemoryStream(LogoPng()));

	/// <summary>
	/// 应用图标（v2.4 起）：bill 提供的几何企鹅原样缩放，不再程序化重画。
	/// v2.2/v2.3 的「代码画企鹅」删掉 —— 设计稿有了，程序里再画一遍只会越画越不像。
	/// 托盘状态靠右下角一枚小绿点表达（有发行版在跑时出现）。
	/// </summary>
	public static Icon MakeIcon(bool running = false, int size = 64)
	{
		Bitmap bmp = RenderBitmap(running, size);
		nint h = bmp.GetHicon();
		bmp.Dispose();
		return Icon.FromHandle(h);
	}

	/// <summary>渲染 logo 位图（透明外角）。供 tray / ico 生成复用。</summary>
	public static Bitmap RenderBitmap(bool running = false, int size = 64)
	{
		var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
		using Graphics g = Graphics.FromImage(bmp);
		g.SmoothingMode = SmoothingMode.AntiAlias;
		g.InterpolationMode = InterpolationMode.HighQualityBicubic;
		g.PixelOffsetMode = PixelOffsetMode.HighQuality;
		g.CompositingQuality = CompositingQuality.HighQuality;
		g.Clear(System.Drawing.Color.Transparent);

		// logo 主体放大：只留 2% 边距，几乎顶满画布
		float pad = size * 0.02f;
		using (var logo = LogoBitmap())
		{
			g.DrawImage(logo, new System.Drawing.RectangleF(pad, pad, size - pad * 2, size - pad * 2));
		}

		if (running)
		{
			// 右下角绿点 + 白圈（浅色托盘上不至于和深色身体糊在一起）
			float d = size * 0.30f;
			float cx = size - d / 2 - size * 0.04f;
			float cy = size - d / 2 - size * 0.04f;
			using var ring = new SolidBrush(System.Drawing.Color.White);
			g.FillEllipse(ring, cx - d / 2 - size * 0.03f, cy - d / 2 - size * 0.03f,
				d + size * 0.06f, d + size * 0.06f);
			using var dot = new SolidBrush(RunDot);
			g.FillEllipse(dot, cx - d / 2, cy - d / 2, d, d);
		}

		return bmp;
	}

	/// <summary>
	/// 给 WPF 用的位图源：直接按目标像素宽解码 PNG（Bgra32，透明原样保留）。
	/// ⚠ 不能走 <c>Icon → Imaging.CreateBitmapSourceFromHIcon</c>：那条路会把透明像素拍成黑色
	/// （HICON 不保留 alpha），在浅色标题栏上就是四个黑角。
	/// </summary>
	public static ImageSource MakeImageSource(bool running = false, int size = 64)
	{
		var bmp = new BitmapImage();
		bmp.BeginInit();
		bmp.CacheOption = BitmapCacheOption.OnLoad;
		bmp.DecodePixelWidth = Math.Max(16, size);
		bmp.StreamSource = new MemoryStream(LogoPng());
		bmp.EndInit();
		bmp.Freeze();
		return bmp;
	}

	/// <summary>把当前图标写成多尺寸 .ico（供构建前的 app.ico 再生成用）。</summary>
	public static void WriteIco(string path, int[] sizes, bool running = false)
	{
		using var ms = new MemoryStream();
		using var bw = new BinaryWriter(ms);
		var pngs = new byte[sizes.Length][];
		for (int i = 0; i < sizes.Length; i++)
		{
			using Bitmap bmp = RenderBitmap(running, sizes[i]);
			using var one = new MemoryStream();
			bmp.Save(one, ImageFormat.Png);
			pngs[i] = one.ToArray();
		}

		bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
		int offset = 6 + 16 * sizes.Length;
		for (int i = 0; i < sizes.Length; i++)
		{
			bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
			bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
			bw.Write((byte)0); bw.Write((byte)0);
			bw.Write((short)1); bw.Write((short)32);
			bw.Write(pngs[i].Length);
			bw.Write(offset);
			offset += pngs[i].Length;
		}
		foreach (var p in pngs) bw.Write(p);

		File.WriteAllBytes(path, ms.ToArray());
	}
}
