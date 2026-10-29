// SPDX-License-Identifier: GPL-3.0-only
// Native wire adaptation of QQMusicApi 2698673d report.py/song.py/versioning.py.
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Feedback;

internal sealed record FeedbackProtocolQqTrack(long Id, int Interval, long SingerId);

internal static class FeedbackProtocolQqRequests
{
    internal const string TrackModule = "music.trackInfo.UniformRuleCtrl";
    internal const string TrackMethod = "CgiGetTrackInfo";
    internal const string VkeyModule = "music.vkey.GetVkey";
    internal const string VkeyMethod = "UrlGetVkey";
    internal const string ReportUrl = "https://stat6.y.qq.com/android/fcgi-bin/imusic_tj";
    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static bool Hex(string value, int length) =>
        value.Length == length && value.All(char.IsAsciiHexDigit) && value.Any(c => c != '0');

    internal static void Device(MusicQqFeedbackDeviceSnapshot device, TimeProvider clock)
    {
        FeedbackProtocolRules.Require(FeedbackProtocolRules.Text(device.InstallationId, 128) &&
            device.DeviceGeneration >= 0 && device.SessionGeneration >= 0 &&
            Hex(device.OpenUdid, 32) && Hex(device.OpenUdid2, 32) && Hex(device.AndroidId, 16) &&
            FeedbackProtocolRules.Text(device.OsVersion, 32) &&
            device.OsVersion.All(c => char.IsAsciiDigit(c) || c == '.') &&
            FeedbackProtocolRules.Text(device.Model, 256) &&
            device.Qimei36.Length == 36 && device.Qimei36.All(char.IsAsciiLetterOrDigit) &&
            FeedbackProtocolRules.Text(device.Uid, 256) && FeedbackProtocolRules.Text(device.Sid, 256),
            "DEVICE_CONTEXT_REQUIRED");
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        FeedbackProtocolRules.Require(device.QimeiRetrievedAt > 0 && device.QimeiRetrievedAt <= now &&
            now - device.QimeiRetrievedAt < 86400000 &&
            device.SessionRetrievedAt > 0 && device.SessionRetrievedAt <= now, "DEVICE_CONTEXT_EXPIRED");
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(now), clock.LocalTimeZone);
        var localSession = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(device.SessionRetrievedAt), clock.LocalTimeZone);
        FeedbackProtocolRules.Require(localNow.Date == localSession.Date, "DEVICE_CONTEXT_EXPIRED");
    }

    internal static bool SameDevice(MusicQqFeedbackDeviceSnapshot a, MusicQqFeedbackDeviceSnapshot b) =>
        a.InstallationId == b.InstallationId && a.DeviceGeneration == b.DeviceGeneration &&
        a.SessionGeneration == b.SessionGeneration && a.OpenUdid == b.OpenUdid && a.OpenUdid2 == b.OpenUdid2 &&
        a.AndroidId == b.AndroidId && a.OsVersion == b.OsVersion && a.Model == b.Model &&
        a.Qimei36 == b.Qimei36 && a.QimeiRetrievedAt == b.QimeiRetrievedAt &&
        a.Uid == b.Uid && a.Sid == b.Sid && a.SessionRetrievedAt == b.SessionRetrievedAt;

    internal static Task<HttpRequestMessage> Track(MusicPlatformSession account, MusicQqFeedbackDeviceSnapshot device,
        MusicFeedbackRecording recording, long now) => Request(account, device, now, TrackModule, TrackMethod,
            new() { ["ctx"] = 0, ["client"] = 1, ["types"] = new JsonArray(0), ["modify_stamp"] = new JsonArray(0),
                ["mids"] = new JsonArray(recording.Id) });

    internal static string Filename(string mid) => "M500" + mid + mid + ".mp3";

    internal static Task<HttpRequestMessage> Vkey(MusicPlatformSession account, MusicQqFeedbackDeviceSnapshot device,
        MusicFeedbackRecording recording, long now) => Request(account, device, now, VkeyModule, VkeyMethod,
            new() { ["uin"] = account.UserId, ["filename"] = new JsonArray(Filename(recording.Id)),
                // Fixed get_guid(): per-request UUIDv4 nonce, never a device or account identity.
                ["guid"] = Guid.NewGuid().ToString("N"), ["songmid"] = new JsonArray(recording.Id),
                ["songtype"] = new JsonArray(0), ["ctx"] = 0 });

    private static void AddDevice(JsonObject comm, MusicPlatformSession account,
        MusicQqFeedbackDeviceSnapshot device, long now, bool xml)
    {
        comm["traceid"] = "10002_" + account.UserId + "_" + Decimal(now);
        comm["QIMEI36"] = device.Qimei36; comm["OpenUDID"] = device.OpenUdid; comm["udid"] = device.OpenUdid;
        comm["uid"] = device.Uid; comm["OpenUDID2"] = device.OpenUdid2; comm["sid"] = device.Sid;
        comm["aid"] = device.AndroidId; comm["os_ver"] = device.OsVersion;
        // versioning.py escapes the model for raw XML. XmlWriter performs that escaping itself.
        comm["phonetype"] = xml ? device.Model : device.Model.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
    }

    private static string Cookie(MusicPlatformSession account)
    {
        var cookies = FeedbackProtocolRules.Cookies(account);
        string key = cookies["musickey"];
        return "uin=" + account.UserId + "; qqmusic_uin=" + account.UserId + "; qm_keyst=" + key + "; qqmusic_key=" + key;
    }

    private static void Headers(HttpRequestMessage request, MusicPlatformSession account, MusicQqFeedbackDeviceSnapshot device)
    {
        FeedbackProtocolRules.Require(device.OsVersion.Length is > 0 and <= 32 &&
            device.OsVersion.All(c => char.IsAsciiDigit(c) || c == '.'), "DEVICE_CONTEXT_REQUIRED");
        request.Headers.Remove("User-Agent");
        // The fixed upstream UA has no product/comment separator accepted by .NET's
        // typed parser. Preserve its literal recipe, as TxWithCookie already does;
        // only the checked numeric OS version is substituted, never caller headers.
        FeedbackProtocolRules.Require(request.Headers.TryAddWithoutValidation("User-Agent",
            "QQMusic 20090008(android " + device.OsVersion + ")"));
        request.Headers.Add("Cookie", Cookie(account));
        request.Headers.Host = request.RequestUri!.Host;
        request.Headers.Add("Accept", "*/*");
    }

    private static async Task<HttpRequestMessage> Request(MusicPlatformSession account, MusicQqFeedbackDeviceSnapshot device,
        long now, string module, string method, JsonObject parameters)
    {
        // Reuse the existing native account/comm recipe; no Python/Node transport or signer.
        var request = MusicPlatformRequests.TxWithCookie(module, method, parameters, account.Cookie);
        try
        {
            var root = JsonNode.Parse(await request.Content!.ReadAsStringAsync().ConfigureAwait(false))!.AsObject();
            AddDevice(root["comm"]!.AsObject(), account, device, now, false);
            string body = root.ToJsonString(FeedbackProtocolRules.Json);
            FeedbackProtocolRules.Require(Encoding.UTF8.GetByteCount(body) <= 65536, "LIMIT");
            request.Content.Dispose();
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            Headers(request, account, device);
            return request;
        }
        catch { request.Dispose(); throw; }
    }

    private static long Integer(JsonNode? node, string code = "INVALID_RESPONSE")
    {
        FeedbackProtocolRules.Require(node is JsonValue, code);
        string raw = node!.ToJsonString();
        FeedbackProtocolRules.Require(long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value), code);
        return value;
    }

    private static JsonObject Success(JsonObject root)
    {
        foreach (var node in new[] { root["code"], (root["req_0"] as JsonObject)?["code"] })
        {
            long code = Integer(node, "UPSTREAM_UNCONFIRMED");
            if (code != 0) throw new MusicFeedbackException(code switch
            {
                1000 or 104400 or 104401 => "AUTH_EXPIRED", 2000 => "SIGNATURE_REQUIRED", 2001 => "RATE_LIMITED",
                _ => "UPSTREAM_UNCONFIRMED"
            });
        }
        FeedbackProtocolRules.Require(root["req_0"]?["data"] is JsonObject, "INVALID_RESPONSE");
        return root["req_0"]!["data"]!.AsObject();
    }

    internal static FeedbackProtocolQqTrack VerifyTrack(JsonObject root, MusicFeedbackStart input)
    {
        var body = Success(root);
        FeedbackProtocolRules.Require(body["tracks"] is JsonArray { Count: 1 } songs && songs[0] is JsonObject, "IDENTITY_MISMATCH");
        var song = body["tracks"]![0]!.AsObject();
        FeedbackProtocolRules.Require(song["mid"] is JsonValue mid && mid.TryGetValue<string>(out var text) &&
            text == input.Recording.Id, "IDENTITY_MISMATCH");
        long id = Integer(song["id"]), duration = Integer(song["interval"]);
        FeedbackProtocolRules.Require(id is > 0 and <= 9007199254740991 &&
            duration is > 0 and <= 86400 && FeedbackProtocolRules.CompatibleDuration(duration, input.Duration), "IDENTITY_MISMATCH");
        long singerId = 0;
        if (song["singer"] is { } singer)
        {
            FeedbackProtocolRules.Require(singer is JsonArray, "INVALID_RESPONSE");
            if (singer.AsArray().Count > 0)
            {
                FeedbackProtocolRules.Require(singer[0] is JsonObject, "INVALID_RESPONSE");
                singerId = Integer(singer[0]!["id"]);
                FeedbackProtocolRules.Require(singerId is >= 0 and <= 9007199254740991, "INVALID_RESPONSE");
            }
        }
        return new(id, (int)duration, singerId);
    }

    internal static string VerifyVkey(JsonObject root, MusicFeedbackRecording recording)
    {
        var body = Success(root);
        FeedbackProtocolRules.Require(body["midurlinfo"] is JsonArray { Count: 1 } rows && rows[0] is JsonObject, "VKEY_REQUIRED");
        var row = body["midurlinfo"]![0]!.AsObject();
        FeedbackProtocolRules.Require(Integer(row["result"], "VKEY_REQUIRED") == 0 &&
            row["songmid"] is JsonValue mid && mid.TryGetValue<string>(out var m) && m == recording.Id &&
            row["filename"] is JsonValue filename && filename.TryGetValue<string>(out var f) && f == Filename(recording.Id) &&
            row["vkey"] is JsonValue key && key.TryGetValue<string>(out var k) && FeedbackProtocolRules.Text(k, 4096),
            "VKEY_REQUIRED");
        return row["vkey"]!.GetValue<string>();
    }

    internal static async Task<HttpRequestMessage> Report(MusicPlatformSession account, MusicQqFeedbackDeviceSnapshot device,
        FeedbackProtocolQqTrack track, string vkey, long listened, long now)
    {
        FeedbackProtocolRules.Require(listened is >= 1 and <= 86400, "INSUFFICIENT_LISTEN_TIME");
        using var template = MusicPlatformRequests.TxWithCookie(TrackModule, TrackMethod, new(), account.Cookie);
        var root = JsonNode.Parse(await template.Content!.ReadAsStringAsync().ConfigureAwait(false))!.AsObject();
        var comm = root["comm"]!.AsObject();
        AddDevice(comm, account, device, now, true);
        using var plain = new MemoryStream();
        using (var xml = XmlWriter.Create(plain, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false, true), Indent = true, IndentChars = "    ",
            NewLineChars = "\r\n", CloseOutput = false, CheckCharacters = true
        }))
        {
            xml.WriteStartDocument(); xml.WriteStartElement("root");
            foreach (var field in comm) xml.WriteElementString(field.Key, FeedbackProtocolRules.Scalar(field.Value));
            xml.WriteElementString("cid", "228"); xml.WriteStartElement("item");
            void Attribute(string name, string value) => xml.WriteAttributeString(name, value);
            Attribute("cmd", "1"); Attribute("optime", Decimal(now)); Attribute("QQ", account.UserId);
            Attribute("uid", device.Uid); Attribute("songtype", "1"); Attribute("playtype", "5");
            Attribute("time", Decimal(listened)); Attribute("play_duration_mi", Decimal(listened * 1000));
            Attribute("audiotime", Decimal(track.Interval * 1000L)); Attribute("songid", Decimal(track.Id));
            Attribute("singerid", Decimal(track.SingerId)); Attribute("vkey", vkey);
            byte[] digestInput = Encoding.UTF8.GetBytes(Decimal(now) + Decimal(listened) + account.UserId + "gk2$Lh-&l4#!4iow");
            try { Attribute("timekey", Convert.ToHexString(MD5.HashData(digestInput))); }
            finally { CryptographicOperations.ZeroMemory(digestInput); }
            xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndDocument();
        }
        FeedbackProtocolRules.Require(plain.Length <= 65536, "LIMIT");
        using var compressed = new MemoryStream();
        plain.Position = 0;
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) plain.CopyTo(gzip);
        FeedbackProtocolRules.Require(compressed.Length <= 65536, "LIMIT");
        var request = new HttpRequestMessage(HttpMethod.Post, ReportUrl) { Content = new ByteArrayContent(compressed.ToArray()) };
        try
        {
            request.Content.Headers.ContentEncoding.Add("gzip");
            Headers(request, account, device);
            return request;
        }
        catch { request.Dispose(); throw; }
    }
}
