using System.Globalization;

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

    private MusicLibraryAreaRecord LibraryState(MusicLibrarySession doc)
    {
        try
        {
            var library = doc.ReadLibrary() ?? EmptyLibrary();
            MusicRules.Require(library.OwnerId == scope && library.StateRevision == library.LibraryRevision, "CORRUPT_LIBRARY");
            return library;
        }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_LIBRARY"); }
    }

    private static MusicCollectionRecord FindCollection(MusicLibrarySession doc, MusicLibraryAreaRecord library, string id)
    {
        var matches = library.Collections.Where(c => c.CollectionId == id && !c.Deleted).ToArray();
        MusicRules.Require(matches.Length <= 1, "CORRUPT_LIBRARY");
        var collection = matches.SingleOrDefault() ?? throw new MusicLibraryException("COLLECTION_NOT_FOUND");
        MusicRules.Require(collection.Order.Distinct(StringComparer.Ordinal).Count() == collection.Order.Length &&
            collection.Order.All(doc.Songs.Contains), "CORRUPT_LIBRARY");
        return collection;
    }

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

    private static MusicLibraryAreaRecord ValidateLibrary(MusicLibraryAreaRecord raw, string owner, Func<string, bool> containsSong)
    {
        try
        {
            MusicRules.Require(raw.OwnerId == owner && MusicRules.IsValidId(owner), "CORRUPT_LIBRARY");
            MusicRules.Require(raw.LibraryRevision is >= 0 and <= MusicRules.MaxInteger &&
                raw.NextId is >= 1 and <= MusicRules.MaxInteger, "CORRUPT_LIBRARY");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var remote = new HashSet<string>(StringComparer.Ordinal);
            long greatestPlaylist = 0;
            foreach (var c in raw.Collections)
            {
                MusicRules.Require(MusicRules.IsValidId(c.CollectionId) && MusicRules.IsValidId(c.Name, null) &&
                    c.Revision is >= 0 and <= MusicRules.MaxInteger, "INVALID_COLLECTION");
                MusicRules.Require(ids.Add(c.CollectionId) && c.Kind is "favorites" or "playlist" &&
                    c.Order.All(containsSong) &&
                    c.Order.Distinct(StringComparer.Ordinal).Count() == c.Order.Length, "INVALID_COLLECTION");
                MusicRules.Require(c.Kind != "favorites" || c.CollectionId == "favorites" &&
                    !c.Deleted && !c.LocallyCreated, "INVALID_FAVORITES");
                if (c.Kind == "playlist")
                {
                    if (!c.CollectionId.StartsWith("playlist:", StringComparison.Ordinal) ||
                        !long.TryParse(c.CollectionId[9..], NumberStyles.None, CultureInfo.InvariantCulture, out long number) ||
                        number <= 0 || number.ToString(CultureInfo.InvariantCulture) != c.CollectionId[9..])
                        throw new MusicLibraryException("INVALID_COLLECTION");
                    greatestPlaylist = Math.Max(greatestPlaylist, number);
                }
                var bound = new HashSet<string>(StringComparer.Ordinal);
                foreach (var b in c.Bindings)
                {
                    MusicRules.Require(MusicRules.IsValidId(b.Platform) && b.AccountId.Length > 0 &&
                        b.RemoteId.Length > 0 && b.Name.Length > 0, "INVALID_BINDING");
                    string key = BindingKey(b.Platform, b.AccountId, b.RemoteId);
                    MusicRules.Require(SyncPlatforms.Contains(b.Platform) && bound.Add(key) && remote.Add(key) && b.Writable &&
                        b.Baseline.All(nativeId => nativeId.Length > 0) &&
                        (b.Platform == "kg" || b.Baseline.Distinct(StringComparer.Ordinal).Count() == b.Baseline.Length),
                        "INVALID_BINDING");
                    MusicRules.Require(raw.Accounts.TryGetValue(b.Platform, out var acc) && acc == b.AccountId, "ACCOUNT_MISMATCH");
                    MusicRules.Require(b.Map.All(p => p.Key.Length > 0 && containsSong(p.Value)), "INVALID_BINDING");
                }
                foreach (var (songId, intent) in c.Intents)
                {
                    MusicRules.Require(containsSong(songId) && intent.Origin is "local" or "remote" &&
                        (intent.Acknowledged is null ||
                            intent.Acknowledged.All(bound.Contains) &&
                            intent.Acknowledged.Distinct(StringComparer.Ordinal).Count() == intent.Acknowledged.Length),
                        "INVALID_INTENT");
                }
            }
            MusicRules.Require(raw.Collections.Count(c => c.Kind == "favorites") == 1, "INVALID_FAVORITES");
            MusicRules.Require(raw.NextId > greatestPlaylist, "INVALID_NEXT_ID");
            MusicRules.Require(raw.Accounts.All(p => SyncPlatforms.Contains(p.Key) && p.Value.Length > 0), "INVALID_ACCOUNT");
            ValidateNetworkPlaylists(raw);
            return raw;
        }
        catch (MusicLibraryException error)
        {
            throw new MusicLibraryException(error.Code == "LIMIT" ? "LIMIT" : "CORRUPT_LIBRARY");
        }
    }
}
