Imports System.Globalization
Imports System.Windows
Imports System.Windows.Data

Namespace Helpers

    <ValueConversion(GetType(Boolean), GetType(Visibility))>
    Public Class BoolToVisibilityConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            Dim b = CBool(value)
            Dim invert = parameter IsNot Nothing AndAlso parameter.ToString() = "!"
            If invert Then b = Not b
            Return If(b, Visibility.Visible, Visibility.Collapsed)
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Return CBool(value)
        End Function
    End Class

    <ValueConversion(GetType(Boolean), GetType(Boolean))>
    Public Class InverseBoolConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            Return Not CBool(value)
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Return Not CBool(value)
        End Function
    End Class
End Namespace
