using Ezvpn.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Ezvpn.App;

/// <summary>
/// <c>true</c> → Visible, <c>false</c>/null → Collapsed. Pass
/// <c>ConverterParameter=Invert</c> to flip it. Classic <c>{Binding}</c> has no
/// implicit bool→Visibility conversion (only <c>x:Bind</c> does).
/// </summary>
public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var on = value is true;
        if (parameter is "Invert")
        {
            on = !on;
        }
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Collapsed when the bound value is null (e.g. no profile selected), Visible
/// otherwise; <c>ConverterParameter=Invert</c> flips it.
/// </summary>
public sealed partial class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var on = value is not null;
        if (parameter is "Invert")
        {
            on = !on;
        }
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// The status colour for a <see cref="ConnectionState"/>: green connected, amber
/// connecting/reconnecting, red error, gray disconnected — the same signal the
/// tray icon gives with teal/gray, readable at a glance in the profile list.
/// </summary>
public sealed partial class StateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value switch
        {
            ConnectionState.Connected => "SystemFillColorSuccessBrush",
            ConnectionState.Connecting => "SystemFillColorCautionBrush",
            ConnectionState.Error => "SystemFillColorCriticalBrush",
            _ => "ControlStrongFillColorDefaultBrush",
        };
        return (Brush)Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
