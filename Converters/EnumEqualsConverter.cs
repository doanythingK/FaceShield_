using System;
using Avalonia.Data;
using Avalonia.Data.Converters;
using System.Globalization;

namespace FaceShield.Converters;

public sealed class EnumEqualsConverter : IValueConverter
{
    // enum -> bool
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
            return false;

        return value.Equals(parameter);
    }

    // bool -> enum
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null)
            return parameter;

        return BindingOperations.DoNothing;
    }
}