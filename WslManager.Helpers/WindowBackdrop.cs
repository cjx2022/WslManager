using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.VisualBasic.CompilerServices;
using Microsoft.Win32;

namespace WslManager.Helpers;

[StandardModule]
public sealed class WindowBackdrop
{
	private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

	private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

	private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

	private const int DWMSBT_NONE = 1;

	private const int DWMSBT_MAINWINDOW = 2;

	private const int DWMSBT_TRANSIENTWINDOW = 3;

	public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

	public static bool SystemUsesLightTheme()
	{
		bool result;
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
			result = Convert.ToInt32(RuntimeHelpers.GetObjectValue(RuntimeHelpers.GetObjectValue(registryKey?.GetValue("AppsUseLightTheme") ?? ((object)1)))) == 1;
		}
		catch (Exception projectError)
		{
			ProjectData.SetProjectError(projectError);
			result = true;
			ProjectData.ClearProjectError();
		}
		return result;
	}

	public static Color GetAccentColor()
	{
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0079: Unknown result type (might be due to invalid IL or missing references)
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		checked
		{
			Color result;
			try
			{
				using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\DWM");
				uint num = (uint)Convert.ToInt32(RuntimeHelpers.GetObjectValue(RuntimeHelpers.GetObjectValue(registryKey?.GetValue("ColorizationColor") ?? ((object)14120960))));
				byte b = (byte)(unchecked((long)num) & 0xFFL);
				byte b2 = (byte)(unchecked((long)(num >> 8)) & 0xFFL);
				byte b3 = (byte)(unchecked((long)(num >> 16)) & 0xFFL);
				result = (((byte)unchecked((uint)(checked((byte)unchecked((uint)(b3 + b2))) + b)) >= 90) ? Color.FromRgb(b3, b2, b) : Color.FromRgb((byte)0, (byte)120, (byte)212));
			}
			catch (Exception projectError)
			{
				ProjectData.SetProjectError(projectError);
				result = Color.FromRgb((byte)0, (byte)120, (byte)212);
				ProjectData.ClearProjectError();
			}
			return result;
		}
	}

	private static nint HwndOf(Window window)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		return new WindowInteropHelper(window).Handle;
	}

	public static bool SetMica(Window window)
	{
		bool result;
		if (!IsWindows11)
		{
			result = false;
		}
		else
		{
			try
			{
				nint num = HwndOf(window);
				if (num == IntPtr.Zero)
				{
					result = false;
				}
				else
				{
					int attrValue = 2;
					result = DwmSetWindowAttribute(num, 38, ref attrValue, Marshal.SizeOf(typeof(int))) >= 0;
				}
			}
			catch (Exception projectError)
			{
				ProjectData.SetProjectError(projectError);
				result = false;
				ProjectData.ClearProjectError();
			}
		}
		return result;
	}

	public static void SetImmersiveDark(Window window, bool dark)
	{
		if (!IsWindows11)
		{
			return;
		}
		try
		{
			nint num = HwndOf(window);
			if (!(num == IntPtr.Zero))
			{
				int attrValue = (dark ? 1 : 0);
				DwmSetWindowAttribute(num, 20, ref attrValue, Marshal.SizeOf(typeof(int)));
			}
		}
		catch (Exception projectError)
		{
			ProjectData.SetProjectError(projectError);
			ProjectData.ClearProjectError();
		}
	}

	public static void SetRoundedCorners(Window window)
	{
		if (!IsWindows11)
		{
			return;
		}
		try
		{
			nint num = HwndOf(window);
			if (!(num == IntPtr.Zero))
			{
				int attrValue = 2;
				DwmSetWindowAttribute(num, 33, ref attrValue, Marshal.SizeOf(typeof(int)));
			}
		}
		catch (Exception projectError)
		{
			ProjectData.SetProjectError(projectError);
			ProjectData.ClearProjectError();
		}
	}

	public static void Apply(Window window, bool dark)
	{
		SetRoundedCorners(window);
		SetImmersiveDark(window, dark);
	}
}
