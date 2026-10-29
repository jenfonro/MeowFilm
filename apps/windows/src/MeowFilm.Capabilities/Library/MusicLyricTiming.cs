// Native translation of music-core/src/lyric-timing.js; no DOM, scripting or external XML entities.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeowFilm.Capabilities.Library;

public static class MusicLyricTiming
{
    private sealed class Word(double time, double? duration, string text)
    { internal double Time = time; internal double? Duration = duration; internal string Text = text; }
    private sealed class Line(double time, double? end, string text, List<Word> words, bool qrc)
    {
        internal double Time = time; internal double? End = end; internal string Text = text;
        internal List<Word> Words = words; internal bool Qrc = qrc; internal string Timing = words.Count > 0 ? "word" : "estimated";
    }
    private static Regex R(string pattern, RegexOptions options = RegexOptions.None) => new(pattern, options | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static double Number(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : 0;
    private static double Clock(string minutes, string seconds) => Number(minutes) * 60 + Number(seconds);
    public static JsonElement Parse(string text, string translation = "", double duration = 0)
    {
        LJ.Require(text is not null && translation is not null && text.Length <= 65536 && translation.Length <= 65536 &&
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
                LJ.Obj(("time", w.Time), ("duration", w.Duration), ("text", w.Text), ("offset", w.Time - line.Time))))), ("timing", line.Timing));
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
        if (R(@"^\s*<").IsMatch(input))
        {
            var attributes = R("\\bLyricContent\\s*=\\s*([\"'])([\\s\\S]*?)\\1").Matches(input);
            if (attributes.Count != 1) return [];
            input = R("&(amp|lt|gt|quot|apos|#\\d+|#[xX][\\da-fA-F]+);").Replace(attributes[0].Groups[2].Value, m =>
            {
                string name = m.Groups[1].Value;
                if (!name.StartsWith('#')) return name switch { "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", _ => "'" };
                bool hex = name.StartsWith("#x", StringComparison.OrdinalIgnoreCase);
                if (!int.TryParse(name[(hex ? 2 : 1)..], hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out int code) ||
                    code is <= 0 or > 0x10ffff or >= 0xd800 and <= 0xdfff) return m.Value;
                return char.ConvertFromUtf32(code);
            });
        }
        double offset = Number(R(@"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase).Match(input).Groups[1].Value) / 1000;
        var lines = new List<Line>();
        foreach (string raw in input.Split('\n'))
        {
            string trimmed = raw.Trim(); var timed = R(@"^\[(\d+),(\d+)\](.*)$").Match(trimmed);
            if (timed.Success)
            {
                string body = timed.Groups[3].Value;
                bool qrc = !R(@"^\(\d+,\d+,\d+\)").IsMatch(body) && R(@"\(\d+,\d+\)$").IsMatch(body);
                double time = Number(timed.Groups[1].Value) / 1000, end = time + Number(timed.Groups[2].Value) / 1000;
                var words = new List<Word>();
                foreach (Match m in (qrc ? R(@"(.*?)\((\d+),(\d+)\)") : R(@"\((\d+),(\d+),\d+\)(.*?)(?=\(\d+,\d+,\d+\)|$)")).Matches(body))
                {
                    string word = m.Groups[qrc ? 1 : 3].Value;
                    if (word.Length > 0) words.Add(new(Number(m.Groups[qrc ? 2 : 1].Value) / 1000 + offset,
                        Math.Max(0, Number(m.Groups[qrc ? 3 : 2].Value) / 1000), word));
                }
                string text = words.Count > 0 ? string.Concat(words.Select(w => w.Text)) : body.Trim();
                if (text.Length > 0) lines.Add(new(Math.Max(0, time + offset), Math.Max(0, end + offset), text, words, qrc));
                continue;
            }
            var head = R(@"^((?:\[\d{1,3}:\d{2}(?:\.\d{1,3})?\])+)(.*)$").Match(trimmed);
            if (!head.Success) continue;
            string content = head.Groups[2].Value.Trim(); if (content.Length == 0) continue;
            foreach (Match tag in R(@"\[(\d{1,3}):(\d{2}(?:\.\d{1,3})?)\]").Matches(head.Groups[1].Value))
            {
                double time = Clock(tag.Groups[1].Value, tag.Groups[2].Value);
                var markers = R(@"(?:\[\[(\d{1,3}):(\d{2}(?:\.\d{1,3})?)\]\]|<(\d{1,3}):(\d{2}(?:\.\d{1,3})?)>)").Matches(content);
                var words = new List<Word>(); string text = content; double? markerEnd = null;
                if (markers.Count > 0)
                {
                    string before = content[..markers[0].Index]; if (before.Length > 0) words.Add(new(time + offset, null, before));
                    bool relative = markers[0].Groups[1].Success && Clock(markers[0].Groups[1].Value, markers[0].Groups[2].Value) < time - 1;
                    for (int i = 0; i < markers.Count; i++)
                    {
                        var m = markers[i]; double stamp = Clock(m.Groups[m.Groups[1].Success ? 1 : 3].Value, m.Groups[m.Groups[1].Success ? 2 : 4].Value);
                        string word = content[(m.Index + m.Length)..(i + 1 < markers.Count ? markers[i + 1].Index : content.Length)];
                        if (word.Length > 0) words.Add(new(Math.Max(0, stamp + (relative ? time : 0) + offset), null, word));
                        else if (i == markers.Count - 1) markerEnd = stamp + (relative ? time : 0) + offset;
                    }
                    text = string.Concat(words.Select(w => w.Text));
                }
                if (text.Length > 0) lines.Add(new(Math.Max(0, time + offset), markerEnd, text, words, false));
            }
        }
        LJ.Require(lines.Count <= 10000, "LIMIT"); lines = lines.OrderBy(l => l.Time).ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i]; double? next = lines.Skip(i + 1).FirstOrDefault(l => l.Time > line.Time)?.Time;
            if (!line.Qrc) line.End = Math.Max(line.Time + .05, line.End ?? next ?? (duration > line.Time ? duration : line.Time + 5));
            if (line.Words.Count == 0)
            {
                var elements = StringInfo.GetTextElementEnumerator(line.Text); var graphemes = new List<string>();
                while (elements.MoveNext()) graphemes.Add(elements.GetTextElement());
                double Weight(string s) => s.Any(char.IsWhiteSpace) ? .25 : s.EnumerateRunes().All(r => Rune.GetUnicodeCategory(r) is
                    UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or
                    UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or
                    UnicodeCategory.OtherPunctuation or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol) ? .5 : 1;
                double total = graphemes.Sum(Weight), t = line.Time;
                foreach (string s in graphemes)
                { double d = (line.End!.Value - line.Time) * Weight(s) / (total > 0 ? total : 1); line.Words.Add(new(t, d, s)); t += d; }
            }
            else for (int j = 0; j < line.Words.Count; j++)
                line.Words[j].Duration ??= Math.Max(.05, (j + 1 < line.Words.Count ? line.Words[j + 1].Time : line.End!.Value) - line.Words[j].Time);
        }
        return lines;
    }
    public static int ActiveIndex(JsonElement lines, double time)
    {
        LJ.Require(lines.ValueKind == JsonValueKind.Array && double.IsFinite(time));
        int left = 0, right = lines.GetArrayLength() - 1, found = -1;
        while (left <= right) { int mid = (left + right) >> 1; if (lines[mid].GetProperty("time").GetDouble() <= time) { found = mid; left = mid + 1; } else right = mid - 1; }
        return found;
    }
    public static double Progress(double wordTime, double wordDuration, double time) =>
        time < wordTime ? 0 : wordDuration > 0 ? Math.Clamp((time - wordTime) / wordDuration, 0, 1) : 1;
}
