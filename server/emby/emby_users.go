package emby

import (
	"crypto/rand"
	"crypto/sha1"
	"database/sql"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"net/http"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"golang.org/x/crypto/bcrypt"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleEmbyUsers(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	debug := embyDebugLogEnabled()

	// POST /Users/AuthenticateByName
	if len(parts) >= 1 && strings.EqualFold(parts[0], "AuthenticateByName") && r.Method == http.MethodPost {
		var body struct {
			Username string `json:"Username"`
			Pw       string `json:"Pw"`
			Password string `json:"Password"`
		}
		if err := readJSON(r, &body); err != nil {
			embyWriteError(w, 400, "Invalid JSON")
			return
		}
		username := strings.TrimSpace(body.Username)
		password := body.Pw
		if password == "" {
			password = body.Password
		}
		if username == "" || password == "" {
			embyWriteError(w, 400, "用户名或密码不能为空")
			return
		}

		row, err := database.GetUserAuthByUsername(username)
		if err != nil {
			if err == sql.ErrNoRows {
				embyWriteError(w, 401, "用户名或密码错误")
				return
			}
			embyWriteError(w, 500, "请求失败")
			return
		}
		id := row.ID
		role := row.Role
		status := row.Status
		if strings.TrimSpace(status) != "active" {
			embyWriteError(w, 403, "该账户已禁用")
			return
		}
		if bcrypt.CompareHashAndPassword([]byte(row.PasswordHash), []byte(password)) != nil {
			embyWriteError(w, 401, "用户名或密码错误")
			return
		}

		token, exp, err := embyIssueToken(database, id)
		if err != nil || token == "" {
			embyWriteError(w, 500, "请求失败")
			return
		}

		writeJSON(w, 200, map[string]any{
			"User": map[string]any{
				"Id":   int64ToStr(id),
				"Name": username,
				"Policy": map[string]any{
					"IsAdministrator":          strings.TrimSpace(role) == "admin",
					"EnableContentDownloading": true,
				},
			},
			"AccessToken": token,
			"ServerId":    serverID,
			"Expires":     exp.Format(time.RFC3339),
		})
		return
	}

	// GET /Users/{id}/GroupingOptions
	if len(parts) >= 2 && strings.EqualFold(parts[1], "GroupingOptions") && r.Method == http.MethodGet {
		u, ok := embyRequireUser(w, r, database)
		if !ok {
			return
		}
		if !embyRequireSameUserOrNotFound(w, u.ID, parts[0]) {
			return
		}
		// Emby expects SpecialViewOptionDto[].
		writeJSON(w, 200, embyGroupingOptions(database))
		return
	}

	// GET /Users/{id}
	if len(parts) == 1 && parts[0] != "" && r.Method == http.MethodGet {
		u, ok := embyRequireUser(w, r, database)
		if !ok {
			return
		}
		if !embyRequireSameUserOrNotFound(w, u.ID, parts[0]) {
			return
		}
		writeJSON(w, 200, map[string]any{
			"Id":   u.ID,
			"Name": u.Username,
			"Policy": map[string]any{
				"IsAdministrator": strings.TrimSpace(u.Role) == "admin",
				// Allow content downloads (clients may hide download UI if this is false).
				"EnableContentDownloading": true,
			},
		})
		return
	}

	// GET /Users/{id}/Views
	if len(parts) >= 2 && strings.EqualFold(parts[1], "Views") && r.Method == http.MethodGet {
		_, ok := embyRequireUser(w, r, database)
		if !ok {
			return
		}
		writeJSON(w, 200, embyUsersViewsResponse(database, serverID))
		return
	}

	// GET /Users/{id}/Items/Latest
	if len(parts) == 3 && strings.EqualFold(parts[1], "Items") && strings.EqualFold(parts[2], "Latest") && r.Method == http.MethodGet {
		u, ok := embyRequireUser(w, r, database)
		if !ok {
			return
		}
		if !embyRequireSameUserOrNotFound(w, u.ID, parts[0]) {
			return
		}
		startIndex := embyQueryIntClamped(r, "StartIndex", 0, 0, 1<<30)
		limit := embyQueryIntClamped(r, "Limit", 24, 1, 60)
		fieldsParam := embyQueryGetCI(r, "fields")

		parentID := embyQueryTrimCI(r, "ParentId")
		excludeLocationTypes := embyQueryTrimCI(r, "ExcludeLocationTypes")
		if parentID == "" {
			embyWriteEmptyArrayOK(w)
			return
		}

		sec, ok := embyResolveHomeSectionByID(database, parentID)
		if !ok {
			embyWriteEmptyArrayOK(w)
			return
		}

		items := embyBuildHomeSectionItems(database, u, sec, startIndex, limit, fieldsParam, serverID, parentID)

		items = embyFilterItemsByExcludeLocationTypes(items, excludeLocationTypes)

		// Emby typically returns an array for this endpoint.
		writeJSON(w, 200, items)
		return
	}

	// GET /Users/{id}/Items/Resume
	if len(parts) == 3 && strings.EqualFold(parts[1], "Items") && strings.EqualFold(parts[2], "Resume") && r.Method == http.MethodGet {
		u, ok := embyRequireUser(w, r, database)
		if !ok {
			return
		}
		if !embyRequireSameUserOrNotFound(w, u.ID, parts[0]) {
			return
		}
		startIndex := embyQueryIntClamped(r, "StartIndex", 0, 0, 1<<30)
		limit := embyQueryIntClamped(r, "Limit", 12, 1, 60)
		fieldsParam := embyQueryGetCI(r, "fields")
		// Some clients pass MediaTypes=Video; ignore for now and always return video resume.

		uid, _ := strconv.ParseInt(strings.TrimSpace(u.ID), 10, 64)
		snaps, ids, err := database.ListResumePlaybackItems(uid, limit, startIndex)
		if err != nil {
			if debug {
				embyDebugPrintf("[emby][resume] query failed err=%q", err.Error())
			}
			writeJSON(w, 200, embyPagedEmpty(startIndex))
			return
		}

		// Return the actual playable item (episode/movie) for Emby clients' "recently watched" section.
		// Dedupe exact duplicates only.
		type resumeRow struct {
			itemID string
			snap   db.PlayHistorySnapshot
		}
		seen := map[string]struct{}{}
		rows := make([]resumeRow, 0, len(ids))
		for i, rawID := range ids {
			rawID = strings.TrimSpace(rawID)
			if rawID == "" {
				continue
			}
			snap := snaps[i]
			if snap.Pos <= 0 {
				continue
			}
			if _, ok := seen[rawID]; ok {
				continue
			}
			seen[rawID] = struct{}{}
			rows = append(rows, resumeRow{itemID: rawID, snap: snap})
		}

		items := make([]map[string]any, 0, len(rows))
		for _, row := range rows {
			jid := strings.TrimSpace(row.itemID)
			snap := row.snap
			obj, err := embyBuildItem(database, jid)
			if err != nil || obj == nil {
				continue
			}
			ud, _ := obj["UserData"].(map[string]any)
			if ud == nil {
				ud = map[string]any{}
			}
			pos := snap.Pos
			if pos < 0 {
				pos = 0
			}
			ud["PlaybackPositionTicks"] = pos
			runtime := snap.Runtime
			if runtime <= 0 {
				// Some clients won't render resume cards without RunTimeTicks.
				// Use a conservative fallback based on current position.
				runtime = pos + int64(60*1e7) // +60s
			}
			if runtime > 0 && pos > 0 {
				ud["PlayedPercentage"] = (float64(pos) / float64(runtime)) * 100.0
			}
			if snap.Updated > 0 {
				ud["LastPlayedDate"] = time.Unix(snap.Updated, 0).UTC().Format(time.RFC3339Nano)
			}
			if _, ok := ud["Key"]; !ok {
				ud["Key"] = embyStableKeyDigits(u.ID + ":" + jid)
			}
			if _, ok := ud["PlayCount"]; !ok {
				ud["PlayCount"] = 0
			}
			if _, ok := ud["IsFavorite"]; !ok {
				ud["IsFavorite"] = false
			}
			ud["Played"] = false
			obj["UserData"] = ud

			obj["RunTimeTicks"] = runtime
			embyEnsureInfuseItemFields(obj, jid, fieldsParam, serverID)
			embyEnsureStandardItem(obj, serverID)
			items = append(items, obj)
		}

		// TotalRecordCount: best-effort (count query).
		total := 0
		if n, err := database.CountResumePlaybackItems(uid); err == nil {
			total = n
		} else if debug {
			embyDebugPrintf("[emby][resume] count failed err=%q", err.Error())
		}
		// Some clients decide visibility purely based on TotalRecordCount.
		if total < startIndex+len(items) {
			total = startIndex + len(items)
		}
		if debug && len(items) == 0 {
			embyDebugPrintf("[emby][resume] empty start=%d limit=%d raw=%d dedup=%d", startIndex, limit, len(ids), len(rows))
		}
		writeJSON(w, 200, embyPagedItems(items, startIndex, total))
		return
	}

	// POST /Users/{id}/Items/{itemId}/HideFromResume
	// Some clients call this for each resume item; 404 may cause them to hide the entire "recently watched" section.
	if len(parts) == 4 && strings.EqualFold(parts[1], "Items") && strings.EqualFold(parts[3], "HideFromResume") && r.Method == http.MethodPost {
		u, ok := embyRequireUser(w, r, database)
		if !ok {
			return
		}
		if !embyRequireSameUserOrNotFound(w, u.ID, parts[0]) {
			return
		}
		itemID := strings.TrimSpace(parts[2])
		var body struct {
			Hide bool `json:"Hide"`
		}
		_ = readJSONLoose(r, &body)

		uid, _ := strconv.ParseInt(strings.TrimSpace(u.ID), 10, 64)
		snap := db.PlayHistorySnapshot{}
		if uid > 0 && database != nil && itemID != "" {
			if m, err := database.GetPlayHistorySnapshotsByPlaybackItemIDs(uid, []string{itemID}); err == nil {
				if v, ok := m[itemID]; ok {
					snap = v
				}
			}
		}

		pos := snap.Pos
		if pos < 0 {
			pos = 0
		}
		runtime := snap.Runtime
		if runtime <= 0 && pos > 0 {
			runtime = pos + int64(60*1e7)
		}
		playedPct := 0.0
		if runtime > 0 && pos > 0 {
			playedPct = (float64(pos) / float64(runtime)) * 100.0
		}
		lastPlayed := ""
		if snap.Updated > 0 {
			lastPlayed = time.Unix(snap.Updated, 0).UTC().Format(time.RFC3339Nano)
		}

		// Mimic Emby: return user-data JSON even when Hide=false (clients treat it as a capability probe).
		// We currently ignore Hide=true to avoid the client accidentally wiping the resume list.
		_ = body.Hide
		writeJSON(w, 200, map[string]any{
			"PlayedPercentage":      playedPct,
			"PlaybackPositionTicks": pos,
			"PlayCount":             0,
			"IsFavorite":            false,
			"LastPlayedDate":        lastPlayed,
			"Played":                false,
		})
		return
	}

	// GET /Users/{id}/Items/{itemId}
	// Some clients use this endpoint to fetch the details of a view/folder item before browsing.
	if len(parts) == 3 && strings.EqualFold(parts[1], "Items") && r.Method == http.MethodGet {
		u, ok := embyRequireUser(w, r, database)
		if !ok {
			return
		}
		if !embyRequireSameUserOrNotFound(w, u.ID, parts[0]) {
			return
		}
		fieldsParam := embyQueryGetCI(r, "fields")
		itemID := strings.TrimSpace(parts[2])
		if itemID == "" {
			embyNotFound(w)
			return
		}

		// Some clients probe download capability by requesting item details with CanDownload field,
		// then immediately issuing a burst of playback/episode requests. Avoid heavy smart probing
		// for a short window after detecting this pattern.
		if embyFieldsHasCI(fieldsParam, "CanDownload") {
			if parsed, ok := embyParseItemID(itemID); ok && parsed != nil && parsed.TMDBID > 0 {
				embyNoteDownloadProbe(u.ID, embyClientDeviceID(r), parsed.TMDBID)
			}
		}
		if v, ok := embyViewFolderItemByID(database, serverID, itemID); ok {
			writeJSON(w, 200, v)
			return
		}

		// During active playback, some clients issue a burst of detail requests derived from the playing item.
		// Return a minimal item shape and avoid any extra parsing/network work.
		if playing, ok := embyGetPlaying(u.ID, embyClientDeviceID(r)); ok {
			if embyIsDerivedFromPlaying(playing.ItemID, itemID) {
				if quick := embyBuildQuickNowPlayingItem(serverID, itemID, playing); quick != nil {
					writeJSON(w, 200, quick)
					return
				}
			}
		}

		obj, err := embyBuildItem(database, itemID)
		if err != nil {
			embyBadGateway(w, err)
			return
		}
		if obj == nil {
			embyNotFound(w)
			return
		}
		// Ensure UserData exists; Infuse uses it.
		if _, ok := obj["UserData"]; !ok {
			obj["UserData"] = map[string]any{"Played": false}
		}
		if parsed, ok := embyParseItemID(itemID); ok && parsed != nil && parsed.Kind == "tv" && parsed.SubKind == "series" {
			if snap, ok := embyQueryPlayHistoryByVideoID(database, u.ID, itemID); ok {
				embyApplyPlayHistoryToItemUserData(u.ID, itemID, obj, snap)
			}
		} else if hit := embyQueryPlayHistoryByItemIDs(database, u.ID, []string{itemID}); len(hit) > 0 {
			if snap, ok := hit[itemID]; ok {
				embyApplyPlayHistoryToItemUserData(u.ID, itemID, obj, snap)
			}
		}
		embyEnsureInfuseItemFields(obj, itemID, fieldsParam, serverID)
		embyEnsureStandardItem(obj, serverID)
		writeJSON(w, 200, obj)
		return
	}

	// GET /Users/{id}/Items?ParentId=view_...
	if len(parts) >= 2 && strings.EqualFold(parts[1], "Items") && r.Method == http.MethodGet {
		u, ok := embyRequireUser(w, r, database)
		if !ok {
			return
		}
		parent := embyQueryTrimCI(r, "ParentId")
		searchTerm := embyQueryTrimCI(r, "SearchTerm")
		includeItemTypes := embyQueryTrimCI(r, "IncludeItemTypes")
		excludeItemTypes := embyQueryTrimCI(r, "ExcludeItemTypes")
		fieldsParam := embyQueryGetCI(r, "fields")
		excludeLocationTypes := embyQueryTrimCI(r, "ExcludeLocationTypes")

		if sec, ok := embyResolveHomeSectionByID(database, parent); ok {
			startIndex := embyQueryIntClamped(r, "StartIndex", 0, 0, 1<<30)
			limit := embyQueryIntClamped(r, "Limit", 24, 1, 60)

			out := embyBuildHomeSectionItems(database, u, sec, startIndex, limit, fieldsParam, serverID, parent)
			out = embyFilterItemsByExcludeLocationTypes(out, excludeLocationTypes)

			// Emby clients page based on TotalRecordCount; Douban doesn't provide a reliable total here.
			// Use a "has more" hint when we return a full page.
			total := startIndex + len(out)
			if len(out) == limit {
				total++
			}
			writeJSON(w, 200, embyPagedItems(out, startIndex, total))
			return
		}

		// Some clients (e.g. 网易爆米花 iOS) probe /Users/{id}/Items without ParentId to obtain "libraries".
		// If we return actual movies/series here, the client may treat each item as a library root and then
		// call /Items/Latest with ParentId=tmdb_movie_xxx (which doesn't match our view logic).
		// Detect this probe shape (ExcludeItemTypes present, no IncludeItemTypes) and return view folders instead.
		if parent == "" && searchTerm == "" && strings.TrimSpace(includeItemTypes) == "" && strings.TrimSpace(excludeItemTypes) != "" {
			startIndex := embyQueryIntClamped(r, "StartIndex", 0, 0, 1<<30)
			limit := embyQueryIntClamped(r, "Limit", 24, 1, 100)
			all := embyViewFolders(database, serverID)
			total := len(all)
			page := []map[string]any{}
			if startIndex < total {
				end := startIndex + limit
				if end > total {
					end = total
				}
				page = all[startIndex:end]
			}
			// Keep schema consistent with other /Items responses.
			for _, obj := range page {
				if obj == nil {
					continue
				}
				jid, _ := obj["Id"].(string)
				embyEnsureInfuseItemFields(obj, jid, fieldsParam, serverID)
				embyEnsureStandardItem(obj, serverID)
			}
			writeJSON(w, 200, embyPagedItems(page, startIndex, total))
			return
		}

		// Some mobile clients send a "library browse" request with IncludeItemTypes but still expect the response
		// to be library roots (views). If we return real items here, the client may treat each item as a library
		// and then call /Items/Latest with ParentId=tmdb_movie_xxx which we don't support.
		// Keep the richer virtual library feed only for Infuse.
		if parent == "" && searchTerm == "" && strings.TrimSpace(includeItemTypes) != "" && !embyIsInfuseClient(r) {
			startIndex := embyQueryIntClamped(r, "StartIndex", 0, 0, 1<<30)
			limit := embyQueryIntClamped(r, "Limit", 24, 1, 100)
			all := embyViewFolders(database, serverID)
			total := len(all)
			page := []map[string]any{}
			if startIndex < total {
				end := startIndex + limit
				if end > total {
					end = total
				}
				page = all[startIndex:end]
			}
			for _, obj := range page {
				if obj == nil {
					continue
				}
				jid, _ := obj["Id"].(string)
				embyEnsureInfuseItemFields(obj, jid, fieldsParam, serverID)
				embyEnsureStandardItem(obj, serverID)
			}
			writeJSON(w, 200, embyPagedItems(page, startIndex, total))
			return
		}

		// Library-style browsing (Filmly/Infuse/Jellyfin clients):
		// /Users/{id}/Items?IncludeItemTypes=Movie,Series&SortBy=DateLastContentAdded&...
		// We don't have a real library index, so expose a deterministic "virtual library" feed derived from our hot lists.
		if parent == "" && searchTerm == "" {
			startIndex := embyQueryIntClamped(r, "StartIndex", 0, 0, 1<<30)
			limit := embyQueryIntClamped(r, "Limit", 24, 1, 100)
			yearsParam := embyQueryTrimCI(r, "Years")
			sortByParam := embyQueryTrimCI(r, "SortBy")

			typesSet := map[string]struct{}{}
			for _, p := range strings.Split(includeItemTypes, ",") {
				t := strings.ToLower(strings.TrimSpace(p))
				if t != "" {
					typesSet[t] = struct{}{}
				}
			}
			// Default to Movie+Series if not specified.
			if len(typesSet) == 0 {
				typesSet["movie"] = struct{}{}
				typesSet["series"] = struct{}{}
			}

			// If the client applies a Year filter, use TMDB discover so the results actually reflect that year.
			yearStart := 0
			yearEnd := 0
			if yearsParam != "" {
				minY := 0
				maxY := 0
				for _, part := range strings.Split(yearsParam, ",") {
					p := strings.TrimSpace(part)
					if p == "" {
						continue
					}
					y := intValStr(p)
					if y <= 0 {
						continue
					}
					if minY == 0 || y < minY {
						minY = y
					}
					if maxY == 0 || y > maxY {
						maxY = y
					}
				}
				yearStart, yearEnd = minY, maxY
			}

			if yearStart > 0 {
				tmdbSortMovie := "popularity.desc"
				tmdbSortTV := "popularity.desc"
				sb := strings.ToLower(strings.TrimSpace(sortByParam))
				switch sb {
				case "communityrating":
					tmdbSortMovie = "vote_average.desc"
					tmdbSortTV = "vote_average.desc"
				case "productionyear", "premieredate":
					tmdbSortMovie = "primary_release_date.desc"
					tmdbSortTV = "first_air_date.desc"
				case "datelastcontentadded":
					tmdbSortMovie = "popularity.desc"
					tmdbSortTV = "popularity.desc"
				}

				// TMDB discover uses 20 items/page. We may need to fetch multiple pages to cover StartIndex+Limit.
				want := startIndex + limit
				pages := want/20 + 1
				if pages < 1 {
					pages = 1
				}
				if pages > 5 {
					pages = 5
				}

				var movies []embyTMDBSearchItem
				var tvs []embyTMDBSearchItem
				totalMovies := 0
				totalTV := 0
				if _, ok := typesSet["movie"]; ok {
					for p := 1; p <= pages; p++ {
						items, total, err := embyTMDBDiscover(database, "movie", yearStart, yearEnd, tmdbSortMovie, p)
						if err != nil {
							embyBadGateway(w, err)
							return
						}
						if totalMovies == 0 {
							totalMovies = total
						}
						movies = append(movies, items...)
						if len(items) == 0 {
							break
						}
					}
				}
				if _, ok := typesSet["series"]; ok {
					for p := 1; p <= pages; p++ {
						items, total, err := embyTMDBDiscover(database, "tv", yearStart, yearEnd, tmdbSortTV, p)
						if err != nil {
							embyBadGateway(w, err)
							return
						}
						if totalTV == 0 {
							totalTV = total
						}
						tvs = append(tvs, items...)
						if len(items) == 0 {
							break
						}
					}
				}

				combined := make([]map[string]any, 0, limit)
				tmp := make([]map[string]any, 0, len(movies)+len(tvs))
				for _, it := range movies {
					base := embyBuildBaseItemFromSearch(it)
					if base == nil {
						continue
					}
					id, _ := base["Id"].(string)
					embyEnsureInfuseItemFields(base, id, fieldsParam, serverID)
					embyEnsureStandardItem(base, serverID)
					tmp = append(tmp, base)
				}
				for _, it := range tvs {
					base := embyBuildBaseItemFromSearch(it)
					if base == nil {
						continue
					}
					id, _ := base["Id"].(string)
					embyEnsureInfuseItemFields(base, id, fieldsParam, serverID)
					embyEnsureStandardItem(base, serverID)
					tmp = append(tmp, base)
				}

				// Slice by StartIndex/Limit.
				if startIndex < len(tmp) {
					end := startIndex + limit
					if end > len(tmp) {
						end = len(tmp)
					}
					combined = tmp[startIndex:end]
				}
				combined = embyFilterItemsByExcludeLocationTypes(combined, excludeLocationTypes)

				total := totalMovies + totalTV
				if total <= 0 {
					// Best-effort fallback.
					total = startIndex + len(combined)
					if len(combined) == limit {
						total++
					}
				}
				writeJSON(w, 200, embyPagedItems(combined, startIndex, total))
				return
			}

			fetchLimit := startIndex + limit
			var movies []map[string]any
			var tv []map[string]any
			if _, ok := typesSet["movie"]; ok {
				movies = embyBuildDoubanHotListItems(database, "movie", "热门", "全部", 0, fetchLimit, serverID, "")
			}
			if _, ok := typesSet["series"]; ok {
				tv = embyBuildDoubanHotListItems(database, "tv", "tv", "tv", 0, fetchLimit, serverID, "")
			}

			combined := make([]map[string]any, 0, len(movies)+len(tv))
			maxLen := len(movies)
			if len(tv) > maxLen {
				maxLen = len(tv)
			}
			for i := 0; i < maxLen; i++ {
				if i < len(movies) {
					combined = append(combined, movies[i])
				}
				if i < len(tv) {
					combined = append(combined, tv[i])
				}
			}

			page := []map[string]any{}
			if startIndex < len(combined) {
				end := startIndex + limit
				if end > len(combined) {
					end = len(combined)
				}
				page = combined[startIndex:end]
			}
			page = embyFilterItemsByExcludeLocationTypes(page, excludeLocationTypes)

			// Ensure fields requested by clients exist and remain type-correct.
			for _, obj := range page {
				if obj == nil {
					continue
				}
				jid, _ := obj["Id"].(string)
				embyEnsureInfuseItemFields(obj, jid, fieldsParam, serverID)
				embyEnsureStandardItem(obj, serverID)
			}

			total := startIndex + len(page)
			if len(page) == limit {
				total++
			}
			writeJSON(w, 200, embyPagedItems(page, startIndex, total))
			return
		}

		// Search: /Users/{id}/Items?searchTerm=...&recursive=true&limit=...
		// TMDB search results are returned to clients.
		// In parallel, we trigger a best-effort site search (3s cap) and render the site hits as additional
		// cards using the same item schema as TMDB (no extra/unknown fields for strict clients like Infuse).
		// Mapping from site card Id -> site detail/playback is stored server-side.
		if parent == "" && searchTerm != "" {
			startIndex := embyQueryIntClamped(r, "StartIndex", 0, 0, 1<<30)
			// During active playback, ignore background search requests to avoid extra parsing/network work.
			if playing, ok := embyGetPlaying(u.ID, embyClientDeviceID(r)); ok {
				if embyIsDerivedSearchTerm(playing, searchTerm) {
					writeJSON(w, 200, embyPagedEmpty(startIndex))
					return
				}
			}
			limit := embyQueryIntClamped(r, "Limit", 24, 1, 60)
			startAt := time.Now()
			scoreTerm := CanonicalSearchTerm(searchTerm)
			if scoreTerm == "" {
				scoreTerm = searchTerm
			}

			// Infuse may send a pair of search requests (Simplified + Traditional) back-to-back.
			// If we detect a folded-duplicate for the same user+device, return an empty page to avoid
			// the client "combining" two near-identical result sets.
			if embyIsInfuseClient(r) {
				deviceID := embyClientDeviceID(r)
				userID := ""
				if u != nil {
					userID = u.ID
				}
				if InfuseShouldDropFoldedDuplicate(userID, deviceID, searchTerm, scoreTerm, startAt) {
					embyDebugPrintf("[emby][search][infuse] drop folded-duplicate term=%q folded=%q user=%q device=%q", searchTerm, scoreTerm, userID, deviceID)
					writeJSON(w, 200, embyPagedEmpty(startIndex))
					return
				}
			}

			cacheKey := SearchCacheKey(u.ID, includeItemTypes, searchTerm)
			cached, ok := SearchCacheGet[embyTMDBSearchItem, embySiteSearchHit](cacheKey)
			tmdbSorted := cached.TMDB
			siteSorted := cached.Sites

			typesSet := map[string]struct{}{}
			for _, p := range strings.Split(includeItemTypes, ",") {
				t := strings.ToLower(strings.TrimSpace(p))
				if t != "" {
					typesSet[t] = struct{}{}
				}
			}
			if len(typesSet) == 0 {
				typesSet["movie"] = struct{}{}
				typesSet["series"] = struct{}{}
			}

			if !ok {
				// Site search fanout: cap at 3s total. Results arriving after the deadline are discarded.
				siteCh := make(chan []embySiteSearchHit, 1)
				go func() { siteCh <- embySearchSitesHits(database, u, scoreTerm, 3*time.Second, 0) }()

				results, err := embyTMDBSearchMulti(database, searchTerm)
				if err != nil {
					embyDebugPrintf("[emby][search] tmdb search failed term=%q err=%q", searchTerm, err.Error())
					results = nil
				}

				siteHits := []embySiteSearchHit{}
				select {
				case siteHits = <-siteCh:
				case <-time.After(3 * time.Second):
				}
				if siteHits == nil {
					siteHits = []embySiteSearchHit{}
				}

				// Sort TMDB items by the same match score as frontend (TMDB always comes first).
				type tmdbRow struct {
					Item     embyTMDBSearchItem
					Score    int
					TitleLen int
					Seq      int
				}
				rows := make([]tmdbRow, 0, len(results))
				seq := 0
				for _, it := range results {
					if it.ID <= 0 || strings.TrimSpace(it.Title) == "" {
						continue
					}
					mt := strings.ToLower(strings.TrimSpace(it.MediaType))
					if mt == "tv" {
						mt = "series"
					}
					if _, ok := typesSet[mt]; !ok {
						continue
					}
					seq++
					rows = append(rows, tmdbRow{
						Item:     it,
						Score:    embyComputeMatchScore(scoreTerm, it.Title),
						TitleLen: embyTitleLenForSort(it.Title),
						Seq:      seq,
					})
				}
				sort.SliceStable(rows, func(i, j int) bool {
					a := rows[i]
					b := rows[j]
					if a.Score != b.Score {
						return a.Score > b.Score
					}
					if a.TitleLen != b.TitleLen {
						return a.TitleLen < b.TitleLen
					}
					return a.Seq < b.Seq
				})
				tmdbSorted = make([]embyTMDBSearchItem, 0, len(rows))
				for _, r := range rows {
					tmdbSorted = append(tmdbSorted, r.Item)
				}

				// Filter site hits by IncludeItemTypes. Site cards are emitted as Series.
				if _, ok := typesSet["series"]; !ok {
					siteHits = []embySiteSearchHit{}
				}
				siteSorted = siteHits

				SearchCachePut(cacheKey, SearchCacheEntry[embyTMDBSearchItem, embySiteSearchHit]{TMDB: tmdbSorted, Sites: siteSorted}, 30*time.Minute)
				if strings.TrimSpace(scoreTerm) != "" && strings.TrimSpace(scoreTerm) != strings.TrimSpace(searchTerm) {
					embyDebugPrintf("[emby][search] term=%q folded=%q tmdb=%d sites=%d cost=%s", searchTerm, scoreTerm, len(tmdbSorted), len(siteSorted), time.Since(startAt).String())
				} else {
					embyDebugPrintf("[emby][search] term=%q tmdb=%d sites=%d cost=%s", searchTerm, len(tmdbSorted), len(siteSorted), time.Since(startAt).String())
				}
			}

			total := len(tmdbSorted) + len(siteSorted)
			if startIndex > total {
				startIndex = total
			}
			// Infuse search UI often doesn't page; return full results so it can show the complete list.
			effectiveLimit := limit
			if embyIsInfuseClient(r) {
				effectiveLimit = 1 << 30
			}
			end := startIndex + effectiveLimit
			if end > total {
				end = total
			}

			page := make([]map[string]any, 0, maxInt(0, end-startIndex))
			for idx := startIndex; idx < end; idx++ {
				rank := idx
				if idx < len(tmdbSorted) {
					it := tmdbSorted[idx]
					base := embyBuildBaseItemFromSearch(it)
					if base == nil {
						continue
					}
					base["ParentId"] = nil
					id, _ := base["Id"].(string)

					// Some clients treat movie search results as playable items and rely on MediaSources.
					isFolder, _ := base["IsFolder"].(bool)
					if !isFolder && strings.TrimSpace(id) != "" {
						mediaPath := embyBuildMediaPath(id, "mp4")
						mediaSourceID := embyStableHex32(id)
						base["LocationType"] = "FileSystem"
						base["Path"] = mediaPath
						base["Container"] = "mp4,m4v"
						base["MediaSources"] = []map[string]any{
							{
								"Protocol":                "File",
								"Id":                      mediaSourceID,
								"MediaSourceId":           mediaSourceID,
								"Path":                    mediaPath,
								"Type":                    "Default",
								"Container":               "mp4",
								"Size":                    0,
								"Name":                    base["Name"],
								"IsRemote":                false,
								"ETag":                    mediaSourceID,
								"RunTimeTicks":            0,
								"ReadAtNativeFramerate":   false,
								"IgnoreDts":               false,
								"IgnoreIndex":             false,
								"GenPtsInput":             false,
								"SupportsTranscoding":     true,
								"SupportsDirectStream":    true,
								"SupportsDirectPlay":      true,
								"IsInfiniteStream":        false,
								"RequiresOpening":         false,
								"RequiresClosing":         false,
								"RequiresLooping":         false,
								"SupportsProbing":         true,
								"VideoType":               "VideoFile",
								"MediaStreams":            []any{},
								"MediaAttachments":        []any{},
								"Formats":                 []any{},
								"Bitrate":                 0,
								"RequiredHttpHeaders":     map[string]any{},
								"DefaultAudioStreamIndex": 0,
							},
						}
						base["AlternateMediaSources"] = []any{}
					}

					embyEnsureInfuseItemFields(base, id, fieldsParam, serverID)
					base["SortName"] = fmt.Sprintf("%06d", rank)
					page = append(page, base)
					continue
				}

				h := siteSorted[idx-len(tmdbSorted)]
				if strings.TrimSpace(h.Name) == "" {
					continue
				}
				siteName := strings.TrimSpace(h.SiteName)
				if siteName == "" {
					siteName = strings.TrimSpace(h.SiteKey)
				}

				// Persist poster/remark for short site ids (images resolved via DB).
				siteVideoID := int64(0)
				if database != nil {
					id, _ := database.UpsertSiteVideo(strings.TrimSpace(h.SiteKey), strings.TrimSpace(h.VideoID), strings.TrimSpace(h.Name), strings.TrimSpace(h.Pic), strings.TrimSpace(h.Remark), time.Now().Unix())
					siteVideoID = id
				}

				siteID := embyBuildSiteSeriesIDV2(siteVideoID)
				if strings.TrimSpace(siteID) == "" {
					continue
				}

				obj := map[string]any{
					"Id":                siteID,
					"Name":              strings.TrimSpace(h.Name),
					"Type":              "Series",
					"IsFolder":          true,
					"ProductionYear":    0,
					"ImageTags":         map[string]any{"Primary": "site"},
					"BackdropImageTags": []string{},
					"ProviderIds":       map[string]any{},
					"Overview":          strings.TrimSpace(h.Remark),
				}
				if siteName != "" {
					obj["ProductionLocations"] = []string{siteName}
				}
				obj["ParentId"] = nil
				embyEnsureInfuseItemFields(obj, siteID, fieldsParam, serverID)
				obj["SortName"] = fmt.Sprintf("%06d", rank)
				page = append(page, obj)
			}

			page = embyFilterItemsByExcludeLocationTypes(page, excludeLocationTypes)
			writeJSON(w, 200, embyPagedItems(page, startIndex, total))
			return
		}

		if parent == "" {
			embyNotFound(w)
			return
		}

		// Default: empty list so clients can still use search UI.
		writeJSON(w, 200, embyPagedEmpty(0))
		return
	}

	embyNotFound(w)
}

func embyStableKeyDigits(s string) string {
	h := embyStableHex32(s)
	if len(h) > 12 {
		h = h[:12]
	}
	v, err := strconv.ParseUint(h, 16, 64)
	if err != nil {
		return embyStableHex32(s)
	}
	return fmt.Sprintf("%d", v)
}

func embyEnsureInfuseItemFields(obj map[string]any, itemID string, fieldsParam string, serverID string) {
	if obj == nil {
		return
	}

	// Ensure a stable Id; prefer the response Id if present.
	id := itemID
	if v, ok := obj["Id"].(string); ok && strings.TrimSpace(v) != "" {
		id = strings.TrimSpace(v)
	} else if strings.TrimSpace(id) != "" {
		obj["Id"] = strings.TrimSpace(id)
	}

	name, _ := obj["Name"].(string)
	name = strings.TrimSpace(name)
	if name != "" {
		if _, ok := obj["SortName"]; !ok {
			obj["SortName"] = name
		}
	}

	if _, ok := obj["Etag"]; !ok && strings.TrimSpace(id) != "" {
		obj["Etag"] = embyStableEtag(id)
	}
	if _, ok := obj["ServerId"]; !ok && strings.TrimSpace(serverID) != "" {
		obj["ServerId"] = serverID
	}

	// Provide a non-empty Path for non-virtual items; some clients treat missing Path as invalid.
	if _, ok := obj["Path"]; !ok && strings.TrimSpace(id) != "" {
		obj["Path"] = "meowfilm://" + strings.TrimSpace(id)
	}

	// Ensure LocationType for browsable items.
	if _, ok := obj["LocationType"]; !ok {
		obj["LocationType"] = "Remote"
	}

	// If the client requests additional fields, ensure type-correct empty defaults.
	fields := embyFieldsSet(fieldsParam)

	nowISO := time.Now().UTC().Format(time.RFC3339)
	if _, want := fields["DateCreated"]; want {
		if _, ok := obj["DateCreated"]; !ok {
			obj["DateCreated"] = nowISO
		}
	}
	if _, want := fields["Genres"]; want {
		if _, ok := obj["Genres"]; !ok {
			obj["Genres"] = []string{}
		}
	}
	if _, want := fields["MediaSources"]; want {
		if _, ok := obj["MediaSources"]; !ok {
			obj["MediaSources"] = []any{}
		}
	}
	if _, want := fields["AlternateMediaSources"]; want {
		if _, ok := obj["AlternateMediaSources"]; !ok {
			obj["AlternateMediaSources"] = []any{}
		}
	}
	if _, want := fields["Overview"]; want {
		if _, ok := obj["Overview"]; !ok {
			obj["Overview"] = ""
		}
	}
	if _, want := fields["ParentId"]; want {
		if _, ok := obj["ParentId"]; !ok {
			obj["ParentId"] = ""
		}
	}
	if _, want := fields["ProviderIds"]; want {
		if _, ok := obj["ProviderIds"]; !ok {
			obj["ProviderIds"] = map[string]any{}
		}
	}
	if _, want := fields["ProductionLocations"]; want {
		if _, ok := obj["ProductionLocations"]; !ok {
			obj["ProductionLocations"] = []string{}
		}
	}
	if _, want := fields["RecursiveItemCount"]; want {
		if _, ok := obj["RecursiveItemCount"]; !ok {
			obj["RecursiveItemCount"] = 0
		}
	}
	if _, want := fields["ChildCount"]; want {
		if _, ok := obj["ChildCount"]; !ok {
			obj["ChildCount"] = 0
		}
	}
	if _, want := fields["SortName"]; want {
		if _, ok := obj["SortName"]; !ok {
			obj["SortName"] = name
		}
	}
	if _, want := fields["Path"]; want {
		if _, ok := obj["Path"]; !ok && strings.TrimSpace(id) != "" {
			obj["Path"] = "meowfilm://" + strings.TrimSpace(id)
		}
	}
}

func embyBuildDoubanHotListItems(database *db.DB, kind string, category string, hotType string, startIndex int, limit int, serverID string, parentID string) []map[string]any {
	list, err := embyDoubanFetchRecentHot(database, kind, category, hotType, startIndex, limit)
	if err != nil || len(list) == 0 {
		return []map[string]any{}
	}

	type resolved struct {
		item   embyDoubanHotItem
		tmdbID int
	}
	res := make([]resolved, len(list))

	jobs := make(chan int, len(list))
	var wg sync.WaitGroup
	workers := 4
	for wkr := 0; wkr < workers; wkr++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for idx := range jobs {
				it := list[idx]
				tid, _ := embyResolveTMDBForDouban(database, kind, it.DoubanID, it.Title, it.Year)
				res[idx] = resolved{item: it, tmdbID: tid}
			}
		}()
	}
	for i := range list {
		jobs <- i
	}
	close(jobs)
	wg.Wait()

	out := make([]map[string]any, 0, len(res))
	nowISO := time.Now().UTC().Format(time.RFC3339)
	parent := strings.TrimSpace(parentID)
	for _, rr := range res {
		it := rr.item
		if it.DoubanID == "" || strings.TrimSpace(it.Title) == "" {
			continue
		}
		name := strings.TrimSpace(it.Title)
		year := it.Year
		tid := rr.tmdbID
		rating := 0.0
		if strings.TrimSpace(it.Rate) != "" {
			if f, err := strconv.ParseFloat(strings.TrimSpace(it.Rate), 64); err == nil && f > 0 {
				rating = f
			}
		}

		if kind == "movie" {
			id := embyBuildDoubanMovieID(it.DoubanID)
			provider := map[string]any{"Douban": it.DoubanID}
			imgTags := map[string]any{}
			if tid > 0 {
				id = embyBuildMovieID(tid)
				provider["Tmdb"] = strconv.Itoa(tid)
				imgTags["Primary"] = "tmdb"
			}
			out = append(out, map[string]any{
				"Id":                      id,
				"Name":                    name,
				"SortName":                name,
				"Type":                    "Movie",
				"MediaType":               "Video",
				"LocationType":            "Remote",
				"IsFolder":                false,
				"ProductionYear":          year,
				"DateCreated":             nowISO,
				"Etag":                    embyStableEtag(id),
				"Genres":                  []string{},
				"Overview":                "",
				"ParentId":                parent,
				"Path":                    "meowfilm://" + id,
				"RecursiveItemCount":      0,
				"ChildCount":              0,
				"MediaSources":            []any{},
				"AlternateMediaSources":   []any{},
				"CommunityRating":         rating,
				"ProviderIds":             provider,
				"ImageTags":               imgTags,
				"BackdropImageTags":       []string{"tmdb"},
				"ServerId":                serverID,
				"UserData":                map[string]any{"Played": false},
				"PrimaryImageAspectRatio": 0.6666667,
			})
		} else {
			id := embyBuildDoubanSeriesID(it.DoubanID)
			provider := map[string]any{"Douban": it.DoubanID}
			imgTags := map[string]any{}
			if tid > 0 {
				id = embyBuildSeriesID(tid)
				provider["Tmdb"] = strconv.Itoa(tid)
				imgTags["Primary"] = "tmdb"
			}
			out = append(out, map[string]any{
				"Id":                      id,
				"Name":                    name,
				"SortName":                name,
				"Type":                    "Series",
				"MediaType":               "Video",
				"LocationType":            "Remote",
				"IsFolder":                true,
				"ProductionYear":          year,
				"DateCreated":             nowISO,
				"Etag":                    embyStableEtag(id),
				"Genres":                  []string{},
				"Overview":                "",
				"ParentId":                parent,
				"Path":                    "meowfilm://" + id,
				"RecursiveItemCount":      0,
				"ChildCount":              0,
				"MediaSources":            []any{},
				"AlternateMediaSources":   []any{},
				"CommunityRating":         rating,
				"ProviderIds":             provider,
				"ImageTags":               imgTags,
				"BackdropImageTags":       []string{"tmdb"},
				"ServerId":                serverID,
				"UserData":                map[string]any{"Played": false},
				"PrimaryImageAspectRatio": 0.6666667,
			})
		}
	}
	return out
}

func embyFilterItemsByExcludeLocationTypes(items []map[string]any, excludeLocationTypes string) []map[string]any {
	exclude := strings.TrimSpace(excludeLocationTypes)
	if exclude == "" || len(items) == 0 {
		return items
	}
	set := map[string]struct{}{}
	for _, part := range strings.Split(exclude, ",") {
		p := strings.ToLower(strings.TrimSpace(part))
		if p != "" {
			set[p] = struct{}{}
		}
	}
	if len(set) == 0 {
		return items
	}
	out := make([]map[string]any, 0, len(items))
	for _, it := range items {
		if it == nil {
			continue
		}
		lt, _ := it["LocationType"].(string)
		if lt == "" {
			out = append(out, it)
			continue
		}
		if _, ok := set[strings.ToLower(strings.TrimSpace(lt))]; ok {
			continue
		}
		out = append(out, it)
	}
	return out
}

func embyStableEtag(id string) string {
	raw := strings.TrimSpace(id)
	if raw == "" {
		return ""
	}
	sum := sha1.Sum([]byte(raw))
	return hex.EncodeToString(sum[:])
}

func embyBuildViewFolderItem(serverID string, id string, name string, collectionType string) map[string]any {
	etag := embyStableEtag(strings.TrimSpace(id) + "|" + strings.TrimSpace(name) + "|" + strings.TrimSpace(collectionType))
	return map[string]any{
		"Id":                id,
		"Name":              name,
		"SortName":          name,
		"Type":              "CollectionFolder",
		"CollectionType":    collectionType,
		"IsFolder":          true,
		"ServerId":          serverID,
		"Etag":              etag,
		"UserData":          map[string]any{"Played": false},
		"ImageTags":         map[string]any{},
		"BackdropImageTags": []any{},
	}
}

func embyIssueToken(database *db.DB, userID int64) (token string, exp time.Time, err error) {
	if database == nil || userID <= 0 {
		return "", time.Time{}, sql.ErrNoRows
	}
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		return "", time.Time{}, err
	}
	token = base64.RawURLEncoding.EncodeToString(b)
	now := time.Now()
	exp = now.Add(30 * 24 * time.Hour)
	if err := database.InsertToken(token, userID, exp); err != nil {
		return "", time.Time{}, err
	}
	return token, exp, nil
}

func int64ToStr(v int64) string {
	return strconv.FormatInt(v, 10)
}
