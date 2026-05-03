using System;
using System.Globalization;
using System.Windows.Data;

namespace Hexel.Converters
{
    /// <summary>
    /// Converts between an enum value and a bool, parameterized by the target enum member name.
    /// Enables radio buttons to bind two-way to an enum property without code-behind.
    ///
    /// Usage in XAML:
    ///   IsChecked="{Binding MyEnum, Converter={StaticResource EnumToBoolConverter}, ConverterParameter=MyValue}"
    /// </summary>
    [ValueConversion(typeof(Enum), typeof(bool))]
    public sealed class EnumToBoolConverter : IValueConverter
    {
        /// <summary>Returns true when <paramref name="value"/>.ToString() matches <paramref name="parameter"/>.</summary>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value?.ToString() == parameter?.ToString();

        /// <summary>
        /// When the radio button becomes checked (value = true) returns the enum member whose
        /// name matches <paramref name="parameter"/>. When unchecked returns Binding.DoNothing
        /// so the VM value is not overwritten.
        /// </summary>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b && parameter != null)
            {
                // targetType may be Nullable<T>; unwrap if needed
                var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
                if (Enum.TryParse(enumType, parameter.ToString(), out object? result))
                    return result!;
            }
            return Binding.DoNothing;
        }
    }
}
