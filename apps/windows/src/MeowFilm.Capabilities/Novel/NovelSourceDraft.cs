using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities;

/// <summary>Page-local form state, not a second source repository.</summary>
internal sealed class NovelSourceDraft
{
    internal string Protocol { get; }
    private readonly JsonObject original;
    private readonly Dictionary<string, string> edits = [];
    internal NovelSourceTab[] Tabs => NovelSourceFields.Tabs(Protocol);
    internal string EnabledKey => Protocol == "yiciyuan" ? "enable" : "enabled";
    internal NovelSourceDraft(string protocol, string? definition = null)
    {
        Protocol = protocol;
        original = definition is null ? new JsonObject { ["bookSourceType"] = 0, ["enabledCookieJar"] = true,
            ["enabledExplore"] = true, [EnabledKey] = true } : JsonNode.Parse(definition) switch
            { JsonObject value => value, JsonValue value when protocol == "qysg" && value.TryGetValue<string>(out _) => new() { ["html"] = value.DeepClone() }, _ => throw new InvalidDataException() };
        if (definition is null) original[protocol switch { "legado" => "ruleSearch", "yiciyuan" => "ruleSearchList", _ => "html" }] =
            protocol == "legado" ? new JsonObject() : JsonValue.Create("");
    }
    private static JsonObject Object(JsonNode? value) => value switch
    {
        JsonObject obj => (JsonObject)obj.DeepClone(),
        JsonValue text when text.TryGetValue<string>(out var json) => ParseObject(json), _ => new()
    };
    private static JsonObject ParseObject(string text) { try { return JsonNode.Parse(text) as JsonObject ?? new(); } catch (JsonException) { return new(); } }
    private JsonNode? Original(NovelSourceField field)
    {
        string[] parts = field.Parts;
        if (parts.Length == 1) return original[parts[0]];
        var group = Object(original[parts[0]]);
        return group[parts[1]] ?? (field.Path == "ruleToc.updateTime" ? group["chapterInfo"] : null);
    }
    private static string Text(JsonNode? value) => value is null ? "" : value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value.ToJsonString();
    private string OriginalText(NovelSourceField field) => field.Kind == NovelSourceFieldKind.Json ? Original(field)?.ToJsonString() ?? "null" : Text(Original(field));
    internal string Text(NovelSourceField field) => edits.TryGetValue(field.Key, out var text) ? text : OriginalText(field);
    internal void Set(NovelSourceField field, string text) { if (text == OriginalText(field)) edits.Remove(field.Key); else edits[field.Key] = text; }
    internal bool Bool(string key, bool fallback = true) { string text = Text(new NovelSourceField(key, key)).Trim().ToLowerInvariant(); return text.Length == 0 ? fallback : text is not ("false" or "0"); }
    internal void SetBool(string key, bool value) => Set(new(key, key, Kind: NovelSourceFieldKind.Bool), value ? "true" : "false");
    internal NovelSourceField[] ExtensionFields
    {
        get
        {
            var known = Tabs.SelectMany(t => t.Fields).Select(f => f.Path).Concat(["bookSourceType", EnabledKey, "enabledExplore", "enabledCookieJar"])
                .Concat(Protocol == "yiciyuan" ? ["enabled", "customOrder"] : Array.Empty<string>()).ToHashSet();
            var fields = new List<NovelSourceField>();
            foreach (var (key, value) in original.Where(pair => !known.Contains(pair.Key)))
            {
                if (known.Any(path => path.StartsWith(key + ".", StringComparison.Ordinal)))
                {
                    foreach (var (child, item) in Object(value).Where(pair => !known.Contains(key + "." + pair.Key)))
                        fields.Add(new(key + "." + child, key + "." + child, 3, Kind(item), [key, child]));
                }
                else fields.Add(new(key, key, 3, Kind(value), [key]));
            }
            return fields.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        }
    }
    private static NovelSourceFieldKind Kind(JsonNode? value) => value?.GetValueKind() switch
    { JsonValueKind.String => NovelSourceFieldKind.Text, JsonValueKind.Number => NovelSourceFieldKind.Number,
        JsonValueKind.True or JsonValueKind.False => NovelSourceFieldKind.Bool, _ => NovelSourceFieldKind.Json };
    internal string Definition()
    {
        var result = (JsonObject)original.DeepClone();
        var fields = Tabs.SelectMany(t => t.Fields).Concat(ExtensionFields).Concat([
            new("bookSourceType", "内容类型", Kind: NovelSourceFieldKind.Number), new(EnabledKey, "启用", Kind: NovelSourceFieldKind.Bool),
            new("enabledExplore", "发现", Kind: NovelSourceFieldKind.Bool), new("enabledCookieJar", "CookieJar", Kind: NovelSourceFieldKind.Bool)]);
        foreach (var field in fields)
        {
            if (!edits.TryGetValue(field.Key, out string? text)) continue;
            JsonNode? value = field.Kind switch
            {
                NovelSourceFieldKind.Text => JsonValue.Create(text),
                NovelSourceFieldKind.Number when double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) => JsonValue.Create(number),
                NovelSourceFieldKind.Bool when text is "true" or "false" => JsonValue.Create(text == "true"),
                NovelSourceFieldKind.Json => JsonNode.Parse(text), _ => throw new InvalidDataException(field.Label + "格式无效")
            };
            string[] parts = field.Parts;
            if (parts.Length == 1) result[parts[0]] = value;
            else { var group = Object(result[parts[0]]); group[parts[1]] = value; result[parts[0]] = group; }
        }
        if (Protocol == "yiciyuan" && edits.ContainsKey(new NovelSourceField(EnabledKey, "").Key)) result["enabled"] = result[EnabledKey]?.DeepClone();
        if (Protocol == "yiciyuan" && edits.ContainsKey(new NovelSourceField("serialNumber", "").Key)) result["customOrder"] = result["serialNumber"]?.DeepClone();
        foreach (string key in new[] { "bookSourceName", "bookSourceUrl" })
        {
            string value = Text(result[key]).Trim();
            if (value.Length == 0) throw new InvalidDataException(key == "bookSourceName" ? "请填写源名称" : "请填写源 URL");
            result[key] = value;
        }
        return result.ToJsonString();
    }
}

internal static class NovelGroups
{
    internal static string[] Split(string text) => text.Split(['，', ',', ';', '；', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Order(StringComparer.CurrentCulture).ToArray();
    internal static string Replacing(string text, string old, string? next) => string.Join(',', Split(text).Where(g => g != old).Concat(next is null ? [] : Split(next)).Distinct().Order(StringComparer.CurrentCulture));
}
