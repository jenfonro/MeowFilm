using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Capabilities;

public partial class FilmSettingsView
{
    private CancellationTokenSource? metadataOperation;
    private bool metadataLoaded, metadataBusy, metadataFailed;
    public bool HasMetadataSettings => metadata is not null;
    public bool MetadataLoaded => metadataLoaded;
    public bool MetadataBusy => metadataBusy;
    public bool MetadataIdle => !metadataBusy;
    public bool MetadataFailed => metadataFailed;
    public bool CanEditMetadata => metadataLoaded && !metadataBusy;
    public sealed record MetadataRoute(string Id, string Label, string Base, string Proxy = "");
    public IReadOnlyList<MetadataRoute> DoubanDataRoutes { get; } =
    [
        new("direct", "无", "https://m.douban.com"),
        new("cors", "Cors Proxy By Zwei", "https://m.douban.com", "https://ciao-cors.is-an.org/"),
        new("cdn-tx", "豆瓣 CDN By CMLiussss（腾讯云）", "https://m.douban.cmliussss.net"),
        new("cdn-ali", "豆瓣 CDN By CMLiussss（阿里云）", "https://m.douban.cmliussss.com"),
        new("custom", "自定义代理", "https://m.douban.com")
    ];
    public IReadOnlyList<MetadataRoute> DoubanImageRoutes { get; } =
    [
        new("direct", "无", "https://img3.doubanio.com"),
        new("cdn-tx", "豆瓣 CDN By CMLiussss（腾讯云）", "https://img.doubanio.cmliussss.net"),
        new("cdn-ali", "豆瓣 CDN By CMLiussss（阿里云）", "https://img.doubanio.cmliussss.com"),
        new("custom", "自定义代理", "https://img3.doubanio.com")
    ];
    private void MetadataStateChanged()
    {
        foreach (string name in new[] { nameof(MetadataLoaded), nameof(MetadataBusy), nameof(MetadataIdle),
            nameof(MetadataFailed), nameof(CanEditMetadata) }) Changed(name);
    }
    private void ReplaceMetadata(FilmMetadataSettings value)
    {
        MetadataApiBase.Text = value.ApiBase == "https://api.themoviedb.org/3" ? "" : value.ApiBase;
        MetadataImageBase.Text = value.ImageBase == "https://image.tmdb.org" ? "" : value.ImageBase;
        MetadataApiToken.Text = value.ApiToken;
        MetadataLanguage.Text = value.Language;
        MetadataRegion.Text = value.Region;
        MetadataIncludeAdult.IsChecked = value.IncludeAdult;
    }
    private void ReplaceDouban(FilmDoubanSettings value)
    {
        DoubanDataProxy.SelectedValue = DoubanDataRoutes.FirstOrDefault(route => route.Id != "custom" &&
            route.Base == value.ApiBase && route.Proxy == value.ApiProxy)?.Id ?? "custom";
        DoubanImageProxy.SelectedValue = DoubanImageRoutes.FirstOrDefault(route => route.Id != "custom" &&
            route.Base == value.ImageBase && route.Proxy == value.ImageProxy)?.Id ?? "custom";
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
        if (!Active || metadata is null || MetadataBusy || provider is not null && !CanEditMetadata) return;
        using var operation = new CancellationTokenSource();
        metadataOperation = operation;
        long ticket = activation;
        metadataBusy = true; metadataFailed = false; Notices.Message = "";
        MetadataStateChanged();
        try
        {
            if (provider == "tmdb")
            {
                var patch = new Dictionary<string, object?>
                {
                    ["apiBase"] = MetadataApiBase.Text.Trim(), ["imageBase"] = MetadataImageBase.Text.Trim(),
                    ["apiKey"] = MetadataApiToken.Text, ["accessToken"] = "",
                    ["language"] = MetadataLanguage.Text, ["region"] = MetadataRegion.Text,
                    ["includeAdult"] = MetadataIncludeAdult.IsChecked == true
                };
                var saved = await metadata.SaveSettingsAsync(patch, operation.Token);
                if (Active && ticket == activation) ReplaceMetadata(saved);
                else metadataLoaded = false;
            }
            else if (provider == "douban")
            {
                var data = (MetadataRoute)DoubanDataProxy.SelectedItem;
                var image = (MetadataRoute)DoubanImageProxy.SelectedItem;
                var patch = new Dictionary<string, object?>
                {
                    ["apiBase"] = data.Base, ["searchBase"] = "https://search.douban.com",
                    ["apiProxy"] = data.Id == "custom" ? DoubanDataAddress.Text.Trim() : data.Proxy,
                    ["imageBase"] = image.Base,
                    ["imageProxy"] = image.Id == "custom" ? DoubanImageAddress.Text.Trim() : image.Proxy,
                    ["cookie"] = DoubanCookie.Text
                };
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
        if (Active && ticket != activation) await ActivateCurrentAsync();
    }
    private async void MetadataSave_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string provider }) await RunMetadataAsync(provider); }
    private async void MetadataRetry_Click(object sender, RoutedEventArgs e) => await LoadMetadataAsync();
}
