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
        AutoStartSwitch.IsOn = AutoStart.IsEnabled();
        StartHiddenSwitch.IsOn = current.StartHidden;
        _current = current;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null
            ? "Claude Graft"
            : $"Claude Graft {version.Major}.{version.Minor}.{version.Build}";

        ReflectUpdate();
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

    // Unlike the flyout, which stays quiet like the Mac dropdown, the settings
    // page reports the outcome in words: a person who opened it and pressed is
    // owed an answer.
    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not null)
        {
            CheckButton.IsEnabled = false;
            CheckButton.Content = "Downloading…";
            try { await App.Instance.InstallUpdateAsync(_update); }
            catch { ReflectUpdate(); ShowStatus("The update could not be downloaded."); }
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
        StartHidden = StartHiddenSwitch.IsOn,
        // Set from the main card rather than here, but a save must not drop it.
        KeepMainWarm = _current.KeepMainWarm,
    };

    /// Whether the person asked to start with Windows — applied to the Startup
    /// folder separately from the settings file.
    public bool AutoStartEnabled => AutoStartSwitch.IsOn;
}
