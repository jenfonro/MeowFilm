using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Capabilities;

public partial class FilmSettingsView
{
    private CancellationTokenSource? metadataOperation;
    private bool metadataLoaded, metadataBusy, metadataFailed, replacingMetadata, clearApiKey, clearAccessToken;
    public bool HasMetadataSettings => metadata is not null;
    public bool MetadataLoaded => metadataLoaded;
    public bool MetadataBusy => metadataBusy;
    public bool MetadataIdle => !metadataBusy;
    public bool MetadataFailed => metadataFailed;
    public bool CanEditMetadata => metadataLoaded && !metadataBusy;
    public string ApiKeyState => clearApiKey ? "保存后清除" : metadata?.Settings?.ApiKeyConfigured == true ? "已配置" : "未配置";
    public string AccessTokenState => clearAccessToken ? "保存后清除" : metadata?.Settings?.AccessTokenConfigured == true ? "已配置" : "未配置";

    private void MetadataStateChanged()
    {
        foreach (string name in new[] { nameof(MetadataLoaded), nameof(MetadataBusy), nameof(MetadataIdle),
            nameof(MetadataFailed), nameof(CanEditMetadata), nameof(ApiKeyState), nameof(AccessTokenState) }) Changed(name);
    }
    private void ReplaceMetadata(FilmMetadataSettings value)
    {
        replacingMetadata = true;
        try
        {
            MetadataApiBase.Text = value.ApiBase;
            MetadataImageBase.Text = value.ImageBase;
            MetadataApiKey.Clear(); MetadataAccessToken.Clear();
            clearApiKey = clearAccessToken = false;
            metadataLoaded = true;
        }
        finally { replacingMetadata = false; }
    }
    private Task LoadMetadataAsync() => RunMetadataAsync(saving: false);
    private async Task RunMetadataAsync(bool saving)
    {
        if (!Active || metadata is null || MetadataBusy || saving && !CanEditMetadata) return;
        using var operation = new CancellationTokenSource();
        metadataOperation = operation;
        long ticket = activation;
        metadataBusy = true; metadataFailed = false; Notices.Message = "";
        MetadataStateChanged();
        try
        {
            FilmMetadataSettings result;
            if (saving)
            {
                var patch = new Dictionary<string, object?>
                {
                    ["apiBase"] = MetadataApiBase.Text,
                    ["imageBase"] = MetadataImageBase.Text
                };
                string key = MetadataApiKey.Password;
                string accessToken = MetadataAccessToken.Password;
                if (clearApiKey) patch["apiKey"] = "";
                else if (!string.IsNullOrWhiteSpace(key)) patch["apiKey"] = key;
                if (clearAccessToken) patch["accessToken"] = "";
                else if (!string.IsNullOrWhiteSpace(accessToken)) patch["accessToken"] = accessToken;
                try { result = await metadata.SaveSettingsAsync(patch, operation.Token); }
                finally { patch.Clear(); }
                // The runtime acknowledged this write even if its initiating view has since left.
                ReplaceMetadata(result);
            }
            else
            {
                result = await metadata.GetSettingsAsync(operation.Token);
                if (Active && ticket == activation) ReplaceMetadata(result);
            }
            if (Active && ticket == activation && saving) Notify("TMDB 设置已保存");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch
        {
            if (Active && ticket == activation)
            {
                metadataFailed = !metadataLoaded;
                Notify("TMDB 操作失败");
            }
        }
        finally
        {
            metadataOperation = null; metadataBusy = false; MetadataStateChanged();
        }
        if (Active && ticket != activation) await ActivateCurrentAsync();
    }
    private async void MetadataSave_Click(object sender, RoutedEventArgs e) => await RunMetadataAsync(saving: true);
    private async void MetadataRetry_Click(object sender, RoutedEventArgs e) => await LoadMetadataAsync();
    private void MetadataCredential_Changed(object sender, RoutedEventArgs e)
    {
        if (replacingMetadata) return;
        if (ReferenceEquals(sender, MetadataApiKey)) clearApiKey = false; else clearAccessToken = false;
        MetadataStateChanged();
    }
    private void MetadataClear_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditMetadata || sender is not Button { Tag: string key }) return;
        replacingMetadata = true;
        try
        {
            if (key == "apiKey") { MetadataApiKey.Clear(); clearApiKey = true; }
            else { MetadataAccessToken.Clear(); clearAccessToken = true; }
        }
        finally { replacingMetadata = false; }
        MetadataStateChanged();
    }
}
