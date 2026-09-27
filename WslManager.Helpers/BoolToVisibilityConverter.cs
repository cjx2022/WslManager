using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Microsoft.VisualBasic.CompilerServices;

namespace WslManager.Helpers;

[ValueConversion(typeof(bool), typeof(Visibility))]
public class BoolToVisibilityConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		bool flag = Conversions.ToBoolean(value);
		if (parameter != null && Operators.CompareString(parameter.ToString(), "!", TextCompare: false) == 0)
		{
			flag = !flag;
		}
		return (object)(Visibility)((!flag) ? 2 : 0);
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return Conversions.ToBoolean(value);
	}
}
