using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MeowFilm.Capabilities.Library;
using MeowFilm.Core.Debug;
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
        var raw = JsonSerializer.Deserialize<JsonElement>(Definition);
        if (raw.ValueKind != JsonValueKind.Object)
            throw new StorageException(StorageError.Corrupt);
        JsonElement Member(string key) => raw.TryGetProperty(key, out var value) ? value : default;
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
                if (content.ValueKind == JsonValueKind.String && content.GetString() is { } contentText &&
                    contentText.AsSpan().TrimStart().StartsWith('{'))
                {
                    try { content = JsonSerializer.Deserialize<JsonElement>(contentText); }
                    catch (JsonException error)
                    {
                        DebugLog.Warn(DebugCategories.Novel, "source.config", "rule_content.invalid_json", new
                        {
                            installationId = InstallationId
                        }, error: error);
                    }
                }
                if (content.ValueKind == JsonValueKind.Object && content.TryGetProperty("payAction", out var pay) &&
                    pay.ToString().Length > 0) actions.Add("payAction");
            }
            if (Has("loginUrl") || Has("loginUi")) actions.Add("login");
        }
        double Number(string key, double fallback = 0) => double.TryParse(Member(key).ToString(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
            double.IsFinite(value) ? value : fallback;
        return new(InstallationId, Revision, Name, SourceUrl, Group, Order, Enabled, EnabledExplore, Protocol,
            actions.ToArray(), Weight: Number("weight"), LastUpdateTime: Number("lastUpdateTime"),
            RespondTime: Number("respondTime", 180000), HasExplore: Protocol == "qysg" || Has("exploreUrl"));
    }
}

internal sealed record NovelSourceConfigurationSnapshot(int Version, long Revision, NovelSourceDefinition[] Sources);

/// <summary>The native configuration owner. Execution and private source state never enter this store.</summary>
internal sealed class NovelSourceConfiguration(IStorageDatabase database)
{
    internal const string StorageOwner = "novel.sources.v1";
    private const long MaximumRevision = 9007199254740991;
    private readonly SemaphoreSlim serial = new(1, 1);
    private StorageRevision storageRevision = StorageRevision.Empty;
    private StoredNovelSourceRow[] savedRows = [];
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
        var (rows, revision, rev) = await database.ReadSnapshotAsync(db => NovelSourceStore.Load(db, StorageOwner), token);
        storageRevision = rev;
        savedRows = rows;
        saved = new NovelSourceConfigurationSnapshot(1, revision, rows.Select(FromRow).ToArray());
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
            var persistedByUrl = new Dictionary<string, NovelSourceDefinition>(StringComparer.Ordinal);
            foreach (var existing in current.Sources) persistedByUrl[existing.SourceUrl] = existing;
            foreach (var (entry, index) in Entries(text).Select((value, at) => (value, at)))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var candidate = Parse(entry, null, next.Count, fileName, url);
                    var persisted = persistedByUrl.GetValueOrDefault(candidate.SourceUrl);
                    var priorInBatch = next.FirstOrDefault(s => s.SourceUrl == candidate.SourceUrl);
                    var value = persisted is not null
                        ? Parse(entry, persisted, next.Count, fileName, url)
                        : priorInBatch is not null
                            ? candidate with { InstallationId = priorInBatch.InstallationId, Revision = 1 }
                            : candidate;
                    next.RemoveAll(s => s.InstallationId == value.InstallationId);
                    next.Add(value);
                    imported.RemoveAll(s => s.InstallationId == value.InstallationId);
                    imported.Add(value);
                }
                catch (Exception error) when (error is JsonException or InvalidDataException or BridgeException)
                { failures.Add(new { index, name = ExtractFailureName(entry, fileName), code = error is BridgeException bridge ? bridge.Code : "INVALID_REQUEST" }); }
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
            { failures.Add(new { index, name = ExtractFailureName(entry, fileName), code = error is BridgeException bridge ? bridge.Code : "INVALID_REQUEST" }); }
        }
        return NovelJson.Value(new { items, failures });
    }
    internal Task UpdateAsync(NovelSource source, JsonElement patch, CancellationToken token) => MutateAsync(current =>
    {
        var old = Require(source);
        if (patch.ValueKind != JsonValueKind.Object) throw new BridgeException("INVALID_REQUEST");
        string name = old.Name, group = old.Group;
        long order = old.Order;
        double? exactOrder = null;
        bool orderChanged = false;
        bool enabled = old.Enabled, enabledExplore = old.EnabledExplore;
        bool changed = false;
        JsonObject? rawObj = null;

        if (patch.TryGetProperty("name", out var nameVal))
        {
            if (nameVal.ValueKind != JsonValueKind.String) throw new BridgeException("INVALID_REQUEST");
            string nextName = nameVal.GetString()!.Trim();
            if (nextName.Length == 0) nextName = old.SourceUrl;
            if (nextName != name) { name = nextName; changed = true; }
        }
        if (patch.TryGetProperty("group", out var groupVal))
        {
            if (groupVal.ValueKind != JsonValueKind.String) throw new BridgeException("INVALID_REQUEST");
            string nextGroup = groupVal.GetString()!;
            if (nextGroup != group) { group = nextGroup; changed = true; }
        }
        if (patch.TryGetProperty("order", out var orderVal))
        {
            var (nextOrder, nextExact) = ParseCustomOrder(orderVal);
            rawObj = JsonNode.Parse(old.Definition) as JsonObject ?? throw new StorageException(StorageError.Corrupt);
            if (!SameCustomOrder(rawObj["customOrder"], old.Order, nextOrder, nextExact))
            {
                order = nextOrder;
                exactOrder = nextExact;
                orderChanged = true;
                changed = true;
            }
        }
        if (patch.TryGetProperty("enabled", out var enabledVal))
        {
            if (enabledVal.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new BridgeException("INVALID_REQUEST");
            bool nextEnabled = enabledVal.GetBoolean();
            if (nextEnabled != enabled) { enabled = nextEnabled; changed = true; }
        }
        if (patch.TryGetProperty("enabledExplore", out var exploreVal))
        {
            if (exploreVal.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new BridgeException("INVALID_REQUEST");
            bool nextExplore = exploreVal.GetBoolean();
            if (nextExplore != enabledExplore) { enabledExplore = nextExplore; changed = true; }
        }
        if (!changed) return current.Sources;
        if (old.Revision == MaximumRevision) throw new BridgeException("LIMIT");

        rawObj ??= JsonNode.Parse(old.Definition) as JsonObject ?? throw new StorageException(StorageError.Corrupt);
        rawObj["bookSourceName"] = name;
        rawObj["bookSourceGroup"] = group;
        if (orderChanged)
        {
            rawObj["customOrder"] = exactOrder is { } exact && Math.Truncate(exact) != exact ? JsonValue.Create(exact) : JsonValue.Create(order);
        }
        rawObj["enabled"] = enabled;
        rawObj["enabledExplore"] = enabledExplore;
        var item = old with
        {
            Revision = old.Revision + 1,
            Name = name,
            Group = group,
            Order = order,
            Enabled = enabled,
            EnabledExplore = enabledExplore,
            Definition = rawObj.ToJsonString()
        };
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
            long nextRevision = current.Revision + 1;
            var expected = storageRevision;
            var previousRows = savedRows;
            var nextRows = values.Select(ToRow).ToArray();
            var committed = await database.WriteAsync(
                db => NovelSourceStore.Save(db, StorageOwner, expected, nextRevision, previousRows, nextRows),
                token);
            storageRevision = committed;
            savedRows = nextRows;
            saved = new NovelSourceConfigurationSnapshot(1, nextRevision, values);
        }
        finally { serial.Release(); }
    }

    private static NovelSourceDefinition FromRow(StoredNovelSourceRow row) => new(
        row.InstallationId, row.Revision, row.Name, row.SourceUrl, row.Group, row.Order,
        row.Enabled, row.EnabledExplore, row.Protocol,
        row.Definition is { Kind: StorageValueKind.Object } def
            ? StorageValueJson.ToJsonString(def)
            : throw new StorageException(StorageError.Corrupt));

    private static StoredNovelSourceRow ToRow(NovelSourceDefinition source)
    {
        using var doc = JsonDocument.Parse(source.Definition);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new StorageException(StorageError.InvalidRequest);
        return new StoredNovelSourceRow(
            source.InstallationId, source.Revision, source.Name, source.SourceUrl, source.Group, source.Order,
            source.Enabled, source.EnabledExplore, source.Protocol,
            StorageValueJson.FromJsonElement(doc.RootElement));
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
        var raw = definition.StartsWith('<') ? NovelJson.Value(new { html = definition }) : JsonSerializer.Deserialize<JsonElement>(definition);
        if (raw.ValueKind == JsonValueKind.Array && raw.GetArrayLength() == 1) raw = raw[0];
        if (raw.ValueKind == JsonValueKind.String && raw.GetString()!.TrimStart().StartsWith('<'))
            raw = NovelJson.Value(new { html = raw.GetString() });
        if (raw.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        JsonElement Member(string key) => raw.TryGetProperty(key, out var v) ? v : default;
        string Text(string key, string fallback = "") => Member(key) is { ValueKind: JsonValueKind.String } value ? value.GetString()! : fallback;
        bool Bool(string key, bool fallback)
        {
            var value = Member(key);
            return value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? fallback :
                value.ToString().Trim().ToLowerInvariant() is not ("false" or "0" or "");
        }
        string? htmlText = Member("html") is { ValueKind: JsonValueKind.String } htmlValue ? htmlValue.GetString() : null;
        string protocol = htmlText is not null && Regex.IsMatch(htmlText, @"<script\b", RegexOptions.IgnoreCase) ? "qysg" :
            new[] { "ruleSearch", "ruleBookInfo", "ruleToc", "ruleContent" }.Any(key =>
                Member(key).ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) ? "legado" :
            htmlText is not null &&
            !string.IsNullOrWhiteSpace(Text("bookSourceName")) &&
            !string.IsNullOrWhiteSpace(Text("bookSourceUrl")) &&
            IsRemoteHtmlList(htmlText) ? "qysg" :
            throw new BridgeException("UNSUPPORTED_PROTOCOL");
        string qysgFallbackUrl = !string.IsNullOrEmpty(origin)
            ? origin
            : "qysg:" + (!string.IsNullOrEmpty(fileName)
                ? fileName
                : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(definition))));
        string sourceUrl = Text("bookSourceUrl", old?.SourceUrl ?? (protocol == "qysg" ? qysgFallbackUrl : "")).Trim();
        if (sourceUrl.Length == 0) throw new InvalidDataException("请填写源 URL");
        string name = Text("bookSourceName", old?.Name ?? fileName ?? "").Trim();
        if (name.Length == 0) name = sourceUrl;
        var orderMember = Member("customOrder");
        long customOrder;
        double? exactCustomOrder = null;
        if (orderMember.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            customOrder = old?.Order ?? order;
        }
        else
        {
            (customOrder, exactCustomOrder) = ParseCustomOrder(orderMember);
        }
        if (old?.Revision == MaximumRevision) throw new BridgeException("LIMIT");
        var source = new NovelSourceDefinition(old?.InstallationId ?? Guid.NewGuid().ToString("N"), (old?.Revision ?? 0) + 1,
            name, sourceUrl, Text("bookSourceGroup", old?.Group ?? ""), customOrder, Bool("enabled", old?.Enabled ?? true),
            Bool("enabledExplore", old?.EnabledExplore ?? (Member("exploreUrl").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) || protocol == "qysg")), protocol, "");
        var patched = JsonNode.Parse(raw.GetRawText()) as JsonObject ?? new JsonObject();
        patched["bookSourceName"] = source.Name;
        patched["bookSourceUrl"] = source.SourceUrl;
        patched["bookSourceGroup"] = source.Group;
        patched["customOrder"] = exactCustomOrder is { } exact && Math.Truncate(exact) != exact
            ? JsonValue.Create(exact) : JsonValue.Create(source.Order);
        patched["enabled"] = source.Enabled;
        patched["enabledExplore"] = source.EnabledExplore;
        return source with { Definition = patched.ToJsonString() };
    }
    private static (long Order, double Exact) ParseCustomOrder(JsonElement value)
    {
        double number;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDouble(out number) || !double.IsFinite(number))
                throw new InvalidDataException("排序格式无效");
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            if (!double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || !double.IsFinite(number))
                throw new InvalidDataException("排序格式无效");
        }
        else
        {
            throw new InvalidDataException("排序格式无效");
        }
        if (number < -MaximumRevision || number > MaximumRevision)
            throw new InvalidDataException("排序格式无效");
        return ((long)Math.Round(number, MidpointRounding.AwayFromZero), number);
    }
    private static bool SameCustomOrder(JsonNode? existingNode, long existingOrder, long nextOrder, double nextExact) =>
        existingOrder == nextOrder &&
        (existingNode is JsonValue existingVal &&
         double.TryParse(existingVal.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
         double.IsFinite(parsed) ? parsed == nextExact : existingOrder == nextExact);
    private static bool IsRemoteHtmlList(string value)
    {
        var entries = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return entries.Length > 0 && entries.All(entry =>
            Uri.TryCreate(entry, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https");
    }
    private static string ExtractFailureName(string entry, string? fileName)
    {
        try
        {
            using var doc = JsonDocument.Parse(entry.Trim().TrimStart('\uFEFF'));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1) root = root[0];
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("bookSourceName", out var prop) && prop.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    return prop.ValueKind == JsonValueKind.String ? prop.GetString()! : prop.GetRawText();
                return "";
            }
        }
        catch (JsonException) { }
        return fileName ?? "";
    }
}
