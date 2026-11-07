using System.Windows;
using System.Windows.Controls;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class MusicAppearanceView : UserControl
{
    private readonly MusicCapability module;
    public MusicAppearanceView(MusicCapability module)
    {
        this.module = module;
        InitializeComponent();
        PaletteSelection.Presets = MusicAppearancePalette.Options
            .Select(option => new ColorPreset(option.Key, option.Name, option.Color)).ToArray();
        DataContext = module;
        Loaded += async (_, _) => { if (IsVisible) await module.EnsureAppearanceAsync(); };
        IsVisibleChanged += async (_, _) => { if (IsLoaded && IsVisible) await module.EnsureAppearanceAsync(); };
    }
    private async void Choice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !IsVisible || sender is not ComboBox { Tag: string setting, SelectedValue: string value } picker) return;
        await module.SetAppearanceAsync(setting, value);
        picker.GetBindingExpression(ComboBox.SelectedValueProperty)?.UpdateTarget();
    }
    private async void Palette_Committed(object? sender, ColorSelectionEventArgs e)
    {
        await module.SetAppearanceAsync("colorPalette", e.Value);
        PaletteSelection.RestoreSelection();
    }
    private async void Retry_Click(object sender, RoutedEventArgs e) => await module.ReloadAppearanceAsync();
}
