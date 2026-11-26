using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public partial class MusicHomeSourceSettingsView : UserControl
{
    private IDisposable? visibility;
    private CancellationTokenSource? activation;
    private bool active;
    public MusicSettingsController Settings { get; }

    public MusicHomeSourceSettingsView(MusicSettingsController settings)
    {
        Settings = settings;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Settings.PropertyChanged -= SettingsChanged;
            Settings.PropertyChanged += SettingsChanged;
            visibility?.Dispose();
            visibility = DesktopVisual.ObserveVisibility(this, visible => _ = UpdateActivation());
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            Settings.PropertyChanged -= SettingsChanged;
            visibility?.Dispose();
            visibility = null;
            _ = UpdateActivation();
        };
    }

    private void SettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicSettingsController.CatalogSourceStatus))
            NoticeOutlet.Post(this, Settings.CatalogSourceStatus, (Settings, "catalog-source"));
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
            await Settings.DeactivateCatalogSourceSettingsAsync(retirePage: true);
            return;
        }
        if (activation is not null) return;
        using var current = new CancellationTokenSource();
        activation = current;
        try { await Settings.EnsureCatalogSourceSettingsAsync(current.Token); }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch { /* The source owner already published CatalogSourceStatus and exposes explicit retry. */ }
        finally { if (ReferenceEquals(activation, current)) activation = null; }
    }

    private async void CatalogSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !DesktopVisual.IsVisible(this) || !Settings.CanEditCatalogSource ||
            sender is not ComboBox { SelectedValue: string platform } picker ||
            platform == Settings.CatalogSourcePlatform) return;
        try
        {
            await Settings.SetCatalogSourceAsync(platform);
            if (IsLoaded) picker.SelectedValue = Settings.CatalogSourcePlatform;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsLoaded) _ = Settings.Notify(MusicRuntimeContext.ErrorText(error));
        }
    }

    private async void CatalogSourceRetry_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || !DesktopVisual.IsVisible(this)) return;
        try { await Settings.ReadCatalogSourceSettingsAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsLoaded) _ = Settings.Notify(MusicRuntimeContext.ErrorText(error));
        }
    }
}
