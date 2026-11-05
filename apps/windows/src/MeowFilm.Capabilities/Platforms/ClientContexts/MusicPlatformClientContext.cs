// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace MeowFilm.Capabilities.Platforms;

internal abstract record MusicPlatformClientContext(
    [property: JsonIgnore] long Generation)
{
    [JsonIgnore] internal abstract string Platform { get; }
    public sealed override string ToString() => "MusicPlatformClientContext (redacted)";
}

internal sealed record MusicWyClientContext(
    long Generation, [property: JsonIgnore] string DeviceId,
    [property: JsonIgnore] string Nuid, [property: JsonIgnore] string Nnid,
    [property: JsonIgnore] string Wnmcid, [property: JsonIgnore] string? Nmtid = null)
    : MusicPlatformClientContext(Generation)
{
    internal override string Platform => "wy";
    // Ordinary response cookies share this observation; an authoritative reload does not.
    [JsonIgnore] internal object Observation { get; init; } = new();
    internal static MusicWyClientContext Create()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var nuid = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var wnmcid = string.Concat(Enumerable.Range(0, 6).Select(_ => (char)('a' + RandomNumberGenerator.GetInt32(26))));
        return new(1, Convert.ToHexString(RandomNumberGenerator.GetBytes(26)), nuid, nuid + "," + now,
            wnmcid + "." + now + ".01.0");
    }
}

internal sealed record MusicKgClientContext(
    long Generation, [property: JsonIgnore] string Seed,
    [property: JsonIgnore] string? Dfid = null)
    : MusicPlatformClientContext(Generation)
{
    internal override string Platform => "kg";
    [JsonIgnore] internal string Mid => new BigInteger(MD5.HashData(Encoding.ASCII.GetBytes(Seed)), true, true)
        .ToString(CultureInfo.InvariantCulture);
    [JsonIgnore] internal string Dev => Seed[..10].ToUpperInvariant();
    internal static MusicKgClientContext Create() => new(1, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
}

internal sealed record MusicClientQimei(
    [property: JsonIgnore] long Generation, [property: JsonIgnore] string Q16,
    [property: JsonIgnore] string Q36, [property: JsonIgnore] long SavedAt)
{
    public override string ToString() => "MusicClientQimei (redacted)";
}

internal sealed record MusicClientDeviceSession(
    [property: JsonIgnore] long Generation, [property: JsonIgnore] string Uid,
    [property: JsonIgnore] string Sid, [property: JsonIgnore] long SavedAt,
    [property: JsonIgnore] long QimeiGeneration)
{
    public override string ToString() => "MusicClientDeviceSession (redacted)";
}

internal sealed record MusicQqClientContext(
    long Generation, [property: JsonIgnore] string InstallationId,
    [property: JsonIgnore] string AndroidId, [property: JsonIgnore] string OpenUdid,
    [property: JsonIgnore] string OpenUdid2, [property: JsonIgnore] string Imei,
    [property: JsonIgnore] MusicClientQimei? Qimei = null,
    [property: JsonIgnore] MusicClientDeviceSession? Session = null)
    : MusicPlatformClientContext(Generation)
{
    internal override string Platform => "tx";
    internal bool QimeiCurrent(DateTimeOffset now) => Qimei is { } q &&
        q.SavedAt <= now.ToUnixTimeMilliseconds() && now.ToUnixTimeMilliseconds() - q.SavedAt < 86400000;
    internal bool SessionCurrent(DateTimeOffset now) => QimeiCurrent(now) && Session is { } session &&
        session.QimeiGeneration == Qimei!.Generation && session.SavedAt <= now.ToUnixTimeMilliseconds() &&
        DateTimeOffset.FromUnixTimeMilliseconds(session.SavedAt).ToLocalTime().Date == now.ToLocalTime().Date;
    internal static MusicQqClientContext Create()
    {
        string digits = string.Concat(Enumerable.Range(0, 14).Select(_ => (char)('0' + RandomNumberGenerator.GetInt32(10))));
        int sum = digits.Select((c, i) => i % 2 == 0 ? c - '0' : (c - '0') * 2 > 9 ? (c - '0') * 2 - 9 : (c - '0') * 2).Sum();
        return new(1, Guid.NewGuid().ToString("D"), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), digits + (char)('0' + (10 - sum % 10) % 10));
    }
}
