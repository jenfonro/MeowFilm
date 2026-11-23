using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

internal static class NovelRuleImport
{
    private static readonly JsonSerializerOptions Options = new(NovelJson.Options)
    { PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict };
    private static readonly string[] NullableKeys =
        ["id", "name", "group", "scope", "excludeScope", "replacement", "isEnabled", "isRegex", "scopeTitle", "scopeContent", "order", "timeoutMillisecond"];

    internal static void Validate(NovelRule rule)
    {
        if (string.IsNullOrEmpty(rule.Id) || rule.Pattern is null || rule.Name is null || rule.Group is null ||
            rule.Replacement is null || rule.Scope is null || rule.ExcludeScope is null)
            throw new InvalidDataException("替换规则格式不对");
        if (rule.TimeoutMillisecond <= 0) throw new InvalidDataException("超时毫秒数无效");
    }

    internal static async Task<string> FetchUrlAsync(string url, CancellationToken token = default)
    {
        try { return await NovelSourceService.FetchTextUrlAsync(url, token); }
        catch (BridgeException error) when (error.Code == "INVALID_REQUEST") { throw new InvalidDataException("网络地址无效"); }
    }

    internal static NovelRule[] Parse(string text)
    {
        var entries = UnwrapEntries(text);
        return entries.Select(ParseEntry).ToArray();
    }

    internal static (NovelRule[] Rules, string[] Failures) ParseBatch(string text, CancellationToken token = default)
    {
        var entries = UnwrapEntries(text);
        var rules = new List<NovelRule>(entries.Length);
        var failures = new List<string>();
        for (int index = 0; index < entries.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var rule = ParseEntry(entries[index]);
                Validate(rule);
                rules.Add(rule);
            }
            catch (OperationCanceledException) { throw; }
            catch { failures.Add($"规则 {index + 1} 无效"); }
        }
        return (rules.ToArray(), failures.ToArray());
    }

    private static JsonNode?[] UnwrapEntries(string text)
    {
        var root = JsonNode.Parse(text.TrimStart('\uFEFF')) ?? throw new InvalidDataException("替换规则格式不对");
        return root is JsonArray array ? array.ToArray() : root is JsonObject obj && obj["rules"] is JsonArray rules ? rules.ToArray() : [root];
    }

    private static NovelRule ParseEntry(JsonNode? entry)
    {
        var value = entry?.DeepClone() as JsonObject ?? throw new InvalidDataException("替换规则格式不对");
        if (value["pattern"]?.GetValueKind() != JsonValueKind.String) throw new InvalidDataException("替换规则格式不对");
        if (value["id"] is JsonValue id && id.GetValueKind() == JsonValueKind.Number) value["id"] = id.ToJsonString();
        foreach (string key in NullableKeys) if (value[key] is null) value.Remove(key);
        var rule = value.Deserialize<NovelRule>(Options) ?? throw new InvalidDataException("替换规则格式不对");
        return rule.Name.Trim().Length == 0 ? rule with { Name = rule.Pattern.Trim() } : rule;
    }

    internal static string Status(NovelRule rule, NovelRule? existing) => existing is null ? "新增" :
        rule.Pattern != existing.Pattern || rule.Replacement != existing.Replacement || rule.IsRegex != existing.IsRegex || rule.Scope != existing.Scope ? "更新" : "已有";
}
