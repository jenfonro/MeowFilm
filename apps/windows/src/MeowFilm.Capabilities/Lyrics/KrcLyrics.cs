// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 jenfonro and contributors; derived from iWebPlayer.
// Native translation of fixed iWebPlus static/kugou-lyrics.js via music-core.
// Local changes: explicit format errors, CPU/size/cancellation budgets; no wrapper.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Lyrics;

internal static class KrcLyrics
{
    private sealed record Row(long Start, string Text, string Yrc);
    private const long Safe = 9007199254740991;
    private static long Number(string value)
    {
        L.Require(long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) &&
            n >= -Safe && n <= Safe, "INVALID_RESPONSE");
        return n;
    }
    internal static (string Text, string Translation)? Convert(string krc, CancellationToken token)
    {
        var rows = new List<Row>(); var offsets = new List<string>(); var languages = new List<string>();
        var head = L.Regex(@"^\[([0-9]+),([0-9]+)\](.*)$");
        var offsetPattern = L.Regex(@"^\[offset:([+-]?[0-9]+)\]$", RegexOptions.IgnoreCase);
        var languagePattern = L.Regex(@"^\[language:([A-Za-z0-9+/=]+)\]$");
        var wordPattern = L.Regex(@"<([0-9]+),([0-9]+),([0-9]+)>(.*?)(?=<[0-9]+,[0-9]+,[0-9]+>|$)");
        int wordsTotal = 0, chars = 0, lineCount = 0;
        foreach (var raw in krc.Split('\n'))
        {
            token.ThrowIfCancellationRequested(); L.Require(++lineCount <= 10000, "LIMIT");
            var line = raw.Trim(); var offset = offsetPattern.Match(line);
            if (offset.Success) { _ = Number(offset.Groups[1].Value); offsets.Add(offset.Groups[1].Value); continue; }
            var language = languagePattern.Match(line);
            if (language.Success) { languages.Add(language.Groups[1].Value); continue; }
            var match = head.Match(line);
            if (!match.Success) { L.Require(!L.Match(line, @"^\[[0-9]+,"), "INVALID_RESPONSE"); continue; }
            var start = Number(match.Groups[1].Value); var duration = Number(match.Groups[2].Value);
            L.Require(start <= Safe - duration, "INVALID_RESPONSE");
            var body = match.Groups[3].Value; var words = wordPattern.Matches(body);
            L.Require((words.Count == 0 || words[0].Index == 0) && (words.Count != 0 || !L.Match(body, @"<[0-9]+,")), "INVALID_RESPONSE");
            var text = new StringBuilder(); var yrc = new StringBuilder();
            yrc.Append('[').Append(start.ToString(CultureInfo.InvariantCulture)).Append(',').Append(duration.ToString(CultureInfo.InvariantCulture)).Append(']');
            foreach (Match word in words)
            {
                token.ThrowIfCancellationRequested(); L.Require(++wordsTotal <= 10000, "LIMIT");
                var at = Number(word.Groups[1].Value); var length = Number(word.Groups[2].Value); var kind = Number(word.Groups[3].Value);
                L.Require(at <= Safe - start, "INVALID_RESPONSE");
                var value = word.Groups[4].Value;
                L.Require(!L.Match(value, @"\([0-9]+,[0-9]+,[0-9]+\)|<[0-9]+,"), "INVALID_RESPONSE");
                text.Append(value);
                yrc.Append('(').Append((start + at).ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(length.ToString(CultureInfo.InvariantCulture)).Append(',').Append(kind.ToString(CultureInfo.InvariantCulture)).Append(')').Append(value);
            }
            if (words.Count == 0) { text.Append(body); yrc.Append(body); }
            chars += yrc.Length + 1; L.Require(chars <= L.MaxSegment, "LIMIT");
            // Empty rows consume language placeholders and must not be dropped here.
            rows.Add(new(start, text.ToString(), yrc.ToString()));
        }
        if (!rows.Any(row => !string.IsNullOrWhiteSpace(row.Text))) return null;
        var offsetText = offsets.FirstOrDefault() ?? "0";
        var prefix = offsets.Count == 0 ? "" : "[offset:" + offsetText + "]\n";
        var original = prefix + string.Join('\n', rows.Select(row => row.Yrc));
        L.Require(original.Length <= L.MaxSegment, "LIMIT");
        var translation = "";
        if (offsets.Count <= 1 && languages.Count == 1)
        {
            // Corrupt/ambiguous language never discards a valid original KRC.
            // Only a single version-1 type-1 track with exact row count can align.
            try
            {
                var bytes = System.Convert.FromBase64String(languages[0]);
                L.Require(bytes.Length <= L.MaxDecodedBytes, "LIMIT");
                var language = L.Parse(bytes, token);
                if (L.Integer(language["version"]) == 1 && language["content"] is JsonArray content)
                {
                    var tracks = content.OfType<JsonObject>().Where(t => PJ.Scalar(t["type"]) == "1").ToArray();
                    if (tracks.Length == 1 && tracks[0]["lyricContent"] is JsonArray translated && translated.Count == rows.Count &&
                        translated.All(t => t is JsonArray { Count: 1 } one && one[0] is JsonValue v && v.TryGetValue<string>(out var s) &&
                            !L.Match(s, @"[\r\n]|\[[0-9]{1,3}:[0-9]{2}|<[0-9]{1,3}:[0-9]{2}")))
                    {
                        var shift = Number(offsetText);
                        long Clock(Row row) => Math.Max(0, row.Start + shift);
                        var counts = rows.GroupBy(Clock).ToDictionary(g => g.Key, g => g.Count());
                        var lines = new List<string>();
                        for (int i = 0; i < rows.Count; i++)
                        {
                            token.ThrowIfCancellationRequested(); var row = rows[i]; var value = translated[i]![0]!.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(row.Text) && counts[Clock(row)] == 1 && !string.IsNullOrWhiteSpace(value))
                                lines.Add(Stamp(row.Start) + value);
                        }
                        if (lines.Count > 0) translation = prefix + string.Join('\n', lines);
                    }
                }
            }
            catch (Exception e) when (e is MusicPlatformException or FormatException or RegexMatchTimeoutException)
            { translation = ""; }
        }
        L.Require(translation.Length <= L.MaxSegment, "LIMIT");
        return (original, translation);
    }
    private static string Stamp(long time) => "[" + (time / 60000).ToString("00", CultureInfo.InvariantCulture) + ":" +
        (time / 1000 % 60).ToString("00", CultureInfo.InvariantCulture) + "." + (time % 1000).ToString("000", CultureInfo.InvariantCulture) + "]";
}
