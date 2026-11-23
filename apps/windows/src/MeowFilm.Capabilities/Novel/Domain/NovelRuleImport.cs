using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MeowFilm.Capabilities;

internal static class NovelRuleImport
{
    private static readonly JsonSerializerOptions Options = new(NovelJson.Options)
    { PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict };
    internal static NovelRule[] Parse(string text)
    {
        var root = JsonNode.Parse(text.TrimStart('\uFEFF')) ?? throw new InvalidDataException("替换规则格式不对");
        var entries = root is JsonArray array ? array.ToArray() : root is JsonObject obj && obj["rules"] is JsonArray rules ? rules.ToArray() : [root];
        return entries.Select(entry =>
        {
            var value = entry?.DeepClone() as JsonObject ?? throw new InvalidDataException("替换规则格式不对");
            if (value["pattern"]?.GetValueKind() != JsonValueKind.String) throw new InvalidDataException("替换规则格式不对");
            if (value["id"] is JsonValue id && id.GetValueKind() == JsonValueKind.Number) value["id"] = id.ToJsonString();
            foreach (string key in new[] { "id", "name", "group", "scope", "excludeScope", "replacement", "isEnabled", "isRegex", "scopeTitle", "scopeContent", "order", "timeoutMillisecond" }) if (value[key] is null) value.Remove(key);
            var rule = value.Deserialize<NovelRule>(Options) ?? throw new InvalidDataException("替换规则格式不对");
            return rule.Name.Trim().Length == 0 ? rule with { Name = rule.Pattern.Trim() } : rule;
        }).ToArray();
    }
    internal static string Status(NovelRule rule, NovelRule? existing) => existing is null ? "新增" :
        rule.Pattern != existing.Pattern || rule.Replacement != existing.Replacement || rule.IsRegex != existing.IsRegex || rule.Scope != existing.Scope ? "更新" : "已有";
}
