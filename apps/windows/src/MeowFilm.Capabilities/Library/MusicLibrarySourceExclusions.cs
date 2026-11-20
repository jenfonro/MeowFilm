using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

/// <summary>
/// User-confirmed wrong sources: one song × one provider (a script installation or the
/// official provider) × one platform, across every quality. Only an explicit restore
/// removes a record; expiry, cache clearing and script updates never do.
/// </summary>
public sealed partial class MusicLibraryOwner
{
    private const string SourceExclusionsArea = "native-source-exclusions";
    internal const string OfficialProvider = "official";
    private static bool IsProvider(string value) => value == OfficialProvider ||
        Regex.IsMatch(value, "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            RegexOptions.CultureInvariant, NativeMusicSong.RegexTimeout);
    private static void ValidateSourceExclusion(JsonObject item)
    {
        LJ.Fields(item, "songId provider platform providerName title artist at", "songId provider platform providerName title artist at");
        LJ.Require(LJ.Id(LJ.S(item["songId"]), 512) && IsProvider(LJ.S(item["provider"])) &&
            NativeMusicSong.Platforms.Contains(LJ.S(item["platform"])), "CORRUPT_STORAGE");
        _ = LJ.Text(item["providerName"], null); _ = LJ.Text(item["title"], null); _ = LJ.S(item["artist"]);
        _ = LJ.I(item["at"]);
    }
    private static string ExclusionKey(JsonNode? item) =>
        LJ.Stringify(new JsonArray(LJ.S(item?["songId"]), LJ.S(item?["provider"]), LJ.S(item?["platform"])));
    private (JsonObject Result, bool Mutate) SourceExclusionsCommand(string method, JsonObject p, MusicLibraryData doc)
    {
        LJ.Require(pending is null, "PENDING_WRITE");
        var saved = OverwriteSettings(doc, SourceExclusionsArea, 1, LJ.Obj(("schema", 1), ("revision", 0)));
        bool exists = doc.Select(SourceExclusionsArea, "schema") is not null;
        if (exists) LJ.Require(doc.HasRelation(SourceExclusionsArea, [], [doc.Scope], "items"), "CORRUPT_STORAGE");
        var rows = doc.Rows(SourceExclusionsArea, ["items"], [doc.Scope], "songId");
        string? song = p["songId"] is null ? null : LJ.Text(p["songId"], 512);
        var selected = rows.Where(row => song is null || LJ.S(row.Value["songId"]) == song).ToArray();
        if (method == "music.sourceExclusions.list")
        {
            var items = new JsonArray(); var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in selected)
            {
                var item = song is null ? doc.Row(SourceExclusionsArea, ["items"], row.Key) :
                    doc.SelectRow(SourceExclusionsArea, ["items"], row.Key, "songId", "provider", "platform");
                ValidateExclusionIdentity(item);
                if (song is null)
                {
                    try { ValidateSourceExclusion(item); }
                    catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
                }
                LJ.Require(identities.Add(ExclusionKey(item)), "CORRUPT_STORAGE");
                items.Add(item);
            }
            return (LJ.Obj(("revision", saved["revision"]), ("items", items)), false);
        }
        bool add = method == "music.sourceExclusions.add";
        LJ.Require(add || method == "music.sourceExclusions.remove", "CAPABILITY_UNAVAILABLE");
        LJ.Fields(p, "", add ? "songId provider platform providerName title artist" : "songId provider platform");
        try { ValidateExclusionIdentity(p); }
        catch (MusicLibraryException) { throw new MusicLibraryException("INVALID_REQUEST"); }
        string key = ExclusionKey(p);
        var matches = selected.Where(row =>
        {
            var identity = doc.SelectRow(SourceExclusionsArea, ["items"], row.Key, "songId", "provider", "platform");
            ValidateExclusionIdentity(identity);
            return ExclusionKey(identity) == key;
        }).ToArray();
        LJ.Require(matches.Length <= 1, "CORRUPT_STORAGE");
        JsonObject? next = null;
        if (add)
        {
            next = LJ.Obj(("songId", p["songId"]), ("provider", p["provider"]), ("platform", p["platform"]),
                ("providerName", p["providerName"]), ("title", p["title"]), ("artist", p["artist"]),
                ("at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            try { ValidateSourceExclusion(next); }
            catch (MusicLibraryException) { throw new MusicLibraryException("INVALID_REQUEST"); }
        }
        if (add == (matches.Length != 0))
            return (LJ.Obj(("revision", saved["revision"]), (add ? "added" : "removed", false)), false);
        if (!exists) Set(doc, SourceExclusionsArea, LJ.Obj(("schema", 1), ("revision", 1), ("items", new JsonArray())));
        else Set(doc, SourceExclusionsArea, LJ.Obj(("schema", 1), ("revision", checked(Revision(saved) + 1))), partial: true);
        var existing = matches.SingleOrDefault();
        doc.ChangeRow(SourceExclusionsArea, ["items"], existing?.Key ?? [doc.Scope, Guid.NewGuid().ToString("D")], next,
            existing?.Ordinal ?? (rows.Count == 0 ? 0 : rows.Max(row => row.Ordinal) + 1));
        return (LJ.Obj(("revision", checked(Revision(saved) + 1)), (add ? "added" : "removed", true)), true);
    }
    private static void ValidateExclusionIdentity(JsonObject item)
    {
        LJ.Require(LJ.Id(LJ.S(item["songId"]), 512) && IsProvider(LJ.S(item["provider"])) &&
            NativeMusicSong.Platforms.Contains(LJ.S(item["platform"])), "CORRUPT_STORAGE");
    }
}
