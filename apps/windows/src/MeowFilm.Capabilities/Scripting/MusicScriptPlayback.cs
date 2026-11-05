using System.Globalization;
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
        bool needsMatch = !isLyrics && platform != "local" && p["sourceKey"] is null && matchSong is not null &&
            !LJ.Array(song["sources"]).OfType<JsonObject>().Any(source => LJ.S(source["source"]) == platform);
        var selected = needsMatch ? (Key: "", Data: new JsonObject()) : SelectRecording(song, platform, p["sourceKey"]);
        string quality = isLyrics ? "" : LJ.Text(p["quality"]);
        LJ.Require(isLyrics || quality == "auto" || MusicQualityPolicy.LxTypes.Contains(quality));
        using var policy = isLyrics ? null : await library.AcquireQualityAsync(external).ConfigureAwait(false);
        if (policy is not null && p.ContainsKey("qualityPolicySignature"))
            LJ.Require(LJ.Text(p["qualityPolicySignature"], 64) == policy.Signature, "QUALITY_POLICY_CHANGED");
        LJ.Require(!p.ContainsKey("qualityCacheSignature") || p.ContainsKey("qualityPolicySignature") && quality != "auto");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(external, lifetime.Token, policy?.Token ?? default);
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, deadline.Token);
        lock (activityGate)
        {
            LJ.Require(!preparing && !closed, "CLOSED");
            onlineOperations.Add(operation);
            if (isLyrics) { lyrics?.Cancel(); lyrics = operation; }
            else { resolution?.Cancel(); resolution = operation; }
        }
        var token = timed.Token;
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
                if (!isLyrics && !needsMatch)
                    choices = MusicQualityPolicy.Select(policy?.Policy ?? MusicQualityPolicy.Default(), quality,
                        selected.Data, capability!, platform == "local");
            }
            finally { gate.Release(); }
            if (needsMatch)
            {
                // Only the missing target platform is matched, outside the owner gate and inside this deadline.
                var original = song;
                song = NativeMusicSong.Normalize(await matchSong!(song, platform, token).ConfigureAwait(false));
                token.ThrowIfCancellationRequested();
                LJ.Require(LJ.S(song["songId"]) == LJ.S(original["songId"]) &&
                    new NativeMusicSong.Recordings(original).CanMerge(new(song)), "TARGET_CHANGED");
                await library.CheckQualityAsync(policy!, token).ConfigureAwait(false);
                await CheckQualitySourceAsync(id, active, token).ConfigureAwait(false);
                var first = LJ.Array(song["sources"]).OfType<JsonObject>().Where(source => LJ.S(source["source"]) == platform)
                    .OrderBy(source => LJ.S(source["sourceKey"]), StringComparer.Ordinal).FirstOrDefault();
                LJ.Require(first is not null, "RECORDING_UNAVAILABLE");
                selected = SelectRecording(song, platform, first!["sourceKey"]);
                choices = MusicQualityPolicy.Select(policy!.Policy, quality, selected.Data,
                    active.Capabilities[platform]!.AsObject(), false);
            }
            if (!isLyrics)
            {
                var media = await ResolveQualityAsync(p, song, selected, id, platform, active, choices, policy, token).ConfigureAwait(false);
                if (needsMatch) media["recordingSong"] = song.DeepClone();
                return media;
            }
            var payload = ScriptPayload(selected.Data);
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
        catch (Exception error) when (deadline.IsCancellationRequested && !operation.IsCancellationRequested &&
            Code(error) == "CANCELLED")
        { throw new MusicLibraryException("TIMEOUT"); }
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
    private static JsonObject ScriptPayload(JsonObject recording, string? quality = null)
    {
        var payload = LJ.Obj(("musicInfo", recording));
        var info = (JsonObject)payload["musicInfo"]!;
        if (info["duration"] is not null)
        {
            double seconds = LJ.N(info["duration"]);
            info["interval"] = Math.Floor(seconds / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
                Math.Floor(seconds % 60).ToString("00", CultureInfo.InvariantCulture);
        }
        if (quality is not null) payload["quality"] = quality;
        return payload;
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
            return (LJ.Text(song["songId"]), new JsonObject());
        }
        var records = LJ.Array(song["sources"]).OfType<JsonObject>()
            .Where(s => LJ.S(s["source"]) == platform && (key is null || LJ.S(s["sourceKey"]) == LJ.Text(key))).ToArray();
        LJ.Require(records.Length == 1, records.Length > 0 ? "TARGET_REQUIRED" : "TARGET_CHANGED");
        return (LJ.Text(records[0]["sourceKey"]), NativeMusicSong.SourceData(records[0]).DeepClone().AsObject());
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
