// Native translation of music-core/src/lyric-timing.js; no DOM, scripting or external XML entities.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeowFilm.Capabilities.Library;

public static class MusicLyricTiming
{
    private sealed class Word(double time, double? duration, string text)
    { internal double Time = time; internal double? Duration = duration; internal string Text = text; }
    private sealed class Line(double time, double? end, string text, List<Word> words)
    {
        internal double Time = time; internal double? End = end; internal string Text = text;
        internal List<Word> Words = words;
    }
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex XmlStartRegex = new(@"^\s*<", RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex LyricContentAttrRegex = new("\\bLyricContent\\s*=\\s*([\"'])([\\s\\S]*?)\\1", RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex XmlEntityRegex = new("&(amp|lt|gt|quot|apos|#\\d+|#[xX][\\da-fA-F]+);", RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex OffsetTagRegex = new(@"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex TimedLineRegex = new(@"^\[(\d+),(\d+)\](.*)$", RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex YrcPrefixRegex = new(@"^\(\d+,\d+,\d+\)", RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex QrcSuffixRegex = new(@"\(\d+,\d+\)$", RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex QrcWordRegex = new(@"(.*?)\((\d+),(\d+)\)", RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex YrcWordRegex = new(@"\((\d+),(\d+),\d+\)(.*?)(?=\(\d+,\d+,\d+\)|$)", RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex LrcHeadRegex = new(@"^((?:\[\d{1,3}:\d{2}(?:\.\d{1,3})?\])+)(.*)$", RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex LrcTagRegex = new(@"\[(\d{1,3}):(\d{2}(?:\.\d{1,3})?)\]", RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex InlineMarkerRegex = new(@"(?:\[\[(\d{1,3}):(\d{2}(?:\.\d{1,3})?)\]\]|<(\d{1,3}):(\d{2}(?:\.\d{1,3})?)>)", RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
    private static double Number(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : 0;
    private static double Clock(string minutes, string seconds) => Number(minutes) * 60 + Number(seconds);
    public static JsonElement Parse(string text, string translation = "", double duration = 0)
    {
        LJ.Require(text is not null && translation is not null &&
            double.IsFinite(duration) && duration >= 0, "INVALID_LYRIC_RESPONSE");
        var original = ParseLines(text!, duration);
        var translations = new Dictionary<long, List<string>>();
        foreach (var line in ParseLines(translation!, 0))
        {
            long key = (long)Math.Floor(line.Time * 1000 + .5);
            if (!translations.TryGetValue(key, out var group)) translations[key] = group = [];
            if (!group.Contains(line.Text)) group.Add(line.Text);
        }
        var result = new JsonArray();
        foreach (var line in original)
        {
            var row = LJ.Obj(("time", line.Time), ("end", line.End), ("text", line.Text), ("words", LJ.Arr(line.Words.Select(w =>
                LJ.Obj(("time", w.Time), ("duration", w.Duration), ("text", w.Text), ("offset", w.Time - line.Time))))));
            if (translations.TryGetValue((long)Math.Floor(line.Time * 1000 + .5), out var group))
            {
                string translated = string.Join('\n', group.Where(t => t != line.Text)); if (translated.Length > 0) row["translation"] = translated;
            }
            result.Add(row);
        }
        return LJ.Element(result);
    }
    private static List<Line> ParseLines(string input, double duration)
    {
        if (XmlStartRegex.IsMatch(input))
        {
            var attributes = LyricContentAttrRegex.Matches(input);
            if (attributes.Count != 1) return [];
            input = XmlEntityRegex.Replace(attributes[0].Groups[2].Value, m =>
            {
                string name = m.Groups[1].Value;
                if (!name.StartsWith('#')) return name switch { "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", _ => "'" };
                bool hex = name.StartsWith("#x", StringComparison.OrdinalIgnoreCase);
                if (!int.TryParse(name[(hex ? 2 : 1)..], hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out int code) ||
                    code is <= 0 or > 0x10ffff or >= 0xd800 and <= 0xdfff) return m.Value;
                return char.ConvertFromUtf32(code);
            });
        }
        double offset = Number(OffsetTagRegex.Match(input).Groups[1].Value) / 1000;
        var lines = new List<Line>();
        foreach (string raw in input.Split('\n'))
        {
            string trimmed = raw.Trim(); var timed = TimedLineRegex.Match(trimmed);
            if (timed.Success)
            {
                string body = timed.Groups[3].Value;
                bool qrc = !YrcPrefixRegex.IsMatch(body) && QrcSuffixRegex.IsMatch(body);
                double time = Number(timed.Groups[1].Value) / 1000, end = time + Number(timed.Groups[2].Value) / 1000;
                var words = new List<Word>();
                foreach (Match m in (qrc ? QrcWordRegex : YrcWordRegex).Matches(body))
                {
                    string word = m.Groups[qrc ? 1 : 3].Value;
                    if (word.Length > 0) words.Add(new(Number(m.Groups[qrc ? 2 : 1].Value) / 1000 + offset,
                        Math.Max(0, Number(m.Groups[qrc ? 3 : 2].Value) / 1000), word));
                }
                string text = words.Count > 0 ? string.Concat(words.Select(w => w.Text)) : body.Trim();
                if (text.Length > 0) lines.Add(new(time + offset, end + offset, text, words));
                continue;
            }
            var head = LrcHeadRegex.Match(trimmed);
            if (!head.Success) continue;
            string content = head.Groups[2].Value.Trim(); if (content.Length == 0) continue;
            foreach (Match tag in LrcTagRegex.Matches(head.Groups[1].Value))
            {
                double time = Clock(tag.Groups[1].Value, tag.Groups[2].Value);
                var markers = InlineMarkerRegex.Matches(content);
                var words = new List<Word>(); string text = content; double? markerEnd = null;
                if (markers.Count > 0)
                {
                    string before = content[..markers[0].Index]; if (before.Length > 0) words.Add(new(time + offset, null, before));
                    bool relative = markers[0].Groups[1].Success && Clock(markers[0].Groups[1].Value, markers[0].Groups[2].Value) < time - 1;
                    for (int i = 0; i < markers.Count; i++)
                    {
                        var m = markers[i]; double stamp = Clock(m.Groups[m.Groups[1].Success ? 1 : 3].Value, m.Groups[m.Groups[1].Success ? 2 : 4].Value);
                        string word = content[(m.Index + m.Length)..(i + 1 < markers.Count ? markers[i + 1].Index : content.Length)];
                        if (word.Length > 0) words.Add(new(stamp + (relative ? time : 0) + offset, null, word));
                        else if (i == markers.Count - 1) markerEnd = stamp + (relative ? time : 0) + offset;
                    }
                    text = string.Concat(words.Select(w => w.Text));
                }
                if (text.Length > 0) lines.Add(new(time + offset, markerEnd, text, words));
            }
        }
        lines = lines.OrderBy(l => l.Time).ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i]; double? next = lines.Skip(i + 1).FirstOrDefault(l => l.Time > line.Time)?.Time;
            double time = Math.Max(0, line.Time);
            double? wordEnd = line.End ?? next;
            double? end = wordEnd ?? (duration > time ? duration : (double?)null);
            line.Time = time;
            line.End = Math.Max(line.Time, end ?? time);
            // Seek bounds are nonnegative; word intervals keep the source boundary after a negative offset.
            for (int j = 0; j < line.Words.Count; j++)
                line.Words[j].Duration ??= Math.Max(0, (j + 1 < line.Words.Count ? line.Words[j + 1].Time : wordEnd ?? line.Words[j].Time) - line.Words[j].Time);
        }
        return lines;
    }
    public static double Progress(double wordTime, double wordDuration, double time) =>
        time < wordTime ? 0 : wordDuration > 0 ? Math.Clamp((time - wordTime) / wordDuration, 0, 1) : 1;
}
