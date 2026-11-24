using System.Buffers;
using System.Text.Json;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

internal sealed record MusicHomeSnapshot(string Platform, MusicCatalogSection[][] Pages);

internal static class MusicHomeSnapshotCodec
{
    internal static async Task<MusicHomeSnapshot?> ReadAsync(
        MusicHomeSnapshotStore store,
        Func<string, MusicSongRecord?> song,
        CancellationToken token = default)
    {
        try
        {
            var record = await store.ReadRecordAsync(token).ConfigureAwait(false);
            if (record is null) return null;

            var songCache = new Dictionary<string, SongRow?>(StringComparer.Ordinal);
            SongRow? ResolveSong(string? songId)
            {
                if (songId is not { Length: > 0 }) return null;
                token.ThrowIfCancellationRequested();
                if (!songCache.TryGetValue(songId, out var resolved))
                {
                    var rec = song(songId);
                    resolved = rec is not null ? SongRow.FromRecord(rec) : null;
                    songCache[songId] = resolved;
                }
                return resolved;
            }

            var pages = new MusicCatalogSection[record.Pages.Count][];
            for (int i = 0; i < record.Pages.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                pages[i] = ProjectPage(record.Pages[i], ResolveSong);
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
            var parsed = ToRecord(snapshot, token);
            await store.SaveRecordAsync(parsed, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException) { }
    }

    private static MusicCatalogSection[] ProjectPage(HomePageRecord page, Func<string?, SongRow?> resolveSong)
    {
        var platform = page.Platform;
        var sections = new List<MusicCatalogSection>(page.Sections.Count);
        foreach (var sec in page.Sections)
        {
            if (sec.HiddenOnly)
            {
                sections.Add(new MusicCatalogSection("", []));
                continue;
            }
            var heading = sec.Title ?? "";
            var tabs = sec.Tabs.Select(tab => new MusicCatalogTab(
                tab.Id ?? "",
                tab.Name ?? "",
                ProjectCards(tab.Items, platform, resolveSong))).ToArray();
            MusicCatalogCard? sectionAction = null;
            if (sec.Action is { } act)
            {
                var actionEl = ActionToElement(act);
                sectionAction = new MusicCatalogCard(
                    "", heading, "", "", act.Type ?? "", platform,
                    ActionId: ActionId(act),
                    Action: actionEl,
                    Locator: act.Locator is { } loc ? LocatorToElement(loc) : null,
                    PageCode: act.Channel?.PageCode ?? "",
                    ChannelKind: act.Channel?.ChannelKind ?? "",
                    RequiresAccount: sec.RequiresAccount ?? false);
            }
            sections.Add(new MusicCatalogSection(
                heading,
                ProjectCards(sec.Items, platform, resolveSong),
                sec.Id ?? "",
                sec.Type ?? "",
                Action: sectionAction,
                Layout: sec.Layout ?? "",
                Tabs: tabs,
                SelectedTab: sec.SelectedTab ?? "",
                ErrorCode: sec.ErrorCode ?? ""));
        }
        return sections.ToArray();
    }

    private static MusicCatalogCard[] ProjectCards(
        IReadOnlyList<HomeCardRecord>? cards,
        string platform,
        Func<string?, SongRow?> resolveSong)
    {
        if (cards is not { Count: > 0 }) return [];
        var result = new MusicCatalogCard[cards.Count];
        for (int i = 0; i < cards.Count; i++)
            result[i] = ProjectCard(cards[i], platform, resolveSong);
        return result;
    }

    private static MusicCatalogCard ProjectCard(HomeCardRecord c, string platform, Func<string?, SongRow?> resolveSong)
    {
        if (c.HiddenOnly)
            return new MusicCatalogCard("", "", "", "", "", platform, Display: false, UsesSongDisplay: false);

        var action = c.Action;
        var locator = c.Locator ?? action?.Locator;
        MusicRankPreview[] previews = c.Previews is { Count: > 0 } list
            ? list.Take(3).Select(p => new MusicRankPreview(
                p.Title ?? "",
                p.Artist ?? "",
                p.Cover ?? "",
                p.Rank is { } r ? (int)r : 0,
                p.LastRank is { } lr ? (int)lr : null,
                resolveSong(p.SongId))).ToArray()
            : [];
        var presentation = c.Presentation switch
        {
            "preview" => MusicCatalogEntryPresentation.Preview,
            "overlay" => MusicCatalogEntryPresentation.Overlay,
            "animated" => MusicCatalogEntryPresentation.Animated,
            _ => MusicCatalogEntryPresentation.Static
        };
        int? dayOfMonth = c.DayOfMonth is >= 1 and <= 31 ? (int)c.DayOfMonth.Value : null;
        int? count = c.Count is >= int.MinValue and <= int.MaxValue ? (int)c.Count.Value : null;
        return new MusicCatalogCard(
            c.Id ?? "",
            c.Title ?? "",
            c.Subtitle ?? "",
            c.Cover ?? "",
            c.Kind ?? "",
            platform,
            resolveSong(c.RefSongId),
            action is not null ? ActionId(action) : "",
            action?.Channel?.PageCode ?? "",
            c.Available ?? true,
            c.ErrorCode ?? "",
            c.Key ?? "",
            action is not null ? ActionToElement(action) : null,
            locator is not null ? LocatorToElement(locator) : null,
            Preview: previews,
            UpdateFrequency: c.UpdateFrequency ?? "",
            Presentation: presentation,
            DayOfMonth: dayOfMonth,
            Decoration: c.Decoration ?? "",
            RequiresAccount: c.RequiresAccount ?? false,
            Display: c.Display ?? true,
            Placement: c.Placement ?? "",
            DirectPlay: c.CanPlay ?? true,
            PlaybackSource: c.PlaybackSource is { } pb ? ActionToElement(pb) : null,
            RankText: c.RankText ?? "",
            SalesText: c.SalesText ?? "",
            Count: count,
            IsManagedPlaylist: c.Relationship == "owned",
            Label: c.Label ?? "",
            ChannelKind: action?.Channel?.ChannelKind ?? "",
            CoverRef: c.CoverRefKind == "playlist" && c.CoverRefId is { Length: > 0 } refId
                ? new("playlist", refId) : null,
            UsesSongDisplay: false,
            ContextText: c.ContextText ?? "",
            PlayCount: c.PlayCount,
            CoverLabel: c.CoverLabel ?? "");
    }

    private static string ActionId(HomeActionRecord action) => (action.Type ?? "") switch
    {
        "daily" or "fm" or "similar" or "heartbeat" => action.Type ?? "",
        "channel" => action.Channel?.Id ?? "",
        _ => action.Id ?? ""
    };

    private static HomeSnapshotRecord ToRecord(MusicHomeSnapshot snapshot, CancellationToken token)
    {
        var pages = new List<HomePageRecord>(snapshot.Pages.Length);
        foreach (var page in snapshot.Pages)
        {
            token.ThrowIfCancellationRequested();
            var sections = new List<HomeSectionRecord>(page.Length);
            foreach (var sec in page)
            {
                var items = sec.Items.Select(ToCardRecord).ToList();
                var tabs = sec.Tabs is { Count: > 0 }
                    ? sec.Tabs.Select(tab => new HomeTabRecord(
                        NullIfEmpty(tab.Id),
                        NullIfEmpty(tab.Title),
                        tab.Items.Select(ToCardRecord).ToList())).ToList()
                    : new List<HomeTabRecord>();
                sections.Add(new HomeSectionRecord(
                    HiddenOnly: false,
                    Id: NullIfEmpty(sec.Id),
                    Title: NullIfEmpty(sec.Title),
                    Type: NullIfEmpty(sec.Type),
                    Layout: NullIfEmpty(sec.Layout),
                    Available: null,
                    ErrorCode: NullIfEmpty(sec.ErrorCode),
                    RequiresAccount: sec.Action?.RequiresAccount == true ? true : null,
                    Display: null,
                    SelectedTab: NullIfEmpty(sec.SelectedTab),
                    SelectedTag: null,
                    Action: ParseAction(sec.Action?.Action),
                    Items: items,
                    Tabs: tabs,
                    Tags: []));
            }
            pages.Add(new HomePageRecord(snapshot.Platform, sections));
        }
        return new HomeSnapshotRecord(snapshot.Platform, pages);
    }

    private static HomeCardRecord ToCardRecord(MusicCatalogCard card)
    {
        if (!card.Display)
            return new HomeCardRecord(true, null, null, null, null, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, false, null, null, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null);

        string? presentation = card.Presentation switch
        {
            MusicCatalogEntryPresentation.Preview => "preview",
            MusicCatalogEntryPresentation.Overlay => "overlay",
            MusicCatalogEntryPresentation.Animated => "animated",
            _ => null
        };
        List<HomePreviewRecord>? previews = card.Preview is { Count: > 0 } list
            ? list.Take(3).Select(p => new HomePreviewRecord(
                NullIfEmpty(p.Title),
                NullIfEmpty(p.Artist),
                NullIfEmpty(p.Cover),
                p.Rank,
                p.LastRank,
                NullIfEmpty(p.Song?.Id))).ToList()
            : null;
        return new HomeCardRecord(
            HiddenOnly: false,
            SongId: null,
            RefSongId: NullIfEmpty(card.Song?.Id),
            Id: NullIfEmpty(card.Id),
            Key: NullIfEmpty(card.StableKey),
            Name: null,
            Title: NullIfEmpty(card.Title),
            Label: NullIfEmpty(card.Label),
            Subtitle: NullIfEmpty(card.Subtitle),
            ContextText: NullIfEmpty(card.ContextText),
            Creator: null,
            Cover: NullIfEmpty(card.Cover),
            CoverLabel: NullIfEmpty(card.CoverLabel),
            Kind: NullIfEmpty(card.Kind),
            PageCode: NullIfEmpty(card.PageCode),
            ChannelKind: NullIfEmpty(card.ChannelKind),
            Available: card.Available ? null : false,
            ErrorCode: NullIfEmpty(card.ErrorCode),
            RequiresAccount: card.RequiresAccount ? true : null,
            Display: true,
            Placement: NullIfEmpty(card.Placement),
            CanPlay: card.DirectPlay ? null : false,
            RankText: NullIfEmpty(card.RankText),
            SalesText: NullIfEmpty(card.SalesText),
            Relationship: card.IsManagedPlaylist ? "owned" : null,
            Count: card.Count,
            PlayCount: card.PlayCount,
            Presentation: presentation,
            DayOfMonth: card.DayOfMonth,
            Decoration: NullIfEmpty(card.Decoration),
            UpdateFrequency: NullIfEmpty(card.UpdateFrequency),
            CoverRefKind: card.CoverRef?.Kind,
            CoverRefId: card.CoverRef?.Id,
            Locator: ParseLocator(card.Locator),
            Action: ParseAction(card.Action),
            PlaybackSource: ParseAction(card.PlaybackSource),
            Previews: previews);
    }

    private static string? NullIfEmpty(string? value) => value is { Length: > 0 } ? value : null;

    private static HomeLocatorRecord? ParseLocator(JsonElement? el)
    {
        if (el is not { ValueKind: JsonValueKind.Object } loc) return null;
        var rec = new HomeLocatorRecord(
            ReadStr(loc, "kind"), ReadScalarText(loc, "id"), ReadScalarText(loc, "playlistId"),
            ReadScalarText(loc, "dirId"), ReadScalarText(loc, "listId"), ReadScalarText(loc, "version"),
            ReadScalarText(loc, "totalVersion"), ReadScalarText(loc, "creatorId"), ReadScalarText(loc, "creatorListId"));
        return (rec.Kind ?? rec.Id ?? rec.PlaylistId ?? rec.DirId ?? rec.ListId ??
            rec.Version ?? rec.TotalVersion ?? rec.CreatorId ?? rec.CreatorListId) is null ? null : rec;
    }

    private static HomeActionRecord? ParseAction(JsonElement? el)
    {
        if (el is not { ValueKind: JsonValueKind.Object } a) return null;
        HomeChannelRecord? channel = null;
        if (a.TryGetProperty("channel", out var ch) && ch.ValueKind == JsonValueKind.Object)
        {
            var cRec = new HomeChannelRecord(
                ReadScalarText(ch, "id"), ReadStr(ch, "name"), ReadStr(ch, "pageCode"),
                ReadStr(ch, "channelKind"), ReadStr(ch, "placement"));
            if ((cRec.Id ?? cRec.Name ?? cRec.PageCode ?? cRec.ChannelKind ?? cRec.Placement) is not null)
                channel = cRec;
        }
        var locator = a.TryGetProperty("locator", out var locEl) && locEl.ValueKind == JsonValueKind.Object
            ? ParseLocator(locEl) : null;
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

    private static JsonElement ActionToElement(HomeActionRecord a)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
            WriteAction(w, a);
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    private static JsonElement LocatorToElement(HomeLocatorRecord loc)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
            WriteLocator(w, loc);
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
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
