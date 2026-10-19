using System.Windows;
using ClaudeUsageMonitor.Core.Settings;
using Microsoft.Win32;

namespace ClaudeUsageMonitor.Services;

public static class ThemeManager
{
    public static bool IsDark { get; private set; } = true;

    public static void Apply(ColorTheme mode)
    {
        IsDark = mode switch
        {
            ColorTheme.Dark => true,
            ColorTheme.Light => false,
            _ => IsSystemDark(),
        };

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var theme = new ResourceDictionary
        {
            Source = new Uri(IsDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative),
        };
        if (dictionaries.Count > 0)
            dictionaries[0] = theme;
        else
            dictionaries.Add(theme);
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return true;
        }
    }
}
