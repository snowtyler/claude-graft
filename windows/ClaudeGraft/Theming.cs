using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace ClaudeGraft;

/// Puts the chosen theme into the brushes the controls draw with. Each is replaced, and
/// `Refresh` then has every control look its brushes up again.
internal static class Theming
{
    private static readonly UISettings System = new();
    private static bool _watching;

    // Which colour of a scheme each brush wears; the brushes App.xaml and WinUI define.
    private static readonly (string Key, Func<PaletteColors, Rgb> Slot, Surface Kind)[] Surfaces =
    [
        ("GraftHeaderBrush", c => c.Header, Surface.Behind),
        ("GraftSidebarBrush", c => c.Sidebar, Surface.Behind),
        ("NavigationViewDefaultPaneBackground", c => c.Sidebar, Surface.Behind),
        ("NavigationViewExpandedPaneBackground", c => c.Sidebar, Surface.Behind),
        ("ApplicationPageBackgroundThemeBrush", c => c.Content, Surface.Page),
        ("NavigationViewContentBackground", c => c.Content, Surface.Page),
        ("LayerFillColorDefaultBrush", c => c.Content, Surface.Page),
        ("GraftTrackBrush", c => c.Track, Surface.Solid),
        ("NavigationViewContentGridBorderBrush", c => c.Divider, Surface.Edge),
        ("GraftTitleBrush", c => c.Header, Surface.Title),
        ("GraftQuietBrush", c => c.ButtonHover, Surface.Clear),
        ("DividerStrokeColorDefaultBrush", c => c.Divider, Surface.Solid),
        // Every clear control at rest, under the pointer and pressed, as a quiet button is.
        ("NavigationViewItemBackground", c => c.ButtonHover, Surface.Clear),
        ("NavigationViewItemBackgroundPointerOver", c => c.ButtonHover, Surface.Solid),
        ("NavigationViewItemBackgroundPressed", c => c.ButtonPressed, Surface.Solid),
        ("MenuFlyoutItemBackground", c => c.ButtonHover, Surface.Clear),
        ("MenuFlyoutItemBackgroundPointerOver", c => c.ButtonHover, Surface.Solid),
        ("MenuFlyoutItemBackgroundPressed", c => c.ButtonPressed, Surface.Solid),
        ("ToggleMenuFlyoutItemBackground", c => c.ButtonHover, Surface.Clear),
        ("ToggleMenuFlyoutItemBackgroundPointerOver", c => c.ButtonHover, Surface.Solid),
        ("ToggleMenuFlyoutItemBackgroundPressed", c => c.ButtonPressed, Surface.Solid),
        ("MenuFlyoutSubItemBackground", c => c.ButtonHover, Surface.Clear),
        ("MenuFlyoutSubItemBackgroundPointerOver", c => c.ButtonHover, Surface.Solid),
        ("MenuFlyoutSubItemBackgroundPressed", c => c.ButtonPressed, Surface.Solid),
        ("ComboBoxItemBackground", c => c.ButtonHover, Surface.Clear),
        ("ComboBoxItemBackgroundPointerOver", c => c.ButtonHover, Surface.Solid),
        ("ComboBoxItemBackgroundPressed", c => c.ButtonPressed, Surface.Solid),
        ("ListViewItemBackground", c => c.ButtonHover, Surface.Clear),
        ("ListViewItemBackgroundPointerOver", c => c.ButtonHover, Surface.Solid),
        ("ListViewItemBackgroundPressed", c => c.ButtonPressed, Surface.Solid),
        ("NavigationViewButtonBackground", c => c.ButtonHover, Surface.Clear),
        ("NavigationViewButtonBackgroundPointerOver", c => c.ButtonHover, Surface.Solid),
        ("NavigationViewButtonBackgroundPressed", c => c.ButtonPressed, Surface.Solid),
        ("ComboBoxBackgroundPressed", c => c.ButtonPressed, Surface.Solid),
        ("NavigationViewItemBackgroundSelected", c => c.Selection, Surface.Solid),
        ("NavigationViewItemBackgroundSelectedPointerOver", c => c.Selection, Surface.Solid),
        ("NavigationViewItemBackgroundSelectedPressed", c => c.Selection, Surface.Solid),
        ("CardBackgroundFillColorDefaultBrush", c => c.Card, Surface.Solid),
        ("CardBackgroundFillColorSecondaryBrush", c => c.Header, Surface.Solid),
        ("ButtonBackground", c => c.Button, Surface.Solid),
        ("ButtonBackgroundPointerOver", c => c.ButtonHover, Surface.Solid),
        ("ButtonBackgroundPressed", c => c.ButtonPressed, Surface.Solid),
        ("ComboBoxBackground", c => c.Button, Surface.Solid),
        ("ComboBoxBackgroundPointerOver", c => c.ButtonHover, Surface.Solid),
        ("TextControlBackground", c => c.Field, Surface.Solid),
        ("TextControlBackgroundPointerOver", c => c.Field, Surface.Solid),
        ("TextControlBackgroundFocused", c => c.Field, Surface.Solid),
        ("ContentDialogBackground", c => c.Card, Surface.Solid),
        ("ContentDialogTopOverlay", c => c.Card, Surface.Solid),
        ("ContentDialogBorderBrush", c => c.Divider, Surface.Solid),
        ("FlyoutPresenterBackground", c => c.Card, Surface.Solid),
        ("MenuFlyoutPresenterBackground", c => c.Card, Surface.Solid),
        ("ComboBoxDropDownBackground", c => c.Card, Surface.Solid),
    ];

    // Edge: the lines framing the page, gone over a backdrop so the page reads as one sheet.
    // Title: the strip over the page, which over a backdrop joins the page beneath it.
    // Clear: a rest colour at alpha 0, the hue its fade starts from rather than clear white.
    private enum Surface { Solid, Behind, Page, Edge, Title, Clear }

    private const byte PageAlpha = 0x40;

    // WinUI's accent brushes, by the shade each mode gives them.
    private static readonly (string Key, Func<PaletteColors, Rgb> Dark, Func<PaletteColors, Rgb> Light)[] Accents =
    [
        ("AccentFillColorDefaultBrush", c => c.AccentLight2, c => c.AccentDark1),
        ("AccentFillColorSecondaryBrush", c => c.AccentLight2, c => c.AccentDark1),
        ("AccentFillColorTertiaryBrush", c => c.AccentLight2, c => c.AccentDark1),
        ("AccentFillColorSelectedTextBackgroundBrush", c => c.Accent, c => c.Accent),
        ("AccentTextFillColorPrimaryBrush", c => c.AccentLight3, c => c.AccentDark2),
        ("AccentTextFillColorSecondaryBrush", c => c.AccentLight3, c => c.AccentDark3),
        ("AccentTextFillColorTertiaryBrush", c => c.AccentLight2, c => c.AccentDark1),
        ("SystemControlHighlightAccentBrush", c => c.Accent, c => c.Accent),
        ("SystemControlForegroundAccentBrush", c => c.Accent, c => c.Accent),
    ];

    public static void Apply(GraftSettings settings)
    {
        var accent = SystemAccent();
        var behind = settings.Backdrop != BackdropMaterial.None;
        var resources = Application.Current.Resources;
        foreach (var (theme, dark) in new[] { ("Default", true), ("Dark", true), ("Light", false) })
        {
            var colors = Palettes.For(settings.Palette, dark, accent);
            // Surfaces only in Graft's own dictionary: WinUI's brushes are shared between many
            // keys, and recolouring one there repainted unrelated controls, the sidebar's hover among them.
            if (Own(resources)?.ThemeDictionaries.TryGetValue(theme, out var own) == true && own is ResourceDictionary ours)
                foreach (var (key, slot, kind) in Surfaces)
                    // Over Mica or Acrylic the sidebar and title bar let the material through,
                    // and the page wears only a light wash of its colour over it.
                    Paint(ours, key, kind == Surface.Title && behind ? colors.Content : slot(colors), kind switch
                    {
                        Surface.Clear => 0,
                        Surface.Behind or Surface.Edge when behind => 0,
                        Surface.Page or Surface.Title when behind => PageAlpha,
                        _ => 0xFF,
                    });
            foreach (var dictionary in ThemeDictionaries(resources, theme))
                foreach (var (key, onDark, onLight) in Accents)
                    Paint(dictionary, key, (dark ? onDark : onLight)(colors), null);
            if (dark)
            {
                resources["SystemAccentColor"] = Color(colors.Accent, 0xFF);
                resources["SystemAccentColorLight1"] = Color(colors.AccentLight1, 0xFF);
                resources["SystemAccentColorLight2"] = Color(colors.AccentLight2, 0xFF);
                resources["SystemAccentColorLight3"] = Color(colors.AccentLight3, 0xFF);
                resources["SystemAccentColorDark1"] = Color(colors.AccentDark1, 0xFF);
                resources["SystemAccentColorDark2"] = Color(colors.AccentDark2, 0xFF);
                resources["SystemAccentColorDark3"] = Color(colors.AccentDark3, 0xFF);
            }
        }
        if (!_watching)
        {
            _watching = true;
            var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            // A new Windows accent reaches the System accent theme without a restart.
            System.ColorValuesChanged += (_, _) => ui?.TryEnqueue(() => { if (App.Settings.Palette == AppPalette.Accent) Apply(App.Settings); });
        }
    }

    private static Rgb SystemAccent()
    {
        var c = System.GetColorValue(UIColorType.Accent);
        return new Rgb(c.R, c.G, c.B);
    }

    private static Color Color(Rgb c, byte alpha) => Windows.UI.Color.FromArgb(alpha, c.R, c.G, c.B);

    // A new brush rather than a recoloured one: a control's hover fade runs from the colour
    // its brush was first drawn with, so a recoloured brush flashed its old colour on hover.
    // An accent brush keeps the opacity WinUI gave it.
    private static void Paint(ResourceDictionary dictionary, string key, Rgb color, byte? alpha)
    {
        if (dictionary.TryGetValue(key, out var value) && value is SolidColorBrush brush)
            dictionary[key] = new SolidColorBrush(Color(color, alpha ?? brush.Color.A));
    }

    // The dictionary App.xaml merges after WinUI's, which holds Graft's colours.
    private static ResourceDictionary? Own(ResourceDictionary root) =>
        root.MergedDictionaries.LastOrDefault(d => d.ThemeDictionaries.TryGetValue("Default", out var t) && t is ResourceDictionary r && r.ContainsKey("GraftSidebarBrush"));

    /// Has an element re-read its theme brushes, which it otherwise keeps from when it was drawn.
    public static void Refresh(FrameworkElement root, ElementTheme wanted)
    {
        root.RequestedTheme = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        root.RequestedTheme = wanted;
    }

    private static IEnumerable<ResourceDictionary> ThemeDictionaries(ResourceDictionary root, string theme)
    {
        if (root.ThemeDictionaries.TryGetValue(theme, out var found) && found is ResourceDictionary dictionary) yield return dictionary;
        foreach (var merged in root.MergedDictionaries)
            foreach (var inner in ThemeDictionaries(merged, theme)) yield return inner;
    }
}
