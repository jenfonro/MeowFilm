using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public partial class FilmSettingsView
{
    private CancellationTokenSource? metadataOperation;
    private FilmDoubanSettings? savedDouban;
    private bool metadataLoaded, metadataBusy, metadataFailed;
    public bool HasMetadataSettings => metadata is not null;
    public bool MetadataLoaded => metadataLoaded;
    public bool MetadataBusy => metadataBusy;
    public bool MetadataIdle => !metadataBusy;
    public bool MetadataFailed => metadataFailed;
    public sealed record MetadataRoute(string Id, string Label, string Base, string Proxy = "");
    public IReadOnlyList<MetadataRoute> DoubanDataRoutes => FilmSettingsLogic.DoubanDataRoutes;
    public IReadOnlyList<MetadataRoute> DoubanImageRoutes => FilmSettingsLogic.DoubanImageRoutes;
    private void MetadataStateChanged()
    {
        foreach (string name in new[] { nameof(MetadataLoaded), nameof(MetadataBusy), nameof(MetadataIdle),
            nameof(MetadataFailed) }) Changed(name);
    }
    private void ReplaceMetadata(FilmMetadataSettings value)
    {
        MetadataApiBase.Text = value.ApiBase == FilmMetadataSettingsStore.TmdbDefaults.ApiBase ? "" : value.ApiBase;
        MetadataImageBase.Text = value.ImageBase == FilmMetadataSettingsStore.TmdbDefaults.ImageBase ? "" : value.ImageBase;
        MetadataApiToken.Text = value.ApiToken;
        MetadataLanguage.Text = value.Language;
        MetadataRegion.Text = value.Region;
        MetadataIncludeAdult.IsChecked = value.IncludeAdult;
    }
    private void ReplaceDouban(FilmDoubanSettings value)
    {
        savedDouban = value;
        DoubanDataProxy.SelectedValue = FilmSettingsLogic.ResolveDoubanDataRouteId(value);
        DoubanImageProxy.SelectedValue = FilmSettingsLogic.ResolveDoubanImageRouteId(value);
        DoubanDataAddress.Text = value.ApiProxy;
        DoubanImageAddress.Text = value.ImageProxy;
        DoubanCookie.Text = value.Cookie;
    }
    private void DoubanRoute_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DoubanDataAddressField is null || DoubanImageAddressField is null) return;
        DoubanDataAddressField.Visibility = Equals(DoubanDataProxy.SelectedValue, "custom") ? Visibility.Visible : Visibility.Collapsed;
        DoubanImageAddressField.Visibility = Equals(DoubanImageProxy.SelectedValue, "custom") ? Visibility.Visible : Visibility.Collapsed;
    }
    private Task LoadMetadataAsync() => RunMetadataAsync(null);
    private async Task RunMetadataAsync(string? provider)
    {
        if (!Active || metadata is null || MetadataBusy || provider is not null && !MetadataLoaded) return;
        using var operation = new CancellationTokenSource();
        metadataOperation = operation;
        long ticket = activation;
        metadataBusy = true; metadataFailed = false;
        MetadataStateChanged();
        try
        {
            if (provider == "tmdb")
            {
                var patch = FilmSettingsLogic.BuildTmdbPatch(
                    MetadataApiBase.Text, MetadataImageBase.Text, MetadataApiToken.Text,
                    MetadataLanguage.Text, MetadataRegion.Text, MetadataIncludeAdult.IsChecked == true);
                var saved = await metadata.SaveSettingsAsync(patch, operation.Token);
                if (Active && ticket == activation) ReplaceMetadata(saved);
                else metadataLoaded = false;
            }
            else if (provider == "douban")
            {
                var patch = FilmSettingsLogic.BuildDoubanPatch(
                    savedDouban,
                    DoubanDataProxy.SelectedItem as MetadataRoute, DoubanDataAddress.Text,
                    DoubanImageProxy.SelectedItem as MetadataRoute, DoubanImageAddress.Text,
                    DoubanCookie.Text);
                var saved = await metadata.SaveDoubanSettingsAsync(patch, operation.Token);
                if (Active && ticket == activation) ReplaceDouban(saved);
                else metadataLoaded = false;
            }
            else
            {
                var tmdbRead = metadata.GetSettingsAsync(operation.Token);
                var doubanRead = metadata.GetDoubanSettingsAsync(operation.Token);
                await Task.WhenAll(tmdbRead, doubanRead);
                if (Active && ticket == activation)
                {
                    ReplaceMetadata(await tmdbRead); ReplaceDouban(await doubanRead);
                    metadataLoaded = true;
                }
            }
            if (Active && ticket == activation && provider is not null) Notify("设置已保存");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch
        {
            if (Active && ticket == activation)
            {
                metadataFailed = !metadataLoaded;
                Notify("数据源设置操作失败");
            }
        }
        finally
        {
            metadataOperation = null; metadataBusy = false; MetadataStateChanged();
        }
        if (Active && ticket != activation) await ActivateCurrentGuardedAsync();
    }
    private async void MetadataSave_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string provider }) await RunMetadataAsync(provider); }
    private async void MetadataRetry_Click(object sender, RoutedEventArgs e) => await LoadMetadataAsync();
}
