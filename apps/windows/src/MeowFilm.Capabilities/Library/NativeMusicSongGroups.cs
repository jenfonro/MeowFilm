using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

/// <summary>Indexes the one native repository. Ambiguous new sources get an independent UUID.</summary>
internal sealed class NativeMusicSongGroups(CancellationToken token = default)
{
    private readonly Dictionary<string, NativeMusicSong.Recordings> ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativeMusicSong.Recordings> sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativeMusicSong.Recordings> files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<NativeMusicSong.Recordings>> candidates = new(StringComparer.Ordinal);
    private readonly HashSet<string> invalidIds = new(StringComparer.Ordinal), invalidSources = new(StringComparer.Ordinal),
        invalidFiles = new(StringComparer.Ordinal);

    internal NativeMusicSong.Recordings Import(JsonObject song) => Insert(new(song, token));
    internal void ReserveInvalid(string id, JsonNode? value)
    {
        invalidIds.Add(id);
        if (value is not JsonObject song) return;
        if (LJ.S(song["localFileId"]) is { Length: > 0 } file) invalidFiles.Add(file);
        if (song["sources"] is JsonArray rows)
            foreach (var source in rows.OfType<JsonObject>())
                if (LJ.S(source["sourceKey"]) is { Length: > 0 } key) invalidSources.Add(key);
    }
    internal NativeMusicSong.Recordings? NativeOwner(string key) => sources.GetValueOrDefault(key);
    internal NativeMusicSong.Recordings? Find(NativeMusicSong.Recordings incoming)
    {
        token.ThrowIfCancellationRequested();
        LJ.Require(!invalidIds.Contains(incoming.Id) && !invalidFiles.Contains(incoming.LocalFileId) &&
            !incoming.Sources.Any(source => invalidSources.Contains(LJ.S(source["sourceKey"]))), "INVALID_SONG");
        NativeMusicSong.Recordings? registered;
        foreach (var source in incoming.Sources)
            if (sources.TryGetValue(LJ.S(source["sourceKey"]), out registered)) return registered;
        if (incoming.LocalFileId.Length > 0 && files.TryGetValue(incoming.LocalFileId, out registered)) return registered;
        if (incoming.Id.Length > 0 && ids.TryGetValue(incoming.Id, out registered)) return registered;
        if (incoming.Identity.Fingerprint.Length == 0 || incoming.Identity.Duration <= 0) return null;
        var matches = candidates.GetValueOrDefault(incoming.Identity.Fingerprint)?
            .Where(record => record.CanMerge(incoming, token)).ToArray() ?? [];
        return matches.Length == 1 ? matches[0] : null;
    }
    internal NativeMusicSong.Recordings Add(NativeMusicSong.Recordings incoming, bool preferIncoming = false)
    {
        var owner = Find(incoming);
        if (owner is null)
        {
            incoming.Id = Guid.NewGuid().ToString("D");
            return Insert(incoming);
        }
        var previous = owner.Identity.Fingerprint;
        // A new observation cannot merge two records or rebind a registered source.
        var eligible = incoming.Sources.Where(source => !sources.TryGetValue(LJ.S(source["sourceKey"]), out var bound) ||
            ReferenceEquals(bound, owner)).ToArray();
        var update = new NativeMusicSong.Recordings(incoming.ToSong(token), eligible, token) { Id = owner.Id };
        owner.TryMerge(update, preferIncoming, token);
        if (previous.Length > 0 && candidates.TryGetValue(previous, out var index))
        { index.Remove(owner); if (index.Count == 0) candidates.Remove(previous); }
        Index(owner);
        return owner;
    }
    private NativeMusicSong.Recordings Insert(NativeMusicSong.Recordings record)
    {
        token.ThrowIfCancellationRequested();
        ids.Add(record.Id, record); Index(record); return record;
    }
    private void Index(NativeMusicSong.Recordings record)
    {
        if (record.LocalFileId.Length > 0) files.TryAdd(record.LocalFileId, record);
        foreach (var source in record.Sources) sources.TryAdd(LJ.S(source["sourceKey"]), record);
        string fingerprint = record.Identity.Fingerprint;
        if (fingerprint.Length == 0) return;
        if (!candidates.TryGetValue(fingerprint, out var index)) candidates[fingerprint] = index = [];
        index.Add(record);
    }
}
