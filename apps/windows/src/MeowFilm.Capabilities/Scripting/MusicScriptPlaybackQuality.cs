using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Scripting;

public sealed partial class MusicScriptOwner
{
    private async Task<JsonObject> ResolveQualityAsync(JsonObject input, JsonObject song,
        (string Key, JsonObject Data) selected, string id, string platform, Live active, string[] choices,
        MusicLibraryOwner.QualityLease? policy, CancellationToken token)
    {
        // At most six distinct declared types, one selected recording and one
        // script generation, all inside OnlineAsync's single deadline.
        foreach (string quality in choices)
        {
            token.ThrowIfCancellationRequested();
            if (policy is not null) await library.CheckQualityAsync(policy, token).ConfigureAwait(false);
            await CheckQualitySourceAsync(id, active, token).ConfigureAwait(false);
            string signature = LJ.Digest(LJ.Obj(("contract", "music.quality.cache.v1"),
                ("policy", policy!.Signature), ("installation", id), ("platform", platform),
                ("revision", active.Revision), ("generation", active.Generation), ("sourceKey", selected.Key),
                ("recording", selected.Data), ("songId", song["songId"]), ("songRevision", song["revision"]),
                ("duration", song["duration"]), ("quality", quality)));
            if (input.ContainsKey("qualityCacheSignature"))
                LJ.Require(LJ.Text(input["qualityCacheSignature"], 64) == signature, "QUALITY_CACHE_CHANGED");
            JsonObject media;
            try
            {
                var result = await ScriptAsync("invoke", new
                {
                    installationId = id, revision = active.Revision, generation = active.Generation,
                    siteId = platform, operation = "musicUrl",
                    payload = LJ.Obj(("musicInfo", selected.Data), ("quality", quality))
                }, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                media = QualityMedia(result["value"], song, selected, id, platform, active, quality);
            }
            catch (MusicLibraryException error) when (error.Code == "MEDIA_UNAVAILABLE" && choices.Length > 1)
            {
                // A definite miss is not a timeout, unknown receipt, denial or
                // malformed media response. None of those authorize a retry.
                if (policy is not null) await library.CheckQualityAsync(policy, token).ConfigureAwait(false);
                await CheckQualitySourceAsync(id, active, token).ConfigureAwait(false);
                continue;
            }
            if (policy is not null) await library.CheckQualityAsync(policy, token).ConfigureAwait(false);
            await CheckQualitySourceAsync(id, active, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            media["qualityPolicySignature"] = policy?.Signature;
            media["qualityCacheSignature"] = signature;
            media["requestedQuality"] = quality;
            return media;
        }
        throw new MusicLibraryException("QUALITY_UNAVAILABLE");
    }
    private async Task CheckQualitySourceAsync(string id, Live active, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { LJ.Require(!blocked && live.TryGetValue(id, out var now) && now == active, "STALE_SESSION"); }
        finally { gate.Release(); }
    }
}
