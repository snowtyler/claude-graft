using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClaudeGraft;

/// <summary>
/// Claude Desktop's version and what to do about it, at the top of the flyout
/// and the window. Red only for something waiting on a person.
/// </summary>
public sealed partial class ClaudeUpdateLine : UserControl
{
    /// Raised for Restart Claudes, which closes work and so needs a warning the
    /// host window shows.
    public event Action? RestartRequested;

    /// Under the words rather than beside them, for the sidebar's narrow foot.
    public bool Stacked
    {
        get => Grid.GetRow(ActionButton) == 1;
        set
        {
            Grid.SetColumn(ActionButton, value ? 0 : 1);
            Grid.SetRow(ActionButton, value ? 1 : 0);
            ActionButton.HorizontalAlignment = value ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            ActionButton.Margin = new Thickness(0, value ? 6 : 0, 0, 0);
        }
    }

    public ClaudeUpdateLine()
    {
        InitializeComponent();
        Loaded += (_, _) => { ClaudeUpdateCenter.Changed += Draw; Draw(); };
        Unloaded += (_, _) => ClaudeUpdateCenter.Changed -= Draw;
    }

    private void Draw()
    {
        var phase = ClaudeUpdateCenter.Now;
        Root.Visibility = phase == ClaudeUpdateCenter.Phase.Hidden ? Visibility.Collapsed : Visibility.Visible;
        Words.Text = ClaudeUpdateCenter.Line;
        Words.Foreground = (Brush)Application.Current.Resources[ClaudeUpdateCenter.Alarming
            ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
        var busy = phase is ClaudeUpdateCenter.Phase.Installing or ClaudeUpdateCenter.Phase.Restarting or ClaudeUpdateCenter.Phase.Checking;
        Busy.IsActive = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        var action = ClaudeUpdateCenter.Action;
        ActionButton.Content = action;
        ActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(ActionButton, action is null ? "" : action.TrimEnd('…') + " Claude Desktop");
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        switch (ClaudeUpdateCenter.Now)
        {
            case ClaudeUpdateCenter.Phase.Available: _ = ClaudeUpdateCenter.UpdateAsync(); break;
            case ClaudeUpdateCenter.Phase.NeedsRestart: RestartRequested?.Invoke(); break;
            case ClaudeUpdateCenter.Phase.Failed: _ = ClaudeUpdateCenter.RetryAsync(); break;
        }
    }
}
