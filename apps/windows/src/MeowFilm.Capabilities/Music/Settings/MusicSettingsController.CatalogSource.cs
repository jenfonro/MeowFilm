using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public sealed partial class MusicSettingsController
{
    private string catalogSourcePlatform = "wy", catalogSourceStatus = "";
    private long catalogSourceRevision, catalogSourceGeneration;
    private bool catalogSourceLoaded, catalogSourceBusy;
    private Task? catalogSourceReadTask;
    private Task catalogSourceWork = Task.CompletedTask;
    private CancellationTokenSource? catalogSourceReadCancellation;

    private bool CatalogSourceCurrent(long currentGeneration) =>
        currentGeneration == catalogSourceGeneration && !stopping && !disposed;

    public MusicCatalogChoice[] CatalogSourceChoices { get; } =
        [new("wy", MusicPlatformInfo.DisplayName("wy")), new("kg", MusicPlatformInfo.DisplayName("kg"))];
    public string CatalogSourcePlatform => catalogSourcePlatform;
    public string CatalogSourceStatus => catalogSourceStatus;
    public bool HasCatalogSourceError => catalogSourceStatus.Length > 0;
    public bool CanEditCatalogSource => !stopping && commands == 0 && catalogSourceLoaded && !catalogSourceBusy;
    public bool CanReadCatalogSource => !stopping && commands == 0 && !catalogSourceBusy;
    internal bool CatalogSourceLoaded => catalogSourceLoaded;

    internal void CancelCatalogSourceReadForShutdown() => catalogSourceReadCancellation?.Cancel();

    private void NotifyCatalogSourceBindings()
    {
        Changed(nameof(CatalogSourcePlatform));
        Changed(nameof(CatalogSourceStatus));
        Changed(nameof(HasCatalogSourceError));
        Changed(nameof(CanEditCatalogSource));
        Changed(nameof(CanReadCatalogSource));
    }

    private void AcceptCatalogSource(MusicCatalogSourceRecord value)
    {
        catalogSourcePlatform = value.Platform;
        catalogSourceRevision = value.Revision;
        catalogSourceLoaded = true;
        catalogSourceStatus = "";
        NotifyCatalogSourceBindings();
    }

    internal Task EnsureCatalogSourceSettingsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (catalogSourceLoaded) return Task.CompletedTask;
        if (catalogSourceReadTask is null)
        {
            long currentGeneration = catalogSourceGeneration;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            catalogSourceReadCancellation = cancellation;
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            loadDrains.Add(drained.Task);
            catalogSourceReadTask = ReadAsync();
            _ = ObserveReadAsync(catalogSourceReadTask);
            static async Task ObserveReadAsync(Task read)
            {
                try { await read; }
                catch { /* All cancelled waiters may have left before the physical read fails. */ }
            }
            async Task ReadAsync()
            {
                await Task.Yield();
                try
                {
                    var settings = await application.Library.GetCatalogSourceAsync(cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (CatalogSourceCurrent(currentGeneration)) AcceptCatalogSource(settings);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    if (CatalogSourceCurrent(currentGeneration))
                    {
                        catalogSourceLoaded = false;
                        catalogSourceStatus = "数据源设置读取失败";
                        NotifyCatalogSourceBindings();
                    }
                    throw;
                }
                finally
                {
                    if (ReferenceEquals(catalogSourceReadCancellation, cancellation))
                    {
                        catalogSourceReadCancellation = null;
                        catalogSourceReadTask = null;
                    }
                    cancellation.Dispose();
                    loadDrains.Remove(drained.Task);
                    drained.TrySetResult();
                }
            }
        }
        return catalogSourceReadTask.WaitAsync(token);
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

    public Task SetCatalogSourceAsync(string nextPlatform)
    {
        if (!CanEditCatalogSource || nextPlatform is not ("wy" or "kg") || nextPlatform == catalogSourcePlatform)
            return Task.CompletedTask;
        long expectedRevision = catalogSourceRevision;
        long currentGeneration = catalogSourceGeneration;
        catalogSourceBusy = true;
        catalogSourceStatus = "";
        NotifyCatalogSourceBindings();
        return catalogSourceWork = RunAsync(async () =>
        {
            try
            {
                var saved = await application.Library.SaveCatalogSourceAsync(expectedRevision, nextPlatform, lifetime.Token);
                if (CatalogSourceCurrent(currentGeneration)) AcceptCatalogSource(saved);
            }
            catch (Exception error) when (ErrorCode(error) is "STORAGE_CONFLICT" or "REVISION_CONFLICT")
            {
                if (!CatalogSourceCurrent(currentGeneration)) return;
                try
                {
                    var current = await application.Library.GetCatalogSourceAsync(lifetime.Token);
                    if (!CatalogSourceCurrent(currentGeneration)) return;
                    AcceptCatalogSource(current);
                    if (catalogSourcePlatform != nextPlatform)
                        catalogSourceStatus = ErrorCode(error) == "REVISION_CONFLICT" ? "数据源设置已更新" : "数据源设置保存失败";
                }
                catch
                {
                    if (!CatalogSourceCurrent(currentGeneration)) return;
                    catalogSourceLoaded = false;
                    catalogSourceStatus = "数据源设置保存失败";
                }
            }
            catch (OperationCanceledException) { if (CatalogSourceCurrent(currentGeneration)) catalogSourceStatus = "已取消"; }
            catch { if (CatalogSourceCurrent(currentGeneration)) catalogSourceStatus = "数据源设置保存失败"; }
            finally
            {
                catalogSourceBusy = false;
                if (CatalogSourceCurrent(currentGeneration)) NotifyCatalogSourceBindings();
            }
        });
    }

    internal async Task DeactivateCatalogSourceSettingsAsync(bool retirePage = false)
    {
        catalogSourceGeneration++;
        if (retirePage || catalogSourceBusy || catalogSourceReadTask is not null) catalogSourceLoaded = false;
        catalogSourceReadCancellation?.Cancel();
        catalogSourceReadCancellation = null;
        catalogSourceReadTask = null;
        NotifyCatalogSourceBindings();
        await catalogSourceWork;
    }
}
