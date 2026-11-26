using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeowFilm.Capabilities;

public sealed record NovelSource(string InstallationId, long Revision, string Name, string SourceUrl,
    string Group, long Order, bool Enabled, bool EnabledExplore, string Protocol, string[] Actions,
    string? ErrorCode = null, double Weight = 0, double LastUpdateTime = 0,
    double RespondTime = 180000, bool HasExplore = false)
{
    public override string ToString() => Name;
}
public sealed record NovelAnchor(string ChapterUrl = "", string ChapterTitle = "", int ChapterIndex = 0,
    int BlockIndex = 0, int Utf16Offset = 0, double Ratio = 0);
[JsonConverter(typeof(NovelBlockConverter))]
public sealed record NovelBlock(string Type, string Text = "", string Src = "", string Alt = "");
public sealed record NovelChapter(string Name, string ChapterUrl, int Index, bool IsVip = false,
    bool IsPay = false, bool IsVolume = false, JsonElement? Variable = null)
{
    // Keep the runtime's public chapter context (including rule-specific fields) at the
    // same JSON level. It is invocation data, not a native copy of the source definition.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Data { get; init; }
    internal NovelChapter Update(JsonElement response)
    {
        var value = System.Text.Json.Nodes.JsonNode.Parse(NovelJson.Value(this).GetRawText())!.AsObject();
        foreach (var property in response.EnumerateObject())
        {
            if (property.Name == "chapterUrl" &&
                (property.Value.ValueKind == JsonValueKind.Null ||
                 (property.Value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(property.Value.GetString()))))
                continue;
            value[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
        }
        return NovelJson.Decode<NovelChapter>(JsonSerializer.SerializeToElement(value, NovelJson.Options));
    }
    internal bool ContentEquals(NovelChapter? other) =>
        other is not null &&
        (ReferenceEquals(this, other) ||
         (Index == other.Index &&
          IsVip == other.IsVip &&
          IsPay == other.IsPay &&
          IsVolume == other.IsVolume &&
          string.Equals(Name, other.Name, StringComparison.Ordinal) &&
          string.Equals(ChapterUrl, other.ChapterUrl, StringComparison.Ordinal) &&
          NovelJson.NullableElementEquals(Variable, other.Variable) &&
          NovelJson.ExtensionDataEquals(Data, other.Data)));
    public override string ToString() => (IsVip && !IsPay ? "付费 · " : "") + Name;
}
public sealed record NovelBook(string InstallationId, long Revision, string BookUrl, string Name,
    string Author = "", string CoverUrl = "", string Intro = "", string TocUrl = "",
    string LatestChapterTitle = "", [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Data = default)
{
    [JsonIgnore]
    public string SourceIdentity => InstallationId + "\n" + BookUrl;
    [JsonIgnore]
    public JsonElement? Variable => Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty("variable", out var value)
        ? value : null;
    internal bool ContentEquals(NovelBook? other) =>
        other is not null &&
        (ReferenceEquals(this, other) ||
         (Revision == other.Revision &&
          string.Equals(InstallationId, other.InstallationId, StringComparison.Ordinal) &&
          string.Equals(BookUrl, other.BookUrl, StringComparison.Ordinal) &&
          string.Equals(Name, other.Name, StringComparison.Ordinal) &&
          string.Equals(Author, other.Author, StringComparison.Ordinal) &&
          string.Equals(CoverUrl, other.CoverUrl, StringComparison.Ordinal) &&
          string.Equals(Intro, other.Intro, StringComparison.Ordinal) &&
          string.Equals(TocUrl, other.TocUrl, StringComparison.Ordinal) &&
          string.Equals(LatestChapterTitle, other.LatestChapterTitle, StringComparison.Ordinal) &&
          NovelJson.ElementEquals(Data, other.Data)));
    public NovelBook WithVariable(JsonElement? variable)
    {
        var data = Data.ValueKind == JsonValueKind.Object
            ? System.Text.Json.Nodes.JsonNode.Parse(Data.GetRawText())!.AsObject() : new System.Text.Json.Nodes.JsonObject();
        data["variable"] = variable is { ValueKind: not JsonValueKind.Undefined } value
            ? System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText())
            : null;
        return this with { Data = JsonSerializer.SerializeToElement(data, NovelJson.Options) };
    }
    public override string ToString() => Name + (Author.Length == 0 ? "" : " · " + Author);
    public object Payload(NovelChapter? chapter = null, IReadOnlyList<NovelChapter>? chapters = null) => new
    {
        bookUrl = BookUrl, tocUrl = TocUrl, book = Json(), variable = Variable,
        chapter = chapter is null ? (JsonElement?)null : NovelJson.Value(chapter),
        chapterUrl = chapter?.ChapterUrl,
        nextChapterUrl = chapter is not null && chapters is not null && chapter.Index + 1 < chapters.Count
            ? chapters[chapter.Index + 1].ChapterUrl : null,
        chapters = chapters is null ? (JsonElement?)null : NovelJson.Value(chapters)
    };
    public JsonElement Json()
    {
        var data = Data.ValueKind == JsonValueKind.Object
            ? System.Text.Json.Nodes.JsonNode.Parse(Data.GetRawText())!.AsObject() : new System.Text.Json.Nodes.JsonObject();
        data["installationId"] = InstallationId; data["bookUrl"] = BookUrl; data["name"] = Name;
        data["author"] = Author; data["coverUrl"] = CoverUrl; data["tocUrl"] = TocUrl;
        data["intro"] = Intro; data["latestChapterTitle"] = LatestChapterTitle;
        return JsonSerializer.SerializeToElement(data, NovelJson.Options);
    }
    public static NovelBook From(JsonElement data, NovelSource source)
    {
        string bookUrl = NovelJson.Text(data, "bookUrl");
        string tocUrl = NovelJson.Text(data, "tocUrl");
        if (string.IsNullOrWhiteSpace(tocUrl)) tocUrl = bookUrl;
        return new(source.InstallationId, source.Revision,
            bookUrl, NovelJson.Text(data, "name"), NovelJson.Text(data, "author"),
            NovelJson.Text(data, "coverUrl"), NovelJson.Text(data, "intro"), tocUrl,
            NovelJson.Text(data, "latestChapterTitle"), data.Clone());
    }
    internal static NovelBook[] ReadList(JsonElement values, NovelSource source, string errorCode)
    {
        if (values.ValueKind != JsonValueKind.Array) throw new JsonException(errorCode);
        var result = new List<NovelBook>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.Object &&
                !string.IsNullOrWhiteSpace(NovelJson.Text(value, "bookUrl")) &&
                !string.IsNullOrWhiteSpace(NovelJson.Text(value, "name")))
            {
                result.Add(From(value, source));
            }
        }
        if (values.GetArrayLength() > 0 && result.Count == 0) throw new JsonException(errorCode);
        return result.ToArray();
    }
}
public sealed record NovelEntry(string BookId, string IdentityKey, NovelBook Book, bool OnShelf = false,
    DateTimeOffset? AddedAt = null, DateTimeOffset? PinnedAt = null, DateTimeOffset? ReadAt = null,
    NovelAnchor? Anchor = null, int ChapterCount = 0, string ContentSignature = "")
{
    [JsonIgnore]
    public string Progress => Anchor is null ? "未开始" : $"{Anchor.ChapterTitle} · {Anchor.Ratio:P0}";
    internal bool ContentEquals(NovelEntry? other) =>
        other is not null &&
        (ReferenceEquals(this, other) ||
         (OnShelf == other.OnShelf &&
          ChapterCount == other.ChapterCount &&
          AddedAt == other.AddedAt &&
          PinnedAt == other.PinnedAt &&
          ReadAt == other.ReadAt &&
          string.Equals(BookId, other.BookId, StringComparison.Ordinal) &&
          string.Equals(IdentityKey, other.IdentityKey, StringComparison.Ordinal) &&
          string.Equals(ContentSignature, other.ContentSignature, StringComparison.Ordinal) &&
          Equals(Anchor, other.Anchor) &&
          Book.ContentEquals(other.Book)));
}
public sealed record NovelRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Group { get; init; } = "";
    public string Pattern { get; init; } = "";
    public string Replacement { get; init; } = "";
    public bool IsRegex { get; init; } = true;
    [JsonPropertyName("isEnabled")]
    public bool Enabled { get; init; } = true;
    public bool ScopeTitle { get; init; }
    public bool ScopeContent { get; init; } = true;
    public string Scope { get; init; } = "";
    public string ExcludeScope { get; init; } = "";
    public long Order { get; init; }
    public int TimeoutMillisecond { get; init; } = 3000;
}
public sealed record NovelAppearance
{
    public string Mode { get; init; } = "none";
    public double FontSize { get; init; } = 22;
    public string FontFamily { get; init; } = "Microsoft YaHei UI";
    public bool Bold { get; init; }
    public double LineHeight { get; init; } = 1.8;
    public double ParagraphSpacing { get; init; } = 14;
    public double Margin { get; init; } = 32;
    public double ContentWidth { get; init; } = 820;
    public string Conversion { get; init; } = "none";
    public int Theme { get; init; }
    public string Background { get; init; } = "#F4F1E9";
    public string Foreground { get; init; } = "#292720";
    public string BackgroundImage { get; init; } = "";
    public double AutoSpeed { get; init; } = 4;
    public static readonly (string Name, string Background, string Foreground)[] Themes =
    [ ("纸白", "#F4F1E9", "#292720"), ("明亮", "#FFFFFF", "#242424"), ("护绿", "#DCE8D1", "#26352A"),
      ("暖黄", "#EFE0B9", "#43351F"), ("浅灰", "#DDDFE0", "#292C2F"), ("粉笺", "#EEDFE0", "#453039"),
      ("夜间", "#212224", "#C8C9CA"), ("墨黑", "#111214", "#AEB0B4") ];
    private static readonly NovelAppearance Default = new();
    public static bool ValidSize(double value) => value > 0 && float.IsFinite((float)value) && (float)value > 0 && value * 1.15 <= float.MaxValue;
    public static bool ValidLineHeight(double size, double value) => value > 0 &&
        float.IsFinite((float)(size * value * 1.15)) && (float)(size * value) > 0;
    public static bool ValidFamily(string value) => !string.IsNullOrWhiteSpace(value) && value.IndexOf('\0') < 0;
    public static bool TryParseColor(string? value, out byte a, out byte r, out byte g, out byte b)
    {
        a = r = g = b = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var span = value.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#')
        {
            var hex = span[1..];
            static bool Hex(char ch, out byte nibble)
            {
                if (ch is >= '0' and <= '9') { nibble = (byte)(ch - '0'); return true; }
                if (ch is >= 'a' and <= 'f') { nibble = (byte)(ch - 'a' + 10); return true; }
                if (ch is >= 'A' and <= 'F') { nibble = (byte)(ch - 'A' + 10); return true; }
                nibble = 0; return false;
            }
            static bool Byte(ReadOnlySpan<char> pair, out byte result)
            {
                if (Hex(pair[0], out byte hi) && Hex(pair[1], out byte lo)) { result = (byte)((hi << 4) | lo); return true; }
                result = 0; return false;
            }
            if (hex.Length == 3 && Hex(hex[0], out byte r1) && Hex(hex[1], out byte g1) && Hex(hex[2], out byte b1))
            { a = 255; r = (byte)(r1 * 17); g = (byte)(g1 * 17); b = (byte)(b1 * 17); return true; }
            if (hex.Length == 4 && Hex(hex[0], out byte a1) && Hex(hex[1], out r1) && Hex(hex[2], out g1) && Hex(hex[3], out b1))
            { a = (byte)(a1 * 17); r = (byte)(r1 * 17); g = (byte)(g1 * 17); b = (byte)(b1 * 17); return true; }
            if (hex.Length == 6 && Byte(hex[..2], out r) && Byte(hex.Slice(2, 2), out g) && Byte(hex.Slice(4, 2), out b))
            { a = 255; return true; }
            if (hex.Length == 8 && Byte(hex[..2], out a) && Byte(hex.Slice(2, 2), out r) && Byte(hex.Slice(4, 2), out g) && Byte(hex.Slice(6, 2), out b))
            { return true; }
            return false;
        }
        foreach (char ch in span) if (!char.IsAsciiLetter(ch)) return false;
        var known = System.Drawing.Color.FromName(span.ToString());
        if (!known.IsKnownColor || known.IsSystemColor) return false;
        a = known.A; r = known.R; g = known.G; b = known.B;
        return true;
    }
    [JsonIgnore]
    internal string ReadingConversion => Conversion is "simplified" or "traditional" ? Conversion : "none";
    internal NovelAppearance ForReading()
    {
        var fallback = Default;
        static bool Positive(double value) => double.IsFinite(value) && value > 0;
        static bool Nonnegative(double value) => double.IsFinite(value) && value >= 0;
        static string Color(string value, string fallback) => TryParseColor(value, out _, out _, out _, out _) ? value : fallback;
        string font = ValidFamily(FontFamily) ? FontFamily : fallback.FontFamily;
        double size = ValidSize(FontSize) ? FontSize : fallback.FontSize;
        double lineHeight = ValidLineHeight(size, LineHeight) ? LineHeight : fallback.LineHeight;
        if (!ValidLineHeight(size, lineHeight)) size = fallback.FontSize;
        return this with
        {
            Mode = Mode is "cover" or "slide" or "none" or "scroll" ? Mode : fallback.Mode,
            Conversion = ReadingConversion, Theme = Theme >= 0 && Theme < Themes.Length ? Theme : fallback.Theme,
            FontFamily = font, FontSize = size,
            LineHeight = lineHeight,
            ParagraphSpacing = Nonnegative(ParagraphSpacing) ? ParagraphSpacing : fallback.ParagraphSpacing,
            Margin = Nonnegative(Margin) && double.IsFinite(Margin * 2) ? Margin : fallback.Margin,
            ContentWidth = Positive(ContentWidth) ? ContentWidth : fallback.ContentWidth,
            AutoSpeed = double.IsFinite(AutoSpeed) && AutoSpeed is >= 1 and <= 10 ? AutoSpeed : fallback.AutoSpeed,
            Background = Color(Background, fallback.Background), Foreground = Color(Foreground, fallback.Foreground)
        };
    }
}
internal static class NovelJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    internal static string Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    internal static T Decode<T>(JsonElement e) => e.Deserialize<T>(Options) ?? throw new InvalidDataException("INVALID_RESPONSE");
    internal static JsonElement Value(object value) => JsonSerializer.SerializeToElement(value, Options);
    internal static bool ElementEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        if (left.ValueKind == JsonValueKind.Undefined) return true;
        return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
    }
    internal static bool NullableElementEquals(JsonElement? left, JsonElement? right) =>
        left.HasValue == right.HasValue && (!left.HasValue || ElementEquals(left.Value, right!.Value));
    internal static bool ExtensionDataEquals(Dictionary<string, JsonElement>? left, Dictionary<string, JsonElement>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        int leftCount = left?.Count ?? 0;
        int rightCount = right?.Count ?? 0;
        if (leftCount != rightCount) return false;
        if (leftCount == 0) return true;
        foreach (var pair in left!)
        {
            if (!right!.TryGetValue(pair.Key, out var other) || !ElementEquals(pair.Value, other))
                return false;
        }
        return true;
    }
}

internal sealed class NovelBlockConverter : JsonConverter<NovelBlock>
{
    public override bool HandleNull => true;
    public override NovelBlock Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader); var value = document.RootElement;
        string kind = NovelJson.Text(value, "type");
        if (kind is not ("text" or "image")) throw new JsonException();
        string member = kind == "text" ? "text" : "src";
        if (!value.TryGetProperty(member, out var material) || material.ValueKind != JsonValueKind.String)
            throw new JsonException();
        string content = material.GetString()!;
        if (kind == "image" && string.IsNullOrWhiteSpace(content)) throw new JsonException();
        return kind == "text" ? new(kind, Text: content) : new(kind, Src: content, Alt: NovelJson.Text(value, "alt"));
    }
    public override void Write(Utf8JsonWriter writer, NovelBlock value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("type", value.Type);
        if (value.Type == "text") writer.WriteString("text", value.Text);
        else { writer.WriteString("src", value.Src); writer.WriteString("alt", value.Alt); }
        writer.WriteEndObject();
    }
}
