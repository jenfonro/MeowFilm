using System.IO;
using System.Text;
using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

// Port of dashboardLogic.js's home/category/detail/play/search probe. RPC replaces
// the Runner HTTP transport; the host owns action discovery and HTTP method routing.
internal sealed class FilmSourceProbe(CatScriptTransport transport, FilmCatSource cat, FilmVideoSource site)
{
    private readonly List<FilmSourceProbeStep> steps = [];
    private CancellationToken cancellation;

    internal async Task<FilmSourceDetection> RunAsync(CancellationToken token)
    {
        cancellation = token;
        var home = await CallAsync("home", new { }).ConfigureAwait(false);
        JsonElement? category = null;
        if (home is { } homeValue)
        {
            string id = Classes(homeValue).Select(item => Pick(item, "type_id", "tid", "id", "typeId"))
                .FirstOrDefault(value => value.Length > 0) ?? "0";
            category = await CallAsync("category", new { id, page = 1, filter = true, filters = new { } })
                .ConfigureAwait(false);
        }
        var categoryItems = category is { } categoryValue ? List(categoryValue).Take(10).ToArray() : [];
        if (category is not null && categoryItems.Length == 0) Empty("category", "EMPTY_LIST");
        var categoryPlay = categoryItems.Length > 0
            ? await TryPlayAsync(categoryItems).ConfigureAwait(false) : new PlayAttempt(false, false);
        bool searchForPlay = category is null || categoryItems.Length == 0 || (!categoryPlay.Ok && categoryPlay.MissingMetadata);
        var search = searchForPlay || categoryPlay.Ok
            ? await CallAsync("search", new { wd = "斗破", page = 1 }).ConfigureAwait(false) : null;
        var searchItems = search is { } searchValue ? List(searchValue).ToArray() : [];
        if (search is not null && searchItems.Length == 0) Empty("search", "EMPTY_LIST");
        var searchPlay = searchForPlay && searchItems.Length > 0
            ? await TryPlayAsync(searchItems).ConfigureAwait(false) : new PlayAttempt(false, false);
        string result = categoryPlay.Ok ? search is not null ? "valid" : "search_error" :
            searchPlay.Ok ? categoryPlay.MissingMetadata ? "valid" : "category_error" : "invalid";
        token.ThrowIfCancellationRequested();
        return new(result, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), categoryPlay.Ok || searchPlay.Ok,
            Array.AsReadOnly(steps.ToArray()));
    }

    private async Task<PlayAttempt> TryPlayAsync(IEnumerable<JsonElement> items)
    {
        bool missing = false;
        // The reference probe samples three videos, and the first episode of every declared line.
        foreach (var video in items.Where(item => item.ValueKind == JsonValueKind.Object).Take(3))
        {
            cancellation.ThrowIfCancellationRequested();
            var candidates = PlayCandidates(video).ToList();
            if (candidates.Count == 0)
            {
                string id = Pick(video, "vod_id", "vodId", "id", "ID");
                if (id.Length == 0) { Empty("detail", "MISSING_VIDEO_ID"); continue; }
                var detail = await CallAsync("detail", new { id, vod_id = id }).ConfigureAwait(false);
                if (detail is null) continue;
                candidates = await DetailCandidatesAsync(detail.Value).ConfigureAwait(false);
                if (candidates.Count == 0)
                {
                    missing = true;
                    Empty("detail", "MISSING_PLAY_METADATA");
                    continue;
                }
            }
            foreach (var candidate in candidates)
            {
                var reply = await CallAsync("play", new
                {
                    flag = candidate.Flag, id = candidate.Id, siteApi = site.Api
                }).ConfigureAwait(false);
                if (reply is null) continue;
                string url = PlayUrl(reply.Value);
                if (url.StartsWith("meow.pan.v1.", StringComparison.Ordinal))
                {
                    // A host-owned pan resource is a resolver input, not a resolved media address.
                    steps.Add(new("play", "unsupported", "PAN_RESOLVER_UNAVAILABLE"));
                    continue;
                }
                if (url.Length > 0) return new(true, missing);
                Empty("play", "MISSING_PLAY_URL");
            }
        }
        return new(false, missing);
    }

    private async Task<List<PlayCandidate>> DetailCandidatesAsync(JsonElement detail)
    {
        var firstList = List(detail).ToArray();
        if (!firstList.Any(item => Navigation(item) is not null || Property(item, "vod_navigation").ValueKind == JsonValueKind.Object))
            return firstList.Length > 0 ? PlayCandidates(firstList[0]).ToList() : [];

        var candidates = new List<PlayCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<NavigationPage>();
        queue.Enqueue(new(detail, null, null, 0, new(StringComparer.Ordinal), new(StringComparer.Ordinal)));
        while (queue.TryDequeue(out var page))
        {
            cancellation.ThrowIfCancellationRequested();
            var list = Property(page.Document, "list");
            if (list.ValueKind != JsonValueKind.Array)
            {
                steps.Add(new("detail", "failed", "INVALID_NAVIGATION"));
                continue;
            }
            long current = PageNumber(page.Document, "page"), count = PageNumber(page.Document, "pagecount");
            if (page.Action is not null && (page.PreviousPage > 0 || (current > 0 && count > current)))
            {
                if (page.PreviousPage > 0 && current <= page.PreviousPage)
                {
                    steps.Add(new(page.Action, "failed", "NAVIGATION_PAGE_NOT_ADVANCING"));
                    continue;
                }
                if (list.GetArrayLength() > 0 && !page.PageLists.Add(Canonical(list)))
                {
                    steps.Add(new(page.Action, "failed", "NAVIGATION_REPEATED_PAGE"));
                    continue;
                }
            }
            foreach (var item in list.EnumerateArray())
            {
                var nav = Navigation(item);
                if (nav is null)
                {
                    candidates.AddRange(PlayCandidates(item));
                    continue;
                }
                string key = nav.Action + ":" + Canonical(nav.Payload);
                if (page.Ancestors.Contains(key))
                {
                    steps.Add(new(nav.Action, "failed", "NAVIGATION_CYCLE"));
                    continue;
                }
                if (!seen.Add(key)) continue;
                var child = await CallAsync(nav.Action, nav.Payload).ConfigureAwait(false);
                if (child is not null)
                {
                    var ancestors = new HashSet<string>(page.Ancestors, StringComparer.Ordinal) { key };
                    queue.Enqueue(new(child.Value, nav.Action, nav.Payload, 0, ancestors, new(StringComparer.Ordinal)));
                }
            }
            if (page.Action is not null && page.Payload is { } payload && list.GetArrayLength() > 0 &&
                current > 0 && count > current)
            {
                var next = payload.EnumerateObject().ToDictionary(pair => pair.Name, pair => (object)pair.Value.Clone(),
                    StringComparer.Ordinal);
                if (next.ContainsKey("pg")) next["pg"] = current + 1;
                if (next.ContainsKey("page") || !next.ContainsKey("pg")) next["page"] = current + 1;
                var body = JsonSerializer.SerializeToElement(next);
                string key = page.Action + ":" + Canonical(body);
                if (!seen.Add(key))
                {
                    steps.Add(new(page.Action, "failed", "NAVIGATION_CYCLE"));
                    continue;
                }
                var following = await CallAsync(page.Action, body).ConfigureAwait(false);
                if (following is not null)
                    queue.Enqueue(page with { Document = following.Value, Payload = body, PreviousPage = current });
            }
        }
        return candidates.Distinct().ToList();
    }

    private async Task<JsonElement?> CallAsync(string operation, object payload)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!site.Actions.Contains(operation, StringComparer.Ordinal))
        {
            steps.Add(new(operation, "unsupported", "CAPABILITY_UNAVAILABLE"));
            return null;
        }
        try
        {
            var value = await transport.InvokeAsync(cat, site, operation, payload, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (value.ValueKind == JsonValueKind.String) value = CatScriptTransport.Parse(value.GetString()!);
            if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                steps.Add(new(operation, "failed", "INVALID_RESPONSE"));
                return null;
            }
            foreach (var response in new[] { value, Property(value, "data") })
            {
                var status = Property(response, "statusCode");
                bool httpFailure = status.ValueKind == JsonValueKind.Number && status.TryGetDouble(out double code) && code >= 400;
                if (httpFailure || Property(response, "ok").ValueKind == JsonValueKind.False ||
                    Property(response, "success").ValueKind == JsonValueKind.False || Text(response, "error").Length > 0)
                {
                    string message = Pick(response, "message", "msg", "error");
                    steps.Add(new(operation, "failed", message.Length > 0 ? message :
                        httpFailure ? "HTTP_" + status.GetRawText() : "SCRIPT_ERROR"));
                    return null;
                }
            }
            steps.Add(new(operation, "success"));
            return value;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            cancellation.ThrowIfCancellationRequested();
            string code = error is FilmSourceException source ? source.Code :
                error is BridgeException bridge ? bridge.Code : "CALL_FAILED";
            if (code is "CANCELLED" or "STALE_SESSION" or "HOST_CLOSED" or "CLOSED" or "DISABLED" or "STARTING")
                throw new OperationCanceledException(code, error, cancellation);
            steps.Add(new(operation, code == "CAPABILITY_UNAVAILABLE" ? "unsupported" : "failed", code));
            return null;
        }
    }

    private void Empty(string operation, string reason) => steps.Add(new(operation, "empty", reason));

    private static IEnumerable<JsonElement> List(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray();
        foreach (var list in new[] { Property(value, "list"), Property(value, "data"),
            Property(Property(value, "data"), "list"), Property(Property(value, "data"), "vod_list"),
            Property(Property(value, "data"), "items") })
            if (list.ValueKind == JsonValueKind.Array) return list.EnumerateArray();
        return [];
    }

    private static IEnumerable<JsonElement> Classes(JsonElement value)
    {
        foreach (var list in new[] { Property(value, "class"), Property(value, "classes"),
            Property(Property(value, "data"), "class"), Property(Property(value, "data"), "classes"),
            Property(Property(value, "data"), "types"), Property(value, "types") })
            if (list.ValueKind == JsonValueKind.Array) return list.EnumerateArray();
        return [];
    }

    private static IEnumerable<PlayCandidate> PlayCandidates(JsonElement value)
    {
        string from = Pick(value, "vod_play_from", "play_from", "playFrom");
        string urls = Pick(value, "vod_play_url", "play_url", "playUrl");
        string[] flags = from.Length == 0 ? [] : from.Split("$$$", StringSplitOptions.None);
        string[] blocks = urls.Length == 0 ? [] : urls.Split("$$$", StringSplitOptions.None);
        var seen = new HashSet<PlayCandidate>();
        for (int i = 0; i < Math.Max(flags.Length, blocks.Length); i++)
        {
            string flag = i < flags.Length && flags[i].Trim().Length > 0 ? flags[i].Trim() : $"源{i + 1}";
            string block = i < blocks.Length ? blocks[i].Trim() : "";
            if (block.Length == 0) continue;
            bool split = flag.Contains("|||", StringComparison.Ordinal);
            string[] subFlags = split ? flag.Split("|||", StringSplitOptions.None) : [flag];
            string[] subBlocks = split && block.Contains("|||", StringComparison.Ordinal)
                ? block.Split("|||", StringSplitOptions.None) : [block];
            for (int j = 0; j < Math.Max(subFlags.Length, subBlocks.Length); j++)
            {
                string label = j < subFlags.Length && subFlags[j].Trim().Length > 0 ? subFlags[j].Trim() : flag;
                string line = j < subBlocks.Length ? subBlocks[j].Split('#')[0].Trim() : "";
                int separator = line.IndexOf('$');
                string id = (separator < 0 ? line : line[(separator + 1)..]).Trim();
                var candidate = new PlayCandidate(label, id);
                if (id.Length > 0 && seen.Add(candidate)) yield return candidate;
            }
        }
    }

    private static string PlayUrl(JsonElement value)
    {
        foreach (var item in new[] { Property(value, "url"), Property(value, "playUrl"), Property(Property(value, "data"), "url") })
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString()!.Trim() is { Length: > 0 } text) return text;
            if (item.ValueKind != JsonValueKind.Array) continue;
            var strings = item.EnumerateArray().Where(part => part.ValueKind == JsonValueKind.String)
                .Select(part => part.GetString()!.Trim()).Where(part => part.Length > 0).ToArray();
            string? url = strings.LastOrDefault(part => part.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? strings.FirstOrDefault();
            if (url is not null) return url;
        }
        return "";
    }

    private static NavigationAction? Navigation(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || Pick(item, "vod_id").Length == 0 ||
            Text(item, "vod_play_from").Length > 0 || Text(item, "vod_play_url").Length > 0) return null;
        var nav = Property(item, "vod_navigation");
        string action = Text(nav, "action");
        if (action.Length == 0 && Text(item, "vod_tag") == "folder") action = "category";
        if (action.Length == 0 || !char.IsAsciiLetter(action[0]) ||
            !action.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return null;
        var payload = Property(nav, "payload");
        if (payload.ValueKind != JsonValueKind.Object)
            payload = action == "category"
                ? JsonSerializer.SerializeToElement(new { id = Property(item, "vod_id"), page = 1 })
                : JsonSerializer.SerializeToElement(new { id = Property(item, "vod_id") });
        return new(action, payload);
    }

    private static JsonElement Property(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) ? property : default;
    private static string Text(JsonElement value, string key) =>
        Property(value, key) is { ValueKind: JsonValueKind.String } text ? text.GetString()!.Trim() : "";
    private static string Pick(JsonElement value, params string[] keys)
    {
        foreach (string key in keys)
        {
            var item = Property(value, key);
            string text = item.ValueKind == JsonValueKind.String ? item.GetString()!.Trim() :
                item.ValueKind == JsonValueKind.Number ? item.GetRawText() : "";
            if (text.Length > 0) return text;
        }
        return "";
    }
    private static long PageNumber(JsonElement value, string key) =>
        long.TryParse(Pick(value, key), out long number) && number > 0 ? number : 0;

    private static string Canonical(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(value, writer);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        static void Write(JsonElement element, Utf8JsonWriter writer)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var item in element.EnumerateObject().OrderBy(pair => pair.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(item.Name);
                    Write(item.Value, writer);
                }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(item, writer);
                writer.WriteEndArray();
            }
            else element.WriteTo(writer);
        }
    }

    private sealed record PlayCandidate(string Flag, string Id);
    private sealed record PlayAttempt(bool Ok, bool MissingMetadata);
    private sealed record NavigationAction(string Action, JsonElement Payload);
    private sealed record NavigationPage(JsonElement Document, string? Action, JsonElement? Payload,
        long PreviousPage, HashSet<string> Ancestors, HashSet<string> PageLists);
}
