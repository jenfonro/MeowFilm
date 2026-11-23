using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class MusicAppearanceView : UserControl
{
    private bool active;
    public MusicSettingsController Settings { get; }

    public MusicAppearanceView(MusicSettingsController settings)
    {
        Settings = settings;
        InitializeComponent();
        PaletteSelection.Presets = MusicAppearancePalette.Options
            .Select(option => new ColorPreset(option.Key, option.Name, option.Color)).ToArray();
        _ = DesktopVisual.ObserveVisibility(this, async _ => await UpdateActivation());
    }

    private async Task UpdateActivation()
    {
        bool next = IsLoaded && DesktopVisual.IsVisible(this);
        if (active == next) return;
        active = next;
        if (active) await Settings.EnsureAppearanceAsync();
        else Settings.RetireAppearanceSettings();
    }

    private async void Choice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !DesktopVisual.IsVisible(this) || sender is not ComboBox { Tag: string setting, SelectedValue: string value } picker) return;
        await Settings.SetAppearanceAsync(setting, value);
        picker.SelectedValue = setting == "colorMode" ? Settings.AppearanceColorMode : Settings.AppearancePlaylistView;
    }

    private async void Palette_Committed(object? sender, ColorSelectionEventArgs e)
    {
        await Settings.SetAppearanceAsync("colorPalette", e.Value);
        PaletteSelection.RestoreSelection();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await Settings.ReloadAppearanceAsync();
}
