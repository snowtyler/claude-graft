using System.Reflection;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

/// <summary>
/// The app's preferences: how it looks, and how it starts. Reads the current
/// settings in and, once Done is pressed, hands back what was chosen for the
/// caller to apply and persist. Auto-start is read from and written to the
/// Startup folder rather than the settings file, so this shows the real state.
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog(GraftSettings current)
    {
        InitializeComponent();

        // The combo order matches the enum order, so the choice round-trips
        // through the selected index without a lookup table.
        ThemeBox.SelectedIndex = (int)current.Theme;
        BackdropBox.SelectedIndex = (int)current.Backdrop;
        // Each scheme beside a dot of its accent, so the list reads before it is tried.
        foreach (var palette in Enum.GetValues<AppPalette>())
        {
            var accent = Palettes.For(palette, dark: true, palette == AppPalette.Accent ? SystemAccent() : null).Accent;
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            item.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, accent.R, accent.G, accent.B)) });
            item.Children.Add(new TextBlock { Text = Palettes.Title(palette) });
            var choice = new ComboBoxItem { Content = item };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(choice, Palettes.Title(palette));
            PaletteBox.Items.Add(choice);
        }
        PaletteBox.SelectedIndex = (int)current.Palette;
        AutoStartSwitch.IsOn = AutoStart.IsEnabled();
        StartHiddenSwitch.IsOn = current.StartHidden;
        HoldUpdatesSwitch.IsOn = current.HoldUpdates;
        // Only an install Graft can update by hand can safely stop updating itself.
        ManualClaudeRow.Visibility = ClaudeDesktopUpdates.IsSquirrel ? Visibility.Visible : Visibility.Collapsed;
        try { ManualClaudeSwitch.IsOn = ManualUpdates.Enabled; }
        catch (IOException) { ManualClaudeSwitch.IsEnabled = false; }
        _current = current;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null
            ? "Claude Graft"
            : $"Claude Graft {version.Major}.{version.Minor}.{version.Build}";

        ReflectUpdate();
        App.UpdateProgressChanged += ReflectDownload;
        Closed += (_, _) => App.UpdateProgressChanged -= ReflectDownload;
        ReflectDownload();
    }

    private readonly GraftSettings _current;
    private Updater.Release? _update;

    private void ReflectUpdate()
    {
        _update = App.AvailableUpdate;
        CheckButton.IsEnabled = true;
        CheckButton.Content = _update is null ? "Check for Updates" : $"Install Update {_update.Version}";
        if (_update is not null) ShowStatus($"Version {_update.Version} is available.");
    }

    private void ReflectDownload()
    {
        if (App.UpdateDownload is not { } fraction) return;
        CheckButton.IsEnabled = false;
        CheckButton.Content = $"Downloading… {(int)Math.Round(fraction * 100)}%";
    }

    // Unlike the flyout, which stays quiet like the Mac dropdown, the settings
    // page reports the outcome in words: a person who opened it and pressed is
    // owed an answer.
    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not null)
        {
            CheckButton.IsEnabled = false;
            CheckButton.Content = "Downloading…";
            if (!await App.Instance.InstallUpdateAsync(_update))
            {
                ReflectUpdate();
                ShowStatus("The update could not be downloaded.");
            }
            return;
        }

        CheckButton.IsEnabled = false;
        CheckButton.Content = "Checking for Updates…";
        ShowStatus(null);
        try
        {
            var found = await App.Instance.RunUpdateCheckAsync();
            ReflectUpdate();
            if (found is null) ShowStatus("You’re up to date.");
        }
        catch
        {
            ReflectUpdate();
            ShowStatus("Couldn’t reach the update server.");
        }
    }

    private static Rgb SystemAccent()
    {
        var c = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        return new Rgb(c.R, c.G, c.B);
    }

    private void ShowStatus(string? text)
    {
        UpdateStatus.Text = text ?? "";
        UpdateStatus.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// The settings as chosen. Read after the dialog closes on Done.
    public GraftSettings Result => new()
    {
        Theme = (AppTheme)ThemeBox.SelectedIndex,
        Backdrop = (BackdropMaterial)BackdropBox.SelectedIndex,
        Palette = (AppPalette)PaletteBox.SelectedIndex,
        StartHidden = StartHiddenSwitch.IsOn,
        HoldUpdates = HoldUpdatesSwitch.IsOn,
        // Set from the main card rather than here, but a save must not drop it.
        KeepMainWarm = _current.KeepMainWarm,
        SharingExplained = _current.SharingExplained,
    };

    /// Manual Claude updates as chosen, or null where the switch was not offered.
    public bool? ManualClaudeUpdates =>
        ManualClaudeRow.Visibility == Visibility.Visible && ManualClaudeSwitch.IsEnabled ? ManualClaudeSwitch.IsOn : null;

    /// Whether the person asked to start with Windows — applied to the Startup
    /// folder separately from the settings file.
    public bool AutoStartEnabled => AutoStartSwitch.IsOn;
}
