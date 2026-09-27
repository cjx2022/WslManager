using System;
using System.IO;
using System.Linq;
using Microsoft.VisualBasic.CompilerServices;

namespace WslManager.Helpers;

[StandardModule]
public sealed class SimpleLog
{
	private static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WslManager", "run.log");

	public static void Write(string msg)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
			File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
			TrimIfNeeded();
		}
		catch (Exception projectError)
		{
			ProjectData.SetProjectError(projectError);
			ProjectData.ClearProjectError();
		}
	}

	private static void TrimIfNeeded()
	{
		try
		{
			if (File.Exists(LogPath))
			{
				string[] array = File.ReadAllLines(LogPath);
				if (array.Length > 400)
				{
					File.WriteAllLines(LogPath, array.Skip(checked(array.Length - 200)).ToArray());
				}
			}
		}
		catch (Exception projectError)
		{
			ProjectData.SetProjectError(projectError);
			ProjectData.ClearProjectError();
		}
	}
}
