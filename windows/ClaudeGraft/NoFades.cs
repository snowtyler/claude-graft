using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClaudeGraft;

/// Takes the background fades out of a page's controls. WinUI's buttons and boxes fade
/// their fill in, and a page shown again replays that fade from blank as a blink.
public static class NoFades
{
    public static void Strip(DependencyObject? root)
    {
        if (root is null) return;
        switch (root)
        {
            case ContentPresenter presenter: presenter.BackgroundTransition = null; break;
            case Border border: border.BackgroundTransition = null; break;
            case Panel panel: panel.BackgroundTransition = null; break;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            Strip(VisualTreeHelper.GetChild(root, i));
    }
}
