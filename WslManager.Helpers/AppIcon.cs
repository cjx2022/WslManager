using System;
using System.Drawing;
using Brush = System.Drawing.Brush;
using Color = System.Drawing.Color;
using FontStyle = System.Drawing.FontStyle;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualBasic.CompilerServices;

namespace WslManager.Helpers;

[StandardModule]
public sealed class AppIcon
{
	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool DestroyIcon(nint hIcon);

	public static Icon MakeIcon(bool running = false, int size = 64)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Expected Obj, but got Unknown
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Expected Obj, but got Unknown
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_006d: Expected Obj, but got Unknown
		//IL_00b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00be: Expected Obj, but got Unknown
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ca: Expected Obj, but got Unknown
		//IL_00ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00df: Expected Obj, but got Unknown
		Bitmap val = new Bitmap(size, size);
		Graphics val2 = Graphics.FromImage((Image)(object)val);
		checked
		{
			try
			{
				val2.SmoothingMode = (SmoothingMode)4;
				val2.TextRenderingHint = (TextRenderingHint)3;
				val2.InterpolationMode = (InterpolationMode)7;
				val2.Clear(Color.Transparent);
				int num = (int)Math.Round((double)size * 0.05);
				Color color = (running ? Color.FromArgb(16, 124, 16) : Color.FromArgb(108, 113, 118));
				SolidBrush val3 = new SolidBrush(color);
				try
				{
					GraphicsPath val4 = new GraphicsPath();
					try
					{
						val4.AddEllipse(num, num, size - num * 2, size - num * 2);
						val2.FillPath((Brush)(object)val3, val4);
					}
					finally
					{
						((IDisposable)val4)?.Dispose();
					}
				}
				finally
				{
					((IDisposable)val3)?.Dispose();
				}
				Font val5 = new Font("Consolas", (float)((double)size * 0.4), (FontStyle)1);
				try
				{
					SolidBrush val6 = new SolidBrush(Color.White);
					try
					{
						StringFormat val7 = new StringFormat
						{
							Alignment = (StringAlignment)1,
							LineAlignment = (StringAlignment)1
						};
						RectangleF rectangleF = new RectangleF(0f, (float)size * 0.06f, size, size);
						val2.DrawString(">_", val5, (Brush)(object)val6, rectangleF, val7);
					}
					finally
					{
						((IDisposable)val6)?.Dispose();
					}
				}
				finally
				{
					((IDisposable)val5)?.Dispose();
				}
			}
			finally
			{
				((IDisposable)val2)?.Dispose();
			}
			return Icon.FromHandle(val.GetHicon());
		}
	}

	public static ImageSource MakeImageSource(bool running = false, int size = 64)
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		Icon val = MakeIcon(running, size);
		try
		{
			return (ImageSource)(object)Imaging.CreateBitmapSourceFromHIcon(val.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
		}
		finally
		{
			try
			{
				DestroyIcon(val.Handle);
			}
			catch (Exception projectError)
			{
				ProjectData.SetProjectError(projectError);
				ProjectData.ClearProjectError();
			}
		}
	}
}
