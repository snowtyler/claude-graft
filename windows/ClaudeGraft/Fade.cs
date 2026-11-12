using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClaudeGraft;

/// Gives a control whose WinUI template switches its fill outright the same hover fade a
/// button has, so every control in the window answers the pointer alike.
public static class Fade
{
    public static readonly DependencyProperty OnHoverProperty = DependencyProperty.RegisterAttached(
        "OnHover", typeof(bool), typeof(Fade), new PropertyMetadata(false, OnChanged));

    public static bool GetOnHover(DependencyObject o) => (bool)o.GetValue(OnHoverProperty);
    public static void SetOnHover(DependencyObject o, bool v) => o.SetValue(OnHoverProperty, v);

    private static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not Control control || e.NewValue is not true) return;
        control.Loaded += (_, _) => Add(control);
    }

    /// The part of a control's template that wears its fill: ComboBox calls it Background,
    /// the title bar's pane button RootGrid, menu, list and sidebar items LayoutRoot.
    public static void Add(Control control)
    {
        if (VisualTreeHelper.GetChildrenCount(control) == 0) return;
        Fill(VisualTreeHelper.GetChild(control, 0), 0);
    }

    private static void Fill(DependencyObject part, int depth)
    {
        if (part is FrameworkElement { Name: "Background" or "RootGrid" or "LayoutRoot" } named)
        {
            switch (named)
            {
                case Border { BackgroundTransition: null } border: border.BackgroundTransition = new BrushTransition(); break;
                case Panel { BackgroundTransition: null } panel: panel.BackgroundTransition = new BrushTransition(); break;
            }
        }
        // A sidebar item keeps its fill inside a presenter a few levels down.
        if (depth > 6) return;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(part); i++) Fill(VisualTreeHelper.GetChild(part, i), depth + 1);
    }
}
