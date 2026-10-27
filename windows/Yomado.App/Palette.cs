using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace Yomado.App;

internal static class Palette
{
    public static void Apply(bool? darkOverride = null)
    {
        var dark = darkOverride ?? Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;
        var resources = Application.Current.Resources;
        resources["PanelBackground"] = Brush(dark ? "#202020" : "#F5F5F5");
        resources["CardBackground"] = Brush(dark ? "#2B2B2B" : "#FFFFFF");
        resources["PanelForeground"] = Brush(dark ? "#F5F5F5" : "#1A1A1A");
        resources["MutedForeground"] = Brush(dark ? "#B3B3B3" : "#626262");
        resources["ControlBackground"] = Brush(dark ? "#363636" : "#FFFFFF");
        resources["ControlBorder"] = Brush(dark ? "#666666" : "#D1D1D1");
        resources["ControlHover"] = Brush(dark ? "#444444" : "#EBEBEB");
        if (SystemParameters.HighContrast)
        {
            resources["PanelBackground"] = resources["CardBackground"] = resources["ControlBackground"] = resources["ControlHover"] = SystemColors.WindowBrush;
            resources["PanelForeground"] = resources["MutedForeground"] = resources["ControlBorder"] = SystemColors.WindowTextBrush;
        }
    }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
