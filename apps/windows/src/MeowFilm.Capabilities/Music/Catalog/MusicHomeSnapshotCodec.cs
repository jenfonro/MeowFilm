using System.Buffers;
using System.Text.Json;

namespace MeowFilm.Capabilities;

internal sealed record MusicHomeSnapshot(string Platform, JsonElement[] Pages);

internal static class MusicHomeSnapshotCodec
{
    internal static async Task<MusicHomeSnapshot?> ReadAsync(
        MusicHomeSnapshotStore store,
        Func<string, JsonElement?> song,
        CancellationToken token = default)
    {
        try
        {
            var record = await store.ReadRecordAsync(token).ConfigureAwait(false);
            if (record is null) return null;

            var songCache = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
            JsonElement? ResolveSong(string songId)
            {
                token.ThrowIfCancellationRequested();
                if (!songCache.TryGetValue(songId, out var resolved))
                {
                    resolved = song(songId);
                    songCache[songId] = resolved;
                }
                return resolved;
            }

            var pages = new JsonElement[record.Pages.Count];
            for (int i = 0; i < record.Pages.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                pages[i] = SerializePage(record.Pages[i], ResolveSong);
            }
            return new MusicHomeSnapshot(record.Platform, pages);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static async Task SaveAsync(
        MusicHomeSnapshotStore store,
        MusicHomeSnapshot snapshot,
        CancellationToken token = default)
    {
        if (snapshot.Platform is not ("wy" or "kg") || snapshot.Pages.Length == 0) return;
        try
        {
            var parsed = ParseSnapshot(snapshot, token);
            await store.SaveRecordAsync(parsed, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException) { }
    }

    internal static HomeSnapshotRecord ParseSnapshot(MusicHomeSnapshot snapshot, CancellationToken token)
    {
        var pages = new List<HomePageRecord>(snapshot.Pages.Length);
        foreach (var page in snapshot.Pages)
        {
            token.ThrowIfCancellationRequested();
            var sections = new List<HomeSectionRecord>();
            foreach (var sec in MusicCatalogDto.Rows(MusicCatalogDto.Field(page, "sections")))
            {
                if (sec.ValueKind != JsonValueKind.Object) continue;
                bool hiddenOnly = sec.TryGetProperty("display", out var disp) && disp.ValueKind == JsonValueKind.False;
                if (hiddenOnly)
                {
                    sections.Add(new HomeSectionRecord(true, null, null, null, null, null, null, null, false, null, null, null, null, [], []));
                    continue;
                }
                List<HomeCardRecord>? items = null;
                if (sec.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
                {
                    items = [];
                    foreach (var cardEl in itemsEl.EnumerateArray())
                        if (cardEl.ValueKind == JsonValueKind.Object)
                            items.Add(ParseCard(cardEl));
                }
                var tabs = new List<HomeTabRecord>();
                foreach (var tabEl in MusicCatalogDto.Rows(MusicCatalogDto.Field(sec, "tabs")))
                {
                    if (tabEl.ValueKind != JsonValueKind.Object) continue;
                    List<HomeCardRecord>? tabItems = null;
                    if (tabEl.TryGetProperty("items", out var tItemsEl) && tItemsEl.ValueKind == JsonValueKind.Array)
                    {
                        tabItems = [];
                        foreach (var cardEl in tItemsEl.EnumerateArray())
                            if (cardEl.ValueKind == JsonValueKind.Object)
                                tabItems.Add(ParseCard(cardEl));
                    }
                    tabs.Add(new HomeTabRecord(ReadScalarText(tabEl, "id"), ReadStr(tabEl, "name"), tabItems));
                }
                var tags = new List<HomeTagRecord>();
                foreach (var tagEl in MusicCatalogDto.Rows(MusicCatalogDto.Field(sec, "tags")))
                {
                    if (tagEl.ValueKind != JsonValueKind.Object) continue;
                    tags.Add(new HomeTagRecord(ReadScalarText(tagEl, "id"), ReadStr(tagEl, "name"), ReadStr(tagEl, "group"), ReadBool(tagEl, "selected")));
                }
                sections.Add(new HomeSectionRecord(
                    false,
                    ReadScalarText(sec, "id"), ReadStr(sec, "title"), ReadStr(sec, "type"), ReadStr(sec, "layout"),
                    ReadBool(sec, "available"), ReadStr(sec, "errorCode"), ReadBool(sec, "requiresAccount"),
                    ReadBool(sec, "display"), ReadStr(sec, "selectedTab"), ReadStr(sec, "selectedTag"),
                    ParseAction(ReadObj(sec, "action")), items, tabs, tags));
            }
            pages.Add(new HomePageRecord(snapshot.Platform, sections));
        }
        return new HomeSnapshotRecord(snapshot.Platform, pages);
    }

    private static HomeCardRecord ParseCard(JsonElement card)
    {
        if (card.TryGetProperty("display", out var disp) && disp.ValueKind == JsonValueKind.False)
            return new HomeCardRecord(true, null, null, null, null, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, false, null, null, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null);

        var refSong = ReadObj(card, "song");
        var coverRef = ReadObj(card, "coverRef");
        List<HomePreviewRecord>? previews = null;
        if (card.TryGetProperty("preview", out var previewEl) && previewEl.ValueKind == JsonValueKind.Array)
        {
            previews = [];
            foreach (var item in previewEl.EnumerateArray().Take(3))
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var pSong = ReadObj(item, "song");
                previews.Add(new HomePreviewRecord(
                    ReadStr(item, "title"), ReadStr(item, "artist"), ReadStr(item, "cover"),
                    ReadInt64(item, "rank"), ReadInt64(item, "lastRank"),
                    pSong is { } ps ? ReadStr(ps, "songId") : null));
            }
        }
        return new HomeCardRecord(
            false,
            ReadStr(card, "songId"),
            refSong is { } rs ? ReadStr(rs, "songId") : null,
            ReadScalarText(card, "id"), ReadScalarText(card, "key"),
            ReadStr(card, "name"), ReadStr(card, "title"), ReadStr(card, "label"), ReadStr(card, "subtitle"),
            ReadStr(card, "contextText"), ReadStr(card, "creator"), ReadStr(card, "cover"), ReadStr(card, "coverLabel"),
            ReadStr(card, "kind"), ReadStr(card, "pageCode"), ReadStr(card, "channelKind"),
            ReadBool(card, "available"), ReadStr(card, "errorCode"), ReadBool(card, "requiresAccount"),
            ReadBool(card, "display"), ReadStr(card, "placement"), ReadBool(card, "canPlay"),
            ReadStr(card, "rankText"), ReadStr(card, "salesText"), ReadStr(card, "relationship"),
            ReadInt64(card, "count"), ReadInt64(card, "playCount"), ReadStr(card, "presentation"),
            ReadInt64(card, "dayOfMonth"), ReadStr(card, "decoration"), ReadStr(card, "updateFrequency"),
            coverRef is { } cr ? ReadStr(cr, "kind") : null,
            coverRef is { } cr2 ? ReadScalarText(cr2, "id") : null,
            ParseLocator(ReadObj(card, "locator")),
            ParseAction(ReadObj(card, "action")),
            ParseAction(ReadObj(card, "playbackSource")),
            previews);
    }

    private static HomeLocatorRecord? ParseLocator(JsonElement? el)
    {
        if (el is not { } loc) return null;
        var rec = new HomeLocatorRecord(
            ReadStr(loc, "kind"), ReadScalarText(loc, "id"), ReadScalarText(loc, "playlistId"),
            ReadScalarText(loc, "dirId"), ReadScalarText(loc, "listId"), ReadScalarText(loc, "version"),
            ReadScalarText(loc, "totalVersion"), ReadScalarText(loc, "creatorId"), ReadScalarText(loc, "creatorListId"));
        return (rec.Kind ?? rec.Id ?? rec.PlaylistId ?? rec.DirId ?? rec.ListId ??
            rec.Version ?? rec.TotalVersion ?? rec.CreatorId ?? rec.CreatorListId) is null ? null : rec;
    }

    private static HomeActionRecord? ParseAction(JsonElement? el)
    {
        if (el is not { } a) return null;
        var chEl = ReadObj(a, "channel");
        HomeChannelRecord? channel = null;
        if (chEl is { } ch)
        {
            var cRec = new HomeChannelRecord(
                ReadScalarText(ch, "id"), ReadStr(ch, "name"), ReadStr(ch, "pageCode"),
                ReadStr(ch, "channelKind"), ReadStr(ch, "placement"));
            if ((cRec.Id ?? cRec.Name ?? cRec.PageCode ?? cRec.ChannelKind ?? cRec.Placement) is not null)
                channel = cRec;
        }
        var locator = ParseLocator(ReadObj(a, "locator"));
        var sourceIds = ReadStringList(a, "sourceIds");
        var ids = ReadStringList(a, "ids");
        var rec = new HomeActionRecord(
            ReadStr(a, "type"), ReadScalarText(a, "id"), ReadStr(a, "branch"), ReadStr(a, "pageCode"),
            ReadStr(a, "provider"), ReadStr(a, "mode"), ReadStr(a, "submode"), ReadStr(a, "ruleId"),
            ReadStr(a, "tabTagId"), ReadStr(a, "sourceType"), ReadStr(a, "sourceName"),
            ReadStr(a, "entry"), ReadStr(a, "songId"), channel, locator, sourceIds, ids);
        if ((rec.Type ?? rec.Id ?? rec.Branch ?? rec.PageCode ?? rec.Provider ?? rec.Mode ?? rec.Submode ??
            rec.RuleId ?? rec.TabTagId ?? rec.SourceType ?? rec.SourceName ?? rec.Entry ?? rec.SongId) is null &&
            rec.Channel is null && rec.Locator is null && rec.SourceIds is null && rec.Ids is null)
            return null;
        return rec;
    }

    internal static JsonElement SerializePage(HomePageRecord page, Func<string, JsonElement?> resolveSong)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("platform", page.Platform);
            w.WriteStartArray("sections");
            foreach (var sec in page.Sections) WriteSection(w, sec, resolveSong);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    private static void WriteSection(Utf8JsonWriter w, HomeSectionRecord sec, Func<string, JsonElement?> resolveSong)
    {
        w.WriteStartObject();
        if (sec.HiddenOnly)
        {
            w.WriteBoolean("display", false);
            w.WriteEndObject();
            return;
        }
        WriteStr(w, "id", sec.Id);
        WriteStr(w, "title", sec.Title);
        WriteStr(w, "type", sec.Type);
        WriteStr(w, "layout", sec.Layout);
        WriteBool(w, "available", sec.Available);
        WriteStr(w, "errorCode", sec.ErrorCode);
        WriteBool(w, "requiresAccount", sec.RequiresAccount);
        WriteBool(w, "display", sec.Display);
        WriteStr(w, "selectedTab", sec.SelectedTab);
        WriteStr(w, "selectedTag", sec.SelectedTag);
        if (sec.Action is { } act) { w.WritePropertyName("action"); WriteAction(w, act); }
        if (sec.Items is { } items)
        {
            w.WriteStartArray("items");
            foreach (var card in items) WriteCard(w, card, resolveSong);
            w.WriteEndArray();
        }
        w.WriteStartArray("tabs");
        foreach (var tab in sec.Tabs)
        {
            w.WriteStartObject();
            WriteStr(w, "id", tab.Id);
            WriteStr(w, "name", tab.Name);
            if (tab.Items is { } tItems)
            {
                w.WriteStartArray("items");
                foreach (var card in tItems) WriteCard(w, card, resolveSong);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("tags");
        foreach (var tag in sec.Tags)
        {
            w.WriteStartObject();
            WriteStr(w, "id", tag.Id);
            WriteStr(w, "name", tag.Name);
            WriteStr(w, "group", tag.Group);
            WriteBool(w, "selected", tag.Selected);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteCard(Utf8JsonWriter w, HomeCardRecord c, Func<string, JsonElement?> resolveSong)
    {
        w.WriteStartObject();
        if (c.HiddenOnly)
        {
            w.WriteBoolean("display", false);
            w.WriteEndObject();
            return;
        }
        WriteStr(w, "songId", c.SongId);
        if (c.RefSongId is { } rsid)
        {
            w.WritePropertyName("song");
            if (resolveSong(rsid) is { } resolved) resolved.WriteTo(w);
            else w.WriteNullValue();
        }
        WriteStr(w, "id", c.Id);
        WriteStr(w, "key", c.Key);
        WriteStr(w, "name", c.Name);
        WriteStr(w, "title", c.Title);
        WriteStr(w, "label", c.Label);
        WriteStr(w, "subtitle", c.Subtitle);
        WriteStr(w, "contextText", c.ContextText);
        WriteStr(w, "creator", c.Creator);
        WriteStr(w, "cover", c.Cover);
        WriteStr(w, "coverLabel", c.CoverLabel);
        WriteStr(w, "kind", c.Kind);
        WriteStr(w, "pageCode", c.PageCode);
        WriteStr(w, "channelKind", c.ChannelKind);
        WriteBool(w, "available", c.Available);
        WriteStr(w, "errorCode", c.ErrorCode);
        WriteBool(w, "requiresAccount", c.RequiresAccount);
        WriteBool(w, "display", c.Display);
        WriteStr(w, "placement", c.Placement);
        WriteBool(w, "canPlay", c.CanPlay);
        WriteStr(w, "rankText", c.RankText);
        WriteStr(w, "salesText", c.SalesText);
        WriteStr(w, "relationship", c.Relationship);
        WriteInt64(w, "count", c.Count);
        WriteInt64(w, "playCount", c.PlayCount);
        WriteStr(w, "presentation", c.Presentation);
        WriteInt64(w, "dayOfMonth", c.DayOfMonth);
        WriteStr(w, "decoration", c.Decoration);
        WriteStr(w, "updateFrequency", c.UpdateFrequency);
        if (c.CoverRefKind is not null || c.CoverRefId is not null)
        {
            w.WriteStartObject("coverRef");
            WriteStr(w, "kind", c.CoverRefKind);
            WriteStr(w, "id", c.CoverRefId);
            w.WriteEndObject();
        }
        if (c.Locator is { } loc) { w.WritePropertyName("locator"); WriteLocator(w, loc); }
        if (c.Action is { } act) { w.WritePropertyName("action"); WriteAction(w, act); }
        if (c.PlaybackSource is { } pb) { w.WritePropertyName("playbackSource"); WriteAction(w, pb); }
        if (c.Previews is { Count: > 0 } previews)
        {
            w.WriteStartArray("preview");
            foreach (var p in previews)
            {
                w.WriteStartObject();
                WriteStr(w, "title", p.Title);
                WriteStr(w, "artist", p.Artist);
                WriteStr(w, "cover", p.Cover);
                WriteInt64(w, "rank", p.Rank);
                WriteInt64(w, "lastRank", p.LastRank);
                if (p.SongId is { } psid)
                {
                    w.WritePropertyName("song");
                    if (resolveSong(psid) is { } resolved) resolved.WriteTo(w);
                    else w.WriteNullValue();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void WriteAction(Utf8JsonWriter w, HomeActionRecord a)
    {
        w.WriteStartObject();
        WriteStr(w, "type", a.Type);
        WriteStr(w, "id", a.Id);
        WriteStr(w, "branch", a.Branch);
        WriteStr(w, "pageCode", a.PageCode);
        WriteStr(w, "provider", a.Provider);
        WriteStr(w, "mode", a.Mode);
        WriteStr(w, "submode", a.Submode);
        WriteStr(w, "ruleId", a.RuleId);
        WriteStr(w, "tabTagId", a.TabTagId);
        WriteStr(w, "sourceType", a.SourceType);
        WriteStr(w, "sourceName", a.SourceName);
        WriteStr(w, "entry", a.Entry);
        WriteStr(w, "songId", a.SongId);
        if (a.Channel is { } ch)
        {
            w.WriteStartObject("channel");
            WriteStr(w, "id", ch.Id);
            WriteStr(w, "name", ch.Name);
            WriteStr(w, "pageCode", ch.PageCode);
            WriteStr(w, "channelKind", ch.ChannelKind);
            WriteStr(w, "placement", ch.Placement);
            w.WriteEndObject();
        }
        if (a.Locator is { } loc) { w.WritePropertyName("locator"); WriteLocator(w, loc); }
        if (a.SourceIds is { } sids)
        {
            w.WriteStartArray("sourceIds");
            foreach (var v in sids) w.WriteStringValue(v);
            w.WriteEndArray();
        }
        if (a.Ids is { } ids)
        {
            w.WriteStartArray("ids");
            foreach (var v in ids) w.WriteStringValue(v);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void WriteLocator(Utf8JsonWriter w, HomeLocatorRecord loc)
    {
        w.WriteStartObject();
        WriteStr(w, "kind", loc.Kind);
        WriteStr(w, "id", loc.Id);
        WriteStr(w, "playlistId", loc.PlaylistId);
        WriteStr(w, "dirId", loc.DirId);
        WriteStr(w, "listId", loc.ListId);
        WriteStr(w, "version", loc.Version);
        WriteStr(w, "totalVersion", loc.TotalVersion);
        WriteStr(w, "creatorId", loc.CreatorId);
        WriteStr(w, "creatorListId", loc.CreatorListId);
        w.WriteEndObject();
    }

    private static void WriteStr(Utf8JsonWriter w, string name, string? val) { if (val is not null) w.WriteString(name, val); }
    private static void WriteBool(Utf8JsonWriter w, string name, bool? val) { if (val is { } v) w.WriteBoolean(name, v); }
    private static void WriteInt64(Utf8JsonWriter w, string name, long? val) { if (val is { } v) w.WriteNumber(name, v); }

    private static JsonElement? ReadObj(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Object ? prop : null;

    private static string? ReadStr(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

    private static string? ReadScalarText(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            _ => null
        };
    }

    private static bool? ReadBool(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean()
            : null;

    private static long? ReadInt64(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var n)
            ? n
            : null;

    private static IReadOnlyList<string>? ReadStringList(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                list.Add(s);
        return list.Count > 0 ? list : null;
    }
}
