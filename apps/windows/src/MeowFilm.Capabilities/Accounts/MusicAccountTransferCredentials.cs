// SPDX-License-Identifier: GPL-3.0-only
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

internal static class MusicAccountTransferCredentials
{
    internal static readonly string[] Wy = ["MUSIC_U", "__csrf", "JSESSIONID", "MUSIC_A", "deviceId", "_ntes_nuid", "_ntes_nnid", "WNMCID", "NMTID"];
    internal static readonly string[] Tx = ["musicid", "str_musicid", "musickey", "openid", "refresh_token", "access_token", "unionid", "refresh_key",
        "expired_at", "musickey_create_time", "key_expires_in", "login_type", "encrypt_uin"];
    internal static readonly string[] Kg = ["userid", "token", "t1", "vip_type", "vip_token", "KUGOU_API_GUID", "KUGOU_API_MID", "KUGOU_API_DEV", "KUGOU_API_MAC", "dfid"];
    private static readonly string[] QqNumbers = ["expired_at", "musickey_create_time", "key_expires_in", "login_type"];
    private static readonly Dictionary<string, string> QqAliases = new(StringComparer.Ordinal)
    {
        ["strMusicid"] = "str_musicid", ["refreshToken"] = "refresh_token", ["accessToken"] = "access_token",
        ["refreshKey"] = "refresh_key", ["expiredAt"] = "expired_at", ["musickeyCreateTime"] = "musickey_create_time",
        ["keyExpiresIn"] = "key_expires_in", ["loginType"] = "login_type", ["encryptUin"] = "encrypt_uin"
    };
    internal static string[] Allowed(string platform) => platform switch { "wy" => Wy, "tx" => Tx, "kg" => Kg, _ => [] };
    internal static string Cookie(TransferEntry entry)
    {
        AccountRules.Id(entry.UserId, "INVALID_CREDENTIALS");
        AccountRules.Require(entry.Fields.Keys.All(key => Allowed(entry.Platform).Contains(key)), "INVALID_CREDENTIALS");
        foreach (var (key, value) in entry.Fields)
            AccountRules.Require(value.All(c => c is >= '!' and <= '~' &&
                c is not (';' or '"' or '\\') && (c != ',' || key == "_ntes_nnid")), "INVALID_CREDENTIALS");
        string cookie = string.Join("; ", entry.Fields.Select(p => p.Key + "=" + p.Value));
        var parts = AccountRules.Cookies(cookie);
        if (entry.Platform == "wy")
        {
            RequireValue(parts, "MUSIC_U");
            RequireValue(parts, "deviceId"); RequireValue(parts, "_ntes_nuid");
            RequireValue(parts, "_ntes_nnid"); RequireValue(parts, "WNMCID");
        }
        else if (entry.Platform == "tx")
        {
            RequireValue(parts, "musickey");
            AccountRules.Require(parts.GetValueOrDefault("musicid") == entry.UserId &&
                (!parts.TryGetValue("str_musicid", out var strId) || strId == entry.UserId), "IDENTITY_MISMATCH");
            foreach (string field in QqNumbers)
                if (parts.TryGetValue(field, out var number))
                    _ = AccountRules.QqNumber(number, "INVALID_CREDENTIALS");
        }
        else
        {
            RequireValue(parts, "token");
            AccountRules.Require(parts.GetValueOrDefault("userid") == entry.UserId, "IDENTITY_MISMATCH");
            RequireValue(parts, "KUGOU_API_GUID"); RequireValue(parts, "KUGOU_API_MID");
            RequireValue(parts, "KUGOU_API_DEV"); RequireValue(parts, "KUGOU_API_MAC");
            AccountRules.Require(AccountRules.Match(parts["KUGOU_API_GUID"], "^[a-fA-F0-9]{32}$") &&
                parts["KUGOU_API_MID"].Length <= 39 && parts["KUGOU_API_MID"].All(char.IsAsciiDigit), "INVALID_CREDENTIALS");
            AccountRules.Require(new MusicKgClientContext(1, parts["KUGOU_API_GUID"]).Mid == parts["KUGOU_API_MID"],
                "DEVICE_CONTEXT_REQUIRED");
        }
        return cookie;
    }
    private static void RequireValue(Dictionary<string, string> parts, string field) =>
        AccountRules.Require(parts.TryGetValue(field, out var value) && value.Length > 0, "INVALID_CREDENTIALS");
    internal static TransferEntry Export(string platform, StoredAccount account, MusicPlatformClientContext? client)
    {
        var parts = AccountRules.Cookies(account.Cookie!);
        if (platform == "wy") parts = MusicWyRequestContext.Cookies((MusicWyClientContext)client!, account.Cookie);
        if (platform == "tx")
        {
            foreach (string field in QqNumbers)
                if (parts.TryGetValue(field, out var number))
                    _ = AccountRules.QqNumber(number, "INVALID_CREDENTIALS");
            foreach (var (alias, canonical) in QqAliases)
                if (parts.TryGetValue(alias, out var value))
                {
                    if (QqNumbers.Contains(canonical)) _ = AccountRules.QqNumber(value, "INVALID_CREDENTIALS");
                    AccountRules.Require(!parts.TryGetValue(canonical, out var existing) ||
                        existing.Length == 0 || value.Length == 0 || existing == value, "INVALID_CREDENTIALS");
                    if (existing is null || existing.Length == 0) parts[canonical] = value;
                }
        }
        if (platform == "kg")
        {
            var device = (MusicKgClientContext)client!;
            var actual = PlatformProtocols.KgIdentity(parts, device);
            parts["KUGOU_API_MID"] = actual.Mid;
            if (actual.Dfid != "-") parts["dfid"] = actual.Dfid;
            if (!parts.ContainsKey("KUGOU_API_GUID") && actual.Mid == device.Mid)
            {
                // This is the exact local device used by the saved legacy credential, not a new device.
                parts["KUGOU_API_GUID"] = device.Seed;
                parts["KUGOU_API_DEV"] = device.Dev;
                parts["KUGOU_API_MAC"] = "02:00:00:00:00:00";
            }
        }
        // Never invent a historical KG seed/device from the current unrelated installation.
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string field in Allowed(platform))
            if (parts.TryGetValue(field, out var value)) output.Add(field, value);
        return new TransferEntry(platform, account.Profile!.UserId, output);
    }
}
