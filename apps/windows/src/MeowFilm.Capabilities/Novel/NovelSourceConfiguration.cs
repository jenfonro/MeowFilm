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
    internal NovelSource Source() => new(InstallationId, Revision, Name, SourceUrl, Group, Order,
        Enabled, EnabledExplore, Protocol, []);

    internal NovelSource SourceFromDefinition()
    {
        // Only an explicit import/edit or an applied execution consumes definition
        // hints. Opening the configuration list never parses source rules.
        JsonElement raw;
        try { raw = JsonSerializer.Deserialize<JsonElement>(Definition); }
        catch (JsonException) { raw = default; }
        JsonElement Member(string key) => raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(key, out var value) ? value : default;
        bool Has(string key)
        {
            var value = Member(key);
            return value.ValueKind switch
            {
                JsonValueKind.Undefined or JsonValueKind.Null => false,
                JsonValueKind.String => value.GetString()!.Length > 0,
                JsonValueKind.Object => value.EnumerateObject().Any(),
                _ => true
            };
        }
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
                var content = Member("ruleContent");
                if (content.ValueKind == JsonValueKind.String)
                    try { content = JsonSerializer.Deserialize<JsonElement>(content.GetString()!); } catch (JsonException) { }
                if (content.ValueKind == JsonValueKind.Object && content.TryGetProperty("payAction", out var pay) &&
                    pay.ToString().Length > 0) actions.Add("payAction");
            }
            if (Has("loginUrl") || Has("loginUi")) actions.Add("login");
        }
        double Number(string key, double fallback = 0) => double.TryParse(Member(key).ToString(),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) &&
            double.IsFinite(value) ? value : fallback;
        return new(InstallationId, Revision, Name, SourceUrl, Group, Order, Enabled, EnabledExplore, Protocol,
            actions.ToArray(), Weight: Number("weight"), LastUpdateTime: Number("lastUpdateTime"),
            RespondTime: Number("respondTime", 180000), HasExplore: Protocol == "qysg" || Has("exploreUrl"));
    }
}

internal sealed record NovelSourceConfigurationSnapshot(int Version, long Revision, NovelSourceDefinition[] Sources)
{
    [JsonIgnore] public JsonElement Original { get; init; }
}

/// <summary>The native configuration owner. Execution and private source state never enter this document.</summary>
internal sealed class NovelSourceConfiguration(IStorageDatabase database)
{
    private readonly TypedRecordRepository store = new(database, DocumentNamespace, NovelStorageRecords.Sources, ToRows, FromRows);
    private static JsonObject ToRows(JsonObject value)
    {
        foreach (var row in value["sources"]!.AsArray().OfType<JsonObject>())
            row["definition"] = JsonNode.Parse(row["definition"]!.GetValue<string>()) as JsonObject ??
                throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        return value;
    }
    private static JsonObject FromRows(JsonObject value)
    {
        if (value["sources"] is not JsonArray sources) throw new DocumentStoreException(DocumentStoreError.Corrupt);
        foreach (var item in sources)
        {
            if (item is not JsonObject row || row["definition"] is not JsonObject definition)
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
            row["definition"] = definition.ToJsonString();
        }
        return value;
    }
    internal const string DocumentNamespace = "novel.sources.v1";
    private const long MaximumRevision = 9007199254740991;
    private readonly SemaphoreSlim serial = new(1, 1);
    private StorageRevision storageRevision = StorageRevision.Empty;
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
        var read = await store.ReadAsync(token);
        NovelSourceConfigurationSnapshot next;
        if (read.Data is null)
            next = new(1, 1, []);
        else
        {
            try { using var json = JsonDocument.Parse(read.Data); next = Decode(json.RootElement); }
            catch (JsonException) { throw new DocumentStoreException(DocumentStoreError.Corrupt); }
        }
        storageRevision = read.Revision; saved = next;
    }
    private static NovelSourceConfigurationSnapshot Decode(JsonElement value)
    {
        try
        {
            int version = BridgeCodec.RequiredProperty(value, "version").GetInt32();
            long revision = BridgeCodec.RequiredProperty(value, "revision").GetInt64();
            if (version != 1 || revision is < 1 or > MaximumRevision) throw new JsonException();
            var rows = BridgeCodec.RequiredProperty(value, "sources").EnumerateArray().ToArray();
            var ids = rows.SelectMany(row => Ids(row, "installationId")).GroupBy(id => id)
                .ToDictionary(group => group.Key, group => group.Count());
            var urls = rows.SelectMany(row => Ids(row, "sourceUrl")).GroupBy(id => id)
                .ToDictionary(group => group.Key, group => group.Count());
            var sources = new List<NovelSourceDefinition>();
            foreach (var row in rows)
            {
                try
                {
                    string id = BridgeCodec.RequiredProperty(row, "installationId").GetString()!;
                    string url = BridgeCodec.RequiredProperty(row, "sourceUrl").GetString()!;
                    if (id is null || url is null || ids[id] != 1 || urls[url] != 1) continue;
                    long sourceRevision = BridgeCodec.RequiredProperty(row, "revision").GetInt64();
                    if (sourceRevision is < 1 or > MaximumRevision) continue;
                    string Text(string key) => BridgeCodec.RequiredProperty(row, key).GetString() ?? throw new JsonException();
                    var source = new NovelSourceDefinition(id, sourceRevision, Text("name"), url, Text("group"),
                        BridgeCodec.RequiredProperty(row, "order").GetInt64(),
                        BridgeCodec.RequiredProperty(row, "enabled").GetBoolean(), BridgeCodec.RequiredProperty(row, "enabledExplore").GetBoolean(),
                        Text("protocol"), Text("definition"));
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url) || source.Protocol is not ("legado" or "qysg")) continue;
                    sources.Add(source);
                }
                catch (Exception error) when (error is JsonException or BridgeException or InvalidOperationException or FormatException) { }
            }
            if (rows.Length > 0 && sources.Count == 0) throw new JsonException();
            var result = new NovelSourceConfigurationSnapshot(version, revision, sources.ToArray()) { Original = value.Clone() };
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or NullReferenceException or BridgeException or InvalidOperationException or FormatException)
        { throw new DocumentStoreException(DocumentStoreError.Corrupt); }
    }
    private static string[] Ids(JsonElement row, string key) => row.ValueKind == JsonValueKind.Object
        ? row.EnumerateObject().Where(item => item.NameEquals(key) && item.Value.ValueKind == JsonValueKind.String)
            .Select(item => item.Value.GetString()!).ToArray() : [];
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
        return NovelJson.Value(new { sources = imported.Select(s => s.SourceFromDefinition()), failures });
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
        var old = Require(source); var raw = JsonSerializer.Deserialize<JsonElement>(old.Definition);
        foreach (var (key, field) in new[] { ("name", "bookSourceName"), ("group", "bookSourceGroup"), ("order", "customOrder"),
            ("enabled", "enabled"), ("enabledExplore", "enabledExplore") })
            if (patch.TryGetProperty(key, out var value)) raw = SettingsDocument.SetMember(raw, field, value);
        var item = Parse(raw.GetRawText(), old, current.Sources.Length);
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
            var original = current.Original.ValueKind == JsonValueKind.Undefined ? NovelJson.Value(current) : current.Original;
            var rows = original.GetProperty("sources").EnumerateArray().ToArray();
            foreach (var added in values.Where(item => current.Sources.All(old => old.InstallationId != item.InstallationId)))
                if (rows.Any(row => Ids(row, "installationId").Contains(added.InstallationId) || Ids(row, "sourceUrl").Contains(added.SourceUrl)))
                    throw new BridgeException("SOURCE_INTEGRITY");
            var before = current.Sources.ToDictionary(item => item.InstallationId);
            var after = values.ToDictionary(item => item.InstallationId);
            var merged = new List<JsonElement>();
            foreach (var row in rows)
            {
                var identities = Ids(row, "installationId");
                if (identities.Length != 1 || !before.TryGetValue(identities[0], out var old)) { merged.Add(row); continue; }
                if (!after.TryGetValue(identities[0], out var changed)) continue;
                var nextRow = row;
                var previous = NovelJson.Value(old);
                foreach (var property in NovelJson.Value(changed).EnumerateObject())
                    if (!JsonElement.DeepEquals(previous.GetProperty(property.Name), property.Value))
                        nextRow = SettingsDocument.SetMember(nextRow, property.Name, property.Value);
                merged.Add(nextRow);
            }
            merged.AddRange(values.Where(item => !before.ContainsKey(item.InstallationId)).Select(item => NovelJson.Value(item)));
            var raw = SettingsDocument.SetMember(original, "revision", NovelJson.Value(current.Revision + 1));
            raw = SettingsDocument.SetMember(raw, "sources", BridgeCodec.Value(merged));
            var next = new NovelSourceConfigurationSnapshot(1, current.Revision + 1, values) { Original = raw };
            var bytes = BridgeCodec.EncodeJSONValue(raw);
            var receipt = await store.WriteAsync(bytes, storageRevision, token);
            storageRevision = receipt; saved = next;
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
        var raw = definition.StartsWith('<') ? NovelJson.Value(new { html = definition }) : JsonSerializer.Deserialize<JsonElement>(definition);
        if (raw.ValueKind == JsonValueKind.Array && raw.GetArrayLength() == 1) raw = raw[0];
        if (raw.ValueKind == JsonValueKind.String && raw.GetString()!.TrimStart().StartsWith('<'))
            raw = NovelJson.Value(new { html = raw.GetString() });
        if (raw.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        JsonElement Member(string key) => raw.TryGetProperty(key, out _) ? BridgeCodec.RequiredProperty(raw, key) : default;
        string Text(string key, string fallback = "") => Member(key) is { ValueKind: JsonValueKind.String } value ? value.GetString()! : fallback;
        bool Bool(string key, bool fallback)
        {
            var value = Member(key);
            return value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? fallback :
                value.ToString().Trim().ToLowerInvariant() is not ("false" or "0" or "");
        }
        string protocol = Member("html").ValueKind == JsonValueKind.String ? "qysg" :
            new[] { "ruleSearch", "ruleBookInfo", "ruleToc", "ruleContent" }.Any(key =>
                Member(key).ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) ? "legado" :
            throw new BridgeException("UNSUPPORTED_PROTOCOL");
        string sourceUrl = Text("bookSourceUrl", old?.SourceUrl ?? (protocol == "qysg" ?
            origin ?? "qysg:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(definition))) : "")).Trim();
        if (sourceUrl.Length == 0) throw new InvalidDataException("请填写源 URL");
        string name = Text("bookSourceName", old?.Name ?? fileName ?? "").Trim();
        if (name.Length == 0) name = sourceUrl;
        long customOrder = Member("customOrder").ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? old?.Order ?? order :
            long.TryParse(Member("customOrder").ToString(), out var number) && number is >= -MaximumRevision and <= MaximumRevision
                ? number : throw new InvalidDataException("排序格式无效");
        if (old?.Revision == MaximumRevision) throw new BridgeException("LIMIT");
        var source = new NovelSourceDefinition(old?.InstallationId ?? Guid.NewGuid().ToString("N"), (old?.Revision ?? 0) + 1,
            name, sourceUrl, Text("bookSourceGroup", old?.Group ?? ""), customOrder, Bool("enabled", old?.Enabled ?? true),
            Bool("enabledExplore", old?.EnabledExplore ?? (Member("exploreUrl").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) || protocol == "qysg")), protocol, "");
        raw = SettingsDocument.Patch(raw, new Dictionary<string, JsonElement>
        {
            ["bookSourceName"] = NovelJson.Value(source.Name), ["bookSourceUrl"] = NovelJson.Value(source.SourceUrl),
            ["bookSourceGroup"] = NovelJson.Value(source.Group), ["customOrder"] = NovelJson.Value(source.Order),
            ["enabled"] = NovelJson.Value(source.Enabled), ["enabledExplore"] = NovelJson.Value(source.EnabledExplore)
        });
        return source with { Definition = raw.GetRawText() };
    }
}
