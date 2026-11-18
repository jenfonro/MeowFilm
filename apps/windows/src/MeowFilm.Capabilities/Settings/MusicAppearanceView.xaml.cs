using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class MusicAppearanceView : UserControl
{
    private readonly MusicCapability module;
    private bool active;
    public MusicAppearanceView(MusicCapability module)
    {
        this.module = module;
        InitializeComponent();
        PaletteSelection.Presets = MusicAppearancePalette.Options
            .Select(option => new ColorPreset(option.Key, option.Name, option.Color)).ToArray();
        DataContext = module;
        Loaded += async (_, _) => await UpdateActivation();
        Unloaded += (_, _) => { active = false; module.RetireAppearanceSettings(); };
        RegisterPropertyChangedCallback(VisibilityProperty, async (_, _) => await UpdateActivation());
        LayoutUpdated += async (_, _) => { if (active != (IsLoaded && DesktopVisual.IsVisible(this))) await UpdateActivation(); };
    }
    private async Task UpdateActivation()
    {
        bool next = IsLoaded && DesktopVisual.IsVisible(this);
        if (active == next) return;
        active = next;
        if (active) await module.EnsureAppearanceAsync();
        else module.RetireAppearanceSettings();
    }

    private async void Choice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !DesktopVisual.IsVisible(this) || sender is not ComboBox { Tag: string setting, SelectedValue: string value } picker) return;
        await module.SetAppearanceAsync(setting, value);
        picker.SelectedValue = setting == "colorMode" ? module.AppearanceColorMode : module.AppearancePlaylistView;
    }
    private async void Palette_Committed(object? sender, ColorSelectionEventArgs e)
    {
        await module.SetAppearanceAsync("colorPalette", e.Value);
        PaletteSelection.RestoreSelection();
    }
    private async void Retry_Click(object sender, RoutedEventArgs e) => await module.ReloadAppearanceAsync();
}
