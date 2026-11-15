using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed record NovelSourceDefinition(string InstallationId, long Revision, string Name, string SourceUrl,
    string Group, long Order, bool Enabled, bool EnabledExplore, string Protocol, string Definition)
{
    internal NovelSource Source()
    {
        var raw = JsonNode.Parse(Definition)!.AsObject();
        bool Has(string key) => raw[key] is { } value && value.ToJsonString() is not ("null" or "\"\"" or "{}");
        var actions = new List<string> { "image", "loginStatus" };
        if (Protocol == "qysg") actions.AddRange(["search", "exploreKinds", "explore", "bookInfo", "toc", "content", "login"]);
        else
        {
            if (Has("searchUrl")) actions.Add("search");
            if (Has("exploreUrl")) actions.AddRange(["exploreKinds", "explore"]);
            if (Has("ruleBookInfo")) actions.Add("bookInfo");
            if (Has("ruleToc")) actions.Add("toc");
            if (Has("ruleContent"))
            {
                actions.Add("content");
                JsonObject? content = raw["ruleContent"] as JsonObject;
                if (content is null)
                    try { content = JsonNode.Parse(raw["ruleContent"]!.ToString()) as JsonObject; } catch (JsonException) { }
                if (content?["payAction"]?.ToString() is { Length: > 0 }) actions.Add("payAction");
            }
            if (Has("loginUrl") || Has("loginUi")) actions.Add("login");
        }
        double Number(string key, double fallback = 0) => double.TryParse(raw[key]?.ToString(),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) &&
            double.IsFinite(value) ? value : fallback;
        return new(InstallationId, Revision, Name, SourceUrl, Group, Order, Enabled, EnabledExplore, Protocol,
            actions.ToArray(), Weight: Number("weight"), LastUpdateTime: Number("lastUpdateTime"),
            RespondTime: Number("respondTime", 180000), HasExplore: Protocol == "qysg" || Has("exploreUrl"));
    }
}

internal sealed record NovelSourceConfigurationSnapshot(int Version, long Revision, NovelSourceDefinition[] Sources);

/// <summary>The native configuration owner. Execution and private source state never enter this document.</summary>
internal sealed class NovelSourceConfiguration(IDocumentStore store)
{
    internal const string DocumentNamespace = "novel.sources.v1";
    private const long MaximumRevision = 9007199254740991;
    private static readonly JsonSerializerOptions Options = new(NovelJson.Options)
    { PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict };
    private readonly SemaphoreSlim serial = new(1, 1);
    private DocumentFingerprint fingerprint = DocumentFingerprint.Missing;
    private NovelSourceConfigurationSnapshot? saved;
    internal NovelSourceConfigurationSnapshot Snapshot => saved ?? throw new InvalidOperationException("NOT_INITIALIZED");

    internal async Task LoadAsync(CancellationToken token, bool reload = false)
    {
        await serial.WaitAsync(token);
        try { await LoadCoreAsync(token, reload); }
        finally { serial.Release(); }
    }
    private async Task LoadCoreAsync(CancellationToken token, bool reload = false)
    {
        if (saved is not null && !reload) return;
        var read = await store.ReadAsync(DocumentNamespace, token);
        NovelSourceConfigurationSnapshot next;
        if (read.Data is null)
            next = new(1, 1, []);
        else
        {
            try { using var json = JsonDocument.Parse(read.Data); next = Decode(json.RootElement); }
            catch (JsonException) { throw new DocumentStoreException(DocumentStoreError.Corrupt); }
        }
        fingerprint = read.Fingerprint; saved = next;
    }
    private static NovelSourceConfigurationSnapshot Decode(JsonElement value)
    {
        try
        {
            static bool Complete(JsonElement item, string[] keys) => item.ValueKind == JsonValueKind.Object &&
                item.EnumerateObject().Count() == keys.Length &&
                keys.All(key => item.EnumerateObject().Count(p => p.NameEquals(key)) == 1);
            if (!Complete(value, ["version", "revision", "sources"]) ||
                value.GetProperty("sources").ValueKind != JsonValueKind.Array ||
                value.GetProperty("sources").EnumerateArray().Any(item => !Complete(item,
                    ["installationId", "revision", "name", "sourceUrl", "group", "order", "enabled", "enabledExplore", "protocol", "definition"])))
                throw new JsonException();
            var result = value.Deserialize<NovelSourceConfigurationSnapshot>(Options) ?? throw new JsonException();
            if (result.Version != 1 || result.Revision is < 1 or > MaximumRevision || result.Sources is null ||
                result.Sources.Select(s => s.InstallationId).Distinct(StringComparer.Ordinal).Count() != result.Sources.Length ||
                result.Sources.Select(s => s.SourceUrl).Distinct(StringComparer.Ordinal).Count() != result.Sources.Length)
                throw new JsonException();
            foreach (var source in result.Sources)
            {
                if (source.InstallationId is not { Length: > 0 and <= 128 } ||
                    !char.IsAsciiLetterOrDigit(source.InstallationId[0]) ||
                    source.InstallationId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')) ||
                    source.Revision is < 1 or > MaximumRevision || source.Order is < -MaximumRevision or > MaximumRevision ||
                    string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.SourceUrl) ||
                    source.Group is null || source.Protocol is not ("legado" or "qysg") ||
                    JsonNode.Parse(source.Definition) is not JsonObject)
                    throw new JsonException();
            }
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or NullReferenceException)
        { throw new DocumentStoreException(DocumentStoreError.Corrupt); }
    }
    internal NovelSourceDefinition Require(NovelSource source) =>
        Snapshot.Sources.FirstOrDefault(s => s.InstallationId == source.InstallationId && s.Revision == source.Revision)
            ?? throw new BridgeException("STALE_SOURCE");
    internal async Task<NovelSourceDefinition> SaveAsync(NovelSource? source, string protocol, string definition, CancellationToken token)
    {
        NovelSourceDefinition? savedSource = null;
        await MutateAsync(current =>
        {
            var old = source is null ? null : Require(source);
            savedSource = Parse(definition, old, current.Sources.Length);
            if (savedSource.Protocol != protocol) throw new BridgeException("SOURCE_INTEGRITY");
            if (current.Sources.Any(s => s.InstallationId != savedSource.InstallationId && s.SourceUrl == savedSource.SourceUrl))
                throw new BridgeException("SOURCE_INTEGRITY");
            return current.Sources.Where(s => s.InstallationId != savedSource.InstallationId).Append(savedSource).ToArray();
        }, token);
        return savedSource!;
    }
    internal async Task<JsonElement> ImportAsync(string text, string? fileName, string? url, CancellationToken token)
    {
        var imported = new List<NovelSourceDefinition>();
        var failures = new List<object>();
        await MutateAsync(current =>
        {
            var next = current.Sources.ToList();
            foreach (var (entry, index) in Entries(text).Select((value, at) => (value, at)))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var candidate = Parse(entry, null, next.Count, fileName, url);
                    var old = next.FirstOrDefault(s => s.SourceUrl == candidate.SourceUrl);
                    var value = old is null ? candidate : Parse(entry, old, next.Count, fileName, url);
                    next.RemoveAll(s => s.InstallationId == value.InstallationId); next.Add(value); imported.Add(value);
                }
                catch (Exception error) when (error is JsonException or InvalidDataException or BridgeException)
                { failures.Add(new { index, name = "", code = error is BridgeException bridge ? bridge.Code : "INVALID_REQUEST" }); }
            }
            return next.ToArray();
        }, token);
        return NovelJson.Value(new { sources = imported.Select(s => s.Source()), failures });
    }
    internal JsonElement Preview(string text, string? fileName, string? url)
    {
        var items = new List<object>(); var failures = new List<object>();
        foreach (var (entry, index) in Entries(text).Select((value, at) => (value, at)))
        {
            try
            {
                var item = Parse(entry, null, Snapshot.Sources.Length, fileName, url);
                items.Add(new { index, name = item.Name, sourceUrl = item.SourceUrl, group = item.Group, protocol = item.Protocol,
                    exists = Snapshot.Sources.Any(s => s.SourceUrl == item.SourceUrl), definition = item.Definition });
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or BridgeException)
            { failures.Add(new { index, name = "", code = error is BridgeException bridge ? bridge.Code : "INVALID_REQUEST" }); }
        }
        return NovelJson.Value(new { items, failures });
    }
    internal Task UpdateAsync(NovelSource source, JsonElement patch, CancellationToken token) => MutateAsync(current =>
    {
        var old = Require(source); var raw = JsonNode.Parse(old.Definition)!.AsObject();
        foreach (var (key, field) in new[] { ("name", "bookSourceName"), ("group", "bookSourceGroup"), ("order", "customOrder"),
            ("enabled", "enabled"), ("enabledExplore", "enabledExplore") })
            if (patch.TryGetProperty(key, out var value)) raw[field] = JsonNode.Parse(value.GetRawText());
        var item = Parse(raw.ToJsonString(), old, current.Sources.Length);
        return current.Sources.Select(s => s.InstallationId == item.InstallationId ? item : s).ToArray();
    }, token);
    internal Task RemoveAsync(NovelSource source, CancellationToken token) => MutateAsync(current =>
    {
        Require(source); return current.Sources.Where(s => s.InstallationId != source.InstallationId).ToArray();
    }, token);
    private async Task MutateAsync(Func<NovelSourceConfigurationSnapshot, NovelSourceDefinition[]> update, CancellationToken token)
    {
        await serial.WaitAsync(token);
        try
        {
            await LoadCoreAsync(token);
            var current = Snapshot;
            var values = update(current);
            if (values.SequenceEqual(current.Sources)) return;
            if (current.Revision == MaximumRevision) throw new BridgeException("LIMIT");
            var next = new NovelSourceConfigurationSnapshot(1, current.Revision + 1, values);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, NovelJson.Options);
            var receipt = await store.WriteAsync(DocumentNamespace, bytes, fingerprint, token);
            fingerprint = receipt; saved = next;
        }
        finally { serial.Release(); }
    }
    private static IEnumerable<string> Entries(string input)
    {
        input = input.Trim().TrimStart('\uFEFF');
        if (input.StartsWith('<')) return [input];
        try
        {
            using var json = JsonDocument.Parse(input);
            var value = json.RootElement;
            if (value.ValueKind == JsonValueKind.Object)
                foreach (string key in new[] { "sources", "bookSources", "data" })
                    if (value.TryGetProperty(key, out var section) && section.ValueKind == JsonValueKind.Array) { value = section; break; }
            return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(v => v.GetRawText()).ToArray() : [value.GetRawText()];
        }
        catch (JsonException) { return input.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); }
    }
    private static NovelSourceDefinition Parse(string definition, NovelSourceDefinition? old, int order,
        string? fileName = null, string? origin = null)
    {
        definition = definition.Trim().TrimStart('\uFEFF');
        if (Encoding.UTF8.GetByteCount(definition) > 16 * 1024 * 1024) throw new BridgeException("LIMIT");
        JsonNode? value = definition.StartsWith('<') ? JsonValue.Create(definition) : JsonNode.Parse(definition);
        if (value is JsonArray array && array.Count == 1) value = array[0];
        var raw = value switch
        {
            JsonObject obj => (JsonObject)obj.DeepClone(),
            JsonValue scalar when scalar.TryGetValue<string>(out var html) && html.TrimStart().StartsWith('<') =>
                new JsonObject { ["html"] = html },
            _ => throw new InvalidDataException()
        };
        string Text(string key, string fallback = "") => raw[key] is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : fallback;
        bool Bool(string key, bool fallback) => raw[key] is null ? fallback :
            raw[key]!.ToString().Trim().ToLowerInvariant() is not ("false" or "0" or "");
        string protocol = raw["html"] is JsonValue htmlValue && htmlValue.TryGetValue<string>(out _) ? "qysg" :
            new[] { "ruleSearch", "ruleBookInfo", "ruleToc", "ruleContent" }.Any(key => raw[key] is not null) ? "legado" :
            throw new BridgeException("UNSUPPORTED_PROTOCOL");
        string sourceUrl = Text("bookSourceUrl", old?.SourceUrl ?? (protocol == "qysg" ?
            origin ?? "qysg:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(definition))) : "")).Trim();
        if (sourceUrl.Length == 0) throw new InvalidDataException("请填写源 URL");
        string name = Text("bookSourceName", old?.Name ?? fileName ?? "").Trim();
        if (name.Length == 0) name = sourceUrl;
        long customOrder = raw["customOrder"] is null ? old?.Order ?? order :
            long.TryParse(raw["customOrder"]!.ToString(), out var number) && number is >= -MaximumRevision and <= MaximumRevision
                ? number : throw new InvalidDataException("排序格式无效");
        if (old?.Revision == MaximumRevision) throw new BridgeException("LIMIT");
        var source = new NovelSourceDefinition(old?.InstallationId ?? Guid.NewGuid().ToString("N"), (old?.Revision ?? 0) + 1,
            name, sourceUrl, Text("bookSourceGroup", old?.Group ?? ""), customOrder, Bool("enabled", old?.Enabled ?? true),
            Bool("enabledExplore", old?.EnabledExplore ?? (raw["exploreUrl"] is not null || protocol == "qysg")), protocol, "");
        raw["bookSourceName"] = source.Name; raw["bookSourceUrl"] = source.SourceUrl; raw["bookSourceGroup"] = source.Group;
        raw["customOrder"] = source.Order; raw["enabled"] = source.Enabled; raw["enabledExplore"] = source.EnabledExplore;
        return source with { Definition = raw.ToJsonString() };
    }
}
