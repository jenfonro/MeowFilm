using System.Buffers;
using System.Globalization;
using System.Text.Json;
using MeowFilm.Platform.Storage;
using Microsoft.Data.Sqlite;

namespace MeowFilm.Capabilities;

internal sealed record MusicHomeSnapshot(string Platform, JsonElement[] Pages);

internal sealed record HomeLocatorRecord(
    string? Kind,
    string? Id,
    string? PlaylistId,
    string? DirId,
    string? ListId,
    string? Version,
    string? TotalVersion,
    string? CreatorId,
    string? CreatorListId);

internal sealed record HomeChannelRecord(
    string? Id,
    string? Name,
    string? PageCode,
    string? ChannelKind,
    string? Placement);

internal sealed record HomeActionRecord(
    string? Type,
    string? Id,
    string? Branch,
    string? PageCode,
    string? Provider,
    string? Mode,
    string? Submode,
    string? RuleId,
    string? TabTagId,
    string? SourceType,
    string? SourceName,
    string? Entry,
    string? SongId,
    HomeChannelRecord? Channel,
    HomeLocatorRecord? Locator,
    IReadOnlyList<string>? SourceIds,
    IReadOnlyList<string>? Ids);

internal sealed record HomePreviewRecord(
    string? Title,
    string? Artist,
    string? Cover,
    long? Rank,
    long? LastRank,
    string? SongId);

internal sealed record HomeCardRecord(
    bool HiddenOnly,
    string? SongId,
    string? RefSongId,
    string? Id,
    string? Key,
    string? Name,
    string? Title,
    string? Label,
    string? Subtitle,
    string? ContextText,
    string? Creator,
    string? Cover,
    string? CoverLabel,
    string? Kind,
    string? PageCode,
    string? ChannelKind,
    bool? Available,
    string? ErrorCode,
    bool? RequiresAccount,
    bool? Display,
    string? Placement,
    bool? CanPlay,
    string? RankText,
    string? SalesText,
    string? Relationship,
    long? Count,
    long? PlayCount,
    string? Presentation,
    long? DayOfMonth,
    string? Decoration,
    string? UpdateFrequency,
    string? CoverRefKind,
    string? CoverRefId,
    HomeLocatorRecord? Locator,
    HomeActionRecord? Action,
    HomeActionRecord? PlaybackSource,
    IReadOnlyList<HomePreviewRecord>? Previews);

internal sealed record HomeTabRecord(string? Id, string? Name, IReadOnlyList<HomeCardRecord>? Items);
internal sealed record HomeTagRecord(string? Id, string? Name, string? Group, bool? Selected);

internal sealed record HomeSectionRecord(
    bool HiddenOnly,
    string? Id,
    string? Title,
    string? Type,
    string? Layout,
    bool? Available,
    string? ErrorCode,
    bool? RequiresAccount,
    bool? Display,
    string? SelectedTab,
    string? SelectedTag,
    HomeActionRecord? Action,
    IReadOnlyList<HomeCardRecord>? Items,
    IReadOnlyList<HomeTabRecord> Tabs,
    IReadOnlyList<HomeTagRecord> Tags);

internal sealed record HomePageRecord(string Platform, IReadOnlyList<HomeSectionRecord> Sections);
internal sealed record HomeSnapshotRecord(string Platform, IReadOnlyList<HomePageRecord> Pages);

// Display content only. Restored actions still require current account authority.
internal sealed class MusicHomeSnapshotStore(IStorageDatabase database)
{
    internal const string Namespace = "music.home.v3";
    private readonly SemaphoreSlim gate = new(1, 1);

    internal async Task<MusicHomeSnapshot?> ReadAsync(Func<string, JsonElement?> song, CancellationToken token = default)
    {
        try
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            var record = await database.ReadSnapshotAsync(db => ReadCore(db, token), token).ConfigureAwait(false);
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
        catch (Exception error) when (IsCacheFailure(error)) { return null; }
    }

    internal async Task SaveAsync(MusicHomeSnapshot snapshot, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Require(snapshot.Platform is "wy" or "kg" && snapshot.Pages.Length > 0);
            token.ThrowIfCancellationRequested();
            var parsed = ParseSnapshot(snapshot, token);
            await database.WriteAsync(db =>
            {
                WriteCore(db, parsed, token);
                db.CommitRevision(Namespace, db.Revision(Namespace), Guid.NewGuid().ToString("D"));
                return true;
            }, token, 4096).ConfigureAwait(false);
        }
        catch (Exception error) when (IsCacheFailure(error)) { }
        finally { gate.Release(); }
    }

    private static HomeSnapshotRecord? ReadCore(DatabaseSession db, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!db.TryQuerySingle("SELECT platform FROM windows_music_home_state WHERE singleton=1",
                row => row.GetString(0), out string platform))
            return null;
        Require(platform is "wy" or "kg");

        var pageRows = db.Query("SELECT page_index,platform FROM windows_music_home_pages ORDER BY page_index",
            row => (Index: row.GetInt32(0), Platform: row.GetString(1)));
        if (pageRows.Count == 0) return null;

        var locators = db.Query("""
            SELECT locator_uid,kind,locator_id,playlist_id,dir_id,list_id,version,total_version,creator_id,creator_list_id
            FROM windows_music_home_locators
            """, r => (Uid: r.GetString(0), Value: new HomeLocatorRecord(
                r.GetNullableString(1), r.GetNullableString(2), r.GetNullableString(3), r.GetNullableString(4),
                r.GetNullableString(5), r.GetNullableString(6), r.GetNullableString(7), r.GetNullableString(8),
                r.GetNullableString(9))))
            .ToDictionary(x => x.Uid, x => x.Value, StringComparer.Ordinal);

        var actionItems = new Dictionary<string, (List<string>? SourceIds, List<string>? Ids)>(StringComparer.Ordinal);
        foreach (var item in db.Query("""
            SELECT action_uid,list_kind,item_value
            FROM windows_music_home_action_items
            ORDER BY action_uid, list_kind, position
            """, r => (Uid: r.GetString(0), Kind: r.GetString(1), Val: r.GetString(2))))
        {
            actionItems.TryGetValue(item.Uid, out var pair);
            if (item.Kind == "sourceIds")
            {
                pair.SourceIds ??= [];
                pair.SourceIds.Add(item.Val);
            }
            else
            {
                pair.Ids ??= [];
                pair.Ids.Add(item.Val);
            }
            actionItems[item.Uid] = pair;
        }

        var actions = db.Query("""
            SELECT action_uid,type,action_id,branch,page_code,provider,mode,submode,rule_id,tab_tag_id,
                   source_type,source_name,entry,song_id,
                   channel_id,channel_name,channel_page_code,channel_kind,channel_placement,locator_uid
            FROM windows_music_home_actions
            """, r =>
            {
                string uid = r.GetString(0);
                string? chId = r.GetNullableString(14);
                string? chName = r.GetNullableString(15);
                string? chPage = r.GetNullableString(16);
                string? chKind = r.GetNullableString(17);
                string? chPlace = r.GetNullableString(18);
                HomeChannelRecord? channel = (chId ?? chName ?? chPage ?? chKind ?? chPlace) is not null
                    ? new HomeChannelRecord(chId, chName, chPage, chKind, chPlace)
                    : null;
                HomeLocatorRecord? loc = r.GetNullableString(19) is { } luid && locators.TryGetValue(luid, out var lv) ? lv : null;
                actionItems.TryGetValue(uid, out var lists);
                return (Uid: uid, Action: new HomeActionRecord(
                    r.GetNullableString(1), r.GetNullableString(2), r.GetNullableString(3), r.GetNullableString(4),
                    r.GetNullableString(5), r.GetNullableString(6), r.GetNullableString(7), r.GetNullableString(8),
                    r.GetNullableString(9), r.GetNullableString(10), r.GetNullableString(11), r.GetNullableString(12),
                    r.GetNullableString(13), channel, loc, lists.SourceIds, lists.Ids));
            }).ToDictionary(x => x.Uid, x => x.Action, StringComparer.Ordinal);

        var previewsByCard = new Dictionary<string, List<HomePreviewRecord>>(StringComparer.Ordinal);
        foreach (var p in db.Query("""
            SELECT card_uid,title,artist,cover,rank,last_rank,song_id
            FROM windows_music_home_card_previews
            ORDER BY card_uid, position
            """, r => (CardUid: r.GetString(0), Preview: new HomePreviewRecord(
                r.GetNullableString(1), r.GetNullableString(2), r.GetNullableString(3),
                r.GetNullableInt64(4), r.GetNullableInt64(5), r.GetNullableString(6)))))
        {
            if (!previewsByCard.TryGetValue(p.CardUid, out var list))
                previewsByCard[p.CardUid] = list = [];
            list.Add(p.Preview);
        }

        var directCardsBySection = new Dictionary<string, List<HomeCardRecord>>(StringComparer.Ordinal);
        var cardsByTab = new Dictionary<string, List<HomeCardRecord>>(StringComparer.Ordinal);
        foreach (var entry in db.Query("""
            SELECT card_uid,section_uid,tab_uid,
                   song_id,ref_song_id,card_id,card_key,name,title,label,subtitle,
                   context_text,creator,cover,cover_label,kind,page_code,channel_kind,available,error_code,
                   requires_account,display,placement,can_play,rank_text,sales_text,relationship,item_count,
                   play_count,presentation,day_of_month,decoration,update_frequency,
                   cover_ref_kind,cover_ref_id,locator_uid,action_uid,playback_action_uid
            FROM windows_music_home_cards
            ORDER BY section_uid, tab_uid, card_index
            """, r =>
            {
                token.ThrowIfCancellationRequested();
                string cardUid = r.GetString(0);
                string sectionUid = r.GetString(1);
                string? tabUid = r.GetNullableString(2);
                bool? display = r.GetNullableBool(21);
                bool hiddenOnly = display == false && r.IsDBNull(5) && r.IsDBNull(8) && r.IsDBNull(15);
                HomeLocatorRecord? loc = r.GetNullableString(35) is { } luid && locators.TryGetValue(luid, out var lv) ? lv : null;
                HomeActionRecord? act = r.GetNullableString(36) is { } auid && actions.TryGetValue(auid, out var av) ? av : null;
                HomeActionRecord? pb = r.GetNullableString(37) is { } puid && actions.TryGetValue(puid, out var pv) ? pv : null;
                previewsByCard.TryGetValue(cardUid, out var previews);
                var card = new HomeCardRecord(
                    hiddenOnly,
                    r.GetNullableString(3), r.GetNullableString(4), r.GetNullableString(5), r.GetNullableString(6),
                    r.GetNullableString(7), r.GetNullableString(8), r.GetNullableString(9), r.GetNullableString(10),
                    r.GetNullableString(11), r.GetNullableString(12), r.GetNullableString(13), r.GetNullableString(14),
                    r.GetNullableString(15), r.GetNullableString(16), r.GetNullableString(17),
                    r.GetNullableBool(18), r.GetNullableString(19), r.GetNullableBool(20), display,
                    r.GetNullableString(22), r.GetNullableBool(23), r.GetNullableString(24), r.GetNullableString(25),
                    r.GetNullableString(26), r.GetNullableInt64(27), r.GetNullableInt64(28), r.GetNullableString(29),
                    r.GetNullableInt64(30), r.GetNullableString(31), r.GetNullableString(32),
                    r.GetNullableString(33), r.GetNullableString(34),
                    loc, act, pb, previews);
                return (SectionUid: sectionUid, TabUid: tabUid, Card: card);
            }))
        {
            if (entry.TabUid is not null)
            {
                if (!cardsByTab.TryGetValue(entry.TabUid, out var list))
                    cardsByTab[entry.TabUid] = list = [];
                list.Add(entry.Card);
            }
            else
            {
                if (!directCardsBySection.TryGetValue(entry.SectionUid, out var list))
                    directCardsBySection[entry.SectionUid] = list = [];
                list.Add(entry.Card);
            }
        }

        var tabsBySection = new Dictionary<string, List<HomeTabRecord>>(StringComparer.Ordinal);
        foreach (var t in db.Query("""
            SELECT tab_uid,section_uid,tab_id,name
            FROM windows_music_home_tabs
            ORDER BY section_uid, tab_index
            """, r =>
            {
                string tabUid = r.GetString(0);
                cardsByTab.TryGetValue(tabUid, out var items);
                return (SectionUid: r.GetString(1), Tab: new HomeTabRecord(r.GetNullableString(2), r.GetNullableString(3), items));
            }))
        {
            if (!tabsBySection.TryGetValue(t.SectionUid, out var list))
                tabsBySection[t.SectionUid] = list = [];
            list.Add(t.Tab);
        }

        var tagsBySection = new Dictionary<string, List<HomeTagRecord>>(StringComparer.Ordinal);
        foreach (var tg in db.Query("""
            SELECT section_uid,tag_id,name,group_name,selected
            FROM windows_music_home_tags
            ORDER BY section_uid, tag_index
            """, r => (SectionUid: r.GetString(0), Tag: new HomeTagRecord(
                r.GetNullableString(1), r.GetNullableString(2), r.GetNullableString(3), r.GetNullableBool(4)))))
        {
            if (!tagsBySection.TryGetValue(tg.SectionUid, out var list))
                tagsBySection[tg.SectionUid] = list = [];
            list.Add(tg.Tag);
        }

        var sectionsByPage = new Dictionary<int, List<HomeSectionRecord>>();
        foreach (var sec in db.Query("""
            SELECT section_uid,page_index,section_id,title,type,layout,available,error_code,
                   requires_account,display,selected_tab,selected_tag,action_uid
            FROM windows_music_home_sections
            ORDER BY page_index, section_index
            """, r =>
            {
                token.ThrowIfCancellationRequested();
                string suid = r.GetString(0);
                int pidx = r.GetInt32(1);
                bool? display = r.GetNullableBool(9);
                bool hiddenOnly = display == false && r.IsDBNull(2) && r.IsDBNull(3) && r.IsDBNull(5);
                HomeActionRecord? act = r.GetNullableString(12) is { } auid && actions.TryGetValue(auid, out var av) ? av : null;
                directCardsBySection.TryGetValue(suid, out var cards);
                var tabs = tabsBySection.TryGetValue(suid, out var tb) ? tb : (IReadOnlyList<HomeTabRecord>)[];
                var tags = tagsBySection.TryGetValue(suid, out var tg) ? tg : (IReadOnlyList<HomeTagRecord>)[];
                return (PageIndex: pidx, Section: new HomeSectionRecord(
                    hiddenOnly,
                    r.GetNullableString(2), r.GetNullableString(3), r.GetNullableString(4), r.GetNullableString(5),
                    r.GetNullableBool(6), r.GetNullableString(7), r.GetNullableBool(8), display,
                    r.GetNullableString(10), r.GetNullableString(11),
                    act, cards, tabs, tags));
            }))
        {
            if (!sectionsByPage.TryGetValue(sec.PageIndex, out var list))
                sectionsByPage[sec.PageIndex] = list = [];
            list.Add(sec.Section);
        }

        var pages = new List<HomePageRecord>(pageRows.Count);
        foreach (var p in pageRows)
            pages.Add(new HomePageRecord(p.Platform, sectionsByPage.TryGetValue(p.Index, out var secs) ? secs : []));

        return new HomeSnapshotRecord(platform, pages);
    }

    private static void WriteCore(DatabaseSession db, HomeSnapshotRecord snapshot, CancellationToken token)
    {
        // Deleting pages cascades locators, actions, action_items, sections, tabs, tags, cards, and previews cleanly.
        db.Execute("DELETE FROM windows_music_home_pages");
        db.Execute("""
            INSERT INTO windows_music_home_state(singleton,platform) VALUES(1,$plat)
            ON CONFLICT(singleton) DO UPDATE SET platform=excluded.platform
            """, ("$plat", snapshot.Platform));

        for (int pIdx = 0; pIdx < snapshot.Pages.Count; pIdx++)
        {
            token.ThrowIfCancellationRequested();
            var page = snapshot.Pages[pIdx];
            db.Execute("INSERT INTO windows_music_home_pages(page_index,platform) VALUES($idx,$plat)",
                ("$idx", pIdx), ("$plat", page.Platform));

            int seq = 0;
            string? InsertLocator(HomeLocatorRecord? loc)
            {
                if (loc is null) return null;
                string uid = $"p{pIdx}_l{seq++}";
                db.Execute("""
                    INSERT INTO windows_music_home_locators(
                        locator_uid,page_index,kind,locator_id,playlist_id,dir_id,list_id,version,total_version,creator_id,creator_list_id)
                    VALUES($uid,$pidx,$kind,$id,$pl,$dir,$lst,$ver,$tver,$cr,$crl)
                    """,
                    ("$uid", uid), ("$pidx", pIdx), ("$kind", loc.Kind), ("$id", loc.Id),
                    ("$pl", loc.PlaylistId), ("$dir", loc.DirId), ("$lst", loc.ListId),
                    ("$ver", loc.Version), ("$tver", loc.TotalVersion), ("$cr", loc.CreatorId), ("$crl", loc.CreatorListId));
                return uid;
            }

            string? InsertAction(HomeActionRecord? act)
            {
                if (act is null) return null;
                string? locUid = InsertLocator(act.Locator);
                string uid = $"p{pIdx}_a{seq++}";
                db.Execute("""
                    INSERT INTO windows_music_home_actions(
                        action_uid,page_index,type,action_id,branch,page_code,provider,mode,submode,rule_id,tab_tag_id,
                        source_type,source_name,entry,song_id,
                        channel_id,channel_name,channel_page_code,channel_kind,channel_placement,locator_uid)
                    VALUES($uid,$pidx,$type,$id,$br,$pcode,$prov,$mode,$sub,$rule,$tag,
                           $stype,$sname,$entry,$sid,
                           $cid,$cname,$cpcode,$ckind,$cplace,$luid)
                    """,
                    ("$uid", uid), ("$pidx", pIdx), ("$type", act.Type), ("$id", act.Id),
                    ("$br", act.Branch), ("$pcode", act.PageCode), ("$prov", act.Provider),
                    ("$mode", act.Mode), ("$sub", act.Submode), ("$rule", act.RuleId), ("$tag", act.TabTagId),
                    ("$stype", act.SourceType), ("$sname", act.SourceName), ("$entry", act.Entry), ("$sid", act.SongId),
                    ("$cid", act.Channel?.Id), ("$cname", act.Channel?.Name), ("$cpcode", act.Channel?.PageCode),
                    ("$ckind", act.Channel?.ChannelKind), ("$cplace", act.Channel?.Placement), ("$luid", locUid));

                if (act.SourceIds is { Count: > 0 } sids)
                    for (int i = 0; i < sids.Count; i++)
                        db.Execute("INSERT INTO windows_music_home_action_items(action_uid,list_kind,position,item_value) VALUES($uid,'sourceIds',$pos,$val)",
                            ("$uid", uid), ("$pos", i), ("$val", sids[i]));
                if (act.Ids is { Count: > 0 } ids)
                    for (int i = 0; i < ids.Count; i++)
                        db.Execute("INSERT INTO windows_music_home_action_items(action_uid,list_kind,position,item_value) VALUES($uid,'ids',$pos,$val)",
                            ("$uid", uid), ("$pos", i), ("$val", ids[i]));
                return uid;
            }

            void InsertCard(string sectionUid, string? tabUid, int cIdx, HomeCardRecord card)
            {
                token.ThrowIfCancellationRequested();
                string cardUid = tabUid is null ? $"{sectionUid}_c{cIdx}" : $"{tabUid}_c{cIdx}";
                if (card.HiddenOnly)
                {
                    db.Execute("""
                        INSERT INTO windows_music_home_cards(card_uid,section_uid,tab_uid,card_index,display)
                        VALUES($cuid,$suid,$tuid,$cidx,0)
                        """, ("$cuid", cardUid), ("$suid", sectionUid), ("$tuid", tabUid), ("$cidx", cIdx));
                    return;
                }

                string? locUid = InsertLocator(card.Locator);
                string? actUid = InsertAction(card.Action);
                string? pbUid = InsertAction(card.PlaybackSource);

                db.Execute("""
                    INSERT INTO windows_music_home_cards(
                        card_uid,section_uid,tab_uid,card_index,song_id,ref_song_id,card_id,card_key,
                        name,title,label,subtitle,context_text,creator,cover,cover_label,kind,page_code,channel_kind,
                        available,error_code,requires_account,display,placement,can_play,rank_text,sales_text,relationship,
                        item_count,play_count,presentation,day_of_month,decoration,update_frequency,
                        cover_ref_kind,cover_ref_id,locator_uid,action_uid,playback_action_uid)
                    VALUES(
                        $cuid,$suid,$tuid,$cidx,$sid,$rsid,$cid,$ckey,
                        $name,$title,$label,$sub,$ctx,$creator,$cover,$clabel,$kind,$pcode,$ckind,
                        $avail,$err,$req,$disp,$place,$canplay,$rankt,$salest,$rel,
                        $cnt,$pcnt,$pres,$dom,$dec,$ufreq,
                        $crkind,$crid,$luid,$auid,$puid)
                    """,
                    ("$cuid", cardUid), ("$suid", sectionUid), ("$tuid", tabUid), ("$cidx", cIdx),
                    ("$sid", card.SongId), ("$rsid", card.RefSongId), ("$cid", card.Id), ("$ckey", card.Key),
                    ("$name", card.Name), ("$title", card.Title), ("$label", card.Label), ("$sub", card.Subtitle),
                    ("$ctx", card.ContextText), ("$creator", card.Creator), ("$cover", card.Cover), ("$clabel", card.CoverLabel),
                    ("$kind", card.Kind), ("$pcode", card.PageCode), ("$ckind", card.ChannelKind),
                    ("$avail", card.Available), ("$err", card.ErrorCode), ("$req", card.RequiresAccount),
                    ("$disp", card.Display), ("$place", card.Placement), ("$canplay", card.CanPlay),
                    ("$rankt", card.RankText), ("$salest", card.SalesText), ("$rel", card.Relationship),
                    ("$cnt", card.Count), ("$pcnt", card.PlayCount), ("$pres", card.Presentation),
                    ("$dom", card.DayOfMonth), ("$dec", card.Decoration), ("$ufreq", card.UpdateFrequency),
                    ("$crkind", card.CoverRefKind), ("$crid", card.CoverRefId),
                    ("$luid", locUid), ("$auid", actUid), ("$puid", pbUid));

                if (card.Previews is { Count: > 0 } previews)
                {
                    for (int i = 0; i < previews.Count; i++)
                    {
                        var pr = previews[i];
                        db.Execute("""
                            INSERT INTO windows_music_home_card_previews(card_uid,position,title,artist,cover,rank,last_rank,song_id)
                            VALUES($cuid,$pos,$title,$artist,$cover,$rank,$lrank,$sid)
                            """,
                            ("$cuid", cardUid), ("$pos", i), ("$title", pr.Title), ("$artist", pr.Artist),
                            ("$cover", pr.Cover), ("$rank", pr.Rank), ("$lrank", pr.LastRank), ("$sid", pr.SongId));
                    }
                }
            }

            for (int sIdx = 0; sIdx < page.Sections.Count; sIdx++)
            {
                token.ThrowIfCancellationRequested();
                var sec = page.Sections[sIdx];
                string sectionUid = $"p{pIdx}_s{sIdx}";
                if (sec.HiddenOnly)
                {
                    db.Execute("""
                        INSERT INTO windows_music_home_sections(section_uid,page_index,section_index,display)
                        VALUES($uid,$pidx,$sidx,0)
                        """, ("$uid", sectionUid), ("$pidx", pIdx), ("$sidx", sIdx));
                    continue;
                }

                string? actUid = InsertAction(sec.Action);
                db.Execute("""
                    INSERT INTO windows_music_home_sections(
                        section_uid,page_index,section_index,section_id,title,type,layout,available,error_code,
                        requires_account,display,selected_tab,selected_tag,action_uid)
                    VALUES($uid,$pidx,$sidx,$sid,$title,$type,$layout,$avail,$err,$req,$disp,$stab,$stag,$auid)
                    """,
                    ("$uid", sectionUid), ("$pidx", pIdx), ("$sidx", sIdx),
                    ("$sid", sec.Id), ("$title", sec.Title), ("$type", sec.Type), ("$layout", sec.Layout),
                    ("$avail", sec.Available), ("$err", sec.ErrorCode), ("$req", sec.RequiresAccount),
                    ("$disp", sec.Display), ("$stab", sec.SelectedTab), ("$stag", sec.SelectedTag), ("$auid", actUid));

                if (sec.Items is { } items)
                    for (int cIdx = 0; cIdx < items.Count; cIdx++)
                        InsertCard(sectionUid, null, cIdx, items[cIdx]);

                for (int tIdx = 0; tIdx < sec.Tabs.Count; tIdx++)
                {
                    var tab = sec.Tabs[tIdx];
                    string tabUid = $"{sectionUid}_t{tIdx}";
                    db.Execute("""
                        INSERT INTO windows_music_home_tabs(tab_uid,section_uid,tab_index,tab_id,name)
                        VALUES($tuid,$suid,$tidx,$tid,$name)
                        """, ("$tuid", tabUid), ("$suid", sectionUid), ("$tidx", tIdx), ("$tid", tab.Id), ("$name", tab.Name));
                    if (tab.Items is { } tItems)
                        for (int cIdx = 0; cIdx < tItems.Count; cIdx++)
                            InsertCard(sectionUid, tabUid, cIdx, tItems[cIdx]);
                }

                for (int gIdx = 0; gIdx < sec.Tags.Count; gIdx++)
                {
                    var tag = sec.Tags[gIdx];
                    db.Execute("""
                        INSERT INTO windows_music_home_tags(section_uid,tag_index,tag_id,name,group_name,selected)
                        VALUES($suid,$gidx,$gid,$name,$grp,$sel)
                        """, ("$suid", sectionUid), ("$gidx", gIdx), ("$gid", tag.Id),
                        ("$name", tag.Name), ("$grp", tag.Group), ("$sel", tag.Selected));
                }
            }
        }
    }

    private static HomeSnapshotRecord ParseSnapshot(MusicHomeSnapshot snapshot, CancellationToken token)
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

    private static JsonElement SerializePage(HomePageRecord page, Func<string, JsonElement?> resolveSong)
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

    private static void Require(bool condition)
    {
        if (!condition) throw new DocumentStoreException(DocumentStoreError.Corrupt);
    }

    private static bool IsCacheFailure(Exception error) =>
        error is DocumentStoreException or SqliteException or JsonException or FormatException or InvalidOperationException;
}
