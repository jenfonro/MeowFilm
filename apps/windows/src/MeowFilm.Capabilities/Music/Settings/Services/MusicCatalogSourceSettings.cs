using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

/// <summary>
/// Standalone domain handler for music home/channel catalog source platform selection.
/// </summary>
internal sealed class MusicCatalogSourceSettings(MusicRuntimeContext ctx, Action<string> notify)
{
    private string platform = "wy", status = "";
    private long revision, generation;
    private bool loaded, busy;
    private Task? readTask;
    private Task work = Task.CompletedTask;
    private CancellationTokenSource? readCancellation;

    internal event Action? CatalogSourceInvalidated;

    private bool IsCurrent(long currentGeneration) =>
        currentGeneration == generation && !ctx.Stopping && !ctx.Disposed;

    internal MusicCatalogChoice[] Choices { get; } =
        [new("wy", MusicPlatformInfo.DisplayName("wy")), new("kg", MusicPlatformInfo.DisplayName("kg"))];
    internal string Platform => platform;
    internal string Status => status;
    internal bool HasError => status.Length > 0;
    internal bool CanEdit => !ctx.Stopping && ctx.Commands == 0 && loaded && !busy;
    internal bool CanRead => !ctx.Stopping && ctx.Commands == 0 && !busy;
    internal bool Loaded => loaded;

    internal void CancelReadForShutdown() => readCancellation?.Cancel();

    internal void NotifyBindings()
    {
        notify(nameof(MusicSettingsController.CatalogSourcePlatform));
        notify(nameof(MusicSettingsController.CatalogSourceStatus));
        notify(nameof(MusicSettingsController.HasCatalogSourceError));
        notify(nameof(MusicSettingsController.CanEditCatalogSource));
        notify(nameof(MusicSettingsController.CanReadCatalogSource));
    }

    private void Accept(MusicCatalogSourceRecord value)
    {
        var nextRevision = value.Revision;
        var nextPlatform = value.Platform;
        MusicDto.Require(nextRevision >= 0 && nextPlatform is "wy" or "kg");
        var changed = nextPlatform != platform;
        platform = nextPlatform;
        revision = nextRevision;
        loaded = true;
        status = "";
        if (changed) CatalogSourceInvalidated?.Invoke();
        NotifyBindings();
    }

    internal Task EnsureAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (loaded) return Task.CompletedTask;
        if (readTask is null)
        {
            long currentGeneration = generation;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ctx.Lifetime.Token);
            readCancellation = cancellation;
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ctx.LoadDrains.Add(drained.Task);
            readTask = ReadAsync();
            _ = ObserveReadAsync(readTask);
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
                    var settings = await ctx.RunSettingReceiptAsync(MusicSettingReceiptKeys.CatalogSource,
                        ct => ctx.Application.GetCatalogSourceAsync(ct), cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (IsCurrent(currentGeneration)) Accept(settings);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    if (IsCurrent(currentGeneration))
                    {
                        loaded = false;
                        status = "数据源设置读取失败";
                        NotifyBindings();
                    }
                    throw;
                }
                finally
                {
                    if (ReferenceEquals(readCancellation, cancellation))
                    {
                        readCancellation = null;
                        readTask = null;
                    }
                    cancellation.Dispose();
                    ctx.LoadDrains.Remove(drained.Task);
                    drained.TrySetResult();
                }
            }
        }
        return readTask.WaitAsync(token);
    }

    internal Task ReadAsync()
    {
        if (!CanRead) return Task.CompletedTask;
        return ctx.RunAsync(async () =>
        {
            loaded = false;
            try { await EnsureAsync(ctx.Lifetime.Token); }
            catch { /* The setting's notice preserves the failed restore; browsing remains gated. */ }
        });
    }

    internal Task SetAsync(string nextPlatform)
    {
        if (!CanEdit || nextPlatform is not ("wy" or "kg") || nextPlatform == platform)
            return Task.CompletedTask;
        var expectedRevision = revision;
        long currentGeneration = generation;
        busy = true;
        status = "";
        NotifyBindings();
        return work = ctx.RunAsync(async () =>
        {
            try
            {
                var saved = await ctx.RunSettingReceiptAsync(MusicSettingReceiptKeys.CatalogSource,
                    ct => ctx.Application.SaveCatalogSourceAsync(expectedRevision, nextPlatform, ct), ctx.Lifetime.Token);
                if (IsCurrent(currentGeneration)) Accept(saved);
            }
            catch (MusicOperationException error) when (error.Code is
                "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" or "PENDING_WRITE" or "REVISION_CONFLICT")
            {
                if (!IsCurrent(currentGeneration)) return;
                try
                {
                    var current = await ctx.RunSettingReceiptAsync(MusicSettingReceiptKeys.CatalogSource,
                        ct => ctx.Application.GetCatalogSourceAsync(ct), ctx.Lifetime.Token);
                    if (!IsCurrent(currentGeneration)) return;
                    Accept(current);
                    if (platform != nextPlatform)
                        status = error.Code == "REVISION_CONFLICT"
                            ? "数据源设置已更新" : "数据源设置保存失败";
                }
                catch
                {
                    if (!IsCurrent(currentGeneration)) return;
                    loaded = false;
                    status = "数据源设置保存失败";
                    CatalogSourceInvalidated?.Invoke();
                }
            }
            catch (OperationCanceledException) { if (IsCurrent(currentGeneration)) status = "已取消"; }
            catch { if (IsCurrent(currentGeneration)) status = "数据源设置保存失败"; }
            finally
            {
                busy = false;
                if (IsCurrent(currentGeneration)) NotifyBindings();
            }
        });
    }

    internal async Task DeactivateAsync(bool retirePage = false)
    {
        generation++;
        if (retirePage || busy || readTask is not null) loaded = false;
        readCancellation?.Cancel();
        readCancellation = null;
        readTask = null;
        NotifyBindings();
        await work;
    }
}
