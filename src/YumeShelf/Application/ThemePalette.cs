using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using YumeShelf.Infrastructure;

namespace YumeShelf.Application;

public static class ThemePalette
{
    public static void Apply(ResourceDictionary resources, AppSettings settings)
    {
        var night = settings.NightMode;
        var accent = "#4B5563";
        if (night) Set(resources, "#0D1117", "#D9151A22", "#E611161E", "#E61B222D", "#1B222D", "#202936", "#303B49", "#EDF2F7", "#97A4B3", "#E87745", "#38231D", "#202936", "#FFF8F2", "#0D1117", "#36000000");
        else Set(resources, "#FFFFFF", "#F8FFFFFF", "#F7F7F8", "#FFFFFFFF", "#FFFFFFFF", "#F7F7F8", "#E5E7EB", "#111111", "#6B7280", accent, "#F1F2F4", "#F7F7F8", "#FFFFFF", "#FFFFFF", "#18000000");
        resources["FeedbackSuccessBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(night ? "#7BDCB5" : "#16724A"));
        resources["FeedbackErrorBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(night ? "#FFB4AB" : "#B42318"));
        resources["FeedbackWarningBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(night ? "#F8CF79" : "#8A5500"));
    }

    private static void Set(ResourceDictionary r, params string[] colors)
    {
        var keys = new[] { "WindowBackground", "ShellBackground", "SidebarBackground", "DetailBackground", "CardBackground", "Surface2Brush", "BorderBrush", "PrimaryTextBrush", "MutedTextBrush", "AccentBrush", "AccentSoftBrush", "SearchBackgroundBrush", "OnAccentBrush", "PreviewBackgroundBrush", "OverlayBrush" };
        for (var i = 0; i < keys.Length; i++) r[keys[i]] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors[i]));
    }
}
