using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeowFilm.Capabilities.Library;

/// <summary>
/// User-confirmed wrong sources: one song × one provider (a script installation or the
/// official provider) × one platform, across every quality. Only an explicit restore
/// removes a record; expiry, cache clearing and script updates never do.
/// </summary>
public sealed partial class MusicLibraryOwner
{
    internal const string OfficialProvider = "official";

    private static bool IsProvider(string value) => value == OfficialProvider ||
        Regex.IsMatch(value, "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            RegexOptions.CultureInvariant, NativeMusicSong.RegexTimeout);

    private static void ValidateExclusionIdentity(string songId, string provider, string platform, string code = "CORRUPT_STORAGE")
    {
        MusicRules.Require(MusicRules.IsValidId(songId, 512) && IsProvider(provider) &&
            NativeMusicSong.Platforms.Contains(platform), code);
    }

    private static void ValidateSourceExclusion(MusicSourceExclusionRecord item, string code = "CORRUPT_STORAGE")
    {
        ValidateExclusionIdentity(item.SongId, item.Provider, item.Platform, code);
        MusicRules.Require(MusicRules.IsValidId(item.ProviderName, null) && MusicRules.IsValidId(item.Title, null), code);
        MusicRules.Require(item.ExcludedAtMs is >= 0 and <= MusicRules.MaxInteger, code);
    }

    private static string ExclusionKey(string songId, string provider, string platform) =>
        MusicRules.CanonicalArray(songId, provider, platform);

    private (JsonElement Result, bool Mutate) SourceExclusionsCommand(string method, JsonElement p, MusicLibrarySession doc)
    {
        MusicRules.Require(pending is null, "PENDING_WRITE");
        long savedRev = doc.ReadSourceExclusionsRevision() ?? 0;
        MusicRules.Require(savedRev is >= 0 and <= MusicRules.MaxInteger, "CORRUPT_STORAGE");
        var rows = doc.ReadSourceExclusions();
        string? song = MusicWireCodec.TryGet(p, "songId", out var songEl) && songEl.ValueKind != JsonValueKind.Null
            ? MusicWireCodec.RequireId(songEl, 512)
            : null;
        var selected = song is null ? rows : rows.Where(row => row.SongId == song).ToArray();

        if (method == "music.sourceExclusions.list")
        {
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in selected)
            {
                ValidateExclusionIdentity(item.SongId, item.Provider, item.Platform);
                if (song is null) ValidateSourceExclusion(item);
                MusicRules.Require(identities.Add(ExclusionKey(item.SongId, item.Provider, item.Platform)), "CORRUPT_STORAGE");
            }
            return (MusicWireCodec.Ok(w =>
            {
                w.WriteNumber("revision", savedRev);
                w.WritePropertyName("items");
                w.WriteStartArray();
                foreach (var item in selected)
                    MusicWireCodec.WriteSourceExclusion(w, item, identityOnly: song is not null);
                w.WriteEndArray();
            }), false);
        }

        bool add = method == "music.sourceExclusions.add";
        MusicRules.Require(add || method == "music.sourceExclusions.remove", "CAPABILITY_UNAVAILABLE");
        MusicWireCodec.RequireFields(p, add ? "songId provider platform providerName title artist" : "songId provider platform");
        string songId = MusicWireCodec.RequireStrProp(p, "songId");
        string provider = MusicWireCodec.RequireStrProp(p, "provider");
        string platform = MusicWireCodec.RequireStrProp(p, "platform");
        ValidateExclusionIdentity(songId, provider, platform, "INVALID_REQUEST");
        string key = ExclusionKey(songId, provider, platform);

        var matches = selected.Where(row =>
        {
            ValidateExclusionIdentity(row.SongId, row.Provider, row.Platform);
            return ExclusionKey(row.SongId, row.Provider, row.Platform) == key;
        }).ToArray();
        MusicRules.Require(matches.Length <= 1, "CORRUPT_STORAGE");

        MusicSourceExclusionRecord? next = null;
        int nextOrdinal = matches.Length > 0 ? matches[0].Ordinal : (rows.Count == 0 ? 0 : rows.Max(r => r.Ordinal) + 1);
        string exclusionId = matches.Length > 0 ? matches[0].ExclusionId : Guid.NewGuid().ToString("D");

        if (add)
        {
            next = new MusicSourceExclusionRecord(
                exclusionId,
                nextOrdinal,
                songId,
                provider,
                platform,
                MusicWireCodec.RequireStrProp(p, "providerName"),
                MusicWireCodec.RequireStrProp(p, "title"),
                MusicWireCodec.RequireStrProp(p, "artist"),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            ValidateSourceExclusion(next, "INVALID_REQUEST");
        }

        if (add == (matches.Length != 0))
        {
            return (MusicWireCodec.Ok(w =>
            {
                w.WriteNumber("revision", savedRev);
                w.WriteBoolean(add ? "added" : "removed", false);
            }), false);
        }

        long nextRev = checked(savedRev + 1);
        doc.SetSourceExclusionsRevision(nextRev);
        doc.StageSourceExclusion(new MusicSourceExclusionMutation(exclusionId, next, nextOrdinal));
        return (MusicWireCodec.Ok(w =>
        {
            w.WriteNumber("revision", nextRev);
            w.WriteBoolean(add ? "added" : "removed", true);
        }), true);
    }
}
