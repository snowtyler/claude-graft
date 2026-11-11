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

    private bool _stacked;

    /// The sidebar's form, as the Mac's: a sync symbol, the name over the
    /// version, a button that checks again, and any install button under it all.
    public bool Stacked
    {
        get => _stacked;
        set
        {
            _stacked = value;
            Grid.SetColumn(ActionButton, value ? 1 : 3);
            Grid.SetRow(ActionButton, value ? 1 : 0);
            ActionButton.HorizontalAlignment = value ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            ActionButton.Margin = new Thickness(0, value ? 6 : 0, 0, 0);
            Glyph.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            Words.Style = value ? null : (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        }
    }

    private void Check_Click(object sender, RoutedEventArgs e) => _ = ClaudeUpdateCenter.CheckNowAsync();
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
        var line = ClaudeUpdateCenter.Line;
        Words.Inlines.Clear();
        const string name = "Claude Desktop";
        // In the sidebar the name stands on a line of its own, as on the Mac, rather
        // than the status breaking wherever the narrow width runs out.
        if (Stacked && line.StartsWith(name + " ", StringComparison.Ordinal))
        {
            Words.MaxLines = 3;
            Words.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            Words.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
            // The hold on updates is the tooltip's to say; the line keeps to the version.
            var rest = line[(name.Length + 1)..].TrimStart('·', ' ');
            var held = rest.IndexOf(" · updates when you ask", StringComparison.Ordinal);
            Words.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = held < 0 ? rest : rest[..held] });
        }
        else Words.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = line });
        ToolTipService.SetToolTip(Words, line);
        Words.Foreground = (Brush)Application.Current.Resources[ClaudeUpdateCenter.Alarming
            ? "SystemFillColorCriticalBrush" : _stacked ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
        var busy = phase is ClaudeUpdateCenter.Phase.Installing or ClaudeUpdateCenter.Phase.Restarting or ClaudeUpdateCenter.Phase.Checking;
        // In the sidebar the spinner takes the check button's place rather than pushing the words over.
        Busy.IsActive = busy && !_stacked;
        Busy.Visibility = busy && !_stacked ? Visibility.Visible : Visibility.Collapsed;
        CheckBusy.IsActive = busy && _stacked;
        CheckBusy.Visibility = busy && _stacked ? Visibility.Visible : Visibility.Collapsed;
        CheckButton.Visibility = _stacked && !busy && phase is not ClaudeUpdateCenter.Phase.Store ? Visibility.Visible : Visibility.Collapsed;
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
