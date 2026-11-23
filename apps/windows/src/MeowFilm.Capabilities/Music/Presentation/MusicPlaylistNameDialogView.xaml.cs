using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public partial class MusicPlaylistNameDialogView : UserControl, INotifyPropertyChanged
{
    private string playlistName;
    private bool isInputEnabled = true;

    public string PlaylistName
    {
        get => playlistName;
        set
        {
            string normalized = value ?? "";
            if (playlistName == normalized) return;
            playlistName = normalized;
            PropertyChanged?.Invoke(this, new(nameof(PlaylistName)));
            PropertyChanged?.Invoke(this, new(nameof(CharacterCountText)));
            NameChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string PlaceholderText { get; }
    public bool ShowCharacterCount { get; }
    public string CharacterCountText => playlistName.Length.ToString(CultureInfo.InvariantCulture);

    public bool IsInputEnabled
    {
        get => isInputEnabled;
        set
        {
            if (isInputEnabled == value) return;
            isInputEnabled = value;
            PropertyChanged?.Invoke(this, new(nameof(IsInputEnabled)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal event EventHandler? NameChanged;

    internal MusicPlaylistNameDialogView(string initialName = "", string placeholderText = "", bool showCharacterCount = false)
    {
        playlistName = initialName ?? "";
        PlaceholderText = placeholderText;
        ShowCharacterCount = showCharacterCount;
        InitializeComponent();
    }

    internal void FocusInput(bool selectAll)
    {
        NameInput.Focus(FocusState.Programmatic);
        if (selectAll) NameInput.SelectAll();
    }
}
