using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Scripting;

public sealed partial class MusicScriptOwner
{
    private async Task<JsonObject> OnlineAsync(string method, JsonObject p, CancellationToken external)
    {
        bool isLyrics = method == "music.online.lyrics";
        LJ.Fields(p, isLyrics ? "installationId platform generation song sourceKey" : "installationId platform generation song sourceKey quality qualityPolicySignature qualityCacheSignature",
            isLyrics ? "installationId platform song" : "installationId platform song quality");
        string id = LJ.Text(p["installationId"], 128), platform = LJ.Text(p["platform"]);
        LJ.Require(NativeMusicSong.Platforms.Append("local").Contains(platform));
        var song = NativeMusicSong.Normalize(p["song"]);
        var selected = SelectRecording(song, platform, p["sourceKey"]);
        string quality = isLyrics ? "" : LJ.Text(p["quality"]);
        LJ.Require(isLyrics || quality == "auto" || MusicQualityPolicy.LxTypes.Contains(quality));
        using var policy = isLyrics ? null : await library.AcquireQualityAsync(external).ConfigureAwait(false);
        if (policy is not null && p.ContainsKey("qualityPolicySignature"))
            LJ.Require(LJ.Text(p["qualityPolicySignature"], 64) == policy.Signature, "QUALITY_POLICY_CHANGED");
        LJ.Require(!p.ContainsKey("qualityCacheSignature") || p.ContainsKey("qualityPolicySignature") && quality != "auto");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(external, lifetime.Token, policy?.Token ?? default);
        operation.CancelAfter(TimeSpan.FromSeconds(25));
        lock (activityGate)
        {
            LJ.Require(!preparing && !closed, "CLOSED");
            onlineOperations.Add(operation);
            if (isLyrics) { lyrics?.Cancel(); lyrics = operation; }
            else { resolution?.Cancel(); resolution = operation; }
        }
        var token = operation.Token;
        try
        {
            Live active;
            string[] choices = [];
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await EnsureAsync(token).ConfigureAwait(false);
                if (isLyrics)
                {
                    var setting = RequireSuccess(await library.ExecuteAsync("music.settings.get", cancellationToken: token).ConfigureAwait(false));
                    if (!LJ.B(setting["settings"]!["lyrics"])) return LJ.Obj(("state", "disabled"), ("lines", new JsonArray()));
                }
                var item = Find(id);
                LJ.Require(LJ.B(item["enabled"]), "DISABLED");
                live.TryGetValue(id, out var old);
                if (p.ContainsKey("generation")) LJ.Require(LJ.I(p["generation"]) == (old?.Generation ?? 0), "STALE_SESSION");
                if (old is null) await ActivateAsync(item.DeepClone().AsObject(), token).ConfigureAwait(false);
                active = live[id];
                var capability = active.Capabilities[platform] as JsonObject;
                LJ.Require(capability is not null && LJ.Array(capability["actions"])
                    .Any(a => LJ.S(a) == (isLyrics ? "lyric" : "musicUrl")), "CAPABILITY_UNAVAILABLE");
                if (!isLyrics)
                    choices = MusicQualityPolicy.Select(policy?.Policy ?? MusicQualityPolicy.Default(), quality,
                        selected.Data, capability!, platform == "local");
            }
            finally { gate.Release(); }
            if (!isLyrics)
                return await ResolveQualityAsync(p, song, selected, id, platform, active, choices, policy, token).ConfigureAwait(false);
            var payload = LJ.Obj(("musicInfo", selected.Data));
            var result = await ScriptAsync("invoke", new { installationId = id, revision = active.Revision,
                generation = active.Generation, siteId = platform, operation = "lyric", payload }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                LJ.Require(!blocked && live.TryGetValue(id, out var now) && now == active, "STALE_SESSION");
            }
            finally { gate.Release(); }
            var raw = result["value"];
            string text = raw is JsonValue ? LJ.S(raw) : LJ.S(raw?["lyric"]);
            LJ.Require(raw is JsonValue v && v.TryGetValue<string>(out _) ||
                raw is JsonObject o && o["lyric"] is JsonValue l && l.TryGetValue<string>(out _), "INVALID_LYRIC_RESPONSE");
            string translation = raw is JsonObject lyricsResult ? LJ.S(lyricsResult["tlyric"]) : "";
            var lines = MusicLyricTiming.Parse(text, translation, LJ.N(song["duration"]));
            return LJ.Obj(("state", lines.GetArrayLength() > 0 ? "ready" : "none"), ("generation", active.Generation), ("lines", lines));
        }
        finally
        {
            lock (activityGate)
            {
                if (ReferenceEquals(resolution, operation)) resolution = null;
                if (ReferenceEquals(lyrics, operation)) lyrics = null;
                onlineOperations.Remove(operation);
            }
        }
    }
    private JsonObject QualityMedia(JsonNode? raw, JsonObject song, (string Key, JsonObject Data) selected,
        string id, string platform, Live active, string quality)
    {
            LJ.Require(raw is not null && !(raw is JsonValue empty && empty.TryGetValue<string>(out var emptyText) &&
                string.IsNullOrWhiteSpace(emptyText)), "MEDIA_UNAVAILABLE");
            var candidate = raw is JsonValue ? LJ.Obj(("url", LJ.S(raw))) : LJ.Object(raw, "INVALID_MEDIA_CANDIDATE");
            string url = LJ.Text(candidate["url"], 8192);
            LJ.Require(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" &&
                uri.UserInfo.Length == 0 && !url.Any(char.IsControl), "INVALID_MEDIA_CANDIDATE");
            foreach (string flag in new[] { "freeTrialInfo", "trial", "preview", "drm", "encrypted", "requiresLicense", "licenseRequired" })
                LJ.Require(!Truthy(candidate[flag]), "INVALID_MEDIA_CANDIDATE");
            if (candidate["expiresAt"] is { } expires)
                LJ.Require(LJ.N(expires, double.NaN) is var expiry && double.IsFinite(expiry) &&
                    expiry > clock.GetUtcNow().ToUnixTimeMilliseconds(), "EXPIRED_MEDIA_CANDIDATE");
            foreach (var pair in new[] { ("sourceKey", selected.Key), ("installationId", id), ("siteKey", platform), ("requestedQuality", quality) })
                LJ.Require(!candidate.ContainsKey(pair.Item1) || LJ.S(candidate[pair.Item1]) == pair.Item2, "TARGET_CHANGED");
            var media = LJ.Obj(("url", url), ("headers", MediaHeaders(candidate["headers"])),
                ("songId", song["songId"]), ("sourceKey", selected.Key), ("installationId", id),
                ("platform", platform), ("generation", active.Generation), ("quality", quality),
                ("expiresAt", candidate["expiresAt"]), ("playbackVerified", false), ("qualityVerified", false));
            return media;
    }
    private static (string Key, JsonObject Data) SelectRecording(JsonObject song, string platform, JsonNode? key)
    {
        if (platform == "local")
        {
            LJ.Require(!NativeMusicSong.IsOnline(song), "TARGET_CHANGED");
            return (LJ.Text(song["songId"]), LJ.Object(song["source_data"]).DeepClone().AsObject());
        }
        var records = LJ.Array(song["sources"]).OfType<JsonObject>()
            .Where(s => LJ.S(s["source"]) == platform && (key is null || LJ.S(s["sourceKey"]) == LJ.Text(key))).ToArray();
        LJ.Require(records.Length == 1, records.Length > 0 ? "TARGET_REQUIRED" : "TARGET_CHANGED");
        return (LJ.Text(records[0]["sourceKey"]), LJ.Object(records[0]["source_data"]).DeepClone().AsObject());
    }
    private static bool Truthy(JsonNode? value)
    {
        if (value is null) return false;
        if (value is not JsonValue scalar) return true;
        if (scalar.TryGetValue<bool>(out var boolean)) return boolean;
        if (scalar.TryGetValue<string>(out var text)) return text.Length > 0;
        return LJ.N(value) != 0;
    }
    private static JsonObject MediaHeaders(JsonNode? node)
    {
        var input = node is null ? new JsonObject() : LJ.Object(node, "INVALID_MEDIA_CANDIDATE");
        LJ.Require(input.Count <= 32, "INVALID_MEDIA_CANDIDATE");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int bytes = 0;
        foreach (var pair in input)
        {
            string name = pair.Key, value = LJ.S(pair.Value), lower = name.ToLowerInvariant();
            LJ.Require(name.Length is > 0 and <= 128 && name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+.^_`|~-".Contains(c)) &&
                seen.Add(name) && pair.Value is JsonValue v && v.TryGetValue<string>(out _) && value.Length <= 8192 &&
                value.All(c => c is >= ' ' and <= '~') && (bytes += name.Length + value.Length) <= 16384, "INVALID_MEDIA_CANDIDATE");
            LJ.Require(!new[] { "host", "range", "accept-encoding", "connection", "keep-alive", "te", "trailer", "transfer-encoding", "upgrade", "expect" }.Contains(lower) &&
                !new[] { "if-", "content-", "proxy-", "sec-" }.Any(prefix => lower.StartsWith(prefix, StringComparison.Ordinal)),
                "MEDIA_HEADERS_UNSUPPORTED");
        }
        return input.DeepClone().AsObject();
    }
}
