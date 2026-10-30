// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;

namespace MeowFilm.Capabilities.Platforms;

// Client fields and account authorization are composed only at the wire boundary.
internal static class MusicWyRequestContext
{
    private static readonly string[] ClientFields = ["deviceId", "_ntes_nuid", "_ntes_nnid", "WNMCID", "NMTID"];
    internal static Dictionary<string, string> Cookies(MusicWyClientContext client, string? accountCookie)
    {
        var values = PJ.CookieHeader(accountCookie);
        bool associated = values.TryGetValue("deviceId", out var device) && device.Length > 0 && device != client.DeviceId;
        if (device is not null)
            PJ.Require(device.Length <= 256 && device.All(c => c is >= '!' and <= '~' &&
                c is not (';' or '"' or '\\' or ',')), "INVALID_CREDENTIALS");
        values.TryAdd("__remember_me", "true");
        if (associated) return values;
        var local = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["deviceId"] = client.DeviceId, ["_ntes_nuid"] = client.Nuid, ["_ntes_nnid"] = client.Nnid,
            ["WNMCID"] = client.Wnmcid, ["NMTID"] = client.Nmtid
        };
        foreach (var (key, value) in local)
        {
            if (value is null) values.Remove(key); else values[key] = value;
        }
        return values;
    }
    internal static string Header(Dictionary<string, string> values) =>
        string.Join("; ", values.Select(p => p.Key + "=" + p.Value));
    internal static Dictionary<string, string?> ResponseFields(IReadOnlyList<string> rows, DateTimeOffset now)
    {
        PJ.Require(rows.Count <= 32, "INVALID_RESPONSE");
        var parts = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string row in rows)
        {
            PJ.Require(row.Length is > 0 and <= 8192 && !row.Any(c => c is < ' ' or '\u007f'), "INVALID_RESPONSE");
            string[] fields = row.Split(';');
            int at = fields[0].IndexOf('=');
            PJ.Require(at > 0, "INVALID_RESPONSE");
            string key = fields[0][..at].Trim(), value = fields[0][(at + 1)..].Trim();
            bool authorization = key is "MUSIC_U" or "__csrf" or "MUSIC_A";
            if (!authorization && !ClientFields.Contains(key, StringComparer.Ordinal)) continue;
            PJ.Require(value.Length <= 8192 && value.All(c => c is >= '!' and <= '~' &&
                c is not (';' or '"' or '\\')) && (key == "_ntes_nnid" || !value.Contains(',')), "INVALID_RESPONSE");
            bool expired = value.Length == 0;
            foreach (string field in fields.Skip(1))
            {
                int eq = field.IndexOf('=');
                string name = (eq < 0 ? field : field[..eq]).Trim().ToLowerInvariant();
                string attribute = eq < 0 ? "" : field[(eq + 1)..].Trim();
                if (name == "domain")
                    PJ.Require(attribute.ToLowerInvariant() is "music.163.com" or ".music.163.com", "INVALID_RESPONSE");
                if (name == "max-age")
                {
                    PJ.Require(long.TryParse(attribute, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var age),
                        "INVALID_RESPONSE");
                    expired |= age <= 0;
                }
                if (name == "expires")
                {
                    PJ.Require(DateTimeOffset.TryParse(attribute, CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var expiry), "INVALID_RESPONSE");
                    expired |= expiry <= now;
                }
            }
            PJ.Require(key != "MUSIC_U" || !expired, "AUTH_EXPIRED");
            PJ.Require(parts.TryAdd(key, expired ? null : value), "INVALID_RESPONSE");
        }
        return parts;
    }
    internal static string Authorization(IReadOnlyList<string> rows, DateTimeOffset now)
    {
        var parts = ResponseFields(rows, now);
        PJ.Require(parts.TryGetValue("MUSIC_U", out var music) && music is { Length: > 0 }, "INVALID_RESPONSE");
        return string.Join("; ", parts.Where(p => p.Value is not null && p.Key is "MUSIC_U" or "__csrf" or "MUSIC_A")
            .Select(p => p.Key + "=" + p.Value));
    }
}

internal sealed partial class MusicPlatformClientContextOwner
{
    internal async Task<MusicWyClientContext> AcceptWyAsync(MusicWyClientContext expected, string? accountCookie,
        IReadOnlyList<string> rows, Action guard, CancellationToken token)
    {
        guard(); token.ThrowIfCancellationRequested();
        MusicClientContextDocument.Require(IsCurrent(expected), NeedsReconciliation ? "COMMIT_UNKNOWN" : "STORAGE_CONFLICT");
        var fields = MusicWyRequestContext.ResponseFields(rows, DateTimeOffset.UtcNow);
        var sent = MusicWyRequestContext.Cookies(expected, accountCookie);
        if (fields.TryGetValue("deviceId", out var device) && device is not null)
            MusicClientContextDocument.Require(device == sent["deviceId"], "IDENTITY_MISMATCH");
        if (sent["deviceId"] != expected.DeviceId) return expected;
        string Value(string key, string previous) => fields.GetValueOrDefault(key) ?? previous;
        var next = expected with
        {
            Nuid = Value("_ntes_nuid", expected.Nuid), Nnid = Value("_ntes_nnid", expected.Nnid),
            Wnmcid = Value("WNMCID", expected.Wnmcid),
            Nmtid = fields.TryGetValue("NMTID", out var nmtid) ? nmtid : expected.Nmtid
        };
        if (next == expected) return expected;
        guard(); token.ThrowIfCancellationRequested();
        var result = await UpdateAsync(expected, document => document with
        { Wy = next with { Generation = Next(expected.Generation) } }, token, guard).ConfigureAwait(false);
        return result.Wy;
    }
}
