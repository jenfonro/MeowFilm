using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Capabilities;

public partial class MusicHomeSourceSettingsView : UserControl
{
    private readonly MusicCapability module;
    private CancellationTokenSource? activation;
    public MusicHomeSourceSettingsView(MusicCapability module)
    {
        this.module = module;
        InitializeComponent();
        DataContext = module;
        Loaded += async (_, _) => await UpdateActivation();
        Unloaded += async (_, _) => await UpdateActivation();
        IsVisibleChanged += async (_, _) => { if (IsLoaded) await UpdateActivation(); };
    }
    private async Task UpdateActivation()
    {
        if (!IsLoaded || !IsVisible)
        {
            activation?.Cancel();
            activation = null;
            await module.DeactivateCatalogSourceSettingsAsync();
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
        if (!IsLoaded || !IsVisible || !module.CanEditCatalogSource ||
            sender is not ComboBox { SelectedValue: string platform } picker ||
            platform == module.CatalogSourcePlatform) return;
        await module.SetCatalogSourceAsync(platform);
        picker.GetBindingExpression(ComboBox.SelectedValueProperty)?.UpdateTarget();
    }
    private async void CatalogSourceRetry_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && IsVisible) await module.ReadCatalogSourceSettingsAsync();
    }
}
