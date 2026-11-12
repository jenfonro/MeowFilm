using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

internal sealed record FilmPlaybackOpen(RuntimeConnection Connection, JsonElement Parameters)
{
    internal static bool SameOwner(RuntimeConnection? previous, RuntimeConnection next) =>
        previous is not null && !previous.Revoked.IsCancellationRequested &&
        ReferenceEquals(previous.Client, next.Client) && previous.Revoked == next.Revoked;

    internal static IReadOnlyList<JsonElement> OriginalSources(JsonElement original, IReadOnlyList<JsonElement> available)
    {
        if (FilmJson.Member(original, "sources").ValueKind == JsonValueKind.Array)
            return FilmJson.Array(original, "sources");
        var raw = FilmJson.Member(FilmJson.Member(original, "target"), "source");
        var recorded = FilmJson.Array(original, "cacheEntries")
            .Where(entry => FilmJson.Text(entry, "kind") is "search" or "detail")
            .Select(entry => FilmJson.Member(entry, "source")).ToList();
        if (FilmJson.Text(raw, "installationId").Length > 0) recorded.Add(raw);
        var facts = FilmJson.Array(original, "results").Concat(FilmJson.Array(original, "resultHints"))
            .Concat(FilmJson.Array(original, "search"))
            .Concat(FilmJson.Array(FilmJson.Member(original, "target"), "members")).ToArray();
        if (new[] { "results", "resultHints", "search" }.Any(name =>
                FilmJson.Member(original, name).ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Array)) ||
            raw.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object) ||
            facts.Any(row => FilmJson.Member(row, "sourceKey").ValueKind != JsonValueKind.String ||
                FilmJson.Text(row, "sourceKey").Length == 0) ||
            recorded.Any(row => FilmJson.Member(row, "key").ValueKind != JsonValueKind.String ||
                FilmJson.Text(row, "key").Length == 0) ||
            raw.ValueKind == JsonValueKind.Object && (FilmJson.Member(raw, "key").ValueKind != JsonValueKind.String ||
                FilmJson.Text(raw, "key").Length == 0))
            throw new FilmPlaybackException("INVALID_REQUEST");
        if (facts.Length == 0 && recorded.Count == 0 && raw.ValueKind != JsonValueKind.Object) return available;
        var keys = facts.Select(row => FilmJson.Text(row, "sourceKey"))
            .Concat(recorded.Select(row => FilmJson.Text(row, "key")))
            .Append(FilmJson.Text(raw, "key")).Where(key => key.Length > 0)
            .Distinct(StringComparer.Ordinal).ToArray();
        return keys.Select(key =>
        {
            var evidence = recorded.Where(source => FilmJson.Text(source, "key") == key).ToArray();
            if (evidence.Length > 0)
            {
                if (evidence.Skip(1).Any(source => !SourceEquals(source, evidence[0])))
                    throw new FilmPlaybackException("INVALID_REQUEST");
                return evidence[0];
            }
            var exact = available.FirstOrDefault(source => FilmJson.Text(source, "key") == key);
            if (exact.ValueKind != JsonValueKind.Object) return default;
            // A key identifies the allowed source, not the generation that produced
            // old rows. Missing provenance never becomes current-owner evidence.
            var scope = JsonNode.Parse(exact.GetRawText())!.AsObject();
            scope.Remove("generation"); scope.Remove("revision");
            return JsonSerializer.SerializeToElement(scope, FilmJson.Options);
        }).Where(source => source.ValueKind == JsonValueKind.Object).ToArray();
    }

    internal static JsonElement RememberResults(JsonElement original, FilmPlaybackSnapshot? snapshot)
    {
        if (snapshot is null) return original;
        var parameters = JsonNode.Parse(original.GetRawText())!.AsObject();
        var hints = parameters["resultHints"] as JsonArray ?? new JsonArray();
        var sources = FilmJson.Array(original, "sources").Select(source =>
        {
            if (FilmJson.Text(source, "key").Length > 0) return source;
            var observed = FilmJson.Array(snapshot.Raw, "sources")
                .Select(row => FilmJson.Member(row, "source")).FirstOrDefault(row => SourceEquals(row, source));
            if (observed.ValueKind != JsonValueKind.Object) return source;
            var named = JsonNode.Parse(source.GetRawText())!.AsObject();
            named["key"] = FilmJson.Text(observed, "key");
            return JsonSerializer.SerializeToElement(named);
        }).ToArray();
        parameters["sources"] = JsonSerializer.SerializeToNode(sources, FilmJson.Options);
        var known = FilmJson.Array(original, "results").Concat(FilmJson.Array(original, "resultHints"))
            .SelectMany(row => FilmJson.Array(row, "items").Select(item =>
                (Key: FilmJson.Text(row, "sourceKey"), Detail: DetailIdentity(item)))).ToList();
        foreach (var row in FilmJson.Array(snapshot.Raw, "sources"))
        {
            var source = FilmJson.Member(row, "source");
            string key = FilmJson.Text(source, "key");
            var detail = FilmJson.Member(source, "detailId");
            if (!FilmJson.IsIdentity(detail) || known.Any(value => value.Key == key &&
                value.Detail.ValueKind != JsonValueKind.Undefined && JsonElement.DeepEquals(value.Detail, detail))) continue;
            // These are discovery pointers, not a fresh search page or detail cache.
            // Raw remarks in original results remain untouched.
            hints.Add(JsonSerializer.SerializeToNode(new
            {
                sourceKey = key,
                items = new[] { new { id = detail, title = FilmJson.Text(row, "title") } }
            }, FilmJson.Options));
            known.Add((key, detail));
        }
        parameters["resultHints"] = hints;
        return JsonSerializer.SerializeToElement(parameters, FilmJson.Options);
    }

    internal static JsonElement ParametersFor(FilmPlaybackPage page, JsonElement original,
        RuntimeConnection connection, IReadOnlyList<JsonElement> available)
    {
        var parameters = JsonNode.Parse(original.GetRawText())!.AsObject();
        var originalSources = OriginalSources(original, available);
        var target = page.CaptureRequest.Target;
        var admitted = originalSources
            .Where(old => target.Kind != "source" || target.Source is { } raw &&
                FilmJson.Text(old, "key") == FilmJson.Text(raw, "key"))
            .Select(old => (Old: old, Current: available.FirstOrDefault(value =>
            FilmJson.Text(value, "engine") == FilmJson.Text(old, "engine") &&
            FilmJson.Text(value, "installationId") == FilmJson.Text(old, "installationId") &&
            FilmJson.Text(value, "siteId") == FilmJson.Text(old, "siteId"))))
            .Where(pair => pair.Current.ValueKind == JsonValueKind.Object).ToArray();
        if (target.Kind == "source" && admitted.Length == 0) throw new FilmPlaybackException("SOURCE_NOT_FOUND");
        if (target.Kind == "group")
        {
            var members = FilmJson.Array(target.Members ?? default).Where(member => admitted.Any(pair =>
                FilmJson.Text(pair.Old, "key") == FilmJson.Text(member, "sourceKey"))).ToArray();
            if (members.Length == 0) throw new FilmPlaybackException("SOURCE_NOT_FOUND");
            target = target with { Members = FilmJson.Encode(members) };
            if (target.Source is { } selected && !members.Any(member =>
                FilmJson.Text(member, "sourceKey") == FilmJson.Text(selected, "key") &&
                JsonElement.DeepEquals(FilmJson.Member(member, "detailId"), FilmJson.Member(selected, "detailId"))))
                target = target with { Source = null };
        }
        var sources = new JsonArray();
        var trusted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (old, current) in admitted)
        {
            var source = JsonNode.Parse(current.GetRawText())!.AsObject();
            string key = FilmJson.Text(old, "key");
            if (key.Length > 0) source["key"] = key;
            sources.Add(source);
            if (key.Length > 0 && SameOwner(page.Request.DataOwner, connection) &&
                FilmJson.Text(old, "generation") == FilmJson.Text(current, "generation") &&
                FilmJson.Text(old, "revision") == FilmJson.Text(current, "revision"))
                trusted.Add(key);
        }
        if (!SameOwner(page.Request.DataOwner, connection))
            parameters.Remove("searchReference");
        parameters["target"] = JsonSerializer.SerializeToNode(target.Wire(), FilmJson.Options);
        if (parameters["searchReference"] is JsonObject && page.HasPlaybackSnapshot)
            parameters["resumeTarget"] = JsonSerializer.SerializeToNode(target.Wire(), FilmJson.Options);
        else parameters.Remove("resumeTarget");
        parameters["metadata"] = page.CaptureRequest.Metadata is { } metadata
            ? JsonNode.Parse(metadata.GetRawText()) : null;
        parameters["sources"] = sources;
        var results = new JsonArray();
        var hints = new JsonArray();
        foreach (var result in FilmJson.Array(original, "results"))
        {
            string key = FilmJson.Text(result, "sourceKey");
            if (!admitted.Any(pair => FilmJson.Text(pair.Old, "key") == key)) continue;
            (trusted.Contains(key) ? results : hints).Add(JsonNode.Parse(result.GetRawText()));
        }
        foreach (var hint in FilmJson.Array(original, "resultHints"))
            if (admitted.Any(pair => FilmJson.Text(pair.Old, "key") == FilmJson.Text(hint, "sourceKey")))
                hints.Add(JsonNode.Parse(hint.GetRawText()));
        parameters["results"] = results;
        parameters["resultHints"] = hints;
        parameters["search"] = new JsonArray(FilmJson.Array(original, "search")
            .Where(row => trusted.Contains(FilmJson.Text(row, "sourceKey")))
            .Select(row => JsonNode.Parse(row.GetRawText())).ToArray());
        parameters["cacheEntries"] = new JsonArray(FilmJson.Array(original, "cacheEntries")
            .Where(entry => FilmJson.Text(entry, "kind") == "metadata" && target.Kind != "source" ||
                SameOwner(page.Request.DataOwner, connection) && admitted.Any(pair =>
                    SourceEquals(FilmJson.Member(entry, "source"), pair.Old) && SourceEquals(pair.Old, pair.Current)))
            .Select(entry => JsonNode.Parse(entry.GetRawText())).ToArray());
        return JsonSerializer.SerializeToElement(parameters, FilmJson.Options);
    }

    internal static bool SourceEquals(JsonElement left, JsonElement right) =>
        new[] { "engine", "installationId", "siteId", "generation", "revision" }
            .All(name => FilmJson.Text(left, name) == FilmJson.Text(right, name));

    private static JsonElement DetailIdentity(JsonElement value)
    {
        foreach (string name in new[] { "vod_id", "id", "detailId" })
            if (FilmJson.IsIdentity(FilmJson.Member(value, name))) return value.GetProperty(name);
        return default;
    }
}
