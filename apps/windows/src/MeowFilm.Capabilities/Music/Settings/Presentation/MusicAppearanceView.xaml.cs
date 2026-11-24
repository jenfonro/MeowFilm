using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class MusicAppearanceView : UserControl
{
    private IDisposable? visibility;
    private bool active;
    public MusicSettingsController Settings { get; }

    public MusicAppearanceView(MusicSettingsController settings)
    {
        Settings = settings;
        InitializeComponent();
        PaletteSelection.Presets = MusicAppearancePalette.Options
            .Select(option => new ColorPreset(option.Key, option.Name, option.Color)).ToArray();
        Loaded += (_, _) =>
        {
            visibility?.Dispose();
            visibility = DesktopVisual.ObserveVisibility(this, visible => _ = UpdateActivation());
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            visibility?.Dispose();
            visibility = null;
            _ = UpdateActivation();
        };
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
        try
        {
            await Settings.SetAppearanceAsync(setting, value);
            if (IsLoaded) picker.SelectedValue = setting == "colorMode" ? Settings.AppearanceColorMode : Settings.AppearancePlaylistView;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsLoaded) _ = Settings.Notify(MusicRuntimeContext.ErrorText(error));
        }
    }

    private async void Palette_Committed(object? sender, ColorSelectionEventArgs e)
    {
        try
        {
            await Settings.SetAppearanceAsync("colorPalette", e.Value);
            if (IsLoaded) PaletteSelection.RestoreSelection();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsLoaded) _ = Settings.Notify(MusicRuntimeContext.ErrorText(error));
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        try { await Settings.ReloadAppearanceAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsLoaded) _ = Settings.Notify(MusicRuntimeContext.ErrorText(error));
        }
    }
}
