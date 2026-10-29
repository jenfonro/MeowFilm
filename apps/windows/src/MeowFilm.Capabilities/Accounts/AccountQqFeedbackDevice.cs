// SPDX-License-Identifier: GPL-3.0-only
// QQMusicApi 2698673d: utils/device.py, qimei.py, android_session.py.
// A fixed source-supported virtual vivo profile; no hardware collection or invented QIMEI.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MeowFilm.Capabilities.Accounts;

internal sealed record StoredQqQimei([property: JsonIgnore] long Generation,
    [property: JsonIgnore] string Q16, [property: JsonIgnore] string Q36, [property: JsonIgnore] long SavedAt)
{
    public override string ToString() => "StoredQqQimei (redacted)";
}
internal sealed record StoredQqDeviceSession([property: JsonIgnore] long Generation, [property: JsonIgnore] string Uid,
    [property: JsonIgnore] string Sid, [property: JsonIgnore] long SavedAt, [property: JsonIgnore] long QimeiGeneration)
{
    public override string ToString() => "StoredQqDeviceSession (redacted)";
}
internal sealed record StoredQqFeedbackDevice
{
    internal long Generation { get; init; }
    internal string InstallationId { get; init; } = "";
    internal string AndroidId { get; init; } = "";
    internal string OpenUdid { get; init; } = "";
    internal string OpenUdid2 { get; init; } = "";
    internal string Imei { get; init; } = "";
    internal StoredQqQimei? Qimei { get; init; }
    internal StoredQqDeviceSession? Session { get; init; }
    internal string? Fingerprint { get; init; }
    internal static bool Same(StoredQqFeedbackDevice a, StoredQqFeedbackDevice b) =>
        a.Generation == b.Generation && a.Fingerprint == b.Fingerprint;
    internal bool QimeiCurrent(DateTimeOffset now) => Qimei is { } q &&
        q.SavedAt <= now.ToUnixTimeMilliseconds() && now.ToUnixTimeMilliseconds() - q.SavedAt < 86400000;
    internal bool SessionCurrent(DateTimeOffset now) => QimeiCurrent(now) && Session is { } s &&
        s.QimeiGeneration == Qimei!.Generation && s.SavedAt <= now.ToUnixTimeMilliseconds() &&
        DateTimeOffset.FromUnixTimeMilliseconds(s.SavedAt).ToLocalTime().Date == now.ToLocalTime().Date;
    internal static StoredQqFeedbackDevice Create()
    {
        string digits = string.Concat(Enumerable.Range(0, 14).Select(_ => (char)('0' + RandomNumberGenerator.GetInt32(10))));
        int sum = digits.Select((c, i) => i % 2 == 0 ? c - '0' : (c - '0') * 2 > 9 ? (c - '0') * 2 - 9 : (c - '0') * 2).Sum();
        return new() { InstallationId = Guid.NewGuid().ToString("D"), AndroidId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            OpenUdid = Guid.NewGuid().ToString("N"), OpenUdid2 = Guid.NewGuid().ToString("N"),
            Imei = digits + (char)('0' + (10 - sum % 10) % 10) };
    }
    public override string ToString() => "StoredQqFeedbackDevice (redacted)";
}

internal sealed partial class AccountDocument
{
    internal const string QqFeedbackDeviceKey = "[\"meowfilm\",\"music\",1,\"platform-device\",\"tx-feedback\"]";
    internal StoredQqFeedbackDevice GetQqFeedbackDevice()
    {
        try
        {
            var row = root["entries"]!.AsArray().FirstOrDefault(n => n!["key"]!.GetValue<string>() == QqFeedbackDeviceKey);
            if (row is null) return new();
            return ReadQqFeedbackDevice(JsonSerializer.SerializeToElement(row["value"], Encoding));
        }
        catch { throw new MusicAccountException("CORRUPT_STORAGE"); }
    }
    private static StoredQqFeedbackDevice ReadQqFeedbackDevice(JsonElement raw)
    {
        string[] fields = ["version", "generation", "profile", "installationId", "androidId", "openUdid", "openUdid2", "imei", "qimei", "session"];
        AccountRules.Fields(raw, fields, fields, "CORRUPT_STORAGE");
        AccountRules.Require(AccountRules.Integer(raw.GetProperty("version")) == 1 &&
            raw.GetProperty("profile").GetString() == "vivo-v1", "CORRUPT_STORAGE");
        long generation = AccountRules.Integer(raw.GetProperty("generation"));
        string Read(string key) => raw.GetProperty(key).GetString()!;
        string installation = Read("installationId"), android = Read("androidId"), udid = Read("openUdid"), udid2 = Read("openUdid2"), imei = Read("imei");
        AccountRules.Require(generation > 0 && Guid.TryParseExact(installation, "D", out _) &&
            AccountRules.Match(android, "^[0-9a-f]{16}$") && AccountRules.Match(udid, "^[0-9a-f]{32}$") &&
            AccountRules.Match(udid2, "^[0-9a-f]{32}$") && udid != udid2 && AccountRules.Match(imei, "^[0-9]{15}$"), "CORRUPT_STORAGE");
        int sum = imei.Select((c, i) => i % 2 == 0 ? c - '0' : (c - '0') * 2 > 9 ? (c - '0') * 2 - 9 : (c - '0') * 2).Sum();
        AccountRules.Require(sum % 10 == 0, "CORRUPT_STORAGE");
        StoredQqQimei? qimei = null; StoredQqDeviceSession? session = null;
        if (raw.GetProperty("qimei") is { ValueKind: not JsonValueKind.Null } q)
        {
            AccountRules.Fields(q, ["generation", "q16", "q36", "savedAt"], ["generation", "q16", "q36", "savedAt"], "CORRUPT_STORAGE");
            qimei = new(AccountRules.Integer(q.GetProperty("generation")), QqDeviceText(q.GetProperty("q16").GetString()),
                QqDeviceText(q.GetProperty("q36").GetString()), AccountRules.Integer(q.GetProperty("savedAt")));
            AccountRules.Require(qimei.Generation is > 0 && qimei.Generation <= generation && qimei.SavedAt is > 0 and <= 253402300799999,
                "CORRUPT_STORAGE");
        }
        if (raw.GetProperty("session") is { ValueKind: not JsonValueKind.Null } s)
        {
            string[] sf = ["generation", "uid", "sid", "savedAt", "qimeiGeneration"];
            AccountRules.Fields(s, sf, sf, "CORRUPT_STORAGE");
            session = new(AccountRules.Integer(s.GetProperty("generation")), QqDeviceText(s.GetProperty("uid").GetString()),
                QqDeviceText(s.GetProperty("sid").GetString()), AccountRules.Integer(s.GetProperty("savedAt")),
                AccountRules.Integer(s.GetProperty("qimeiGeneration")));
            AccountRules.Require(qimei is not null && session.Generation is > 0 && session.Generation <= generation &&
                session.SavedAt is > 0 and <= 253402300799999 && session.QimeiGeneration > 0 &&
                session.QimeiGeneration <= qimei!.Generation, "CORRUPT_STORAGE");
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(raw, Encoding);
        try { return new() { Generation = generation, InstallationId = installation, AndroidId = android,
            OpenUdid = udid, OpenUdid2 = udid2, Imei = imei, Qimei = qimei, Session = session,
            Fingerprint = Convert.ToHexStringLower(SHA256.HashData(bytes)) }; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static string QqDeviceText(string? value)
    {
        AccountRules.Require(value is { Length: > 0 and <= 512 } && value.All(c => c is >= '!' and <= '~') &&
            !value.Any(c => c is '"' or '\\' or '<' or '>' or '&' or ';' or ','), "INVALID_RESPONSE");
        return value!;
    }
    internal byte[] ReplaceQqFeedbackDevice(StoredQqFeedbackDevice expected, StoredQqFeedbackDevice next)
    {
        var current = GetQqFeedbackDevice();
        AccountRules.Require(StoredQqFeedbackDevice.Same(current, expected), "STORAGE_CONFLICT");
        AccountRules.Require(current.Generation < AccountRules.MaxInteger && next.Generation == current.Generation + 1, "LIMIT");
        AccountRules.Require(current.Generation == 0 || current.InstallationId == next.InstallationId && current.AndroidId == next.AndroidId &&
            current.OpenUdid == next.OpenUdid && current.OpenUdid2 == next.OpenUdid2 && current.Imei == next.Imei, "INVALID_REQUEST");
        var value = new JsonObject { ["version"] = 1, ["generation"] = next.Generation, ["profile"] = "vivo-v1",
            ["installationId"] = next.InstallationId, ["androidId"] = next.AndroidId, ["openUdid"] = next.OpenUdid,
            ["openUdid2"] = next.OpenUdid2, ["imei"] = next.Imei,
            ["qimei"] = next.Qimei is { } q ? new JsonObject { ["generation"] = q.Generation, ["q16"] = q.Q16, ["q36"] = q.Q36, ["savedAt"] = q.SavedAt } : null,
            ["session"] = next.Session is { } s ? new JsonObject { ["generation"] = s.Generation, ["uid"] = s.Uid, ["sid"] = s.Sid,
                ["savedAt"] = s.SavedAt, ["qimeiGeneration"] = s.QimeiGeneration } : null };
        var entries = root["entries"]!.AsArray();
        var row = entries.FirstOrDefault(n => n!["key"]!.GetValue<string>() == QqFeedbackDeviceKey);
        if (row is null)
        {
            AccountRules.Require(entries.Count < CurrentEntryCount, "LIMIT");
            entries.Add(new JsonObject { ["key"] = QqFeedbackDeviceKey, ["value"] = value });
        }
        else row["value"] = value;
        _ = GetQqFeedbackDevice(); // Apply the same strict validation before persistence.
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(root, Encoding);
        if (bytes.Length <= MaximumBytes) return bytes;
        CryptographicOperations.ZeroMemory(bytes); throw new MusicAccountException("LIMIT");
    }
}
