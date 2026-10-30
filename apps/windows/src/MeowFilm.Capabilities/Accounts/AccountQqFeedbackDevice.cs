// SPDX-License-Identifier: GPL-3.0-only
// Published account-v1 QQ device data is a read-only migration source.
using System.Text.Json;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

internal sealed partial class AccountDocument
{
    internal const string QqFeedbackDeviceKey = "[\"meowfilm\",\"music\",1,\"platform-device\",\"tx-feedback\"]";
    internal MusicQqClientContext? GetQqFeedbackDevice()
    {
        try
        {
            var row = root["entries"]!.AsArray().FirstOrDefault(n => n!["key"]!.GetValue<string>() == QqFeedbackDeviceKey);
            if (row is null) return null;
            return ReadQqFeedbackDevice(JsonSerializer.SerializeToElement(row["value"], Encoding));
        }
        catch { throw new MusicAccountException("CORRUPT_STORAGE"); }
    }
    private static MusicQqClientContext ReadQqFeedbackDevice(JsonElement raw)
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
        MusicClientQimei? qimei = null; MusicClientDeviceSession? session = null;
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
        return new(generation, installation, android, udid, udid2, imei, qimei, session);
    }
    private static string QqDeviceText(string? value)
    {
        AccountRules.Require(value is { Length: > 0 and <= 512 } && value.All(c => c is >= '!' and <= '~') &&
            !value.Any(c => c is '"' or '\\' or '<' or '>' or '&' or ';' or ','), "INVALID_RESPONSE");
        return value!;
    }
}
