using System.Text.Json;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    // Installed by the existing coordinator; called only after the local CAS has left the owner gate.
    internal Func<string, CancellationToken, Task<JsonElement>>? SynchronizeNetworkPlaylists { get; set; }

    internal static string NetworkPlaylistId(string platform, string remoteId)
    {
        MusicRules.Require(NativeMusicSong.Platforms.Contains(platform) && remoteId.Length > 0, "INVALID_PLAYLIST_ID");
        return "network:" + platform + ":" + remoteId;
    }

    private static string RequireNonEmptyText(JsonElement p, string key)
    {
        string text = MusicWireCodec.RequireStrProp(p, key);
        MusicRules.Require(text.Length > 0);
        return text;
    }

    internal static MusicNetworkPlaylistRecord NewNetworkPlaylist(
        MusicLibraryAreaRecord library, string platform, string remoteId, string name, string cover, long count) =>
        new(
            NetworkPlaylistId(platform, remoteId),
            platform,
            remoteId,
            name,
            NativeMusicSong.PublicUrl(cover),
            count,
            checked(library.LibraryRevision + 1),
            true,
            null, null, null, null, null, null, null);

    private static void ValidateNetworkPlaylists(MusicLibraryAreaRecord library)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in library.NetworkPlaylists)
        {
            MusicRules.Require(reference.Platform.Length > 0 && reference.RemoteId.Length > 0 &&
                reference.Name.Length > 0 && reference.Revision is >= 0 and <= MusicRules.MaxInteger &&
                reference.SongCount is >= 0 and <= int.MaxValue);
            MusicRules.Require(reference.PlaylistId == NetworkPlaylistId(reference.Platform, reference.RemoteId) &&
                ids.Add(reference.PlaylistId));
            MusicRules.Require(reference.Cover == NativeMusicSong.PublicUrl(reference.Cover));
            if (reference.SubAccountId is not null || reference.SubPresent is not null)
            {
                MusicRules.Require(reference.SubAccountId is { Length: > 0 } && reference.SubPresent is not null &&
                    SyncPlatforms.Contains(reference.Platform) &&
                    library.Accounts.TryGetValue(reference.Platform, out var acc) && acc == reference.SubAccountId);
            }
            if (reference.IntentAccountId is not null || reference.IntentPresent is not null)
            {
                MusicRules.Require(reference.IntentAccountId is { Length: > 0 } && reference.IntentPresent is { } intentPresent &&
                    SyncPlatforms.Contains(reference.Platform) && intentPresent == reference.Present);
                string account = library.Accounts.TryGetValue(reference.Platform, out var acc) ? acc : "";
                MusicRules.Require(account.Length == 0 || account == reference.IntentAccountId);
            }
        }
    }

    private (JsonElement Result, bool Mutate) NetworkPlaylistCommand(string method, JsonElement p, MusicLibrarySession doc)
    {
        var library = LibraryState(doc);
        var references = library.NetworkPlaylists.ToList();

        if (method == "music.library.network.list")
        {
            var visible = references.Where(r => r.Present).ToArray();
            return (MusicWireCodec.Ok(w =>
            {
                MusicWireCodec.WritePage(w, visible, p, MusicWireCodec.WriteNetworkPlaylistView);
                w.WriteNumber("revision", library.LibraryRevision);
            }), false);
        }

        if (method == "music.library.network.get")
        {
            MusicWireCodec.RequireFields(p, "platform id");
            string key = NetworkPlaylistId(RequireNonEmptyText(p, "platform"), RequireNonEmptyText(p, "id"));
            var reference = references.FirstOrDefault(r => r.PlaylistId == key);
            return (MusicWireCodec.Ok(w =>
            {
                w.WriteBoolean("present", reference is { Present: true });
                w.WritePropertyName("collection");
                if (reference is null) w.WriteNullValue();
                else MusicWireCodec.WriteNetworkPlaylistView(w, reference);
            }), false);
        }

        MusicRules.Require(method is "music.library.network.save" or "music.library.network.remove", "INVALID_REQUEST");
        MusicNetworkPlaylistRecord selected;
        MusicNetworkPlaylistRecord? before;
        string platform;
        bool added = false;
        int existingIndex;

        if (method == "music.library.network.save")
        {
            MusicWireCodec.RequireFields(p, "platform id name");
            platform = RequireNonEmptyText(p, "platform");
            string id = RequireNonEmptyText(p, "id");
            string key = NetworkPlaylistId(platform, id);
            string name = RequireNonEmptyText(p, "name");
            bool hasCover = MusicWireCodec.Has(p, "cover");
            string cover = hasCover ? NativeMusicSong.PublicUrl(MusicWireCodec.Str(p, "cover")) : "";
            bool hasCount = MusicWireCodec.Has(p, "count");
            long count = hasCount ? MusicWireCodec.RequireIntProp(p, "count", 0, int.MaxValue) : 0;

            existingIndex = references.FindIndex(r => r.PlaylistId == key);
            before = existingIndex >= 0 ? references[existingIndex] : null;
            added = before is null;
            selected = before is null
                ? NewNetworkPlaylist(library, platform, id, name, cover, count)
                : before with
                {
                    Present = true,
                    Name = name,
                    Cover = hasCover ? cover : before.Cover,
                    SongCount = hasCount ? count : before.SongCount
                };
        }
        else
        {
            MusicWireCodec.RequireFields(p, "id ifRevision");
            string key = RequireNonEmptyText(p, "id");
            existingIndex = references.FindIndex(r => r.PlaylistId == key);
            if (existingIndex < 0) throw new MusicLibraryException("COLLECTION_NOT_FOUND");
            before = references[existingIndex];
            MusicRules.Require(before.Revision == MusicWireCodec.RequireIntProp(p, "ifRevision"), "REVISION_CONFLICT");
            platform = before.Platform;
            selected = before with { Present = false };
        }

        var syncState = LibrarySync(doc);
        var platformSettings = SyncPlatforms.Contains(platform) && syncState.Platforms.TryGetValue(platform, out var ps) ? ps : null;
        bool enabled = platformSettings is { Playlists: true };
        string accountId = platformSettings?.AccountId ?? "";
        bool presenceChanged = added || before!.Present != selected.Present;
        if (accountId.Length > 0 && (presenceChanged || selected.SubPresent is { } subPresent && subPresent != selected.Present))
            selected = selected with { IntentAccountId = accountId, IntentPresent = selected.Present };

        bool changed = before != selected;
        if (changed)
        {
            if (!added) selected = selected with { Revision = checked(selected.Revision + 1) };
            bool fenced = syncState.Pending is { } fence && fence.CollectionId == selected.PlaylistId;
            bool hasSub = selected.SubAccountId is not null;
            bool hasIntent = selected.IntentAccountId is not null;
            if (!selected.Present && !hasSub && !hasIntent && !fenced)
            {
                if (existingIndex >= 0) references.RemoveAt(existingIndex);
            }
            else if (existingIndex >= 0)
            {
                references[existingIndex] = selected;
            }
            else
            {
                references.Add(selected);
            }

            long nextRev = checked(library.LibraryRevision + 1);
            doc.SetLibrary(library with
            {
                StateRevision = nextRev,
                LibraryRevision = nextRev,
                NetworkPlaylists = references.ToArray()
            });
        }

        return (MusicWireCodec.Ok(w =>
        {
            w.WritePropertyName("collection");
            MusicWireCodec.WriteNetworkPlaylistView(w, selected);
            w.WriteBoolean("present", selected.Present);
            w.WriteBoolean("localSaved", true);
            w.WriteBoolean("synchronize", enabled);
        }), changed);
    }

    private async Task<JsonElement> ExecuteNetworkPlaylistMutationAsync(string method, object? parameters, CancellationToken token)
    {
        var local = await ExecuteCoreAsync(method, parameters, token, false).ConfigureAwait(false);
        if (!local.GetProperty("ok").GetBoolean()) return local;
        bool synchronize = MusicWireCodec.Bool(local, "synchronize");
        string platform = MusicWireCodec.Str(local.GetProperty("collection"), "platform");
        JsonElement sync = MusicWireCodec.Ok(w => w.WriteString("state", "local-only"));
        if (synchronize)
        {
            try
            {
                sync = SynchronizeNetworkPlaylists is { } run
                    ? await run(platform, token).ConfigureAwait(false)
                    : MusicWireCodec.Fail("SYNC_UNAVAILABLE");
            }
            catch (Exception error)
            {
                string code = error switch { MusicLibraryException e => e.Code, OperationCanceledException => "CANCELLED", _ => "SYNC_FAILED" };
                sync = MusicWireCodec.Fail(code);
            }
        }
        return MusicWireCodec.BuildObject(w =>
        {
            foreach (var prop in local.EnumerateObject())
                if (prop.Name != "synchronize")
                    prop.WriteTo(w);
            w.WritePropertyName("sync");
            sync.WriteTo(w);
        });
    }
}
