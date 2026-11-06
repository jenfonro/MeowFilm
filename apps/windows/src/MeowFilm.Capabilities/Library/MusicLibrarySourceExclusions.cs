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
    private JsonObject SourceExclusions(JsonObject doc) => Value(doc, SourceExclusionsArea) as JsonObject ??
        LJ.Obj(("schema", 1), ("revision", 0), ("items", new JsonArray()));
    private static bool IsProvider(string value) => value == OfficialProvider ||
        Regex.IsMatch(value, "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            RegexOptions.CultureInvariant, NativeMusicSong.RegexTimeout);
    private static void ValidateSourceExclusion(JsonObject item)
    {
        LJ.Fields(item, "songId provider platform providerName title artist at", "songId provider platform providerName title artist at");
        LJ.Require(LJ.Id(LJ.S(item["songId"]), 512) && IsProvider(LJ.S(item["provider"])) &&
            NativeMusicSong.Platforms.Contains(LJ.S(item["platform"])), "CORRUPT_STORAGE");
        _ = LJ.Text(item["providerName"], 255); _ = LJ.Text(item["title"], 512); _ = LJ.S(item["artist"]);
        LJ.Require(LJ.S(item["artist"]).Length <= 512, "CORRUPT_STORAGE");
        _ = LJ.I(item["at"]);
    }
    private static string ExclusionKey(JsonNode? item) =>
        LJ.Stringify(new JsonArray(LJ.S(item?["songId"]), LJ.S(item?["provider"]), LJ.S(item?["platform"])));
    private static void ValidateSourceExclusions(JsonObject value)
    {
        LJ.Fields(value, "schema revision items", "schema revision items");
        LJ.Require(LJ.I(value["schema"]) == 1, "CORRUPT_STORAGE"); _ = Revision(value);
        var items = LJ.Array(value["items"]);
        foreach (var item in items) ValidateSourceExclusion(LJ.Object(item, "CORRUPT_STORAGE"));
        LJ.Require(items.Select(ExclusionKey).Distinct(StringComparer.Ordinal).Count() == items.Count, "CORRUPT_STORAGE");
    }
    private (JsonObject Result, bool Mutate) SourceExclusionsCommand(string method, JsonObject p, JsonObject doc)
    {
        LJ.Require(pending is null, "PENDING_WRITE");
        var saved = SourceExclusions(doc);
        var items = LJ.Array(saved["items"]);
        if (method == "music.sourceExclusions.list")
        {
            LJ.Fields(p, "songId", "");
            var listed = p["songId"] is { } song
                ? LJ.Arr(items.Where(item => LJ.S(item?["songId"]) == LJ.Text(song, 512)))
                : items.DeepClone().AsArray();
            return (LJ.Obj(("revision", saved["revision"]), ("items", listed)), false);
        }
        JsonArray next;
        if (method == "music.sourceExclusions.add")
        {
            LJ.Fields(p, "songId provider platform providerName title artist", "songId provider platform providerName title artist");
            var item = LJ.Obj(("songId", p["songId"]), ("provider", p["provider"]), ("platform", p["platform"]),
                ("providerName", p["providerName"]), ("title", p["title"]), ("artist", p["artist"]),
                ("at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            try { ValidateSourceExclusion(item); }
            catch (MusicLibraryException) { throw new MusicLibraryException("INVALID_REQUEST"); }
            string key = ExclusionKey(item);
            if (items.Any(existing => ExclusionKey(existing) == key)) return (LJ.Obj(("revision", saved["revision"]), ("added", false)), false);
            next = LJ.Arr(items.Select(existing => existing!.DeepClone()));
            next.Add(item);
        }
        else
        {
            LJ.Require(method == "music.sourceExclusions.remove", "CAPABILITY_UNAVAILABLE");
            LJ.Fields(p, "songId provider platform", "songId provider platform");
            LJ.Require(LJ.Id(LJ.Text(p["songId"], 512), 512) && IsProvider(LJ.Text(p["provider"], 128)) &&
                NativeMusicSong.Platforms.Contains(LJ.Text(p["platform"], 8)));
            string key = ExclusionKey(p);
            if (!items.Any(existing => ExclusionKey(existing) == key)) return (LJ.Obj(("revision", saved["revision"]), ("removed", false)), false);
            next = LJ.Arr(items.Where(existing => ExclusionKey(existing) != key).Select(existing => existing!.DeepClone()));
        }
        var value = LJ.Obj(("schema", 1), ("revision", checked(Revision(saved) + 1)), ("items", next));
        Set(doc, SourceExclusionsArea, value);
        return (LJ.Obj(("revision", value["revision"]), (method.EndsWith(".add", StringComparison.Ordinal) ? "added" : "removed", true)), true);
    }
}
