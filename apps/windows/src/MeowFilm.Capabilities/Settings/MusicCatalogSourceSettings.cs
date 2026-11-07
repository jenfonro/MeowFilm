using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private string catalogSourcePlatform = "wy", catalogSourceStatus = "";
    private long catalogSourceRevision;
    private bool catalogSourceLoaded, catalogSourceBusy;
    private Task? catalogSourceRead;
    private Task catalogSourceWork = Task.CompletedTask;
    private CancellationTokenSource? catalogSourceReadCancellation, catalogSourceSaveCancellation;

    public MusicCatalogChoice[] CatalogSourceChoices { get; } =
        [new("wy", "网易云音乐"), new("kg", "酷狗音乐")];
    public string CatalogSourcePlatform => catalogSourcePlatform;
    public string CatalogSourceStatus => catalogSourceStatus;
    public bool HasCatalogSourceError => catalogSourceStatus.Length > 0;
    public bool CanEditCatalogSource => !stopping && commands == 0 && catalogSourceLoaded && !catalogSourceBusy;
    public bool CanReadCatalogSource => !stopping && commands == 0 && !catalogSourceBusy;

    private void CatalogSourceBindings()
    {
        foreach (var name in new[] { nameof(CatalogSourcePlatform), nameof(CatalogSourceStatus),
            nameof(HasCatalogSourceError), nameof(CanEditCatalogSource), nameof(CanReadCatalogSource) })
            Changed(name);
    }

    private void AcceptCatalogSourceSettings(JsonElement reply)
    {
        var value = reply.GetProperty("settings");
        MusicDto.Require(MusicDto.Integer(value, "schema") == 1);
        var revision = MusicDto.Integer(value, "revision");
        var platform = MusicDto.Text(value, "platform");
        MusicDto.Require(revision >= 0 && platform is "wy" or "kg");
        var changed = platform != catalogSourcePlatform;
        catalogSourcePlatform = platform;
        catalogSourceRevision = revision;
        catalogSourceLoaded = true;
        catalogSourceStatus = "";
        if (changed) RetireCatalogSourcePages();
        CatalogSourceBindings();
    }

    internal Task EnsureCatalogSourceSettingsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (catalogSourceLoaded) return Task.CompletedTask;
        if (catalogSourceRead is null)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            catalogSourceReadCancellation = cancellation;
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            loadDrains.Add(drained.Task);
            catalogSourceRead = ReadAsync();
            _ = ObserveReadAsync(catalogSourceRead);
            static async Task ObserveReadAsync(Task read)
            {
                try { await read; }
                catch { /* All cancelled waiters may have left before the physical read fails. */ }
            }
            async Task ReadAsync()
            {
                await Task.Yield(); // Publish the shared read before any port can complete synchronously.
                try
                {
                    var reply = await CallSettled("music.catalogSource.settings.get", cancellationToken: cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    AcceptCatalogSourceSettings(reply);
                }
                catch
                {
                    catalogSourceLoaded = false;
                    if (!stopping)
                    {
                        catalogSourceStatus = "数据源设置读取失败";
                        CatalogSourceBindings();
                    }
                    throw;
                }
                finally
                {
                    if (ReferenceEquals(catalogSourceReadCancellation, cancellation))
                    {
                        catalogSourceReadCancellation = null;
                        catalogSourceRead = null;
                    }
                    cancellation.Dispose();
                    loadDrains.Remove(drained.Task);
                    drained.TrySetResult();
                }
            }
        }
        return catalogSourceRead.WaitAsync(token);
    }

    public Task ReadCatalogSourceSettingsAsync()
    {
        if (!CanReadCatalogSource) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            catalogSourceLoaded = false;
            try { await EnsureCatalogSourceSettingsAsync(lifetime.Token); }
            catch { /* The setting's notice preserves the failed restore; browsing remains gated. */ }
        });
    }

    public Task SetCatalogSourceAsync(string platform)
    {
        if (!CanEditCatalogSource || platform is not ("wy" or "kg") || platform == catalogSourcePlatform)
            return Task.CompletedTask;
        var revision = catalogSourceRevision;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        catalogSourceSaveCancellation = cancellation;
        catalogSourceBusy = true;
        catalogSourceStatus = "";
        CatalogSourceBindings();
        return catalogSourceWork = RunAsync(async () =>
        {
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                AcceptCatalogSourceSettings(await CallSettledReceipt("music.catalogSource.settings.save",
                    new { ifRevision = revision, platform }, cancellation.Token));
            }
            catch (MusicOperationException error) when (error.Code is
                "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" or "PENDING_WRITE" or "REVISION_CONFLICT")
            {
                try
                {
                    AcceptCatalogSourceSettings(await CallSettled("music.catalogSource.settings.get",
                        cancellationToken: lifetime.Token));
                    if (catalogSourcePlatform != platform)
                        catalogSourceStatus = error.Code == "REVISION_CONFLICT"
                            ? "数据源设置已更新" : "数据源设置保存失败";
                }
                catch
                {
                    catalogSourceLoaded = false;
                    catalogSourceStatus = "数据源设置保存失败";
                    RetireCatalogSourcePages();
                }
            }
            catch (OperationCanceledException) { catalogSourceStatus = "已取消"; }
            catch { catalogSourceStatus = "数据源设置保存失败"; }
            finally
            {
                if (ReferenceEquals(catalogSourceSaveCancellation, cancellation))
                    catalogSourceSaveCancellation = null;
                cancellation.Dispose();
                catalogSourceBusy = false;
                CatalogSourceBindings();
            }
        });
    }

    internal async Task DeactivateCatalogSourceSettingsAsync()
    {
        catalogSourceSaveCancellation?.Cancel();
        await catalogSourceWork;
    }
}
