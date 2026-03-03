package emby

import (
	"fmt"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

func embyBuildHomeSectionItems(database *db.DB, u *embyUser, sec db.EmbyHomeSection, startIndex int, limit int, fieldsParam string, serverID string, parentID string) []map[string]any {
	switch strings.ToLower(strings.TrimSpace(sec.Module)) {
	case "douban_tv":
		return embyBuildDoubanHotListItems(database, "tv", "tv", "tv", startIndex, limit, serverID, parentID)
	case "douban_movie":
		return embyBuildDoubanHotListItems(database, "movie", "热门", "全部", startIndex, limit, serverID, parentID)
	case "bangumi_anime":
		// Temporary mapping: reuse Douban recent_hot tv_animation.
		return embyBuildDoubanHotListItems(database, "tv", "tv", "tv_animation", startIndex, limit, serverID, parentID)
	case "douban_variety":
		return embyBuildDoubanHotListItems(database, "tv", "show", "show", startIndex, limit, serverID, parentID)
	case "history":
		return embyBuildHistorySectionItems(database, u, startIndex, limit, fieldsParam, serverID, parentID)
	case "site_data":
		return embyBuildSiteCategorySectionItems(database, u, sec, startIndex, limit, fieldsParam, serverID, parentID)
	default:
		return []map[string]any{}
	}
}

func embyBuildHistorySectionItems(database *db.DB, u *embyUser, startIndex int, limit int, fieldsParam string, serverID string, parentID string) []map[string]any {
	if database == nil || u == nil {
		return []map[string]any{}
	}
	uid, _ := parseInt64(strings.TrimSpace(u.ID))
	if uid <= 0 {
		return []map[string]any{}
	}

	// Use full play history rows so we can normalize/merge entries (TMDB + site title dedupe).
	fetchLimit := limit * 8
	if fetchLimit < 64 {
		fetchLimit = 64
	}
	if fetchLimit > 400 {
		fetchLimit = 400
	}
	hist, err := database.ListPlayHistory(uid, fetchLimit)
	if err != nil || len(hist) == 0 {
		return []map[string]any{}
	}

	type histRow struct {
		itemID    string
		groupID   string
		dedupeKey string
		snap      db.PlayHistorySnapshot
	}
	seen := map[string]struct{}{}
	rows := make([]histRow, 0, len(hist))
	for _, h := range hist {
		itemID := strings.TrimSpace(h.PlaybackItemID)
		if itemID == "" {
			continue
		}
		// Dedupe key priority:
		// 1) TMDB id (collapses per-episode/per-item rows into one series/movie card)
		// 2) normalized title (keyword-based collapse when no TMDB id)
		// 3) group id / item id fallback
		dedupeKey := ""
		typ := strings.TrimSpace(h.TMDBType)
		if h.TMDBID > 0 && (typ == "tv" || typ == "movie") {
			dedupeKey = "tmdb:" + typ + ":" + fmt.Sprintf("%d", h.TMDBID)
		} else {
			title := strings.TrimSpace(h.VideoTitle)
			titleKey := strings.ToLower(strings.TrimSpace(embyNormalizeAggKey(title)))
			if titleKey != "" {
				dedupeKey = "kw:" + titleKey
			}
		}

		groupID := ""
		if strings.TrimSpace(h.TMDBType) == "tv" && h.TMDBID > 0 {
			groupID = embyBuildSeriesID(h.TMDBID)
		} else if strings.TrimSpace(h.TMDBType) == "movie" && h.TMDBID > 0 {
			groupID = embyBuildMovieID(h.TMDBID)
		} else if siteVideoID, _, _, ok := embyParseSiteEpisodeIDV2(itemID); ok && siteVideoID > 0 {
			groupID = embyBuildSiteSeriesIDV2(siteVideoID)
		} else if strings.TrimSpace(h.VideoID) != "" {
			groupID = strings.TrimSpace(h.VideoID)
		} else {
			groupID = itemID
		}
		groupID = strings.TrimSpace(groupID)
		if groupID == "" {
			continue
		}

		if strings.TrimSpace(dedupeKey) == "" {
			dedupeKey = "gid:" + groupID
		}
		// Dedupe: ensure history contains only one card per keyword/TMDB id.
		if _, ok := seen[dedupeKey]; ok {
			continue
		}
		seen[dedupeKey] = struct{}{}

		rows = append(rows, histRow{
			itemID:    itemID,
			groupID:   groupID,
			dedupeKey: dedupeKey,
			snap:      db.PlayHistorySnapshot{Pos: h.PlaybackPositionTicks, Runtime: h.PlaybackRuntimeTicks, Updated: h.UpdatedAt},
		})
		if len(rows) >= limit {
			break
		}
	}

	items := make([]map[string]any, 0, len(rows))
	parent := strings.TrimSpace(parentID)
	for _, row := range rows {
		jid := strings.TrimSpace(row.groupID)
		obj, err := embyBuildItem(database, jid)
		if err != nil || obj == nil {
			continue
		}
		if _, ok := obj["UserData"]; !ok {
			obj["UserData"] = map[string]any{"Played": false}
		}

		snap := row.snap
		ud, _ := obj["UserData"].(map[string]any)
		if ud == nil {
			ud = map[string]any{}
		}
		pos := snap.Pos
		if pos < 0 {
			pos = 0
		}
		// Use last episode/movie progress as a hint on the series card.
		if snap.Runtime > 0 && pos > 0 {
			ud["PlayedPercentage"] = (float64(pos) / float64(snap.Runtime)) * 100.0
		}
		if snap.Updated > 0 {
			ud["LastPlayedDate"] = time.Unix(snap.Updated, 0).UTC().Format(time.RFC3339Nano)
		}
		if _, ok := ud["Key"]; !ok {
			ud["Key"] = embyStableKeyDigits(u.ID + ":" + row.itemID)
		}
		obj["UserData"] = ud
		obj["ParentId"] = parent
		embyEnsureInfuseItemFields(obj, jid, fieldsParam, serverID)
		embyEnsureStandardItem(obj, serverID)
		items = append(items, obj)
	}
	return items
}

func embyBuildSiteCategorySectionItems(database *db.DB, u *embyUser, sec db.EmbyHomeSection, startIndex int, limit int, fieldsParam string, serverID string, parentID string) []map[string]any {
	if database == nil || u == nil {
		return []map[string]any{}
	}
	siteKey := strings.TrimSpace(sec.SiteKey)
	categoryID := strings.TrimSpace(sec.CategoryID)
	if siteKey == "" || categoryID == "" {
		return []map[string]any{}
	}
	cardStyle := strings.ToLower(strings.TrimSpace(sec.CardStyle))
	if cardStyle != "tmdb" && cardStyle != "site" {
		cardStyle = "tmdb"
	}
	mediaType := strings.ToLower(strings.TrimSpace(sec.MediaType))
	if mediaType != "tv" && mediaType != "movie" {
		mediaType = "tv"
	}

	apiBase := strings.TrimSpace(embyResolveCatApiBaseForUser(database, u))
	if apiBase == "" {
		return []map[string]any{}
	}

	rawSites, _ := database.ListVideoSourceSites()
	if len(rawSites) == 0 {
		return []map[string]any{}
	}
	states, _ := database.ReadVideoSourceSiteStates()

	siteName := ""
	spiderAPI := ""
	for _, s := range rawSites {
		if strings.TrimSpace(s.Key) != siteKey {
			continue
		}
		if st, ok := states[siteKey]; ok && !st.Enabled {
			return []map[string]any{}
		}
		siteName = strings.TrimSpace(s.Name)
		spiderAPI = strings.TrimSpace(s.API)
		break
	}
	if spiderAPI == "" {
		return []map[string]any{}
	}
	if siteName == "" {
		siteName = siteKey
	}

	pageSize := 20
	if startIndex < 0 {
		startIndex = 0
	}
	if limit <= 0 {
		limit = 24
	}

	startPage := startIndex/pageSize + 1
	offset := startIndex % pageSize
	need := limit + offset
	maxPages := 4
	if maxPages < 1 {
		maxPages = 1
	}

	collected := make([]catpawrunner.SearchItem, 0, need)
	lastErr := ""
	emptySummary := ""
	homeSummary := ""
	for p := startPage; p < startPage+maxPages; p++ {
		// Ensure filters is always an object (some spiders call Object.keys(filters)).
		// Use empty filters so the spider's own defaults apply (often "time" for latest).
		filters := map[string]any{}
		raw, err := catpawrunner.RequestSpider(apiBase, spiderAPI, "category", map[string]any{
			// Different spiders expect different parameter names; send a superset for compatibility.
			"id":   categoryID,
			"tid":  categoryID,
			"t":    categoryID,
			"page": p,
			"pg":   p,
			// Some spiders call Object.keys(...) on filters directly.
			"filters": filters,
			"filter":  filters,
		})
		if err != nil || raw == nil {
			if err != nil {
				lastErr = strings.TrimSpace(err.Error())
			}
			break
		}
		list := catpawrunner.NormalizeSearchList(raw)
		if len(list) == 0 {
			if emptySummary == "" && embyDebugLogEnabled() {
				topLen := 0
				if v, ok := raw["list"].([]any); ok {
					topLen = len(v)
				}
				nestedLen := 0
				if d, ok := raw["data"].(map[string]any); ok && d != nil {
					if v, ok := d["list"].([]any); ok {
						nestedLen = len(v)
					}
				}
				msg := ""
				if m, ok := raw["message"].(string); ok {
					msg = strings.TrimSpace(m)
				}
				emptySummary = "list=" + intToStr(topLen) + " data.list=" + intToStr(nestedLen)
				if msg != "" {
					emptySummary += " msg=" + msg
				}
			}
			break
		}
		collected = append(collected, list...)
		if len(collected) >= need {
			break
		}
	}

	// Fallback: if category returns nothing, try home list (some spiders don't implement category filters).
	if len(collected) == 0 {
		homeRaw, err := catpawrunner.RequestSpider(apiBase, spiderAPI, "home", map[string]any{})
		if err == nil && homeRaw != nil {
			homeList := catpawrunner.NormalizeSearchList(homeRaw)
			if embyDebugLogEnabled() && homeSummary == "" {
				topLen := 0
				if v, ok := homeRaw["list"].([]any); ok {
					topLen = len(v)
				}
				nestedLen := 0
				if d, ok := homeRaw["data"].(map[string]any); ok && d != nil {
					if v, ok := d["list"].([]any); ok {
						nestedLen = len(v)
					}
				}
				homeSummary = "list=" + intToStr(topLen) + " data.list=" + intToStr(nestedLen)
			}
			if len(homeList) > 0 {
				collected = append(collected, homeList...)
			}
		} else if err != nil && lastErr == "" {
			lastErr = strings.TrimSpace(err.Error())
		}
	}

	_ = lastErr
	_ = emptySummary
	_ = homeSummary

	if offset > len(collected) {
		return []map[string]any{}
	}
	if offset > 0 {
		collected = collected[offset:]
	}
	if len(collected) > limit {
		collected = collected[:limit]
	}

	if cardStyle == "site" {
		return embyBuildSiteCardsFromDetail(database, u, apiBase, siteKey, siteName, spiderAPI, collected, fieldsParam, serverID, parentID)
	}
	return embyBuildSiteCardsFromCategoryList(database, siteKey, siteName, spiderAPI, collected, mediaType, cardStyle, serverID, parentID)
}

type embySiteDetailMeta struct {
	Name     string
	Pic      string
	Remark   string
	Overview string
	Year     int
}

func embyExtractSiteDetailMeta(raw map[string]any) embySiteDetailMeta {
	pick := func(m map[string]any) embySiteDetailMeta {
		if m == nil {
			return embySiteDetailMeta{}
		}
		name := strings.TrimSpace(embyAnyToString(m["vod_name"]))
		if name == "" {
			name = strings.TrimSpace(embyAnyToString(m["name"]))
		}
		pic := strings.TrimSpace(embyAnyToString(m["vod_pic"]))
		if pic == "" {
			pic = strings.TrimSpace(embyAnyToString(m["pic"]))
		}
		remark := strings.TrimSpace(embyAnyToString(m["vod_remarks"]))
		if remark == "" {
			remark = strings.TrimSpace(embyAnyToString(m["remark"]))
		}
		overview := strings.TrimSpace(embyAnyToString(m["vod_content"]))
		if overview == "" {
			overview = strings.TrimSpace(embyAnyToString(m["content"]))
		}
		year := intValStr(strings.TrimSpace(embyAnyToString(m["vod_year"])))
		return embySiteDetailMeta{Name: name, Pic: pic, Remark: remark, Overview: overview, Year: year}
	}
	if v, ok := raw["list"].([]any); ok && len(v) > 0 {
		if m, ok := v[0].(map[string]any); ok {
			return pick(m)
		}
	}
	if d, ok := raw["data"].(map[string]any); ok {
		if v, ok := d["list"].([]any); ok && len(v) > 0 {
			if m, ok := v[0].(map[string]any); ok {
				return pick(m)
			}
		}
	}
	if m, ok := raw["vod"].(map[string]any); ok {
		return pick(m)
	}
	return embySiteDetailMeta{}
}

func embyBuildSiteCardsFromDetail(database *db.DB, u *embyUser, apiBase string, siteKey string, siteName string, spiderAPI string, items []catpawrunner.SearchItem, fieldsParam string, serverID string, parentID string) []map[string]any {
	parent := strings.TrimSpace(parentID)
	if len(items) == 0 {
		return []map[string]any{}
	}

	type job struct {
		idx int
		it  catpawrunner.SearchItem
	}
	type res struct {
		idx  int
		meta embySiteDetailMeta
	}
	jobs := make(chan job, len(items))
	results := make(chan res, len(items))

	workers := 4
	if workers < 1 {
		workers = 1
	}
	deadline := time.Now().Add(5 * time.Second)
	for w := 0; w < workers; w++ {
		go func() {
			for jb := range jobs {
				remain := time.Until(deadline)
				if remain <= 0 {
					continue
				}
				vid := strings.TrimSpace(jb.it.ID)
				if vid == "" {
					continue
				}
				raw, err := catpawrunner.RequestSpiderWithTimeout(apiBase, spiderAPI, "detail", map[string]any{"id": vid}, remain)
				if err != nil || raw == nil {
					continue
				}
				results <- res{idx: jb.idx, meta: embyExtractSiteDetailMeta(raw)}
			}
		}()
	}
	for i, it := range items {
		jobs <- job{idx: i, it: it}
	}
	close(jobs)

	metaByIdx := make(map[int]embySiteDetailMeta, len(items))
	timer := time.NewTimer(time.Until(deadline))
	defer timer.Stop()
	received := 0
	for received < len(items) {
		select {
		case r := <-results:
			metaByIdx[r.idx] = r.meta
			received++
		case <-timer.C:
			received = len(items)
		}
	}

	out := make([]map[string]any, 0, len(items))
	for i, it := range items {
		vid := strings.TrimSpace(it.ID)
		name := strings.TrimSpace(it.Name)
		if vid == "" || name == "" {
			continue
		}
		meta := metaByIdx[i]
		pic := strings.TrimSpace(it.Pic)
		remark := strings.TrimSpace(it.Remark)
		overview := strings.TrimSpace(it.Remark)
		year := 0
		if strings.TrimSpace(meta.Pic) != "" {
			pic = strings.TrimSpace(meta.Pic)
		}
		if strings.TrimSpace(meta.Remark) != "" {
			remark = strings.TrimSpace(meta.Remark)
		}
		if strings.TrimSpace(meta.Overview) != "" {
			overview = strings.TrimSpace(meta.Overview)
		}
		if meta.Year > 0 {
			year = meta.Year
		}

		// Persist poster/remark for short site ids (images resolved via DB).
		siteVideoID := int64(0)
		if database != nil {
			id, _ := database.UpsertSiteVideo(strings.TrimSpace(siteKey), strings.TrimSpace(vid), strings.TrimSpace(name), strings.TrimSpace(pic), strings.TrimSpace(remark), time.Now().Unix())
			siteVideoID = id
		}

		siteID := embyBuildSiteSeriesIDV2(siteVideoID)
		if strings.TrimSpace(siteID) == "" {
			continue
		}
		obj := embyBuildSiteSeriesCard(siteID, name, overview, year, parent, siteName, serverID)
		if siteName != "" {
			obj["ProductionLocations"] = []string{siteName}
		}
		embyEnsureInfuseItemFields(obj, siteID, fieldsParam, serverID)
		embyEnsureStandardItem(obj, serverID)
		out = append(out, obj)
	}
	return out
}

func embyBuildTMDBCardsFromSiteList(database *db.DB, u *embyUser, siteKey string, siteName string, spiderAPI string, items []catpawrunner.SearchItem, mediaType string, fieldsParam string, serverID string, parentID string) []map[string]any {
	parent := strings.TrimSpace(parentID)
	out := make([]map[string]any, 0, len(items))
	wantKind := "tv"
	if strings.EqualFold(strings.TrimSpace(mediaType), "movie") {
		wantKind = "movie"
	}

	for _, it := range items {
		vid := strings.TrimSpace(it.ID)
		name := strings.TrimSpace(it.Name)
		if vid == "" || name == "" {
			continue
		}

		tmdbID := 0
		if database != nil {
			results, err := embyTMDBSearchMulti(database, name)
			if err == nil && len(results) > 0 {
				bestScore := 0
				bestID := 0
				for _, r := range results {
					mt := strings.ToLower(strings.TrimSpace(r.MediaType))
					if mt == "tv" {
						mt = "tv"
					}
					if mt != wantKind {
						continue
					}
					title := strings.TrimSpace(r.Title)
					if title == "" {
						continue
					}
					score := embyComputeMatchScore(name, title)
					if score > bestScore {
						bestScore = score
						bestID = r.ID
					}
				}
				if bestScore >= 60 && bestID > 0 {
					tmdbID = bestID
				}
			}
		}

		if tmdbID > 0 {
			jid := ""
			if wantKind == "movie" {
				jid = embyBuildMovieID(tmdbID)
			} else {
				jid = embyBuildSeriesID(tmdbID)
			}
			obj, err := embyBuildItem(database, jid)
			if err == nil && obj != nil {
				obj["ParentId"] = parent
				embyEnsureInfuseItemFields(obj, jid, fieldsParam, serverID)
				embyEnsureStandardItem(obj, serverID)
				out = append(out, obj)
				continue
			}
		}

		// Fallback: site card (short id).
		siteVideoID := int64(0)
		if database != nil {
			id, _ := database.UpsertSiteVideo(strings.TrimSpace(siteKey), strings.TrimSpace(vid), strings.TrimSpace(name), strings.TrimSpace(it.Pic), strings.TrimSpace(it.Remark), time.Now().Unix())
			siteVideoID = id
		}
		siteID := embyBuildSiteSeriesIDV2(siteVideoID)
		if strings.TrimSpace(siteID) == "" {
			continue
		}
		obj := embyBuildSiteSeriesCard(siteID, name, strings.TrimSpace(it.Remark), 0, parent, siteName, serverID)
		if siteName != "" {
			obj["ProductionLocations"] = []string{siteName}
		}
		embyEnsureInfuseItemFields(obj, siteID, fieldsParam, serverID)
		embyEnsureStandardItem(obj, serverID)
		out = append(out, obj)
	}
	return out
}

func embyBuildSiteCardsFromCategoryList(database *db.DB, siteKey string, siteName string, spiderAPI string, items []catpawrunner.SearchItem, mediaType string, cardStyle string, serverID string, parentID string) []map[string]any {
	parent := strings.TrimSpace(parentID)
	out := make([]map[string]any, 0, len(items))
	for _, it := range items {
		vid := strings.TrimSpace(it.ID)
		name := strings.TrimSpace(it.Name)
		if vid == "" || name == "" {
			continue
		}
		siteVideoID := int64(0)
		if database != nil {
			id, _ := database.UpsertSiteVideo(strings.TrimSpace(siteKey), strings.TrimSpace(vid), strings.TrimSpace(name), strings.TrimSpace(it.Pic), strings.TrimSpace(it.Remark), time.Now().Unix())
			siteVideoID = id
		}
		siteID := embyBuildSiteSeriesIDV2(siteVideoID)
		if strings.TrimSpace(siteID) == "" {
			continue
		}
		obj := embyBuildSiteSeriesCard(siteID, name, strings.TrimSpace(it.Remark), 0, parent, siteName, serverID)
		out = append(out, obj)
	}
	return out
}

func embyBuildSiteSeriesCard(id string, name string, overview string, year int, parentID string, siteName string, serverID string) map[string]any {
	jid := strings.TrimSpace(id)
	title := strings.TrimSpace(name)
	parent := strings.TrimSpace(parentID)
	nowISO := time.Now().UTC().Format(time.RFC3339)
	obj := map[string]any{
		"Id":                      jid,
		"Name":                    title,
		"SortName":                title,
		"Type":                    "Series",
		"MediaType":               "Video",
		"LocationType":            "Remote",
		"IsFolder":                true,
		"ProductionYear":          year,
		"DateCreated":             nowISO,
		"Etag":                    embyStableEtag(jid),
		"Genres":                  []string{},
		"Overview":                strings.TrimSpace(overview),
		"ParentId":                parent,
		"Path":                    "meowfilm://" + jid,
		"RecursiveItemCount":      0,
		"ChildCount":              0,
		"MediaSources":            []any{},
		"AlternateMediaSources":   []any{},
		"ProviderIds":             map[string]any{},
		"ImageTags":               map[string]any{"Primary": "site"},
		"BackdropImageTags":       []string{},
		"ServerId":                serverID,
		"UserData":                map[string]any{"Played": false},
		"PrimaryImageAspectRatio": 0.6666667,
	}
	if strings.TrimSpace(siteName) != "" {
		obj["ProductionLocations"] = []string{strings.TrimSpace(siteName)}
	}
	return obj
}

func embyApplyStableRankSortName(items []map[string]any, startIndex int) {
	if len(items) == 0 {
		return
	}
	base := startIndex
	if base < 0 {
		base = 0
	}
	now := time.Now().UTC()
	for i, it := range items {
		if it == nil {
			continue
		}
		it["SortName"] = fmt.Sprintf("%06d", base+i)
		it["IndexNumber"] = base + i + 1
		// Some clients ignore array order and re-sort by DateCreated/PremiereDate.
		// Assign a stable descending timestamp so the original order is preserved.
		ts := now.Add(-time.Duration(base+i) * time.Second).Format(time.RFC3339Nano)
		it["DateCreated"] = ts
		it["PremiereDate"] = ts
		it["DateLastContentAdded"] = ts
		it["DateLastMediaAdded"] = ts
	}
}

func embyPreviewcatpawrunnerItems(list []catpawrunner.SearchItem, n int) string {
	if n <= 0 {
		n = 5
	}
	if len(list) == 0 {
		return "[]"
	}
	if n > len(list) {
		n = len(list)
	}
	parts := make([]string, 0, n)
	for i := 0; i < n; i++ {
		it := list[i]
		name := strings.TrimSpace(it.Name)
		id := strings.TrimSpace(it.ID)
		if name == "" && id == "" {
			continue
		}
		parts = append(parts, fmt.Sprintf("%d:%s(%s)", i, name, id))
	}
	if len(parts) == 0 {
		return "[]"
	}
	return "[" + strings.Join(parts, " | ") + "]"
}

func parseInt64(s string) (int64, bool) {
	s = strings.TrimSpace(s)
	if s == "" {
		return 0, false
	}
	v := int64(0)
	neg := false
	for i, r := range s {
		if i == 0 && r == '-' {
			neg = true
			continue
		}
		if r < '0' || r > '9' {
			return 0, false
		}
		v = v*10 + int64(r-'0')
	}
	if neg {
		v = -v
	}
	return v, true
}

func intToStr(v int) string {
	if v == 0 {
		return "0"
	}
	neg := v < 0
	if neg {
		v = -v
	}
	buf := make([]byte, 0, 16)
	for v > 0 {
		buf = append(buf, byte('0'+v%10))
		v /= 10
	}
	if neg {
		buf = append(buf, '-')
	}
	// reverse
	for i, j := 0, len(buf)-1; i < j; i, j = i+1, j-1 {
		buf[i], buf[j] = buf[j], buf[i]
	}
	return string(buf)
}
