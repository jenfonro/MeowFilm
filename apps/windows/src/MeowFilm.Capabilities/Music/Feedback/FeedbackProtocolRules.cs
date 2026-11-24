// SPDX-License-Identifier: GPL-3.0-only
// Native listening/attempt facts; platform wire protocols are owned by Node.
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Feedback;

internal static class FeedbackProtocolRules
{
    internal static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static void Require([DoesNotReturnIf(false)] bool valid, string code = "INVALID_REQUEST")
    { if (!valid) throw new MusicFeedbackException(code); }
    internal static bool Text(string? value, bool empty = false)
    {
        if (value is null || !empty && value.Length == 0) return false;
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false; }
            else if (char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
    internal static bool Id(string? value) => Text(value);
    internal static MusicFeedbackStart Start(MusicPlatformSession account, MusicFeedbackStart value)
    {
        Require(value is not null && value.Recording is not null);
        Require(value!.Recording.Platform == account.Platform, "IDENTITY_MISMATCH");
        Require(value.Attempt is not null);
        var recording = value.Recording;
        Require(Id(recording.Id), "INVALID_REQUEST");
        if (account.Platform == "wy")
        {
            Require(FactSeconds(value.Duration) && FactSeconds(value.Position) &&
                (value.Duration == 0 || value.Position <= value.Duration) &&
                double.IsFinite(value.Rate) && value.Rate > 0);
            Require(value.PlaybackId != Guid.Empty && value.StartedAt >= DateTimeOffset.UnixEpoch &&
                value.AdmittedAt >= DateTimeOffset.UnixEpoch);
            // Preserve Unicode scalars in the persisted playback facts.
            Require(Text(value.Source, true) && Text(value.SourceId, true) && Text(value.Algorithm, true));
            return value;
        }
        Require(Seconds(value.Duration) && value.Duration > 0, "DURATION_REQUIRED");
        Require(Seconds(value.Position) && value.Position <= value.Duration + (account.Platform == "kg" ? 1 : 0));
        Require(Rate(value.Rate, account.Platform));
        Require(!value.IsQueueRecommendation);
        return account.Platform == "kg" ? value with { Recording = recording with { Id = recording.Id.ToUpperInvariant() } } : value;
    }
    private static bool FactSeconds(double n) => double.IsFinite(n) && n >= 0;
    private static bool Seconds(double n) => double.IsFinite(n) && n is >= 0 and <= 86400;
    private static bool Rate(double n, string platform) => double.IsFinite(n) && n > 0 && n <= 16 && (platform != "kg" || n >= .25);
    internal static bool CompatibleDuration(double a, double b) =>
        Seconds(a) && Seconds(b) && a > 0 && b > 0 && Math.Abs(a - b) <= Math.Max(3, Math.Min(a, b) * .02);
    internal static void End(MusicFeedbackStart start, MusicFeedbackEnd end, TimeSpan elapsed)
    {
        Require(end is not null);
        if (start.Recording.Platform == "wy")
        {
            Require(FactSeconds(end.Listened) && FactSeconds(end.MediaListened) && FactSeconds(end.Duration) &&
                FactSeconds(end.Position) && double.IsFinite(end.Rate) && end.Rate > 0 &&
                (end.Duration == 0 || end.Position <= end.Duration));
            Require(end.PlaybackId == start.PlaybackId && end.EndedAt >= DateTimeOffset.UnixEpoch);
            Require(end.Reason is "playend" or "ui" or "interrupt" or "exception");
            return; // Shared monotonic facts, never elapsed since a late dispatch.
        }
        Require(Seconds(end.Listened) && Seconds(end.MediaListened) && Seconds(end.Duration) && Seconds(end.Position) &&
            Rate(end.Rate, start.Recording.Platform));
        Require(CompatibleDuration(start.Duration, end.Duration), "IDENTITY_MISMATCH");
        Require(end.Position <= end.Duration + (start.Recording.Platform == "kg" ? 1 : 0));
        Require(end.Reason is "playend" or "ui" or "interrupt" or "exception");
        Require(end.Listened <= Math.Max(0, elapsed.TotalSeconds) + 2 &&
            end.MediaListened <= end.Listened * 16 + 1 &&
            (start.Recording.Platform != "kg" || end.MediaListened + 1 >= end.Listened * .25), "INVALID_LISTEN_TIME");
    }
    internal static string Fingerprint(MusicPlatformSession account, MusicFeedbackStart start)
    {
        // Attempt-local equality check; no process-wide history or outbound queue.
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            account.Platform, account.Scope, account.Generation, account.UserId, account.Cookie,
            Start = start, start.IsQueueRecommendation
        }, Json);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
    internal static string SafeError(Exception error) => error is MusicPlatformException known ? known.Code : "UPSTREAM_ERROR";
}
