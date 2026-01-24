package routes

import (
	"io"
	"math"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/TV_Server/internal/auth"
	"github.com/jenfonro/TV_Server/internal/db"
)

type userSettingsRow struct {
	CatAPIBase      string
	CatAPIKey       string
	CatProxy        string
	ThreadCount     int
	SearchOrder     string
	SearchCoverSite string
	CatSites        string
	CatSiteStatus   string
	CatSiteHome     string
	CatSiteOrder    string
	CatSiteAvail    string
}

func APIHandler(database *db.DB, authMw *auth.Auth) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		path := strings.TrimPrefix(r.URL.Path, "/api")
		switch path {
		case "/home":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIHome(w, r, database)
			})).ServeHTTP(w, r)
		case "/bootstrap":
			handleAPIBootstrap(w, r, database)
		case "/video/sites":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIVideoSites(w, r, database)
			})).ServeHTTP(w, r)
		case "/login":
			if r.Method != http.MethodPost {
				methodNotAllowed(w)
				return
			}
			parseForm(r)
			username := r.FormValue("username")
			password := r.FormValue("password")
			// support JSON body too
			if strings.TrimSpace(username) == "" && strings.TrimSpace(password) == "" && strings.Contains(r.Header.Get("Content-Type"), "application/json") {
				var body struct {
					Username string `json:"username"`
					Password string `json:"password"`
				}
				_ = readJSONLoose(r, &body)
				username = body.Username
				password = body.Password
			}
			status, msg := authMw.Login(w, username, password)
			if msg != "" {
				writeJSON(w, status, map[string]any{"success": false, "message": msg})
				return
			}
			writeJSON(w, 200, map[string]any{"success": true})
		case "/logout":
			if r.Method != http.MethodGet {
				methodNotAllowed(w)
				return
			}
			authMw.Logout(w, r)
			http.Redirect(w, r, "/", http.StatusFound)
		case "/searchhistory":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPISearchHistory(w, r, database)
			})).ServeHTTP(w, r)
		case "/playhistory/one":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIPlayHistoryOne(w, r, database)
			})).ServeHTTP(w, r)
		case "/playhistory":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIPlayHistory(w, r, database)
			})).ServeHTTP(w, r)
		case "/favorites":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIFavorites(w, r, database)
			})).ServeHTTP(w, r)
		case "/favorites/status":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIFavoritesStatus(w, r, database)
			})).ServeHTTP(w, r)
		case "/favorites/toggle":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIFavoritesToggle(w, r, database)
			})).ServeHTTP(w, r)
		case "/user/settings":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIUserSettings(w, r, database)
			})).ServeHTTP(w, r)
		case "/user/pan-login-settings":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIUserPanLoginSettings(w, r, database)
			})).ServeHTTP(w, r)
		case "/user/sites":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIUserSites(w, r, database)
			})).ServeHTTP(w, r)
		case "/user/sites/availability":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIUserSitesAvailability(w, r, database)
			})).ServeHTTP(w, r)
		case "/user/sites/status":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIUserSitesStatus(w, r, database)
			})).ServeHTTP(w, r)
		case "/user/sites/home":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIUserSitesHome(w, r, database)
			})).ServeHTTP(w, r)
		case "/user/sites/order":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIUserSitesOrder(w, r, database)
			})).ServeHTTP(w, r)
		case "/douban/image":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIDoubanImage(w, r)
			})).ServeHTTP(w, r)
		case "/catpawopen/spider/home", "/catpawopen/spider/category", "/catpawopen/spider/detail", "/catpawopen/spider/play", "/catpawopen/spider/search":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				writeJSON(w, http.StatusGone, map[string]any{"success": false, "message": "CatPawOpen 接口异常"})
			})).ServeHTTP(w, r)
		default:
			http.NotFound(w, r)
		}
	})
}

func handleAPIBootstrap(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	siteName := database.GetSetting("site_name")
	u := auth.CurrentUser(r)
	if u == nil || u.Status != "active" {
		writeJSON(w, 200, map[string]any{"authenticated": false, "siteName": siteName})
		return
	}

	page := strings.TrimSpace(r.URL.Query().Get("page"))
	settings := map[string]any{}
	if page == "index" || page == "douban" || page == "play" || page == "site" || page == "dashboard" {
		if page != "dashboard" {
			settings["doubanDataProxy"] = defaultString(database.GetSetting("douban_data_proxy"), "direct")
			settings["doubanDataCustom"] = database.GetSetting("douban_data_custom")
			settings["doubanImgProxy"] = defaultString(database.GetSetting("douban_img_proxy"), "direct-browser")
			settings["doubanImgCustom"] = database.GetSetting("douban_img_custom")
			settings["videoSourceApiBase"] = database.GetSetting("video_source_api_base")
			settings["catPawOpenApiBase"] = database.GetSetting("catpawopen_api_base")
			settings["openListApiBase"] = database.GetSetting("openlist_api_base")
			settings["openListToken"] = database.GetSetting("openlist_token")
			settings["openListQuarkTvMode"] = strings.TrimSpace(database.GetSetting("openlist_quark_tv_mode")) == "1"
			settings["openListQuarkTvMount"] = database.GetSetting("openlist_quark_tv_mount")
			settings["goProxyEnabled"] = strings.TrimSpace(database.GetSetting("goproxy_enabled")) == "1"
			settings["goProxyAutoSelect"] = strings.TrimSpace(database.GetSetting("goproxy_auto_select")) == "1"
			settings["goProxyServers"] = normalizeGoProxyServers(database.GetSetting("goproxy_servers"))

			settings["magicEpisodeRules"] = parseJSONStringArray(database.GetSetting("magic_episode_rules"))
			cleanRules := parseJSONStringArray(database.GetSetting("magic_episode_clean_regex_rules"))
			settings["magicEpisodeCleanRegexRules"] = cleanRules
			if len(cleanRules) > 0 {
				settings["magicEpisodeCleanRegex"] = cleanRules[0]
			} else {
				settings["magicEpisodeCleanRegex"] = strings.TrimSpace(database.GetSetting("magic_episode_clean_regex"))
			}
			settings["magicAggregateRules"] = parseJSONStringArray(database.GetSetting("magic_aggregate_rules"))
			settings["magicAggregateRegexRules"] = parseJSONStringArray(database.GetSetting("magic_aggregate_regex_rules"))

			var (
				userCatBase  string
				userCatKey   string
				userCatProxy string
				threadCount  int
				searchOrder  string
				searchCover  string
			)
			_ = database.SQL().QueryRow(`
				SELECT cat_api_base, cat_api_key, cat_proxy, search_thread_count, cat_search_order, cat_search_cover_site
				FROM users WHERE id=? LIMIT 1
			`, u.ID).Scan(&userCatBase, &userCatKey, &userCatProxy, &threadCount, &searchOrder, &searchCover)
			if threadCount < 1 {
				threadCount = 5
			}
			settings["userCatPawOpenApiBase"] = userCatBase
			settings["userCatPawOpenApiKey"] = userCatKey
			settings["userCatPawOpenProxy"] = userCatProxy
			settings["searchThreadCount"] = threadCount

				if u.Role == "user" {
					settings["searchSiteOrder"] = parseJSONStringArray(searchOrder)
					settings["searchCoverSite"] = strings.TrimSpace(searchCover)
				} else {
					sites := mergeVideoSourceSites(database)
					settings["searchSiteOrder"] = parseJSONStringArray(database.GetSetting("video_source_site_order"))
					settings["searchCoverSite"] = resolveSearchCoverSite(sites, database.GetSetting("video_source_search_cover_site"))
				}
			}
		}

	var userCount int
	if page == "dashboard" && u.Role == "admin" {
		_ = database.SQL().QueryRow(`SELECT COUNT(1) FROM users`).Scan(&userCount)
	}

	if page == "index" || page == "douban" || page == "play" || page == "site" {
		settings["homeSites"] = fetchUserHomeSites(database, u)
	}

	writeJSON(w, 200, map[string]any{
		"authenticated": true,
		"siteName":       siteName,
		"user":           map[string]any{"username": u.Username, "role": u.Role},
		"settings":       settings,
		"users":          []any{},
		"userCount":      userCount,
	})
}

func handleAPIHome(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	u := auth.CurrentUser(r)
	if u == nil {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"error": "Unauthorized"})
		return
	}

	q := r.URL.Query()
	includePlayHistory := parseBoolQuery(q.Get("includePlayHistory"), true)
	includeFavorites := parseBoolQuery(q.Get("includeFavorites"), true)
	includePanLoginSettings := parseBoolQuery(q.Get("includePanLoginSettings"), true)
	playHistoryLimit := parseIntQuery(q.Get("playHistoryLimit"), 20, 1, 50)
	favoritesLimit := parseIntQuery(q.Get("favoritesLimit"), 50, 1, 200)

	out := map[string]any{"success": true}

	if includePlayHistory {
		limit := minInt(500, maxInt(50, playHistoryLimit*10))
			rows, err := database.SQL().Query(`
				SELECT
				  content_key,
				  site_key,
				  site_name,
				  spider_api,
				  video_id,
				  video_title,
				  video_poster,
				  video_remark,
				  pan_label,
				  play_flag,
				  episode_index,
				  episode_name,
				  updated_at
				FROM play_history
			WHERE user_id = ?
			ORDER BY updated_at DESC
			LIMIT ?
		`, u.ID, limit)
		if err == nil {
			defer rows.Close()
			seen := map[string]struct{}{}
			list := []map[string]any{}
				for rows.Next() {
					var (
						contentKey   string
						siteKey      string
						siteName     string
						spiderAPI    string
					videoID      string
					videoTitle   string
					videoPoster  string
					videoRemark  string
					panLabel     string
					playFlag     string
					episodeIndex int
					episodeName  string
					updatedAt    int64
				)
				_ = rows.Scan(&contentKey, &siteKey, &siteName, &spiderAPI, &videoID, &videoTitle, &videoPoster, &videoRemark, &panLabel, &playFlag, &episodeIndex, &episodeName, &updatedAt)
					if isNetDiskHistoryItem(videoID, playFlag) {
						continue
					}
					key := strings.TrimSpace(contentKey)
					if key == "" {
						key = normalizeContentKey(videoTitle)
						contentKey = key
					}
				if key == "" {
					key = siteKey + "::" + videoID
				}
				if key == "" {
					continue
				}
				if _, ok := seen[key]; ok {
					continue
				}
				seen[key] = struct{}{}
				list = append(list, map[string]any{
					"contentKey":   contentKey,
					"siteKey":      siteKey,
					"siteName":     siteName,
					"spiderApi":    spiderAPI,
						"videoId":      videoID,
						"videoTitle":   videoTitle,
						"videoPoster":  videoPoster,
						"videoRemark":  videoRemark,
						"panLabel":     panLabel,
						"playFlag":     playFlag,
						"episodeIndex": episodeIndex,
						"episodeName":  episodeName,
						"updatedAt":    updatedAt,
					})
				if len(list) >= playHistoryLimit {
					break
				}
			}
			out["playHistory"] = list
		}
	}

	if includeFavorites {
		rows, err := database.SQL().Query(`
			SELECT site_key, site_name, spider_api, video_id, video_title, video_poster, video_remark, updated_at
			FROM favorites
			WHERE user_id = ?
			ORDER BY updated_at DESC
			LIMIT ?
		`, u.ID, favoritesLimit)
		if err == nil {
			defer rows.Close()
			list := []map[string]any{}
			for rows.Next() {
				var (
					siteKey     string
					siteName    string
					spiderAPI   string
					videoID     string
					videoTitle  string
					videoPoster string
					videoRemark string
					updatedAt   int64
				)
				_ = rows.Scan(&siteKey, &siteName, &spiderAPI, &videoID, &videoTitle, &videoPoster, &videoRemark, &updatedAt)
				list = append(list, map[string]any{
					"siteKey":     siteKey,
					"siteName":    siteName,
					"spiderApi":   spiderAPI,
					"videoId":     videoID,
					"videoTitle":  videoTitle,
					"videoPoster": videoPoster,
					"videoRemark": videoRemark,
					"updatedAt":   updatedAt,
				})
			}
			out["favorites"] = list
		}
	}

	if includePanLoginSettings && u.Role == "shared" {
		out["panLoginSettings"] = parseJSONMap(database.GetSetting("pan_login_settings"))
	}

	writeJSON(w, 200, out)
}

func handleAPIVideoSites(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	sites := normalizeSitesFromJSON(database.GetSetting("video_source_sites"))
	statusMap := parseJSONBoolMap(database.GetSetting("video_source_site_status"))
	homeMap := parseJSONBoolMap(database.GetSetting("video_source_site_home"))
	order := parseJSONStringArray(database.GetSetting("video_source_site_order"))
	ordered := applySiteOrder(sites, order)
	out := make([]map[string]any, 0, len(ordered))
	for _, s := range ordered {
		enabled, ok := statusMap[s.Key]
		if !ok {
			enabled = true
		}
		home, ok := homeMap[s.Key]
		if !ok {
			home = defaultHomeForSite(s)
		}
		row := map[string]any{
			"key":     s.Key,
			"name":    s.Name,
			"api":     s.API,
			"enabled": enabled,
			"home":    home,
		}
		if s.Type != nil {
			row["type"] = *s.Type
		}
		out = append(out, row)
	}
	writeJSON(w, 200, map[string]any{"success": true, "sites": out})
}

func handleAPISearchHistory(w http.ResponseWriter, r *http.Request, database *db.DB) {
	u := auth.CurrentUser(r)
	if u == nil {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"error": "Unauthorized"})
		return
	}
	switch r.Method {
	case http.MethodGet:
		rows, err := database.SQL().Query(`SELECT keyword FROM search_history WHERE user_id=? ORDER BY updated_at DESC LIMIT 20`, u.ID)
		if err != nil {
			writeJSON(w, 200, []string{})
			return
		}
		defer rows.Close()
		list := []string{}
		for rows.Next() {
			var kw string
			_ = rows.Scan(&kw)
			kw = strings.TrimSpace(kw)
			if kw != "" {
				list = append(list, kw)
			}
		}
		writeJSON(w, 200, list)
	case http.MethodPost:
		parseForm(r)
		kw := strings.TrimSpace(r.FormValue("keyword"))
		if kw == "" && strings.Contains(r.Header.Get("Content-Type"), "application/json") {
			var body struct {
				Keyword string `json:"keyword"`
			}
			_ = readJSONLoose(r, &body)
			kw = strings.TrimSpace(body.Keyword)
		}
		kw = strings.Join(strings.Fields(kw), " ")
		if kw == "" {
			writeJSON(w, http.StatusBadRequest, map[string]any{"error": "Keyword is required"})
			return
		}
		_, _ = database.SQL().Exec(`
			INSERT INTO search_history(user_id, keyword, updated_at)
			VALUES(?,?,?)
			ON CONFLICT(user_id, keyword) DO UPDATE SET updated_at = excluded.updated_at
		`, u.ID, kw, time.Now().Unix())
		handleAPISearchHistory(w, withMethod(r, http.MethodGet), database)
	case http.MethodDelete:
		kw := strings.TrimSpace(r.URL.Query().Get("keyword"))
		if kw != "" {
			_, _ = database.SQL().Exec(`DELETE FROM search_history WHERE user_id=? AND keyword=?`, u.ID, kw)
		} else {
			_, _ = database.SQL().Exec(`DELETE FROM search_history WHERE user_id=?`, u.ID)
		}
		handleAPISearchHistory(w, withMethod(r, http.MethodGet), database)
	default:
		methodNotAllowed(w)
	}
}

func withMethod(r *http.Request, method string) *http.Request {
	cp := r.Clone(r.Context())
	cp.Method = method
	return cp
}

func isNetDiskHistoryItem(videoID string, playFlag string) bool {
	id := strings.ToLower(strings.TrimSpace(videoID))
	if strings.HasSuffix(id, "######wodepan") {
		return true
	}
	return false
}

func handleAPIPlayHistoryOne(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	u := auth.CurrentUser(r)
	siteKey := strings.TrimSpace(r.URL.Query().Get("siteKey"))
	videoID := strings.TrimSpace(r.URL.Query().Get("videoId"))
	if siteKey == "" || videoID == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "Invalid params"})
		return
	}
	var (
		contentKey   string
		siteName     string
		spiderAPI    string
		videoTitle   string
		videoPoster  string
		videoRemark  string
		panLabel     string
		playFlag     string
		episodeIndex int
		episodeName  string
		updatedAt    int64
	)
	err := database.SQL().QueryRow(`
		SELECT content_key, site_name, spider_api, video_title, video_poster, video_remark, pan_label, play_flag, episode_index, episode_name, updated_at
		FROM play_history
		WHERE user_id=? AND site_key=? AND video_id=?
		ORDER BY updated_at DESC
		LIMIT 1
	`, u.ID, siteKey, videoID).Scan(&contentKey, &siteName, &spiderAPI, &videoTitle, &videoPoster, &videoRemark, &panLabel, &playFlag, &episodeIndex, &episodeName, &updatedAt)
	if err != nil {
		writeJSON(w, 200, nil)
		return
	}
	if isNetDiskHistoryItem(videoID, playFlag) {
		writeJSON(w, 200, nil)
		return
	}
	if strings.TrimSpace(contentKey) == "" {
		contentKey = normalizeContentKey(videoTitle)
	}
	writeJSON(w, 200, map[string]any{
		"contentKey":   contentKey,
		"siteKey":      siteKey,
		"siteName":     siteName,
		"spiderApi":    spiderAPI,
		"videoId":      videoID,
		"videoTitle":   videoTitle,
		"videoPoster":  videoPoster,
		"videoRemark":  videoRemark,
		"panLabel":     panLabel,
		"playFlag":     playFlag,
		"episodeIndex": episodeIndex,
		"episodeName":  episodeName,
		"updatedAt":    updatedAt,
	})
}

func handleAPIPlayHistory(w http.ResponseWriter, r *http.Request, database *db.DB) {
	u := auth.CurrentUser(r)
	switch r.Method {
		case http.MethodGet:
			limit := parseIntQuery(r.URL.Query().Get("limit"), 20, 1, 50)
			sourceLimit := minInt(500, maxInt(50, limit*10))
			rows, err := database.SQL().Query(`
				SELECT content_key, site_key, site_name, spider_api, video_id, video_title, video_poster, video_remark, pan_label, play_flag, episode_index, episode_name, updated_at
				FROM play_history
				WHERE user_id=?
				ORDER BY updated_at DESC
				LIMIT ?
			`, u.ID, sourceLimit)
		if err != nil {
			writeJSON(w, 200, []any{})
			return
		}
		defer rows.Close()
		seen := map[string]struct{}{}
		list := []map[string]any{}
				for rows.Next() {
					var (
						contentKey   string
						siteKey      string
						siteName     string
						spiderAPI    string
						videoID      string
						videoTitle   string
						videoPoster  string
						videoRemark  string
						panLabel     string
						playFlag     string
						episodeIndex int
						episodeName  string
						updatedAt    int64
					)
					_ = rows.Scan(&contentKey, &siteKey, &siteName, &spiderAPI, &videoID, &videoTitle, &videoPoster, &videoRemark, &panLabel, &playFlag, &episodeIndex, &episodeName, &updatedAt)
					if isNetDiskHistoryItem(videoID, playFlag) {
						continue
					}
					key := strings.TrimSpace(contentKey)
				if key == "" {
					key = normalizeContentKey(videoTitle)
					contentKey = key
				}
			if key == "" {
				key = siteKey + "::" + videoID
			}
			if key == "" {
				continue
			}
			if _, ok := seen[key]; ok {
				continue
			}
			seen[key] = struct{}{}
			list = append(list, map[string]any{
				"contentKey":   contentKey,
				"siteKey":      siteKey,
				"siteName":     siteName,
				"spiderApi":    spiderAPI,
						"videoId":      videoID,
						"videoTitle":   videoTitle,
						"videoPoster":  videoPoster,
						"videoRemark":  videoRemark,
						"panLabel":     panLabel,
						"playFlag":     playFlag,
						"episodeIndex": episodeIndex,
						"episodeName":  episodeName,
						"updatedAt":    updatedAt,
					})
					if len(list) >= limit {
						break
			}
		}
		writeJSON(w, 200, list)
	case http.MethodPost:
		var body map[string]any
		_ = readJSONLoose(r, &body)
		getS := func(k string) string {
			v, ok := body[k]
			if !ok || v == nil {
				return ""
			}
			s, _ := v.(string)
			return strings.TrimSpace(s)
		}
		getI := func(k string) int {
			v, ok := body[k]
			if !ok || v == nil {
				return 0
			}
			switch vv := v.(type) {
			case float64:
				return int(vv)
			case string:
				n, _ := strconv.Atoi(strings.TrimSpace(vv))
				return n
			default:
				return 0
			}
		}
		siteKey := getS("siteKey")
		spiderAPI := getS("spiderApi")
		videoID := getS("videoId")
		videoTitle := getS("videoTitle")
		if siteKey == "" || spiderAPI == "" || videoID == "" || videoTitle == "" {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "参数不完整"})
			return
		}
			siteName := getS("siteName")
			videoPoster := getS("videoPoster")
			videoRemark := getS("videoRemark")
			panLabel := getS("panLabel")
			playFlag := getS("playFlag")
			episodeIndex := getI("episodeIndex")
			if episodeIndex < 0 {
				episodeIndex = 0
		}
			episodeName := getS("episodeName")

			if isNetDiskHistoryItem(videoID, playFlag) {
				writeJSON(w, 200, map[string]any{"success": true})
				return
			}

			forcePosterUpdate := false
			if v, ok := body["forcePosterUpdate"]; ok && v != nil {
				forcePosterUpdate = parseAnyBool(v, false)
			}

		contentKey := normalizeContentKey(videoTitle)
		if contentKey == "" {
			contentKey = siteKey + "::" + videoID
		}

		lockedPoster := ""
		_ = database.SQL().QueryRow(`
			SELECT video_poster
			FROM play_history
			WHERE user_id = ? AND content_key = ? AND video_poster <> ''
			ORDER BY updated_at DESC
			LIMIT 1
		`, u.ID, contentKey).Scan(&lockedPoster)
		lockedPoster = strings.TrimSpace(lockedPoster)

		finalPoster := videoPoster
		if !forcePosterUpdate || strings.TrimSpace(videoPoster) == "" {
			if lockedPoster != "" {
				finalPoster = lockedPoster
			}
		}

		// Keep only one record per content (videoTitle) per user: always the latest played site.
		_, _ = database.SQL().Exec(`
			DELETE FROM play_history
			WHERE user_id = ? AND (content_key = ? OR video_title = ?)
		`, u.ID, contentKey, videoTitle)

			now := time.Now().Unix()
			_, _ = database.SQL().Exec(`
				INSERT INTO play_history(
				  user_id, content_key, site_key, site_name, spider_api, video_id, video_title, video_poster, video_remark,
				  pan_label, play_flag, episode_index, episode_name, updated_at
				)
				VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)
				ON CONFLICT(user_id, site_key, video_id) DO UPDATE SET
				  content_key = excluded.content_key,
				  site_name = excluded.site_name,
				  spider_api = excluded.spider_api,
				  video_title = excluded.video_title,
				  video_poster = excluded.video_poster,
				  video_remark = excluded.video_remark,
				  pan_label = excluded.pan_label,
				  play_flag = excluded.play_flag,
				  episode_index = excluded.episode_index,
				  episode_name = excluded.episode_name,
				  updated_at = excluded.updated_at
			`, u.ID, contentKey, siteKey, siteName, spiderAPI, videoID, videoTitle, finalPoster, videoRemark, panLabel, playFlag, episodeIndex, episodeName, now)
			writeJSON(w, 200, map[string]any{"success": true})
		default:
			methodNotAllowed(w)
		}
	}

func handleAPIFavorites(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	u := auth.CurrentUser(r)
	limit := parseIntQuery(strings.TrimSpace(r.URL.Query().Get("limit")), 200, 1, 200)
	rows, err := database.SQL().Query(`
		SELECT site_key, site_name, spider_api, video_id, video_title, video_poster, video_remark, updated_at
		FROM favorites
		WHERE user_id=?
		ORDER BY updated_at DESC
		LIMIT ?
	`, u.ID, limit)
	if err != nil {
		writeJSON(w, 200, []any{})
		return
	}
	defer rows.Close()
	list := []map[string]any{}
	for rows.Next() {
		var (
			siteKey     string
			siteName    string
			spiderAPI   string
			videoID     string
			videoTitle  string
			videoPoster string
			videoRemark string
			updatedAt   int64
		)
		_ = rows.Scan(&siteKey, &siteName, &spiderAPI, &videoID, &videoTitle, &videoPoster, &videoRemark, &updatedAt)
		list = append(list, map[string]any{
			"siteKey":     siteKey,
			"siteName":    siteName,
			"spiderApi":   spiderAPI,
			"videoId":     videoID,
			"videoTitle":  videoTitle,
			"videoPoster": videoPoster,
			"videoRemark": videoRemark,
			"updatedAt":   updatedAt,
		})
	}
	writeJSON(w, 200, list)
}

func handleAPIFavoritesStatus(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	u := auth.CurrentUser(r)
	siteKey := strings.TrimSpace(r.URL.Query().Get("siteKey"))
	videoID := strings.TrimSpace(r.URL.Query().Get("videoId"))
	if siteKey == "" || videoID == "" {
		writeJSON(w, 200, map[string]any{"favorited": false})
		return
	}
	var v int
	err := database.SQL().QueryRow(`SELECT 1 FROM favorites WHERE user_id=? AND site_key=? AND video_id=? LIMIT 1`, u.ID, siteKey, videoID).Scan(&v)
	writeJSON(w, 200, map[string]any{"favorited": err == nil})
}

func handleAPIFavoritesToggle(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	u := auth.CurrentUser(r)
	var body map[string]any
	_ = readJSONLoose(r, &body)
	getS := func(k string) string {
		v, ok := body[k]
		if !ok || v == nil {
			return ""
		}
		s, _ := v.(string)
		return strings.TrimSpace(s)
	}
	siteKey := getS("siteKey")
	spiderAPI := getS("spiderApi")
	videoID := getS("videoId")
	videoTitle := getS("videoTitle")
	if siteKey == "" || spiderAPI == "" || videoID == "" || videoTitle == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "参数无效"})
		return
	}
	var exists int
	_ = database.SQL().QueryRow(`SELECT COUNT(1) FROM favorites WHERE user_id=? AND site_key=? AND video_id=?`, u.ID, siteKey, videoID).Scan(&exists)
	if exists > 0 {
		_, _ = database.SQL().Exec(`DELETE FROM favorites WHERE user_id=? AND site_key=? AND video_id=?`, u.ID, siteKey, videoID)
		writeJSON(w, 200, map[string]any{"success": true, "favorited": false})
		return
	}
	now := time.Now().Unix()
	siteName := getS("siteName")
	videoPoster := getS("videoPoster")
	videoRemark := getS("videoRemark")
	_, _ = database.SQL().Exec(`
		INSERT INTO favorites(user_id, site_key, site_name, spider_api, video_id, video_title, video_poster, video_remark, updated_at)
		VALUES(?,?,?,?,?,?,?,?,?)
		ON CONFLICT(user_id, site_key, video_id) DO UPDATE SET
		  site_name=excluded.site_name,
		  spider_api=excluded.spider_api,
		  video_title=excluded.video_title,
		  video_poster=excluded.video_poster,
		  video_remark=excluded.video_remark,
		  updated_at=excluded.updated_at
	`, u.ID, siteKey, siteName, spiderAPI, videoID, videoTitle, videoPoster, videoRemark, now)
	writeJSON(w, 200, map[string]any{"success": true, "favorited": true})
}

func handleAPIUserSettings(w http.ResponseWriter, r *http.Request, database *db.DB) {
	u := auth.CurrentUser(r)
	switch r.Method {
	case http.MethodGet:
		var (
			catBase     string
			catKey      string
			catProxy    string
			threadCount int
			searchOrder string
			searchCover string
		)
		_ = database.SQL().QueryRow(`
			SELECT cat_api_base, cat_api_key, cat_proxy, search_thread_count, cat_search_order, cat_search_cover_site
			FROM users WHERE id=? LIMIT 1
		`, u.ID).Scan(&catBase, &catKey, &catProxy, &threadCount, &searchOrder, &searchCover)
		if threadCount < 1 {
			threadCount = 5
		}
		writeJSON(w, 200, map[string]any{
			"success": true,
			"settings": map[string]any{
				"catApiBase":        catBase,
				"catApiKey":         catKey,
				"catProxy":          catProxy,
				"searchThreadCount": threadCount,
				"searchSiteOrder":   parseJSONStringArray(searchOrder),
				"searchCoverSite":   strings.TrimSpace(searchCover),
			},
		})
	case http.MethodPut:
		var body map[string]any
		_ = readJSONLoose(r, &body)

		var prev userSettingsRow
		_ = database.SQL().QueryRow(`
			SELECT
			  cat_api_base, cat_api_key, cat_proxy,
			  search_thread_count, cat_search_order, cat_search_cover_site,
			  cat_sites, cat_site_status, cat_site_home, cat_site_order, cat_site_availability
			FROM users WHERE id = ? LIMIT 1
		`, u.ID).Scan(
			&prev.CatAPIBase, &prev.CatAPIKey, &prev.CatProxy,
			&prev.ThreadCount, &prev.SearchOrder, &prev.SearchCoverSite,
			&prev.CatSites, &prev.CatSiteStatus, &prev.CatSiteHome, &prev.CatSiteOrder, &prev.CatSiteAvail,
		)

		getOptionalString := func(k string) (string, bool) {
			v, ok := body[k]
			if !ok {
				return "", false
			}
			if v == nil {
				return "", false
			}
			s, ok := v.(string)
			if !ok {
				return "", false
			}
			return s, true
		}

		// CatPawOpen settings (support both camelCase and snake_case input).
		catApiBase := prev.CatAPIBase
		if s, ok := getOptionalString("catApiBase"); ok {
			catApiBase = strings.TrimSpace(s)
		} else if s, ok := getOptionalString("cat_api_base"); ok {
			catApiBase = strings.TrimSpace(s)
		}
		normalizedApiBase := ""
		if strings.TrimSpace(catApiBase) != "" {
			normalizedApiBase = normalizeCatPawOpenAPIBase(catApiBase)
			if normalizedApiBase == "" {
				writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "CatPawOpen 接口地址不是合法 URL"})
				return
			}
		}
		if u.Role == "user" && normalizedApiBase == "" {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "CatPawOpen 接口地址未设置"})
			return
		}

		catApiKey := prev.CatAPIKey
		if s, ok := getOptionalString("catApiKey"); ok {
			catApiKey = s
		} else if s, ok := getOptionalString("cat_api_key"); ok {
			catApiKey = s
		}
		catProxy := prev.CatProxy
		if s, ok := getOptionalString("catProxy"); ok {
			catProxy = s
		} else if s, ok := getOptionalString("cat_proxy"); ok {
			catProxy = s
		}

		// search_thread_count (supports string/number)
		stRaw, hasST := body["searchThreadCount"]
		if !hasST {
			stRaw, hasST = body["search_thread_count"]
		}
		if !hasST || stRaw == nil {
			stRaw = prev.ThreadCount
		}
		threadCount := 5
		if n, ok := intFromAnyFloor(stRaw); ok {
			threadCount = n
		} else {
			threadCount = -1
		}
		if threadCount < 1 || threadCount > 50 {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "搜索线程数必须是 1-50 的整数"})
			return
		}

		// Optional search order/cover inputs.
		var (
			providedSearchOrder    []string
			hasProvidedSearchOrder bool
		)
		if v, ok := body["searchSiteOrder"]; ok {
			if v != nil {
				hasProvidedSearchOrder = true
				providedSearchOrder = parseStringArrayAny(v)
				if providedSearchOrder == nil {
					providedSearchOrder = []string{}
				}
			}
		} else if v, ok := body["search_site_order"]; ok {
			if v != nil {
				hasProvidedSearchOrder = true
				providedSearchOrder = parseStringArrayAny(v)
				if providedSearchOrder == nil {
					providedSearchOrder = []string{}
				}
			}
		}
		var providedSearchCover *string
		if v, ok := body["searchCoverSite"]; ok {
			if v != nil {
				s := strings.TrimSpace(anyToString(v))
				providedSearchCover = &s
			}
		} else if v, ok := body["search_cover_site"]; ok {
			if v != nil {
				s := strings.TrimSpace(anyToString(v))
				providedSearchCover = &s
			}
		}

		sitesSync := map[string]any{"ok": true, "refreshed": false, "count": 0}
		cookieSync := map[string]any{"ok": true, "updated": 0}

		var reconciledSitesForSearch []site
		if normalizedApiBase != "" {
			if v, ok := body["sites"]; ok && v != nil {
				nextSites, ok := parseSitesAny(v)
				if ok {
					prevStatus := parseJSONBoolMap(prev.CatSiteStatus)
					prevHome := parseJSONBoolMap(prev.CatSiteHome)
					prevOrder := parseJSONStringArray(prev.CatSiteOrder)
					prevAvail := parseAvailabilityJSON(prev.CatSiteAvail)
					reconciled := reconcileSites(nextSites, prevStatus, prevHome, nil, prevOrder, prevAvail)
					reconciledSitesForSearch = reconciled.Sites
					changed, err := persistUserCatSites(database, u.ID, prev, reconciled)
					if err != nil {
						sitesSync["ok"] = false
						sitesSync["message"] = "站点列表更新失败"
					} else {
						sitesSync["refreshed"] = changed
						sitesSync["count"] = len(reconciled.Sites)
					}
				}
			}
		}

		resolveAvailableSearchKeys := func() []string {
			if len(reconciledSitesForSearch) > 0 {
				keys := make([]string, 0, len(reconciledSitesForSearch))
				for _, s := range reconciledSitesForSearch {
					if strings.TrimSpace(s.Key) != "" {
						keys = append(keys, s.Key)
					}
				}
				return keys
			}
			hasUserAPI := strings.TrimSpace(prev.CatAPIBase) != ""
			canFallback := u.Role != "user"
			if hasUserAPI {
				sites := normalizeSitesFromJSON(prev.CatSites)
				keys := make([]string, 0, len(sites))
				for _, s := range sites {
					keys = append(keys, s.Key)
				}
				return keys
			}
			if canFallback {
				sites := normalizeSitesFromJSON(database.GetSetting("video_source_sites"))
				keys := make([]string, 0, len(sites))
				for _, s := range sites {
					keys = append(keys, s.Key)
				}
				return keys
			}
			sites := normalizeSitesFromJSON(prev.CatSites)
			keys := make([]string, 0, len(sites))
			for _, s := range sites {
				keys = append(keys, s.Key)
			}
			return keys
		}

		normalizeSearchOrder := func(keys []string, currentOrder []string) []string {
			all := keys
			keySet := map[string]struct{}{}
			for _, k := range all {
				if k == "" {
					continue
				}
				keySet[k] = struct{}{}
			}
			next := []string{}
			seen := map[string]struct{}{}
			for _, k := range currentOrder {
				key := strings.TrimSpace(k)
				if key == "" {
					continue
				}
				if _, ok := keySet[key]; !ok {
					continue
				}
				if _, ok := seen[key]; ok {
					continue
				}
				seen[key] = struct{}{}
				next = append(next, key)
			}
			for _, key := range all {
				if key == "" {
					continue
				}
				if _, ok := seen[key]; ok {
					continue
				}
				seen[key] = struct{}{}
				next = append(next, key)
			}
			return next
		}

		availableKeys := resolveAvailableSearchKeys()
		prevSearchOrder := parseJSONStringArray(prev.SearchOrder)
		orderInput := prevSearchOrder
		if hasProvidedSearchOrder {
			orderInput = providedSearchOrder
		}
		nextSearchOrder := normalizeSearchOrder(availableKeys, orderInput)

		prevCover := strings.TrimSpace(prev.SearchCoverSite)
		coverCandidate := prevCover
		if providedSearchCover != nil {
			coverCandidate = strings.TrimSpace(*providedSearchCover)
		}
		nextCover := ""
		if coverCandidate != "" && containsString(nextSearchOrder, coverCandidate) {
			nextCover = coverCandidate
		} else if len(nextSearchOrder) > 0 {
			nextCover = nextSearchOrder[0]
		}

		prevOrderStr := prev.SearchOrder
		nextOrderStr := marshalJSON(nextSearchOrder)
		prevCoverStr := prev.SearchCoverSite
		nextCoverStr := nextCover

		settingsChanged :=
			prev.CatAPIBase != normalizedApiBase ||
				prev.CatAPIKey != catApiKey ||
				prev.CatProxy != catProxy ||
				prev.ThreadCount != threadCount ||
				prevOrderStr != nextOrderStr ||
				prevCoverStr != nextCoverStr

		if settingsChanged {
			_, err := database.SQL().Exec(`
				UPDATE users
				SET cat_api_base = ?, cat_api_key = ?, cat_proxy = ?, search_thread_count = ?, cat_search_order = ?, cat_search_cover_site = ?
				WHERE id = ?
			`, normalizedApiBase, catApiKey, catProxy, threadCount, nextOrderStr, nextCoverStr, u.ID)
			if err != nil {
				writeJSON(w, http.StatusInternalServerError, map[string]any{"success": false, "message": "保存失败"})
				return
			}
		}

		writeJSON(w, 200, map[string]any{"success": true, "sitesSync": sitesSync, "cookieSync": cookieSync})
	default:
		methodNotAllowed(w)
	}
}

func intFromAnyFloor(v any) (int, bool) {
	switch vv := v.(type) {
	case int:
		return vv, true
	case int64:
		return int(vv), true
	case float64:
		if math.IsNaN(vv) || math.IsInf(vv, 0) {
			return 0, false
		}
		return int(math.Floor(vv)), true
	case string:
		s := strings.TrimSpace(vv)
		if s == "" {
			return 0, false
		}
		f, err := strconv.ParseFloat(s, 64)
		if err != nil || math.IsNaN(f) || math.IsInf(f, 0) {
			return 0, false
		}
		return int(math.Floor(f)), true
	case bool:
		if vv {
			return 1, true
		}
		return 0, true
	default:
		return 0, false
	}
}

func anyToString(v any) string {
	switch vv := v.(type) {
	case string:
		return vv
	case float64:
		if math.IsNaN(vv) || math.IsInf(vv, 0) {
			return ""
		}
		return strconv.FormatFloat(vv, 'f', -1, 64)
	case bool:
		if vv {
			return "true"
		}
		return "false"
	default:
		return ""
	}
}

func parseStringArrayAny(v any) []string {
	switch vv := v.(type) {
	case []any:
		out := make([]string, 0, len(vv))
		for _, it := range vv {
			s, ok := it.(string)
			if !ok {
				continue
			}
			s = strings.TrimSpace(s)
			if s != "" {
				out = append(out, s)
			}
		}
		return out
	case string:
		return parseJSONStringArray(vv)
	default:
		return nil
	}
}

func parseSitesAny(v any) ([]site, bool) {
	list, ok := v.([]any)
	if !ok {
		return nil, false
	}
	out := make([]site, 0, len(list))
	for _, it := range list {
		m, ok := it.(map[string]any)
		if !ok || m == nil {
			continue
		}
		key, _ := m["key"].(string)
		name, _ := m["name"].(string)
		api, _ := m["api"].(string)
		key = strings.TrimSpace(key)
		api = strings.TrimSpace(api)
		if key == "" || api == "" {
			continue
		}
		var typ *int
		if tv, ok := m["type"]; ok && tv != nil {
			if n, ok := intFromAnyFloor(tv); ok {
				t := n
				typ = &t
			}
		}
		out = append(out, site{Key: key, Name: name, API: api, Type: typ})
	}
	return out, true
}

func persistUserCatSites(database *db.DB, userID int64, prev userSettingsRow, next reconciledSiteState) (changed bool, err error) {
	nextSites := marshalJSON(next.Sites)
	nextStatus := marshalJSON(next.Status)
	nextHome := marshalJSON(next.Home)
	nextOrder := marshalJSON(next.Order)
	nextAvail := marshalJSON(next.Availability)

	if prev.CatSites == nextSites && prev.CatSiteStatus == nextStatus && prev.CatSiteHome == nextHome && prev.CatSiteOrder == nextOrder && prev.CatSiteAvail == nextAvail {
		return false, nil
	}

	_, err = database.SQL().Exec(`
		UPDATE users
		SET cat_sites = ?, cat_site_status = ?, cat_site_home = ?, cat_site_order = ?, cat_site_availability = ?
		WHERE id = ?
	`, nextSites, nextStatus, nextHome, nextOrder, nextAvail, userID)
	if err != nil {
		return false, err
	}
	return true, nil
}

func containsString(list []string, needle string) bool {
	for _, s := range list {
		if s == needle {
			return true
		}
	}
	return false
}

func handleAPIUserPanLoginSettings(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	u := auth.CurrentUser(r)
	if u.Role != "shared" {
		writeJSON(w, http.StatusForbidden, map[string]any{"success": false, "message": "无权限"})
		return
	}
	store := parseJSONMap(database.GetSetting("pan_login_settings"))
	writeJSON(w, 200, map[string]any{"success": true, "settings": store})
}

func handleAPIUserSites(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	u := auth.CurrentUser(r)
	state, err := resolveUserCatSites(database, u)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"success": false, "message": "请求失败"})
		return
	}
	availabilityAny := map[string]any{}
	for k, v := range state.Availability {
		availabilityAny[k] = v
	}
	searchMap := parseJSONBoolMap(database.GetSetting("video_source_site_search"))
	errorMap := parseJSONStringMap(database.GetSetting("video_source_site_error"))
	merged := mergeSitesWithState(state.Sites, state.Status, state.Home, state.Order, availabilityAny, searchMap, errorMap)
	writeJSON(w, 200, map[string]any{"success": true, "sites": merged, "requiresCatApiBase": !state.HasUserAPI && !state.CanFallback})
}

func handleAPIUserSitesAvailability(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	u := auth.CurrentUser(r)
	var body struct {
		Key          string `json:"key"`
		Availability string `json:"availability"`
	}
	_ = readJSONLoose(r, &body)
	key := strings.TrimSpace(body.Key)
	avail := normalizeAvailability(body.Availability)
	if key == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "参数无效"})
		return
	}
	state, err := resolveUserCatSites(database, u)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"success": false, "message": "请求失败"})
		return
	}
	if _, ok := userSiteKeySet(state.Sites)[key]; !ok {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "站点不存在"})
		return
	}
	if state.Availability[key] == avail {
		writeJSON(w, 200, map[string]any{"success": true})
		return
	}
	state.Availability[key] = avail
	_, _ = database.SQL().Exec(`UPDATE users SET cat_site_availability=? WHERE id=?`, marshalJSON(state.Availability), u.ID)
	writeJSON(w, 200, map[string]any{"success": true})
}

func handleAPIUserSitesStatus(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	u := auth.CurrentUser(r)
	var body struct {
		Key     string `json:"key"`
		Enabled *bool  `json:"enabled"`
	}
	_ = readJSONLoose(r, &body)
	key := strings.TrimSpace(body.Key)
	if key == "" || body.Enabled == nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "参数无效"})
		return
	}
	state, err := resolveUserCatSites(database, u)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"success": false, "message": "请求失败"})
		return
	}
	if _, ok := userSiteKeySet(state.Sites)[key]; !ok {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "站点不存在"})
		return
	}
	if state.Status[key] == *body.Enabled {
		writeJSON(w, 200, map[string]any{"success": true})
		return
	}
	state.Status[key] = *body.Enabled
	_, _ = database.SQL().Exec(`UPDATE users SET cat_site_status=? WHERE id=?`, marshalJSON(state.Status), u.ID)
	writeJSON(w, 200, map[string]any{"success": true})
}

func handleAPIUserSitesHome(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	u := auth.CurrentUser(r)
	var body struct {
		Key  string `json:"key"`
		Home *bool  `json:"home"`
	}
	_ = readJSONLoose(r, &body)
	key := strings.TrimSpace(body.Key)
	if key == "" || body.Home == nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "参数无效"})
		return
	}
	state, err := resolveUserCatSites(database, u)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"success": false, "message": "请求失败"})
		return
	}
	if _, ok := userSiteKeySet(state.Sites)[key]; !ok {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "站点不存在"})
		return
	}
	if state.Home[key] == *body.Home {
		writeJSON(w, 200, map[string]any{"success": true})
		return
	}
	state.Home[key] = *body.Home
	_, _ = database.SQL().Exec(`UPDATE users SET cat_site_home=? WHERE id=?`, marshalJSON(state.Home), u.ID)
	writeJSON(w, 200, map[string]any{"success": true})
}

func handleAPIUserSitesOrder(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	u := auth.CurrentUser(r)
	var body struct {
		Order []string `json:"order"`
	}
	_ = readJSONLoose(r, &body)
	state, err := resolveUserCatSites(database, u)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"success": false, "message": "请求失败"})
		return
	}
	keySet := userSiteKeySet(state.Sites)
	next := []string{}
	seen := map[string]struct{}{}
	for _, k := range body.Order {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		if _, ok := keySet[key]; !ok {
			continue
		}
		if _, ok := seen[key]; ok {
			continue
		}
		seen[key] = struct{}{}
		next = append(next, key)
	}
	for _, s := range state.Sites {
		if s.Key == "" {
			continue
		}
		if _, ok := seen[s.Key]; ok {
			continue
		}
		seen[s.Key] = struct{}{}
		next = append(next, s.Key)
	}
	_, _ = database.SQL().Exec(`UPDATE users SET cat_site_order=? WHERE id=?`, marshalJSON(next), u.ID)
	writeJSON(w, 200, map[string]any{"success": true})
}

func handleAPIDoubanImage(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}

	const maxBytes = 15 * 1024 * 1024
	raw := strings.TrimSpace(r.URL.Query().Get("url"))
	if raw == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "参数无效"})
		return
	}
	parsed, err := url.Parse(raw)
	if err != nil || parsed.Scheme == "" || parsed.Host == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "URL 无效"})
		return
	}
	if parsed.Scheme != "http" && parsed.Scheme != "https" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "URL 无效"})
		return
	}
	if !isAllowedDoubanImageHost(parsed.Hostname()) {
		writeJSON(w, http.StatusForbidden, map[string]any{"success": false, "message": "不允许的图片域名"})
		return
	}

	client := &http.Client{
		Timeout: 10 * time.Second,
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			if len(via) >= 5 {
				return http.ErrUseLastResponse
			}
			return nil
		},
	}
	req, _ := http.NewRequest(http.MethodGet, parsed.String(), nil)
	req.Header.Set("User-Agent", "Mozilla/5.0")
	req.Header.Set("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8")
	req.Header.Set("Referer", "https://movie.douban.com/")
	resp, err := client.Do(req)
	if err != nil || resp == nil {
		w.WriteHeader(http.StatusBadGateway)
		return
	}
	defer resp.Body.Close()

	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		w.WriteHeader(resp.StatusCode)
		return
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, maxBytes+1))
	if err != nil {
		w.WriteHeader(http.StatusBadGateway)
		return
	}
	if len(body) > maxBytes {
		w.WriteHeader(http.StatusRequestEntityTooLarge)
		return
	}
	if ct := resp.Header.Get("Content-Type"); ct != "" {
		w.Header().Set("Content-Type", ct)
	}
	if cc := resp.Header.Get("Cache-Control"); cc != "" {
		w.Header().Set("Cache-Control", cc)
	}
	w.Header().Set("X-Content-Type-Options", "nosniff")
	w.Header().Set("Content-Length", strconv.Itoa(len(body)))
	w.WriteHeader(resp.StatusCode)
	_, _ = w.Write(body)
}

func minInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}

func maxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}
