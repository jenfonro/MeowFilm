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

    internal static MusicQualityPolicyRecord Default(string request = "flac") => new(
        Revision: 0,
        Request: request,
        QualityOrder: [.. Categories],
        QualityEnabled: [.. Categories],
        SpatialOrder: [.. Spatial],
        SpatialEnabled: [.. Spatial]);

    internal static string Category(string level) => level switch
    {
        "master" or "jymaster" or "qq_master" or "viper_tape" => "master",
        "spatial" or "dolby" or "dts_x" or "sony360" or "sky" or "vivid" or "qq_atmos" or "viper_atmos" or "atmos" or "atmos_plus" or "atmos_2" => "spatial",
        "enhanced" or "jyeffect" or "qq_enhanced" or "viper_clear" => "enhanced",
        "hires" or "flac24bit" => "hires",
        "lossless" or "flac" => "lossless",
        "high" or "higher" or "exhigh" or "320k" or "192k" => "high",
        "standard" or "128k" => "standard",
        _ => ""
    };

    private static string[] Ordered(string[] order, string[] enabled) =>
        order.Concat(enabled.Except(order)).ToArray();

    internal static bool HasEnabled(MusicQualityPolicyRecord policy) => policy.QualityEnabled
        .Any(id => Categories.Contains(id) && (id != "spatial" || policy.SpatialEnabled.Any(Spatial.Contains)));

    internal static void Validate(MusicQualityPolicyRecord policy)
    {
        MusicRules.Require(policy.Revision is >= 0 and <= MusicRules.MaxInteger);
        MusicRules.Require(policy.Request == "auto" || RequestTypes.Contains(policy.Request));
        static void List(string[] values, string[] known)
        {
            MusicRules.Require(values.Length <= known.Length &&
                values.Distinct(StringComparer.Ordinal).Count() == values.Length &&
                values.All(v => MusicRules.IsValidId(v, 32) && known.Contains(v)));
        }
        List(policy.QualityOrder, Categories);
        List(policy.QualityEnabled, Categories);
        List(policy.SpatialOrder, Spatial);
        List(policy.SpatialEnabled, Spatial);
    }

    internal static bool Enabled(string level, MusicQualityPolicyRecord policy)
    {
        string category = Category(level);
        return policy.QualityEnabled.Contains(category) && (category != "spatial" ||
            (level == "spatial" ? policy.SpatialEnabled.Length > 0 : policy.SpatialEnabled.Contains(level)));
    }

    internal static int Priority(string level, MusicQualityPolicyRecord policy)
    {
        if (!Enabled(level, policy)) return int.MaxValue;
        int category = Array.IndexOf(policy.QualityOrder, Category(level));
        if (category < 0) category = Categories.Length;
        int spatial = Category(level) == "spatial" && level != "spatial"
            ? Array.IndexOf(policy.SpatialOrder, level) : 0;
        if (spatial < 0) spatial = Spatial.Length;
        return category * (Spatial.Length + 1) + spatial;
    }

    /// <summary>Exact official request levels; spatial and enhanced are platform effects, not LX types.</summary>
    internal static readonly string[] OfficialTypes = [.. RequestTypes, "jyeffect", "dolby", "sky", "vivid"];
    private static readonly string[] TxOfficialTypes = ["128k", "320k", "flac", "master"];
    private static readonly string[] KgOfficialTypes = ["128k", "320k", "flac", "flac24bit"];

    internal static string[] PlatformOfficialTypes(string platform) => platform switch
    {
        "wy" => OfficialTypes,
        "tx" => TxOfficialTypes,
        "kg" => KgOfficialTypes,
        _ => []
    };

    internal static bool SupportsOfficialQuality(string platform, string quality) =>
        PlatformOfficialTypes(platform).Contains(quality, StringComparer.Ordinal);

    /// <summary>The request levels of one category in preference order (spatial follows its own order).</summary>
    internal static string[] Levels(string category, MusicQualityPolicyRecord policy) => category switch
    {
        "master" => ["master"], "hires" => ["flac24bit"], "lossless" => ["flac"], "high" => ["320k", "192k"],
        "standard" => ["128k"], "enhanced" => ["jyeffect"],
        "spatial" => Ordered(policy.SpatialOrder, policy.SpatialEnabled).Where(level => OfficialTypes.Contains(level) &&
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
    internal static Tier[] Tiers(MusicQualityPolicyRecord policy)
    {
        string request = policy.Request;
        var order = Ordered(policy.QualityOrder, policy.QualityEnabled);
        int start = request == "auto" ? 0 : Math.Max(0, Array.IndexOf(order, Category(request)));
        return order.Skip(start).Where(category => policy.QualityEnabled.Contains(category))
            .Select(category => new Tier(category, category == Category(request) && request != "auto" &&
                Levels(category, policy).Contains(request)
                    ? Levels(category, policy).SkipWhile(level => level != request).ToArray()
                    : Levels(category, policy)))
            .Where(tier => tier.Levels.Length > 0).ToArray();
    }

    /// <summary>One manually chosen category or spatial level; never followed by another tier.</summary>
    internal static Tier? Exact(string choice, MusicQualityPolicyRecord policy)
    {
        if (Categories.Contains(choice))
            return policy.QualityEnabled.Contains(choice) && Levels(choice, policy) is { Length: > 0 } levels
                ? new(choice, levels) : null;
        return Spatial.Contains(choice) && OfficialTypes.Contains(choice) && Enabled(choice, policy)
            ? new("spatial", [choice]) : null;
    }
}
