// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics.CodeAnalysis;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Feedback;

internal readonly record struct FeedbackAttemptKey(
    string Platform,
    string Scope,
    long Generation,
    string UserId,
    string Cookie,
    MusicFeedbackStart Start,
    bool IsQueueRecommendation);

internal static class FeedbackProtocolRules
{
    internal static void Require([DoesNotReturnIf(false)] bool valid, string code = "INVALID_REQUEST")
    { if (!valid) throw new MusicFeedbackException(code); }
    internal static bool Id(string? value) => !string.IsNullOrEmpty(value);
    internal static MusicFeedbackStart Start(MusicPlatformSession account, MusicFeedbackStart value)
    {
        if (value?.Recording is not { } recording || value.Attempt is null)
            throw new MusicFeedbackException("INVALID_REQUEST");
        Require(recording.Platform == account.Platform, "IDENTITY_MISMATCH");
        Require(Id(recording.Id));
        if (account.Platform == "wy") return value;
        Require(Seconds(value.Duration) && value.Duration > 0, "DURATION_REQUIRED");
        Require(Seconds(value.Position) && value.Position <= value.Duration + (account.Platform == "kg" ? 1 : 0));
        Require(Rate(value.Rate, account.Platform) && !value.IsQueueRecommendation);
        return account.Platform == "kg" ? value with { Recording = recording with { Id = recording.Id.ToUpperInvariant() } } : value;
    }
    private static bool Seconds(double n) => double.IsFinite(n) && n is >= 0 and <= 86400;
    private static bool Rate(double n, string platform) => double.IsFinite(n) && n > 0 && n <= 16 && (platform != "kg" || n >= .25);
    internal static bool CompatibleDuration(double a, double b) =>
        Seconds(a) && Seconds(b) && a > 0 && b > 0 && Math.Abs(a - b) <= Math.Max(3, Math.Min(a, b) * .02);
    internal static void End(MusicFeedbackStart start, MusicFeedbackEnd end, TimeSpan elapsed)
    {
        Require(end is not null && end.Reason is "playend" or "ui" or "interrupt" or "exception");
        if (start.Recording.Platform == "wy")
        {
            Require(end.PlaybackId == start.PlaybackId);
            return;
        }
        Require(Seconds(end.Listened) && Seconds(end.MediaListened) && Seconds(end.Duration) && Seconds(end.Position) &&
            Rate(end.Rate, start.Recording.Platform));
        Require(CompatibleDuration(start.Duration, end.Duration), "IDENTITY_MISMATCH");
        Require(end.Position <= end.Duration + (start.Recording.Platform == "kg" ? 1 : 0));
        Require(end.Listened <= Math.Max(0, elapsed.TotalSeconds) + 2 &&
            end.MediaListened <= end.Listened * 16 + 1 &&
            (start.Recording.Platform != "kg" || end.MediaListened + 1 >= end.Listened * .25), "INVALID_LISTEN_TIME");
    }
    internal static FeedbackAttemptKey AttemptKey(MusicPlatformSession account, MusicFeedbackStart start) =>
        new(account.Platform, account.Scope, account.Generation, account.UserId, account.Cookie, start, start.IsQueueRecommendation);
    internal static string SafeError(Exception error) => error is MusicPlatformException known ? known.Code : "UPSTREAM_ERROR";
}
