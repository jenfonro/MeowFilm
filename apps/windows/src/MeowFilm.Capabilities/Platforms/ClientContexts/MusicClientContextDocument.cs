// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Platforms;

internal sealed record MusicClientContextDocument(MusicWyClientContext Wy,
    MusicKgClientContext Kg, MusicQqClientContext Qq)
{
    internal const int MaximumBytes = 65536;
    private const long MaximumInteger = 9007199254740991;
    private const long MaximumTimestamp = 253402300799999;
    internal static MusicClientContextDocument Create() =>
        new(MusicWyClientContext.Create(), MusicKgClientContext.Create(), MusicQqClientContext.Create());
    internal byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 1,
        wy = new { generation = Wy.Generation, deviceId = Wy.DeviceId, nuid = Wy.Nuid,
            nnid = Wy.Nnid, wnmcid = Wy.Wnmcid, nmtid = Wy.Nmtid },
        kg = new { generation = Kg.Generation, seed = Kg.Seed, dfid = Kg.Dfid },
        tx = new { generation = Qq.Generation, installationId = Qq.InstallationId, androidId = Qq.AndroidId,
            openUdid = Qq.OpenUdid, openUdid2 = Qq.OpenUdid2, imei = Qq.Imei,
            qimei = Qq.Qimei is { } q ? new { generation = q.Generation, q16 = q.Q16, q36 = q.Q36, savedAt = q.SavedAt } : null,
            session = Qq.Session is { } s ? new { generation = s.Generation, uid = s.Uid, sid = s.Sid,
                savedAt = s.SavedAt, qimeiGeneration = s.QimeiGeneration } : null }
    });

    internal static MusicClientContextDocument Decode(DocumentSnapshot document)
    {
        try
        {
            Require(document.Data is not null && document.Data.Length <= MaximumBytes &&
                document.Fingerprint == DocumentFingerprint.FromBytes(document.Data), "CORRUPT_STORAGE");
            using var json = JsonDocument.Parse(document.Data!, new JsonDocumentOptions { MaxDepth = 12 });
            CheckJson(json.RootElement, 0);
            var root = json.RootElement;
            Fields(root, "version", "wy", "kg", "tx");
            Require(Integer(root, "version") == 1, "CORRUPT_STORAGE");
            var wy = root.GetProperty("wy"); Fields(wy, "generation", "deviceId", "nuid", "nnid", "wnmcid", "nmtid");
            var kg = root.GetProperty("kg"); Fields(kg, "generation", "seed", "dfid");
            var qq = root.GetProperty("tx"); Fields(qq, "generation", "installationId", "androidId", "openUdid", "openUdid2", "imei", "qimei", "session");
            var w = new MusicWyClientContext(Generation(wy), Text(wy, "deviceId", 256),
                Text(wy, "nuid", 128), Text(wy, "nnid", 256), Text(wy, "wnmcid", 256), Optional(wy, "nmtid", 8192));
            var k = new MusicKgClientContext(Generation(kg), Text(kg, "seed", 32), Optional(kg, "dfid", 128));
            Require(Regex.IsMatch(k.Seed, "^[a-f0-9]{32}$"), "CORRUPT_STORAGE");
            if (k.Dfid is { } dfid) Require(Regex.IsMatch(dfid, "^[A-Za-z0-9_-]{8,128}$"), "CORRUPT_STORAGE");
            var installation = Text(qq, "installationId", 36);
            Require(Guid.TryParseExact(installation, "D", out _), "CORRUPT_STORAGE");
            var android = Text(qq, "androidId", 16); var udid = Text(qq, "openUdid", 32); var udid2 = Text(qq, "openUdid2", 32);
            Require(Regex.IsMatch(android, "^[a-f0-9]{16}$") && Regex.IsMatch(udid, "^[a-f0-9]{32}$") &&
                Regex.IsMatch(udid2, "^[a-f0-9]{32}$") && udid != udid2, "CORRUPT_STORAGE");
            var imei = Text(qq, "imei", 15);
            Require(Regex.IsMatch(imei, "^[0-9]{15}$"), "CORRUPT_STORAGE");
            int sum = imei.Select((c, i) => i % 2 == 0 ? c - '0' : (c - '0') * 2 > 9 ? (c - '0') * 2 - 9 : (c - '0') * 2).Sum();
            Require(sum % 10 == 0, "CORRUPT_STORAGE");
            var qqGeneration = Generation(qq);
            MusicClientQimei? qimei = null; MusicClientDeviceSession? session = null;
            if (qq.GetProperty("qimei") is { ValueKind: not JsonValueKind.Null } q)
            {
                Fields(q, "generation", "q16", "q36", "savedAt");
                qimei = new(Generation(q), DeviceText(q, "q16"), DeviceText(q, "q36"), Timestamp(q, "savedAt"));
                Require(qimei.Generation <= qqGeneration, "CORRUPT_STORAGE");
            }
            if (qq.GetProperty("session") is { ValueKind: not JsonValueKind.Null } s)
            {
                Fields(s, "generation", "uid", "sid", "savedAt", "qimeiGeneration");
                session = new(Generation(s), DeviceText(s, "uid"), DeviceText(s, "sid"),
                    Timestamp(s, "savedAt"), Generation(s, "qimeiGeneration"));
                Require(qimei is not null && session.Generation <= qqGeneration &&
                    session.QimeiGeneration <= qimei.Generation, "CORRUPT_STORAGE");
            }
            return new(w, k, new(qqGeneration, installation, android, udid, udid2, imei, qimei, session));
        }
        catch (MusicPlatformException) { throw; }
        catch { throw new MusicPlatformException("CORRUPT_STORAGE"); }
        finally { if (document.Data is not null) CryptographicOperations.ZeroMemory(document.Data); }
    }
    internal static void Require(bool condition, string code)
    { if (!condition) throw new MusicPlatformException(code); }
    private static void Fields(JsonElement value, params string[] names)
    {
        Require(value.ValueKind == JsonValueKind.Object, "CORRUPT_STORAGE");
        var actual = value.EnumerateObject().Select(p => p.Name).ToArray();
        Require(actual.Length == names.Length && actual.ToHashSet(StringComparer.Ordinal).SetEquals(names), "CORRUPT_STORAGE");
    }
    private static void CheckJson(JsonElement value, int depth)
    {
        Require(depth <= 12, "CORRUPT_STORAGE");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            { Require(names.Add(p.Name), "CORRUPT_STORAGE"); CheckJson(p.Value, depth + 1); }
        }
        else if (value.ValueKind == JsonValueKind.Array) throw new MusicPlatformException("CORRUPT_STORAGE");
    }
    private static long Integer(JsonElement value, string name)
    {
        var n = value.GetProperty(name);
        Require(n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var result) &&
            result is >= 0 and <= MaximumInteger, "CORRUPT_STORAGE");
        return n.GetInt64();
    }
    private static long Generation(JsonElement value, string name = "generation")
    { var n = Integer(value, name); Require(n > 0, "CORRUPT_STORAGE"); return n; }
    private static long Timestamp(JsonElement value, string name)
    { var n = Integer(value, name); Require(n is > 0 and <= MaximumTimestamp, "CORRUPT_STORAGE"); return n; }
    private static string DeviceText(JsonElement value, string name)
    {
        var text = Text(value, name, 512);
        Require(!text.Any(c => c is '<' or '>' or '&' or ','), "CORRUPT_STORAGE");
        return text;
    }
    internal static string ValidateDeviceText(string? text)
    {
        Require(text is { Length: > 0 and <= 512 } && text.All(c => c is >= '!' and <= '~') &&
            !text.Any(c => c is '"' or '\\' or '<' or '>' or '&' or ';' or ','), "INVALID_RESPONSE");
        return text!;
    }
    private static string Text(JsonElement value, string name, int maximum)
    {
        var field = value.GetProperty(name);
        Require(field.ValueKind == JsonValueKind.String, "CORRUPT_STORAGE");
        var text = field.GetString()!;
        Require(text.Length is > 0 && text.Length <= maximum &&
            text.All(c => c is >= '!' and <= '~' && c is not (';' or '"' or '\\')), "CORRUPT_STORAGE");
        return text;
    }
    private static string? Optional(JsonElement value, string name, int maximum) =>
        value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Text(value, name, maximum);
}
