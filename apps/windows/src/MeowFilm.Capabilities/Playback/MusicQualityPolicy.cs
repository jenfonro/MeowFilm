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
    internal static JsonObject Default(string request = "flac") => LJ.Obj(("schema", 1), ("revision", 0),
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

    /// <summary>Exact official request levels; spatial and enhanced are platform effects, not LX types.</summary>
    internal static readonly string[] OfficialTypes = [.. RequestTypes, "jyeffect", "dolby", "sky", "vivid"];
    /// <summary>The request levels of one category in preference order (spatial follows its own order).</summary>
    internal static string[] Levels(string category, JsonObject policy) => category switch
    {
        "master" => ["master"], "hires" => ["flac24bit"], "lossless" => ["flac"], "high" => ["320k", "192k"],
        "standard" => ["128k"], "enhanced" => ["jyeffect"],
        "spatial" => Strings(policy["spatialOrder"]).Where(level => OfficialTypes.Contains(level) &&
            Enabled(level, policy)).ToArray(),
        _ => []
    };
    internal sealed record Tier(string Category, string[] Levels);
    // The source browser has its own fixed traversal. Saved automatic preferences
    // never remove a declared request from this manual exploration.
    internal static readonly Tier[] SourceTiers =
    [
        new("high", ["320k", "192k"]), new("lossless", ["flac"]), new("hires", ["flac24bit"]),
        new("master", ["master"]), new("standard", ["128k"]),
        new("spatial", ["dolby", "sky", "vivid"]), new("enhanced", ["jyeffect"])
    ];
    /// <summary>
    /// Automatic order: the preferred category (auto: the first), then every later enabled
    /// category of the saved order. A category is exhausted across all sources before the next.
    /// </summary>
    internal static Tier[] Tiers(JsonObject policy)
    {
        string request = LJ.S(policy["request"]);
        var order = Strings(policy["qualityOrder"]);
        int start = request == "auto" ? 0 : Math.Max(0, Array.IndexOf(order, Category(request)));
        return order.Skip(start).Where(category => Strings(policy["qualityEnabled"]).Contains(category))
            .Select(category => new Tier(category, category == Category(request) && request != "auto" &&
                Levels(category, policy).Contains(request)
                    ? Levels(category, policy).SkipWhile(level => level != request).ToArray()
                    : Levels(category, policy)))
            .Where(tier => tier.Levels.Length > 0).ToArray();
    }
    /// <summary>One manually chosen category or spatial level; never followed by another tier.</summary>
    internal static Tier? Exact(string choice, JsonObject policy)
    {
        if (Categories.Contains(choice))
            return Strings(policy["qualityEnabled"]).Contains(choice) && Levels(choice, policy) is { Length: > 0 } levels
                ? new(choice, levels) : null;
        return Spatial.Contains(choice) && OfficialTypes.Contains(choice) && Enabled(choice, policy)
            ? new("spatial", [choice]) : null;
    }
}
