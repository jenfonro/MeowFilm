namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static readonly IReadOnlyDictionary<string, MusicCollectionIntentRecord> EmptyIntents =
        new Dictionary<string, MusicCollectionIntentRecord>(StringComparer.Ordinal);

    private static MusicCollectionRecord FreshCollection(string id, string name, string kind) =>
        new(id, name, kind, 0, false, false, false, [], [], EmptyIntents);

    private MusicLibraryAreaRecord EmptyLibrary() => new(
        0, scope, 0, 1,
        new Dictionary<string, string>(StringComparer.Ordinal),
        [FreshCollection("favorites", "我的收藏", "favorites")],
        []);

    private MusicLibraryAreaRecord LibraryState(MusicLibrarySession doc) =>
        doc.ReadLibrary() ?? EmptyLibrary();

    private static MusicCollectionRecord FindCollection(MusicLibraryAreaRecord library, string id) =>
        library.Collections.FirstOrDefault(c => c.CollectionId == id && !c.Deleted)
            ?? throw new MusicLibraryException("COLLECTION_NOT_FOUND");

    internal static string BindingKey(string platform, string accountId, string remoteId) =>
        MusicRules.CanonicalArray(platform, accountId, remoteId);

    internal static bool LibraryAreaEquals(MusicLibraryAreaRecord a, MusicLibraryAreaRecord b)
    {
        if (a.OwnerId != b.OwnerId || a.LibraryRevision != b.LibraryRevision || a.NextId != b.NextId ||
            a.Accounts.Count != b.Accounts.Count ||
            a.Collections.Length != b.Collections.Length ||
            a.NetworkPlaylists.Length != b.NetworkPlaylists.Length)
            return false;
        foreach (var (k, v) in a.Accounts)
            if (!b.Accounts.TryGetValue(k, out var bv) || v != bv)
                return false;
        for (int i = 0; i < a.Collections.Length; i++)
        {
            var ca = a.Collections[i]; var cb = b.Collections[i];
            if (!ca.HeaderEquals(cb) || !ca.Order.SequenceEqual(cb.Order, StringComparer.Ordinal) ||
                !ca.BindingsEqual(cb) || !ca.IntentsEqual(cb))
                return false;
        }
        for (int i = 0; i < a.NetworkPlaylists.Length; i++)
            if (a.NetworkPlaylists[i] != b.NetworkPlaylists[i])
                return false;
        return true;
    }
}
