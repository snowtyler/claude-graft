using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClaudeGraft;

/// One icon in the picker, named as a screen reader reads it.
public sealed class IconChoice(string preset, string? previewFile)
{
    public string Preset { get; } = preset;
    public string Title { get; } = IconPreset.Title(preset);
    public BitmapImage? Preview { get; } = previewFile is null ? null : new BitmapImage(new Uri(previewFile));
    public override string ToString() => Title;
}

/// One selectable source, with the label a person reads.
public sealed class SourceOption
{
    public required string Label { get; init; }
    public required ShortcutSource Source { get; init; }
}

/// <summary>
/// A Claude's name, icon, folder and where it reads chats, edited on its page.
/// A new one is a draft until Create; an existing one changes only on Save.
/// Saving an invalid folder is refused, and choosing a source warns first that
/// merging two histories cannot be undone.
/// </summary>
public sealed partial class ProfileEditor : UserControl
{
    /// The edited copy and whether it is new, once Save or Create is pressed.
    public event Action<Shortcut, Shortcut, bool>? Saved;
    public event Action<Shortcut>? DeleteRequested;
    public event Action? Discarded;

    private Shortcut? _original;
    private Shortcut _shortcut = Shortcut.New("");
    private bool _isNew;
    private bool _folderEdited;
    private bool _filling;

    public ProfileEditor()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ShowIcons();
    }

    /// An existing Claude, or a new draft when null.
    public void Edit(Shortcut? existing)
    {
        _original = existing;
        _isNew = existing is null;
        _shortcut = existing?.Copy() ?? Shortcut.New(App.Store.UniqueName());
        if (_isNew) _shortcut.IconPreset = App.Store.NextIconPreset();
        _folderEdited = !_isNew;
        Fill();
    }

    private void Fill()
    {
        _filling = true;
        NameBox.Text = _shortcut.Name;
        FolderBox.Text = _shortcut.Folder;
        var options = App.Store.AvailableSources(_shortcut)
            .Select(s => new SourceOption { Label = App.Store.Label(s), Source = s }).ToList();
        SourceBox.ItemsSource = options;
        SourceBox.SelectedItem = options.FirstOrDefault(o => o.Source.Equals(_shortcut.Source)) ?? options.FirstOrDefault();
        MergeNote.IsOpen = _shortcut.Source.Kind != SourceKind.Own;
        Problem.IsOpen = false;
        _filling = false;
        DeleteButton.Visibility = _isNew ? Visibility.Collapsed : Visibility.Visible;
        SaveText.Text = _isNew ? "Create" : "Save";
        RevertButton.Content = _isNew ? "Discard" : "Revert";
        AutomationProperties.SetName(SaveButton, _isNew ? "Create Claude" : "Save Changes");
        AutomationProperties.SetName(RevertButton, _isNew ? "Discard New Claude" : "Revert Changes");
        if (IconGrid.ItemsSource is IReadOnlyList<IconChoice> choices) ShowIcon(choices);
        Changed();
    }

    private bool _busy;

    /// While a save moves files and makes the shortcut, which can take seconds:
    /// the button spins, and nothing here can start a second one.
    public bool Busy
    {
        get => _busy;
        set
        {
            _busy = value;
            SaveText.Opacity = value ? 0 : 1;
            SaveRing.IsActive = value;
            SaveRing.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            DeleteButton.IsEnabled = !value;
            Changed();
        }
    }

    /// Save and Revert wait for something to save or revert; a draft can always
    /// be created or discarded.
    private void Changed()
    {
        if (_busy)
        {
            SaveButton.IsEnabled = RevertButton.IsEnabled = false;
            return;
        }
        var dirty = _isNew || _original is null
            || NameBox.Text.Trim() != _original.Name || FolderBox.Text.Trim() != _original.Folder
            || !_shortcut.Source.Equals(_original.Source) || _shortcut.IconPreset != _original.IconPreset;
        SaveButton.IsEnabled = dirty;
        RevertButton.IsEnabled = dirty;
    }

    private static IReadOnlyDictionary<string, string?>? _previews;

    private async Task ShowIcons()
    {
        _previews ??= await Task.Run(ShortcutIcons.Previews);
        var choices = IconPreset.All.Select(p => new IconChoice(p, _previews.GetValueOrDefault(p))).ToList();
        IconGrid.ItemsSource = choices;
        ShowIcon(choices);
    }

    private void ShowIcon(IReadOnlyList<IconChoice> choices)
    {
        var current = choices.FirstOrDefault(c => c.Preset == IconPreset.Normalize(_shortcut.IconPreset));
        IconGrid.SelectedItem = current;
        IconImage.Source = current?.Preview;
        AutomationProperties.SetItemStatus(IconButton, current?.Title ?? "");
    }

    private void IconGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not IconChoice choice) return;
        _shortcut.IconPreset = choice.Preset;
        ShowIcon((IReadOnlyList<IconChoice>)IconGrid.ItemsSource);
        IconFlyout.Hide();
        Changed();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_filling) return;
        // Until the folder is typed by hand, a new Claude's name keeps rewriting it.
        if (_isNew && !_folderEdited)
        {
            _filling = true;
            FolderBox.Text = Shortcut.FolderName(NameBox.Text);
            _filling = false;
        }
        Changed();
    }

    private void FolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_filling) return;
        _folderEdited = true;
        Changed();
    }

    private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceBox.SelectedItem is not SourceOption option) return;
        _shortcut.Source = option.Source;
        MergeNote.IsOpen = option.Source.Kind != SourceKind.Own;
        if (!_filling) Changed();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var name = NameBox.Text.Trim();
        var folder = FolderBox.Text.Trim();
        if (name.Length == 0) { Reject("The Claude needs a name."); return; }
        if (Graft.ValidateFolder(folder) is string problem) { Reject(problem); return; }
        Problem.IsOpen = false;
        var result = _shortcut.Copy();
        result.Name = name;
        result.Folder = folder;
        Saved?.Invoke(_original ?? result, result, _isNew);
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_isNew) Discarded?.Invoke();
        else Edit(_original);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_original is not null) DeleteRequested?.Invoke(_original);
    }

    private void Reject(string message)
    {
        Problem.Message = message;
        Problem.IsOpen = true;
    }
}
