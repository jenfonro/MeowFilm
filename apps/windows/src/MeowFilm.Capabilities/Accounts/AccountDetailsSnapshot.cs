// SPDX-License-Identifier: GPL-3.0-only
// Fixed iWebPlus membership semantics, translated against actual upstream wire
// aliases, not the Python facade's snake_case output. See LICENSES/NOTICE.md.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Accounts;

internal static class AccountDetailsSnapshot
{
    internal const long Day = 86400000;
    internal const long ProfileTtl = 7 * Day;
    internal const long Retry = 60000;
    internal static readonly string[] WyLevels =
        ["standard", "higher", "exhigh", "lossless", "hires", "jyeffect", "dolby", "sky", "jymaster", "vivid"];
    internal static readonly string[] Tiers = ["none", "unknown", "vip", "svip", "huge_vip", "twelve", "eight",
        "group_vip", "cp_lover", "star", "ystar", "child_vip", "exp_vip", "ad_vip",
        "concept:svip", "concept:tvip", "concept:wvip", "concept:qvip", "concept:dvip", "concept:evip"];
    internal static MusicAccountEntitlements Unknown(long now, string? error = null, bool kugou = false) =>
        new(new(null, "unknown", null), now, 0, Array.Empty<string>(), true, error, kugou ? now + Retry : 0);
    private static string Scalar(JsonNode? value) =>
        value is JsonValue j && j.TryGetValue<string>(out var s) ? s : value is JsonValue ? value.ToJsonString() : "";
    internal static void Identity(JsonNode? value, string userId, bool required = false)
    {
        if (value is null) { AccountRules.Require(!required, "IDENTITY_MISMATCH"); return; }
        AccountRules.Require(AccountRules.Id(Scalar(value)) == userId, "IDENTITY_MISMATCH");
    }
    private static long? Integer(JsonNode? value)
    {
        if (value is not JsonValue j || j.TryGetValue<string>(out _)) return null;
        using var json = JsonDocument.Parse(value.ToJsonString());
        return json.RootElement.ValueKind == JsonValueKind.Number && json.RootElement.TryGetDecimal(out var n) &&
            n is >= 0 and <= AccountRules.MaxInteger &&
            decimal.Truncate(n) == n ? (long)n : null;
    }
    private static int? Flag(JsonNode? value) => Integer(value) is { } n && n is 0 or 1 ? (int)n : null;
    internal static long? Expiry(JsonNode? value)
    {
        string text = Scalar(value);
        long? number = Integer(value);
        if (number is null && value is JsonValue j && j.TryGetValue<string>(out _) &&
            text.Length is >= 10 and <= 13 && text.All(char.IsAsciiDigit) &&
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)) number = parsed;
        if (number is { } n)
        {
            if (n <= 0) return null;
            long time = n < 1000000000000 ? n * 1000 : n;
            return time <= AccountRules.MaxInteger ? time : null;
        }
        if (value is not JsonValue str || !str.TryGetValue<string>(out _)) return null;
        string[] local = ["yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss"];
        if (DateTime.TryParseExact(text, local, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).ToUnixTimeMilliseconds() is > 0 and var at
                ? at : null;
        if (!AccountRules.Match(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:Z|[+-]\d{2}:\d{2})$"))
            return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset) &&
            offset.ToUnixTimeMilliseconds() > 0 ? offset.ToUnixTimeMilliseconds() : null;
    }
    internal static MusicAccountEntitlements Wy(JsonObject data, string userId, long now)
    {
        Identity(data["userId"], userId, required: true);
        long upstream = Integer(data["now"]) is > 0 and var stamp ? stamp : now;
        long? a = Integer((data["associator"] as JsonObject)?["expireTime"]),
            m = Integer((data["musicPackage"] as JsonObject)?["expireTime"]),
            r = Integer((data["redplus"] as JsonObject)?["expireTime"]);
        long remaining(long? expiry) => Math.Max(0, (expiry ?? 0) - upstream);
        long vipRemaining = Math.Max(remaining(a), remaining(m)), svipRemaining = remaining(r);
        bool svip = svipRemaining > 0, vip = svip || vipRemaining > 0, known = a is not null || m is not null || r is not null;
        long? expired = new[] { a, m, r }.Where(t => t > 0 && t <= upstream).DefaultIfEmpty().Max();
        long until = vip ? checked(now + (svip ? svipRemaining : vipRemaining)) : known ? now + Day : 0;
        AccountRules.Require(until <= AccountRules.MaxInteger, "INVALID_RESPONSE");
        return new(new(vip ? true : known ? false : null, vip ? svip ? "svip" : "vip" : known ? "none" : "unknown",
            vip ? svip ? r : Math.Max(a ?? 0, m ?? 0) : expired), now, until,
            Array.AsReadOnly(vip ? WyLevels.Take(svip ? 10 : 6).ToArray() : []), !known);
    }
    private sealed record Product(string Tier, JsonNode? Flag, JsonNode? End, JsonNode? Start = null);
    internal static MusicAccountEntitlements Qq(JsonObject data, long now)
    {
        var identity = data["identity"] as JsonObject ?? new();
        var summary = (data["userinfo"] as JsonObject)?["expire"];
        Product[] products = [
            new("svip", data["svip"], summary),
            new("huge_vip", identity["HugeVip"], identity["HugeVipEnd"], identity["HugeVipStart"]),
            new("twelve", identity["twelve"], identity["twelveEnd"], identity["twelveStart"]),
            new("eight", identity["eight"], identity["eightEnd"], identity["eightStart"]),
            new("group_vip", identity["GroupVipFlag"], identity["GroupVipEnd"], identity["GroupVipStart"]),
            new("cp_lover", identity["CPLoverFlag"], identity["CPLoverEnd"], identity["CPLoverStart"]),
            new("star", data["star"], data["starend"], data["starstart"]),
            new("ystar", data["ystar"], data["ystarend"], data["ystarstart"]),
            new("vip", identity["vip"], summary), new("child_vip", identity["ChildVip"], null),
            new("exp_vip", identity["ExpVip"], null), new("ad_vip", identity["AdVipFlag"], null)
        ];
        var membership = Select(products, now);
        long until = membership.Vip == false ? now + Day :
            membership.Vip == true && membership.ExpiresAt is { } end ? end : 0;
        foreach (var p in products.Where(p => Flag(p.Flag) == 1))
            foreach (long? boundary in new[] { Expiry(p.End), Expiry(p.Start) })
                if (boundary > now && until > 0) until = Math.Min(until, boundary.Value);
        return new(membership, now, until, Array.Empty<string>(), membership.Vip is null);
    }
    private static MusicAccountMembership Select(IEnumerable<Product> products, long now)
    {
        bool known = false, uncertain = false; long? expired = null;
        MusicAccountMembership? active = null;
        foreach (var p in products)
        {
            if (p.Flag is null) continue;
            int? flag = Flag(p.Flag);
            if (flag is null) { uncertain = true; continue; }
            known = true;
            long? end = Expiry(p.End), start = Expiry(p.Start);
            if (end <= now) expired = Math.Max(expired ?? 0, end.Value);
            if (flag == 1 && (start is null || start <= now) && (end is null || end > now))
                active ??= new(true, p.Tier, end);
        }
        return active ?? new(known && !uncertain ? false : null, known && !uncertain ? "none" : "unknown", expired);
    }
    internal static MusicAccountEntitlements Kg(JsonObject data, string userId, long now)
    {
        Identity(data["userid"], userId);
        var rows = data["busi_vip"] as JsonArray ?? new();
        AccountRules.Require(rows.Count <= 100, "LIMIT");
        string[] names = ["svip", "tvip", "wvip", "qvip", "dvip", "evip"];
        var products = new List<Product>();
        foreach (var row in rows)
        {
            if (row is JsonObject item) Identity(item["userid"], userId);
            if (row is not JsonObject p || p["busi_type"] is not null && Scalar(p["busi_type"]) != "concept" ||
                !names.Contains(Scalar(p["product_type"])))
            { products.Add(new("unknown", JsonValue.Create(-1), null)); continue; }
            // Missing is_vip is an uncertain row, not a model-default zero.
            products.Add(new("concept:" + Scalar(p["product_type"]), p["is_vip"] ?? JsonValue.Create(-1),
                p["vip_end_time"], p["vip_begin_time"]));
        }
        products.Add(new("vip", data["is_vip"], data["vip_end_time"], data["vip_begin_time"]));
        var membership = Select(products, now);
        long until = membership.Vip is null ? 0 : membership.Vip == true ? membership.ExpiresAt ?? now + Retry : now + Day;
        foreach (var row in new JsonNode?[] { data }.Concat(rows))
            if (row is JsonObject p && Flag(p["is_vip"]) is not null)
                foreach (long? boundary in new[] { Expiry(p["vip_begin_time"]), Expiry(p["vip_end_time"]) })
                    if (boundary > now && until > 0) until = Math.Min(until, boundary.Value);
        return new(membership, now, until, Array.Empty<string>(), membership.Vip is null,
            RetryAt: membership.Vip is null ? now + Retry : 0);
    }
    internal static MusicAccountEntitlements Validate(MusicAccountEntitlements value, string platform)
    {
        if (value is null || value.Membership is null || value.AudioLevels is null)
            throw new MusicAccountException("INVALID_RESPONSE");
        AccountRules.Require(value.CheckedAt is > 0 and <= AccountRules.MaxInteger && value.RecheckAt is >= 0 and <= AccountRules.MaxInteger &&
            value.RetryAt is >= 0 and <= AccountRules.MaxInteger && value.Membership.ExpiresAt is null or > 0,
            "INVALID_RESPONSE");
        AccountRules.Require(Tiers.Contains(value.Membership.Tier) &&
            (value.Membership.Vip is null ? value.Membership.Tier == "unknown" :
             value.Membership.Vip == false ? value.Membership.Tier == "none" :
             value.Membership.Tier is not ("unknown" or "none")), "INVALID_RESPONSE");
        AccountRules.Require(value.Membership.ExpiresAt is null or <= AccountRules.MaxInteger &&
            (value.RecheckAt == 0 || value.RecheckAt >= value.CheckedAt) &&
            value.AudioLevels.Count <= 10 &&
            value.AudioLevels.Distinct().Count() == value.AudioLevels.Count &&
            value.AudioLevels.All(WyLevels.Contains) &&
            (value.AudioLevels.Count == 0 || platform == "wy" && value.Membership.Vip == true) &&
            (value.ErrorCode is null || AccountRules.SafeCode(value.ErrorCode) == value.ErrorCode), "INVALID_RESPONSE");
        return value with { AudioLevels = Array.AsReadOnly(value.AudioLevels.ToArray()) };
    }
}
