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
            value[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
        return NovelJson.Decode<NovelChapter>(JsonSerializer.SerializeToElement(value, NovelJson.Options));
    }
    public override string ToString() => (IsVip && !IsPay ? "付费 · " : "") + Name;
}
public sealed record NovelBook(string InstallationId, long Revision, string BookUrl, string Name,
    string Author = "", string CoverUrl = "", string Intro = "", string TocUrl = "",
    string LatestChapterTitle = "", [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Data = default)
{
    [JsonIgnore]
    public string SourceIdentity => InstallationId + "\n" + BookUrl;
    public override string ToString() => Name + (Author.Length == 0 ? "" : " · " + Author);
    public object Payload(NovelChapter? chapter = null, IReadOnlyList<NovelChapter>? chapters = null) => new
    {
        bookUrl = BookUrl, tocUrl = TocUrl, book = Json(),
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
    public static NovelBook From(JsonElement data, NovelSource source) => new(source.InstallationId, source.Revision,
        NovelJson.Text(data, "bookUrl"), NovelJson.Text(data, "name"), NovelJson.Text(data, "author"),
        NovelJson.Text(data, "coverUrl"), NovelJson.Text(data, "intro"), NovelJson.Text(data, "tocUrl"),
        NovelJson.Text(data, "latestChapterTitle"), data.Clone());
}
public sealed record NovelEntry(string BookId, string IdentityKey, NovelBook Book, bool OnShelf = false,
    DateTimeOffset? AddedAt = null, DateTimeOffset? PinnedAt = null, DateTimeOffset? ReadAt = null,
    NovelAnchor? Anchor = null, int ChapterCount = 0, string ContentSignature = "")
{
    [JsonIgnore]
    public string Progress => Anchor is null ? "未开始" : $"{Anchor.ChapterTitle} · {Anchor.Ratio:P0}";
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
}
internal static class NovelJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    internal static string Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    internal static T Decode<T>(JsonElement e) => e.Deserialize<T>(Options) ?? throw new InvalidDataException("INVALID_RESPONSE");
    internal static JsonElement Value(object value) => JsonSerializer.SerializeToElement(value, Options);
}

internal sealed class NovelBlockConverter : JsonConverter<NovelBlock>
{
    public override NovelBlock Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader); var value = document.RootElement;
        string kind = NovelJson.Text(value, "type");
        if (kind is not ("text" or "image")) throw new JsonException();
        return new(kind, NovelJson.Text(value, "text"), NovelJson.Text(value, "src"), NovelJson.Text(value, "alt"));
    }
    public override void Write(Utf8JsonWriter writer, NovelBlock value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("type", value.Type);
        if (value.Type == "text") writer.WriteString("text", value.Text);
        else { writer.WriteString("src", value.Src); writer.WriteString("alt", value.Alt); }
        writer.WriteEndObject();
    }
}
