using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

/// One selectable source, with the label a person reads.
public sealed class SourceOption
{
    public required string Label { get; init; }
    public required ShortcutSource Source { get; init; }
}

/// <summary>
/// Add or edit one profile: its name, the folder its data lives in, and where it
/// reads its chats from. Blocks Save on an invalid folder and warns, before the
/// fact, that choosing a source merges two histories in a way that cannot be
/// undone.
/// </summary>
public sealed partial class ProfileDialog : ContentDialog, INotifyPropertyChanged
{
    private readonly ShortcutStore _store;
    private readonly Shortcut _shortcut;
    private readonly bool _isNew;
    private bool _folderEdited;

    public ProfileDialog(ShortcutStore store, Shortcut? existing)
    {
        _store = store;
        _isNew = existing is null;
        // Edit a copy, so Cancel leaves the stored one untouched.
        _shortcut = existing is null
            ? Shortcut.New(store.UniqueName())
            : new Shortcut
            {
                Id = existing.Id, Name = existing.Name, Folder = existing.Folder,
                Source = existing.Source, InstalledName = existing.InstalledName,
            };
        _folderEdited = !_isNew;

        InitializeComponent();
        Title = _isNew ? "Add Profile" : "Edit Profile";
        if (!_isNew) SecondaryButtonText = "Delete Profile…";

        _name = _shortcut.Name;
        _folder = _shortcut.Folder;

        var options = store.AvailableSources(_shortcut)
            .Select(s => new SourceOption { Label = store.Label(s), Source = s }).ToList();
        SourceBox.ItemsSource = options;
        SourceBox.SelectedItem = options.FirstOrDefault(o => o.Source.Equals(_shortcut.Source)) ?? options[0];

        PrimaryButtonClick += OnSave;
    }

    // MARK: - Bound fields

    private string _name = "";
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            // Until the folder is typed by hand, a rename keeps rewriting it.
            if (_isNew && !_folderEdited) SetFolderFromName(Shortcut.FolderName(value));
            OnPropertyChanged();
        }
    }

    private string _folder = "";
    public string Folder
    {
        get => _folder;
        set
        {
            if (_folder == value) return;
            // A programmatic rewrite from Name must not count as the user editing.
            if (!_settingFolderFromName) _folderEdited = true;
            _folder = value;
            OnPropertyChanged();
        }
    }

    private bool _settingFolderFromName;
    private void SetFolderFromName(string value)
    {
        _settingFolderFromName = true;
        Folder = value;
        _settingFolderFromName = false;
    }

    // MARK: - Source

    private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceBox.SelectedItem is SourceOption option)
        {
            _shortcut.Source = option.Source;
            MergeNote.IsOpen = option.Source.Kind != SourceKind.Own;
        }
    }

    // MARK: - Save

    /// The shortcut the caller should persist, filled in only after Save.
    public Shortcut Result => _shortcut;
    public bool IsNew => _isNew;

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var name = Name.Trim();
        var folder = Folder.Trim();
        if (name.Length == 0)
        {
            Reject("The profile needs a name.");
            args.Cancel = true;
            return;
        }
        if (Graft.ValidateFolder(folder) is string problem)
        {
            Reject(problem);
            args.Cancel = true;
            return;
        }
        _shortcut.Name = name;
        _shortcut.Folder = folder;
        if (SourceBox.SelectedItem is SourceOption option) _shortcut.Source = option.Source;
    }

    private void Reject(string message)
    {
        Problem.Message = message;
        Problem.IsOpen = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
