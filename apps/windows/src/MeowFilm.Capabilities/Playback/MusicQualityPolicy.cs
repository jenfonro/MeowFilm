using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

// Native request policy, not an assertion about the decoder or output device.
internal static class MusicQualityPolicy
{
    internal static readonly string[] Categories = ["master", "spatial", "enhanced", "hires", "lossless", "high", "standard"];
    internal static readonly string[] Spatial = ["dolby", "dts_x", "sony360", "sky", "vivid", "qq_atmos", "viper_atmos"];
    internal static readonly string[] RequestTypes = ["128k", "192k", "320k", "flac", "flac24bit", "master"];
    // Script host protocol vocabulary, not the identity of an official provider.
    internal static readonly string[] LxTypes = [.. RequestTypes];
    internal const string Fields = "schema revision request qualityOrder qualityEnabled spatialOrder spatialEnabled";
    internal static JsonObject Default(string request = "320k") => LJ.Obj(("schema", 1), ("revision", 0),
        ("request", request), ("qualityOrder", LJ.Arr(Categories)), ("qualityEnabled", LJ.Arr(Categories)),
        ("spatialOrder", LJ.Arr(Spatial)), ("spatialEnabled", LJ.Arr(Spatial)));

    internal static string Category(string level) => level switch
    {
        "master" or "jymaster" or "qq_master" or "viper_tape" => "master",
        "spatial" or "dolby" or "dts_x" or "sony360" or "sky" or "vivid" or "qq_atmos" or "viper_atmos" => "spatial",
        "enhanced" or "jyeffect" or "qq_enhanced" or "viper_clear" => "enhanced",
        "hires" or "flac24bit" => "hires",
        "lossless" or "flac" => "lossless",
        "high" or "higher" or "exhigh" or "320k" or "192k" => "high",
        "standard" or "128k" => "standard",
        _ => ""
    };
    internal static string[] Strings(JsonNode? node) => LJ.Array(node).Select(n => LJ.Text(n, 32)).ToArray();
    internal static bool HasEnabled(JsonObject policy) => Strings(policy["qualityEnabled"])
        .Any(id => Categories.Contains(id) && (id != "spatial" || Strings(policy["spatialEnabled"]).Any(Spatial.Contains)));

    internal static void Validate(JsonObject policy)
    {
        LJ.Fields(policy, Fields, Fields);
        LJ.Require(LJ.I(policy["schema"]) == 1);
        _ = LJ.I(policy["revision"]);
        LJ.Require(LJ.S(policy["request"]) == "auto" || RequestTypes.Contains(LJ.S(policy["request"])));
        static void List(JsonNode? n, string[] known, bool complete)
        {
            var values = Strings(n);
            LJ.Require(values.Length <= known.Length && (!complete || values.Length == known.Length) &&
                values.Distinct(StringComparer.Ordinal).Count() == values.Length && values.All(known.Contains));
        }
        List(policy["qualityOrder"], Categories, true); List(policy["qualityEnabled"], Categories, false);
        List(policy["spatialOrder"], Spatial, true); List(policy["spatialEnabled"], Spatial, false);
        LJ.Require(HasEnabled(policy), "QUALITY_REQUIRED");
    }
    internal static bool Enabled(string level, JsonObject policy)
    {
        string category = Category(level);
        return Strings(policy["qualityEnabled"]).Contains(category) && (category != "spatial" ||
            (level == "spatial" ? Strings(policy["spatialEnabled"]).Length > 0 : Strings(policy["spatialEnabled"]).Contains(level)));
    }
    internal static int Priority(string level, JsonObject policy) => !Enabled(level, policy) ? int.MaxValue :
        Array.IndexOf(Strings(policy["qualityOrder"]), Category(level)) * (Spatial.Length + 1) +
        (Category(level) == "spatial" && level != "spatial" ? Array.IndexOf(Strings(policy["spatialOrder"]), level) : 0);

    internal static string[] Select(JsonObject policy, string request, JsonObject recording, JsonObject capability, bool local)
    {
        LJ.Require(request == "auto" || LxTypes.Contains(request));
        if (local)
        {
            // The fixed LX local contract deliberately has qualitys: [].
            // Preserve explicit local requests, but never claim an automatic
            // platform-quality intersection for a file without that metadata.
            LJ.Require(request != "auto", "QUALITY_UNAVAILABLE");
            LJ.Require(Enabled(request, policy), "QUALITY_DISABLED");
            return [request];
        }
        var declared = Strings(capability["qualitys"]);
        // Native WY metadata can omit types. That is unknown, not a definite
        // miss: an explicit declared request may ask the real resolver, without
        // claiming verification. Auto still requires a known intersection.
        bool unknown = !recording.ContainsKey("types") && !LJ.B(recording["qualityKnown"]);
        var types = (recording["types"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(n => LJ.S(n["type"])).ToArray();
        var available = LxTypes.Where(t => declared.Contains(t) && types.Contains(t) && Enabled(t, policy));
        if (request != "auto")
        {
            LJ.Require(Enabled(request, policy), "QUALITY_DISABLED");
            LJ.Require(declared.Contains(request), "CAPABILITY_UNAVAILABLE");
            LJ.Require(unknown || types.Contains(request), "RECORDING_QUALITY_UNAVAILABLE");
            return [request];
        }
        var ordered = available.OrderBy(t => Priority(t, policy))
            .ThenBy(t => t == "320k" ? 0 : t == "192k" ? 1 : 2).ToArray();
        LJ.Require(ordered.Length > 0, "QUALITY_UNAVAILABLE");
        return ordered;
    }
}
