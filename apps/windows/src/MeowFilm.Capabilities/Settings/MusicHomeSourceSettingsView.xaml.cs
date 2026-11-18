using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public partial class MusicHomeSourceSettingsView : UserControl
{
    private readonly MusicCapability module;
    private CancellationTokenSource? activation;
    private bool active;
    public MusicHomeSourceSettingsView(MusicCapability module)
    {
        this.module = module;
        InitializeComponent();
        DataContext = module;
        Loaded += async (_, _) => await UpdateActivation();
        Unloaded += async (_, _) => { active = false; activation?.Cancel(); activation = null; await module.DeactivateCatalogSourceSettingsAsync(retirePage: true); };
        RegisterPropertyChangedCallback(VisibilityProperty, async (_, _) => await UpdateActivation());
        LayoutUpdated += async (_, _) => { if (active != (IsLoaded && DesktopVisual.IsVisible(this))) await UpdateActivation(); };
    }
    private async Task UpdateActivation()
    {
        bool next = IsLoaded && DesktopVisual.IsVisible(this);
        if (next == active) return;
        active = next;
        if (!active)
        {
            activation?.Cancel();
            activation = null;
            await module.DeactivateCatalogSourceSettingsAsync(retirePage: true);
            return;
        }
        if (activation is not null) return;
        using var current = new CancellationTokenSource();
        activation = current;
        try { await module.EnsureCatalogSourceSettingsAsync(current.Token); }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch { /* The source owner publishes restore failure and exposes explicit retry. */ }
        finally { if (ReferenceEquals(activation, current)) activation = null; }
    }
    private async void CatalogSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !DesktopVisual.IsVisible(this) || !module.CanEditCatalogSource ||
            sender is not ComboBox { SelectedValue: string platform } picker ||
            platform == module.CatalogSourcePlatform) return;
        await module.SetCatalogSourceAsync(platform);
        picker.SelectedValue = module.CatalogSourcePlatform;
    }
    private async void CatalogSourceRetry_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && DesktopVisual.IsVisible(this)) await module.ReadCatalogSourceSettingsAsync();
    }
}
