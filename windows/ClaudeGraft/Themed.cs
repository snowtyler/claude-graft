using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ClaudeGraft;

/// Theme brushes for code. Application.Current.Resources answers in the mode the app
/// launched in and never again, so code is handed one brush per key, recoloured here
/// whenever the mode changes.
internal static class Themed
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();

    public static Brush Brush(string key)
    {
        if (Brushes.TryGetValue(key, out var held)) return held;
        if (Find(key) is not SolidColorBrush source) return (Brush)Application.Current.Resources[key];
        return Brushes[key] = new SolidColorBrush(source.Color) { Opacity = source.Opacity };
    }

    public static void Refresh()
    {
        foreach (var (key, brush) in Brushes)
            if (Find(key) is SolidColorBrush source)
            {
                brush.Color = source.Color;
                brush.Opacity = source.Opacity;
            }
    }

    private static object? Find(string key)
    {
        var dark = Appearance.ToElementTheme(App.Settings.Theme) == ElementTheme.Dark;
        return Search(Application.Current.Resources, dark ? ["Dark", "Default"] : ["Light"], key);
    }

    // A dictionary's own entries before its merged ones, and later merged ones first, as XAML looks.
    private static object? Search(ResourceDictionary root, string[] themes, string key)
    {
        foreach (var theme in themes)
            if (root.ThemeDictionaries.TryGetValue(theme, out var t) && t is ResourceDictionary d && d.TryGetValue(key, out var v))
                return v;
        for (var i = root.MergedDictionaries.Count - 1; i >= 0; i--)
            if (Search(root.MergedDictionaries[i], themes, key) is { } found) return found;
        return null;
    }
}
