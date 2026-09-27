using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.VisualBasic.CompilerServices;

namespace WslManager.Helpers;

[StandardModule]
public sealed class AppSettings
{
	private static Dictionary<string, string> _values;

	private static string FilePath
	{
		get
		{
			string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WslManager");
			Directory.CreateDirectory(text);
			return Path.Combine(text, "settings.ini");
		}
	}

	private static Dictionary<string, string> Values()
	{
		if (_values == null)
		{
			_values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			try
			{
				if (File.Exists(FilePath))
				{
					string[] array = File.ReadAllLines(FilePath);
					foreach (string text in array)
					{
						int num = text.IndexOf('=');
						if (num > 0)
						{
							_values[text.Substring(0, num).Trim()] = text.Substring(checked(num + 1)).Trim();
						}
					}
				}
			}
			catch (Exception projectError)
			{
				ProjectData.SetProjectError(projectError);
				ProjectData.ClearProjectError();
			}
		}
		return _values;
	}

	public static string GetValue(string key, string def = "")
	{
		string value = null;
		if (Values().TryGetValue(key, out value))
		{
			return value;
		}
		return def;
	}

	public static void SetValue(string key, string value)
	{
		Values()[key] = value;
		Save();
	}

	private static void Save()
	{
		try
		{
			StringBuilder stringBuilder = new StringBuilder();
			foreach (KeyValuePair<string, string> item in Values())
			{
				stringBuilder.AppendLine(item.Key + "=" + item.Value);
			}
			File.WriteAllText(FilePath, stringBuilder.ToString());
		}
		catch (Exception projectError)
		{
			ProjectData.SetProjectError(projectError);
			ProjectData.ClearProjectError();
		}
	}
}
