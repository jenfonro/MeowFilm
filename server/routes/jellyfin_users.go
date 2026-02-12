package routes

import (
	"crypto/rand"
	"crypto/sha1"
	"database/sql"
	"encoding/base64"
	"encoding/hex"
	"net/http"
	"os"
	"strconv"
	"strings"
	"sync"
	"time"

	"golang.org/x/crypto/bcrypt"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleJellyfinUsers(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	debug := strings.TrimSpace(os.Getenv("MEOWFILM_JELLYFIN_DEBUG_LOG")) == "1"

	// POST /Users/AuthenticateByName
	if len(parts) >= 1 && strings.EqualFold(parts[0], "AuthenticateByName") && r.Method == http.MethodPost {
		var body struct {
			Username string `json:"Username"`
			Pw       string `json:"Pw"`
			Password string `json:"Password"`
		}
		if err := readJSON(r, &body); err != nil {
			jellyfinWriteError(w, 400, "Invalid JSON")
			return
		}
		username := strings.TrimSpace(body.Username)
		password := body.Pw
		if password == "" {
			password = body.Password
		}
		if username == "" || password == "" {
			jellyfinWriteError(w, 400, "用户名或密码不能为空")
			return
		}

		var (
			id     int64
			hashed string
			role   string
			status string
		)
		err := database.SQL().QueryRow(`SELECT id, password, role, status FROM users WHERE username=? LIMIT 1`, username).Scan(&id, &hashed, &role, &status)
		if err != nil {
			if err == sql.ErrNoRows {
				jellyfinWriteError(w, 401, "用户名或密码错误")
				return
			}
			jellyfinWriteError(w, 500, "请求失败")
			return
		}
		if strings.TrimSpace(status) != "active" {
			jellyfinWriteError(w, 403, "该账户已禁用")
			return
		}
		if bcrypt.CompareHashAndPassword([]byte(hashed), []byte(password)) != nil {
			jellyfinWriteError(w, 401, "用户名或密码错误")
			return
		}

		token, exp, err := jellyfinIssueToken(database, id)
		if err != nil || token == "" {
			jellyfinWriteError(w, 500, "请求失败")
			return
		}

		writeJSON(w, 200, map[string]any{
			"User": map[string]any{
				"Id":   int64ToStr(id),
				"Name": username,
				"Policy": map[string]any{
					"IsAdministrator": strings.TrimSpace(role) == "admin",
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
		u, ok := jellyfinRequireUser(w, r, database)
		if !ok {
			return
		}
		if strings.TrimSpace(parts[0]) != strings.TrimSpace(u.ID) {
			jellyfinWriteError(w, 404, "Not found")
			return
		}
		// Jellyfin expects SpecialViewOptionDto[].
		// Return a minimal, static set to satisfy client probes.
		writeJSON(w, 200, []map[string]any{
			{"Name": "TMDB 剧集", "Id": "view_tmdb_tv"},
			{"Name": "TMDB 电影", "Id": "view_tmdb_movies"},
		})
		return
	}

	// GET /Users/{id}
	if len(parts) == 1 && parts[0] != "" && r.Method == http.MethodGet {
		u, ok := jellyfinRequireUser(w, r, database)
		if !ok {
			return
		}
		if strings.TrimSpace(parts[0]) == strings.TrimSpace(u.ID) {
			writeJSON(w, 200, map[string]any{
				"Id":   u.ID,
				"Name": u.Username,
				"Policy": map[string]any{
					"IsAdministrator": strings.TrimSpace(u.Role) == "admin",
				},
			})
			return
		}
		jellyfinWriteError(w, 404, "Not found")
		return
	}

	// GET /Users/{id}/Views
	if len(parts) >= 2 && strings.EqualFold(parts[1], "Views") && r.Method == http.MethodGet {
		_, ok := jellyfinRequireUser(w, r, database)
		if !ok {
			return
		}
		writeJSON(w, 200, map[string]any{
			"Items": []map[string]any{
				jellyfinBuildViewFolderItem(serverID, "view_tmdb_tv", "TMDB 剧集", "tvshows"),
				jellyfinBuildViewFolderItem(serverID, "view_tmdb_movies", "TMDB 电影", "movies"),
			},
			"TotalRecordCount": 2,
		})
		return
	}

	// GET /Users/{id}/Items/Latest
	if len(parts) == 3 && strings.EqualFold(parts[1], "Items") && strings.EqualFold(parts[2], "Latest") && r.Method == http.MethodGet {
		u, ok := jellyfinRequireUser(w, r, database)
		if !ok {
			return
		}
		if strings.TrimSpace(parts[0]) != strings.TrimSpace(u.ID) {
			jellyfinWriteError(w, 404, "Not found")
			return
		}
		startIndex, _ := strconv.Atoi(jellyfinQueryGetCI(r, "StartIndex"))
		if startIndex < 0 {
			startIndex = 0
		}
		limit, _ := strconv.Atoi(jellyfinQueryGetCI(r, "Limit"))
		if limit <= 0 {
			limit = 24
		}
		if limit > 60 {
			limit = 60
		}

		parentID := strings.TrimSpace(jellyfinQueryGetCI(r, "ParentId"))
		excludeLocationTypes := strings.TrimSpace(jellyfinQueryGetCI(r, "ExcludeLocationTypes"))
		if parentID == "" {
			// Contract: this endpoint is typically scoped by ParentId; avoid guessing.
			writeJSON(w, 200, []any{})
			return
		}

		kind := ""
		category := ""
		hotType := ""
		if parentID == "view_tmdb_tv" {
			kind = "tv"
			category = "tv"
			hotType = "tv"
		} else if parentID == "view_tmdb_movies" {
			kind = "movie"
			category = "热门"
			hotType = "全部"
		} else {
			writeJSON(w, 200, []any{})
			return
		}

		items := jellyfinBuildDoubanHotListItems(database, kind, category, hotType, startIndex, limit, serverID, parentID)

		items = jellyfinFilterItemsByExcludeLocationTypes(items, excludeLocationTypes)

		if debug && len(items) == 0 {
			jellyfinDebugPrintf("[jellyfin][debug] latest empty kind=%s parentId=%q", kind, parentID)
		}

		// Jellyfin/Emby typically returns an array for this endpoint.
		writeJSON(w, 200, items)
		return
	}

	// GET /Users/{id}/Items/Resume
	if len(parts) == 3 && strings.EqualFold(parts[1], "Items") && strings.EqualFold(parts[2], "Resume") && r.Method == http.MethodGet {
		u, ok := jellyfinRequireUser(w, r, database)
		if !ok {
			return
		}
		if strings.TrimSpace(parts[0]) != strings.TrimSpace(u.ID) {
			jellyfinWriteError(w, 404, "Not found")
			return
		}
		writeJSON(w, 200, map[string]any{
			"Items":            []any{},
			"StartIndex":       0,
			"TotalRecordCount": 0,
		})
		return
	}

	// GET /Users/{id}/Items/{itemId}
	// Some clients use this endpoint to fetch the details of a view/folder item before browsing.
	if len(parts) == 3 && strings.EqualFold(parts[1], "Items") && r.Method == http.MethodGet {
		u, ok := jellyfinRequireUser(w, r, database)
		if !ok {
			return
		}
		if strings.TrimSpace(parts[0]) != strings.TrimSpace(u.ID) {
			jellyfinWriteError(w, 404, "Not found")
			return
		}
		itemID := strings.TrimSpace(parts[2])
		if itemID == "" {
			http.NotFound(w, r)
			return
		}
		if itemID == "view_tmdb_tv" {
			writeJSON(w, 200, jellyfinBuildViewFolderItem(serverID, itemID, "TMDB 剧集", "tvshows"))
			return
		}
		if itemID == "view_tmdb_movies" {
			writeJSON(w, 200, jellyfinBuildViewFolderItem(serverID, itemID, "TMDB 电影", "movies"))
			return
		}
		obj, err := jellyfinBuildItem(database, itemID)
		if err != nil {
			jellyfinWriteError(w, 502, err.Error())
			return
		}
		if obj == nil {
			http.NotFound(w, r)
			return
		}
		// Ensure UserData exists; Infuse uses it.
		if _, ok := obj["UserData"]; !ok {
			obj["UserData"] = map[string]any{"Played": false}
		}
		writeJSON(w, 200, obj)
		return
	}

	// GET /Users/{id}/Items?ParentId=view_...
	if len(parts) >= 2 && strings.EqualFold(parts[1], "Items") && r.Method == http.MethodGet {
		_, ok := jellyfinRequireUser(w, r, database)
		if !ok {
			return
		}
		parent := jellyfinQueryGetCI(r, "ParentId")
		excludeLocationTypes := strings.TrimSpace(jellyfinQueryGetCI(r, "ExcludeLocationTypes"))

		if parent == "" {
			http.NotFound(w, r)
			return
		}

		// For Infuse MVP: populate the two main views from Douban "recent hot",
		// then lazily resolve/cache a TMDB id for stable metadata & posters.
		if parent == "view_tmdb_movies" || parent == "view_tmdb_tv" {
			startIndex, _ := strconv.Atoi(jellyfinQueryGetCI(r, "StartIndex"))
			if startIndex < 0 {
				startIndex = 0
			}
			limit, _ := strconv.Atoi(jellyfinQueryGetCI(r, "Limit"))
			if limit <= 0 {
				limit = 24
			}
			if limit > 60 {
				limit = 60
			}

			kind := "movie"
			category := "热门"
			hotType := "全部"
			if parent == "view_tmdb_tv" {
				kind = "tv"
				category = "tv"
				hotType = "tv"
			}

			out := jellyfinBuildDoubanHotListItems(database, kind, category, hotType, startIndex, limit, serverID, parent)
			out = jellyfinFilterItemsByExcludeLocationTypes(out, excludeLocationTypes)
			if debug && len(out) == 0 {
				jellyfinDebugPrintf("[jellyfin][debug] users.items empty parent=%q kind=%s start=%d limit=%d", parent, kind, startIndex, limit)
			}

			writeJSON(w, 200, map[string]any{
				"Items":            out,
				"StartIndex":       startIndex,
				"TotalRecordCount": len(out),
			})
			return
		}

		// Default: empty list so clients can still use search UI.
		writeJSON(w, 200, map[string]any{
			"Items":            []any{},
			"StartIndex":       0,
			"TotalRecordCount": 0,
		})
		return
	}

	http.NotFound(w, r)
}

func jellyfinBuildDoubanHotListItems(database *db.DB, kind string, category string, hotType string, startIndex int, limit int, serverID string, parentID string) []map[string]any {
	list, err := jellyfinDoubanFetchRecentHot(database, kind, category, hotType, startIndex, limit)
	if err != nil || len(list) == 0 {
		return []map[string]any{}
	}

	type resolved struct {
		item   jellyfinDoubanHotItem
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
				tid, _ := jellyfinResolveTMDBForDouban(database, kind, it.DoubanID, it.Title, it.Year)
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
			id := jellyfinBuildDoubanMovieID(it.DoubanID)
			provider := map[string]any{"Douban": it.DoubanID}
			imgTags := map[string]any{}
			if tid > 0 {
				id = jellyfinBuildMovieID(tid)
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
				"Etag":                    jellyfinStableEtag(id),
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
				"BackdropImageTags":       []any{},
				"ServerId":                serverID,
				"UserData":                map[string]any{"Played": false},
				"PrimaryImageAspectRatio": 0.6666667,
			})
		} else {
			id := jellyfinBuildDoubanSeriesID(it.DoubanID)
			provider := map[string]any{"Douban": it.DoubanID}
			imgTags := map[string]any{}
			if tid > 0 {
				id = jellyfinBuildSeriesID(tid)
				provider["Tmdb"] = strconv.Itoa(tid)
				imgTags["Primary"] = "tmdb"
			}
			out = append(out, map[string]any{
				"Id":                      id,
				"Name":                    name,
				"SortName":                name,
				"Type":                    "Series",
				"LocationType":            "Remote",
				"IsFolder":                true,
				"ProductionYear":          year,
				"DateCreated":             nowISO,
				"Etag":                    jellyfinStableEtag(id),
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
				"BackdropImageTags":       []any{},
				"ServerId":                serverID,
				"UserData":                map[string]any{"Played": false},
				"PrimaryImageAspectRatio": 0.6666667,
			})
		}
	}
	return out
}

func jellyfinFilterItemsByExcludeLocationTypes(items []map[string]any, excludeLocationTypes string) []map[string]any {
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

func jellyfinStableEtag(id string) string {
	raw := strings.TrimSpace(id)
	if raw == "" {
		return ""
	}
	sum := sha1.Sum([]byte(raw))
	return hex.EncodeToString(sum[:])
}

func jellyfinBuildViewFolderItem(serverID string, id string, name string, collectionType string) map[string]any {
	return map[string]any{
		"Id":                id,
		"Name":              name,
		"Type":              "CollectionFolder",
		"CollectionType":    collectionType,
		"IsFolder":          true,
		"ServerId":          serverID,
		"UserData":          map[string]any{"Played": false},
		"ImageTags":         map[string]any{},
		"BackdropImageTags": []any{},
	}
}

func jellyfinIssueToken(database *db.DB, userID int64) (token string, exp time.Time, err error) {
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
	_, err = database.SQL().Exec(`INSERT INTO auth_tokens(token, user_id, created_at, expires_at) VALUES (?,?,?,?)`,
		token, userID, now.UnixMilli(), exp.UnixMilli())
	if err != nil {
		return "", time.Time{}, err
	}
	return token, exp, nil
}

func int64ToStr(v int64) string {
	return strconv.FormatInt(v, 10)
}
