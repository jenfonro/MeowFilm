using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed class PanSettingsStore(IRuntimeClient runtime, IStorageDatabase database)
{
    private readonly PanCredentialRepository repository = new(database);

    internal static IReadOnlyList<PanProviderDescriptor> Providers { get; } =
    [
        new("baidu", "百度网盘", ["cookie"], SupportsQr: true, SupportsRefresh: true),
        new("quark", "夸克网盘", ["cookie"], SupportsQr: true, SupportsRefresh: true),
        new("uc", "UC 网盘", ["cookie"], SupportsQr: true, SupportsRefresh: true),
        new("tianyi", "天翼云盘", ["cookie", "username", "password"], SupportsQr: false, SupportsRefresh: true),
        new("yidong", "移动云盘", ["authorization", "refreshedAt"], SupportsQr: false, SupportsRefresh: true)
    ];

    internal static PanProviderDescriptor? FindProvider(string provider)
    {
        foreach (var item in Providers)
            if (item.Provider == provider) return item;
        return null;
    }

    internal async Task<IReadOnlyList<PanAccountSnapshot>> ReadAsync(CancellationToken token)
    {
        try { return await repository.ReadSnapshotsAsync(token).ConfigureAwait(false); }
        catch (DocumentStoreException error) { throw StorageError(error); }
    }

    internal async Task<PanAccountSnapshot> ReadAsync(string provider, CancellationToken token)
    {
        try { return await repository.ReadSnapshotAsync(provider, token).ConfigureAwait(false); }
        catch (DocumentStoreException error) { throw StorageError(error); }
    }

    internal Task<PanAccountSnapshot> SaveAsync(string provider, IReadOnlyDictionary<string, object> credentials,
        CancellationToken token = default) => ChangeAsync(provider, credentials, token);

    internal Task<PanAccountSnapshot> ClearAsync(string provider, CancellationToken token = default) =>
        ChangeAsync(provider, new Dictionary<string, object>(), token);

    private async Task<PanAccountSnapshot> ChangeAsync(string provider, IReadOnlyDictionary<string, object> credentials, CancellationToken token)
    {
        PanAccountSnapshot saved;
        try
        {
            var (accountRevision, rowRevision) = await repository.ReadRevisionsAsync(provider, token).ConfigureAwait(false);
            saved = await repository.ReplaceSnapshotAsync(provider, credentials, accountRevision, rowRevision, token).ConfigureAwait(false);
        }
        catch (DocumentStoreException error) { throw StorageError(error); }
        // A runtime failure cannot change the successful local commit.
        try { (runtime as IRuntimeSettingsNotifications)?.SettingsChanged("pan.accounts"); }
        catch (Exception) { }
        return saved;
    }

    private static Exception StorageError(DocumentStoreException error) => error.Error switch
    {
        DocumentStoreError.Conflict => new PanAccountException("STALE_ACCOUNT"),
        DocumentStoreError.Corrupt or DocumentStoreError.UnsupportedVersion => new BridgeException("CORRUPT_STORAGE"),
        DocumentStoreError.Closed => new BridgeException("CLOSED"),
        _ => new BridgeException("STORAGE_ERROR")
    };
}
