using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class MusicManualSourcePolicyTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("manual source explicit script qualities bypass automatic enablement and keep exact refresh identity", ScriptQualities);
        yield return new("manual source recording evidence and protocol vocabulary remain mandatory", RecordingAdmission);
        yield return new("manual source official standard bypasses automatic enablement while preserving authorization and provider enablement", OfficialAdmission);
    }
    private static void Check(bool value, string message) => QualityFixture.Check(value, message);
    private static async Task<JsonElement> OnlyLossless(QualityFixture f)
    {
        var policy = await f.SaveParameters("flac");
        policy["qualityEnabled"] = new JsonArray("lossless");
        policy["qualityOrder"] = new JsonArray("standard", "high", "lossless", "hires", "enhanced", "spatial", "master");
        return await f.Ok("music.quality.settings.save", policy);
    }
    private static async Task ScriptQualities()
    {
        await using var f = new QualityFixture();
        string id = await f.Install();
        var before = await OnlyLossless(f);
        foreach (string quality in new[] { "192k", "flac24bit", "master" })
        {
            int calls = f.Runtime.Qualities.Count;
            await f.Error("music.online.resolve", f.Args(id, quality), "QUALITY_DISABLED");
            Check(f.Runtime.Qualities.Count == calls, "automatic denial must happen before script execution");
            var input = f.Args(id, quality);
            input["manualSource"] = true;
            var media = await f.Ok("music.online.resolve", input);
            Check(media.GetProperty("quality").GetString() == quality &&
                !media.GetProperty("qualityVerified").GetBoolean(), "an explicit request never invents measured quality");
            input["sourceKey"] = media.GetProperty("sourceKey").GetString();
            input["qualityPolicySignature"] = media.GetProperty("qualityPolicySignature").GetString();
            input["qualityCacheSignature"] = media.GetProperty("qualityCacheSignature").GetString();
            var refreshed = await f.Ok("music.online.resolve", input);
            Check(refreshed.GetProperty("qualityCacheSignature").GetString() ==
                media.GetProperty("qualityCacheSignature").GetString(), "manual renewal retains exact content identity");
        }
        Check(f.Runtime.Qualities.SequenceEqual(new[] { "192k", "192k", "flac24bit", "flac24bit", "master", "master" }) &&
            f.Runtime.Invoked.All(value => value == id), "only the selected installation and quality execute");
        var after = await f.Ok("music.quality.settings.get");
        Check(after.GetProperty("settings").GetRawText() == before.GetProperty("settings").GetRawText() &&
            after.GetProperty("signature").GetString() == before.GetProperty("signature").GetString(),
            "manual requests never rewrite automatic defaults, enabled categories or order");
    }
    private static async Task RecordingAdmission()
    {
        await using var f = new QualityFixture();
        string id = await f.Install();
        await OnlyLossless(f);
        var contrary = f.Args(id, "master", QualityFixture.Song("11", ["flac"]));
        contrary["manualSource"] = true;
        await f.Error("music.online.resolve", contrary, "RECORDING_QUALITY_UNAVAILABLE");
        var unknown = f.Args(id, "invented");
        unknown["manualSource"] = true;
        await f.Error("music.online.resolve", unknown, "QUALITY_UNAVAILABLE");
        var malformed = f.Args(id, "master");
        malformed["manualSource"] = "true";
        await f.Error("music.online.resolve", malformed, "INVALID_REQUEST");
        Check(f.Runtime.Qualities.Count == 0, "manual source mode cannot bypass recording or request validation");
    }
    private static async Task OfficialAdmission()
    {
        await using var store = new QualityFixture.DocumentStore();
        var runtime = new QualityFixture.ScriptRuntime();
        int calls = 0;
        runtime.Platform.OnRequest = (call, _) =>
        {
            Check(call.Platform == "wy" && call.Operation == "audio", "only real audio resolution crosses the platform port");
            calls++;
            return Task.FromResult(MusicPlatformRuntimeFixture.Json(new {
                url = "https://m701.music.126.net/manual-fixture.mp3", requestQuality = "128k",
                actualQuality = "128k", qualityVerified = true, duration = 100, size = 1600000,
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds(), full = true }));
        };
        await using var app = new MusicNativeApplication(store, runtime);
        async Task<JsonElement> Ok(string method, object? input = null)
        {
            var value = await app.ExecuteAsync(method, input);
            Check(value.GetProperty("ok").GetBoolean(), method + ": " + value.GetRawText());
            return value;
        }
        var initial = await Ok("music.quality.settings.get");
        var saved = JsonNode.Parse(initial.GetProperty("settings").GetRawText())!.AsObject();
        saved["ifRevision"] = saved["revision"]!.DeepClone();
        saved.Remove("schema"); saved.Remove("revision");
        saved["request"] = "flac"; saved["qualityEnabled"] = new JsonArray("lossless");
        var policy = await Ok("music.quality.settings.save", saved);
        var song = await MusicSongFixture.AdoptAsync(app,
            MusicSongFixture.Online("wy", "1", "Synthetic", "Fixture", duration: 100));
        string signature = policy.GetProperty("signature").GetString()!;
        QualityFixture.Code(await app.ExecuteAsync("music.official.candidates",
            new { song, quality = "128k", qualityPolicySignature = signature }), "QUALITY_DISABLED");
        var candidates = await Ok("music.official.candidates",
            new { song, quality = "128k", qualityPolicySignature = signature, manualSource = true });
        Check(candidates.GetProperty("candidates").GetArrayLength() == 1 && calls == 0,
            "enabled official guest standard remains a local candidate without automatic standard enabled");
        var input = new { platform = "wy", song, quality = "128k", qualityPolicySignature = signature, manualSource = true };
        var context = await Ok("music.official.cacheContext", input);
        Check(calls == 0, "manual cache context is still local and grants no new URL");
        var resolved = await Ok("music.official.resolve", input);
        Check(resolved.GetProperty("quality").GetString() == "128k", "manual guest standard resolves its real quality");
        await Ok("music.official.confirm", input);
        Check(calls == 1, "confirmation does not request another media URL");
        QualityFixture.Code(await app.ExecuteAsync("music.official.resolve",
            new { platform = "wy", song, quality = "master", manualSource = true }), "AUTH_REQUIRED");
        Check(calls == 1, "manual higher quality never manufactures login authority");
        await Ok("music.officialSources.settings.save", new { ifRevision = 0, enabled = Array.Empty<string>() });
        Check(!app.IsOfficialPlaybackCurrent("wy", context.GetProperty("scope").GetString()!,
            context.GetProperty("generation").GetInt64()), "disabling the provider revokes its issued scope");
        foreach (string method in new[] { "resolve", "cacheContext", "confirm" })
            QualityFixture.Code(await app.ExecuteAsync("music.official." + method, input), "OFFICIAL_SOURCE_DISABLED");
        var disabled = await Ok("music.official.candidates",
            new { song, quality = "128k", qualityPolicySignature = signature, manualSource = true });
        Check(disabled.GetProperty("candidates").GetArrayLength() == 0 && calls == 1,
            "manual mode cannot resolve or read through an explicitly disabled official provider");
    }
}
