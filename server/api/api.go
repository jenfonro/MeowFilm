package api

import (
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"regexp"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/auth"
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawopen"
	"github.com/jenfonro/meowfilm/server/config"
	"github.com/jenfonro/meowfilm/server/emby"
	"github.com/jenfonro/meowfilm/server/metadata/douban"
	"github.com/jenfonro/meowfilm/server/metadata/tmdb"
	mfnet "github.com/jenfonro/meowfilm/server/net"
	"github.com/jenfonro/meowfilm/server/netdisk"
	"github.com/jenfonro/meowfilm/server/search"
	"github.com/jenfonro/meowfilm/server/static"
)

func panAPINoAuthAllowed(r *http.Request) bool {
	if strings.TrimSpace(os.Getenv("MEOWFILM_PAN_API_NOAUTH")) != "1" {
		return false
	}
	host, _, err := net.SplitHostPort(strings.TrimSpace(r.RemoteAddr))
	if err != nil {
		host = strings.TrimSpace(r.RemoteAddr)
	}
	ip := net.ParseIP(host)
	if ip == nil {
		return false
	}
	return ip.IsLoopback()
}

func Handler(database *db.DB, authMw *auth.Auth) http.Handler {
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
				search.HistoryHandler(database).ServeHTTP(w, r)
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
		case "/user/sites":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIUserSites(w, r, database)
			})).ServeHTTP(w, r)
		case "/douban/image":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIDoubanImage(w, r)
			})).ServeHTTP(w, r)
		case "/tmdb/search":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				tmdb.HandleSearch(w, r, database)
			})).ServeHTTP(w, r)
		case "/tmdb/detail":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				tmdb.HandleDetail(w, r, database)
			})).ServeHTTP(w, r)
		case "/pan/189/list":
			h := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { netdisk.HandleAPI189List(w, r, database) })
			if panAPINoAuthAllowed(r) {
				h.ServeHTTP(w, r)
			} else {
				authMw.RequireAuthAPI(h).ServeHTTP(w, r)
			}
		case "/pan/189/play":
			h := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { netdisk.HandleAPI189Play(w, r, database) })
			if panAPINoAuthAllowed(r) {
				h.ServeHTTP(w, r)
			} else {
				authMw.RequireAuthAPI(h).ServeHTTP(w, r)
			}
		case "/pan/139/list":
			h := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { netdisk.HandleAPI139List(w, r, database) })
			if panAPINoAuthAllowed(r) {
				h.ServeHTTP(w, r)
			} else {
				authMw.RequireAuthAPI(h).ServeHTTP(w, r)
			}
		case "/pan/139/play":
			h := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { netdisk.HandleAPI139Play(w, r, database) })
			if panAPINoAuthAllowed(r) {
				h.ServeHTTP(w, r)
			} else {
				authMw.RequireAuthAPI(h).ServeHTTP(w, r)
			}
		case "/pan/quark/list":
			h := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { netdisk.HandleAPIQuarkList(w, r, database) })
			if panAPINoAuthAllowed(r) {
				h.ServeHTTP(w, r)
			} else {
				authMw.RequireAuthAPI(h).ServeHTTP(w, r)
			}
		case "/pan/quark/status":
			h := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { netdisk.HandleAPIQuarkStatus(w, r, database) })
			if panAPINoAuthAllowed(r) {
				h.ServeHTTP(w, r)
			} else {
				authMw.RequireAuthAPI(h).ServeHTTP(w, r)
			}
		case "/pan/quark_tv/refresh":
			h := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { netdisk.HandleAPIQuarkTVRefresh(w, r, database) })
			if panAPINoAuthAllowed(r) {
				h.ServeHTTP(w, r)
			} else {
				authMw.RequireAuthAPI(h).ServeHTTP(w, r)
			}
		case "/pan/quark/play":
			h := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { netdisk.HandleAPIQuarkPlay(w, r, database) })
			if panAPINoAuthAllowed(r) {
				h.ServeHTTP(w, r)
			} else {
				authMw.RequireAuthAPI(h).ServeHTTP(w, r)
			}
		case "/pan/uc/list":
			h := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { netdisk.HandleAPIUCList(w, r, database) })
			if panAPINoAuthAllowed(r) {
				h.ServeHTTP(w, r)
			} else {
				authMw.RequireAuthAPI(h).ServeHTTP(w, r)
			}
		case "/pan/uc/play":
			h := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { netdisk.HandleAPIUCPlay(w, r, database) })
			if panAPINoAuthAllowed(r) {
				h.ServeHTTP(w, r)
			} else {
				authMw.RequireAuthAPI(h).ServeHTTP(w, r)
			}
		case "/pan/uc_tv/refresh":
			h := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { netdisk.HandleAPIUCTVRefresh(w, r, database) })
			if panAPINoAuthAllowed(r) {
				h.ServeHTTP(w, r)
			} else {
				authMw.RequireAuthAPI(h).ServeHTTP(w, r)
			}
		case "/pan/baidu/list":
			h := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { netdisk.HandleAPIBaiduList(w, r, database) })
			if panAPINoAuthAllowed(r) {
				h.ServeHTTP(w, r)
			} else {
				authMw.RequireAuthAPI(h).ServeHTTP(w, r)
			}
		case "/pan/baidu/play":
			h := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { netdisk.HandleAPIBaiduPlay(w, r, database) })
			if panAPINoAuthAllowed(r) {
				h.ServeHTTP(w, r)
			} else {
				authMw.RequireAuthAPI(h).ServeHTTP(w, r)
			}
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
	cfg, _ := database.ReadAppConfig()
	siteName := cfg.SiteName
	u := auth.CurrentUser(r)
	if u == nil || u.Status != "active" {
		writeJSON(w, 200, map[string]any{"authenticated": false, "siteName": siteName})
		return
	}

	page := strings.TrimSpace(r.URL.Query().Get("page"))
	settings := map[string]any{}
	if page == "index" || page == "search" || page == "play" || page == "site" || page == "dashboard" {
		if page != "dashboard" {
			rawCat, _ := database.ListCatPawOpenServers()
			catServers := make([]catpawopen.Server, 0, len(rawCat))
			for _, s := range rawCat {
				catServers = append(catServers, catpawopen.Server{Name: s.Name, APIBase: s.APIBase})
			}
			settings["catPawOpenApiBase"] = catpawopen.ResolveActiveBase(catServers, cfg.CatPawOpenActive)

			// Index page includes Douban browse sections.
			if page == "index" {
				settings["doubanDataProxy"] = defaultString(cfg.DoubanDataProxy, "direct")
				settings["doubanDataCustom"] = cfg.DoubanDataCustom
				settings["doubanImgProxy"] = defaultString(cfg.DoubanImgProxy, "direct-browser")
				settings["doubanImgCustom"] = cfg.DoubanImgCustom
			}

			// Search / playback: settings used by front-end to talk to CatPawOpen and to normalize titles.
			if page == "search" || page == "play" || page == "site" {
				if v, _ := database.ListMagicAggregateRegexRules(); v != nil {
					settings["magicAggregateRegexRules"] = v
				} else {
					settings["magicAggregateRegexRules"] = []string{}
				}

				displayMode := strings.TrimSpace(cfg.SearchDisplayMode)
				if displayMode != "tmdb" && displayMode != "both" && displayMode != "sites" {
					displayMode = "sites"
				}
				settings["searchDisplayMode"] = displayMode
			}

			// Only include heavy playback/magic settings on pages that need them.
			if page == "play" || page == "site" {
				settings["goProxyEnabled"] = cfg.GoProxyEnabled
				settings["goProxyAutoSelect"] = cfg.GoProxyAutoSelect
				rawGoProxy, _ := database.ListGoProxyServers()
				goProxyServers := make([]goProxyServer, 0, len(rawGoProxy))
				for _, s := range rawGoProxy {
					goProxyServers = append(goProxyServers, goProxyServer{
						Name:        s.Name,
						DisplayName: s.DisplayName,
						Base:        s.Base,
						Pans:        goProxyPans{Baidu: s.PansBaidu, Quark: s.PansQuark},
					})
				}
				settings["goProxyServers"] = goProxyServers

				if v, _ := database.ListMagicEpisodeRules(); v != nil {
					settings["magicEpisodeRules"] = v
				} else {
					settings["magicEpisodeRules"] = []string{}
				}
				if v, _ := database.ListMagicEpisodeCleanRegexRules(); v != nil {
					settings["magicEpisodeCleanRegexRules"] = v
				} else {
					settings["magicEpisodeCleanRegexRules"] = []string{}
				}
				if v, _ := database.ListMagicMovieRules(); v != nil {
					settings["magicMovieRules"] = v
				} else {
					settings["magicMovieRules"] = []string{}
				}
				if v, _ := database.ListSmartSourcePriorityTokens(); v != nil {
					settings["smartSourcePriorityTokens"] = v
				} else {
					settings["smartSourcePriorityTokens"] = []string{}
				}
				if v, _ := database.ListSmartPanMatchTokens(); v != nil {
					settings["smartPanMatchTokens"] = v
				} else {
					settings["smartPanMatchTokens"] = []string{}
				}
				settings["smartSourceExtractPriority"] = config.NormalizeSourceExtractPriority(cfg.SmartSourceExtractPriority)
			}

			// User search configuration: used by search/play pages only.
			if page == "search" || page == "play" || page == "site" {
				sites := mergeVideoSourceSites(database)
				keys := make([]string, 0, len(sites))
				for _, s := range sites {
					k, _ := s["key"].(string)
					k = strings.TrimSpace(k)
					if k != "" {
						keys = append(keys, k)
					}
				}
				if len(keys) > 0 {
					settings["searchThreadCount"] = len(keys)
				} else {
					settings["searchThreadCount"] = 5
				}
				settings["searchSiteOrder"] = keys
				settings["searchCoverSite"] = resolveSearchCoverSite(sites, cfg.VideoSourceSearchCoverSite)
				if v, _ := database.ListSmartSkipSiteKeys(); v != nil {
					settings["smartSkipSiteKeys"] = v
				} else {
					settings["smartSkipSiteKeys"] = []string{}
				}
			}
			}
		}

	var userCount int
	if page == "dashboard" && u.Role == "admin" {
		if n, err := database.CountUsers(); err == nil {
			userCount = n
		}
	}

	if page == "play" || page == "site" {
		settings["homeSites"] = fetchHomeSites(database)
	}

	writeJSON(w, 200, map[string]any{
		"authenticated": true,
		"siteName":      siteName,
		"user":          map[string]any{"username": u.Username, "role": u.Role},
		"settings":      settings,
		"users":         []any{},
		"userCount":     userCount,
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
	includePlayHistory := parseBoolQuery(q.Get("includePlayHistory"), false)
	includeFavorites := parseBoolQuery(q.Get("includeFavorites"), false)
	includePanLoginSettings := parseBoolQuery(q.Get("includePanLoginSettings"), false)
	playHistoryLimit := parseIntQuery(q.Get("playHistoryLimit"), 20, 1, 50)
	favoritesLimit := parseIntQuery(q.Get("favoritesLimit"), 50, 1, 200)

	cfg, _ := database.ReadAppConfig()
	doubanImgProxy := defaultString(cfg.DoubanImgProxy, "direct-browser")
	doubanImgCustom := cfg.DoubanImgCustom

	out := map[string]any{"success": true}

	if includePlayHistory {
		limit := minInt(500, maxInt(50, playHistoryLimit*10))
		rows, err := database.ListPlayHistory(u.ID, limit)
		if err == nil {
			seen := map[string]struct{}{}
			list := []map[string]any{}
			for _, row := range rows {
				if isNetDiskHistoryItem(row.VideoID, row.PlayFlag) {
					continue
				}
				tmdbType := strings.TrimSpace(row.TMDBType)
				contentKey := strings.TrimSpace(row.ContentKey)
				key := contentKey
				if row.TMDBID > 0 && (tmdbType == "tv" || tmdbType == "movie") {
					key = strings.ToLower("tmdb:" + tmdbType + ":" + strconv.Itoa(row.TMDBID))
					contentKey = key
				} else if key == "" {
					key = normalizeContentKey(row.VideoTitle)
					contentKey = key
				}
				if key == "" {
					key = row.SiteKey + "::" + row.VideoID
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
					"siteKey":      row.SiteKey,
					"siteName":     row.SiteName,
					"spiderApi":    row.SpiderAPI,
					"videoId":      row.VideoID,
					"videoTitle":   row.VideoTitle,
					"videoPoster":  douban.RewriteVideoPosterURL(row.VideoPoster, doubanImgProxy, doubanImgCustom),
					"videoRemark":  row.VideoRemark,
					"tmdbId":       row.TMDBID,
					"tmdbType":     tmdbType,
					"panLabel":     row.PanLabel,
					"playFlag":     row.PlayFlag,
					"episodeIndex": row.EpisodeIndex,
					"episodeName":  row.EpisodeName,
					"updatedAt":    row.UpdatedAt,
				})
				if len(list) >= playHistoryLimit {
					break
				}
			}
			out["playHistory"] = list
		}
	}

	if includeFavorites {
		rows, err := database.ListFavorites(u.ID, favoritesLimit)
		if err == nil {
			list := []map[string]any{}
			for _, row := range rows {
				list = append(list, map[string]any{
					"siteKey":     row.SiteKey,
					"siteName":    row.SiteName,
					"spiderApi":   row.SpiderAPI,
					"videoId":     row.VideoID,
					"videoTitle":  row.VideoTitle,
					"videoPoster": douban.RewriteVideoPosterURL(row.VideoPoster, doubanImgProxy, doubanImgCustom),
					"videoRemark": row.VideoRemark,
					"updatedAt":   row.UpdatedAt,
				})
			}
			out["favorites"] = list
		}
	}

	if includePanLoginSettings {
		if store, err := database.ReadPanLoginSettings(); err == nil && store != nil {
			out["panLoginSettings"] = store
		} else {
			out["panLoginSettings"] = map[string]any{}
		}
	}

	writeJSON(w, 200, out)
}

func handleAPIVideoSites(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	merged := mergeVideoSourceSites(database)
	out := make([]map[string]any, 0, len(merged))
	for _, s := range merged {
		row := map[string]any{
			"key":     s["key"],
			"name":    s["name"],
			"api":     s["api"],
			"enabled": s["enabled"],
			"home":    s["home"],
		}
		if v, ok := s["type"]; ok && v != nil {
			row["type"] = v
		}
		out = append(out, row)
	}
	writeJSON(w, 200, map[string]any{"success": true, "sites": out})
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
			contentKey  string
			siteName    string
			spiderAPI   string
			videoTitle  string
			videoPoster string
			videoRemark string
			tmdbID      int
			tmdbType    string
			panLabel    string
			playFlag    string
			episodeIndex int
			episodeName  string
			updatedAt    int64
		)
	row, err := database.GetPlayHistoryLatestBySiteVideo(u.ID, siteKey, videoID)
	if err != nil || row == nil {
		writeJSON(w, 200, nil)
		return
	}
	contentKey = row.ContentKey
	siteName = row.SiteName
	spiderAPI = row.SpiderAPI
	videoTitle = row.VideoTitle
	videoPoster = row.VideoPoster
		videoRemark = row.VideoRemark
		tmdbID = row.TMDBID
		tmdbType = row.TMDBType
		panLabel = row.PanLabel
		playFlag = row.PlayFlag
		episodeIndex = row.EpisodeIndex
		episodeName = row.EpisodeName
	updatedAt = row.UpdatedAt

	if isNetDiskHistoryItem(videoID, playFlag) {
		writeJSON(w, 200, nil)
		return
	}
	tmdbType = strings.TrimSpace(tmdbType)
	if tmdbID > 0 && (tmdbType == "tv" || tmdbType == "movie") {
		contentKey = strings.ToLower("tmdb:" + tmdbType + ":" + strconv.Itoa(tmdbID))
	} else if strings.TrimSpace(contentKey) == "" {
		contentKey = normalizeContentKey(videoTitle)
	}
	cfg, _ := database.ReadAppConfig()
	doubanImgProxy := defaultString(cfg.DoubanImgProxy, "direct-browser")
	doubanImgCustom := cfg.DoubanImgCustom
	writeJSON(w, 200, map[string]any{
		"contentKey":   contentKey,
		"siteKey":      siteKey,
		"siteName":     siteName,
		"spiderApi":    spiderAPI,
		"videoId":      videoID,
		"videoTitle":   videoTitle,
		"videoPoster":  douban.RewriteVideoPosterURL(videoPoster, doubanImgProxy, doubanImgCustom),
			"videoRemark":  videoRemark,
			"tmdbId":       tmdbID,
			"tmdbType":     tmdbType,
			"tmdbSeasons":  "",
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
		cfg, _ := database.ReadAppConfig()
		doubanImgProxy := defaultString(cfg.DoubanImgProxy, "direct-browser")
		doubanImgCustom := cfg.DoubanImgCustom
		limit := parseIntQuery(r.URL.Query().Get("limit"), 20, 1, 50)
		sourceLimit := minInt(500, maxInt(50, limit*10))
		rows, err := database.ListPlayHistory(u.ID, sourceLimit)
		if err != nil {
			writeJSON(w, 200, []any{})
			return
		}
		seen := map[string]struct{}{}
		list := []map[string]any{}
		for _, row := range rows {
			if isNetDiskHistoryItem(row.VideoID, row.PlayFlag) {
				continue
			}
			tmdbType := strings.TrimSpace(row.TMDBType)
			contentKey := strings.TrimSpace(row.ContentKey)
			key := contentKey
			if row.TMDBID > 0 && (tmdbType == "tv" || tmdbType == "movie") {
				key = strings.ToLower("tmdb:" + tmdbType + ":" + strconv.Itoa(row.TMDBID))
				contentKey = key
			} else if key == "" {
				key = normalizeContentKey(row.VideoTitle)
				contentKey = key
			}
			if key == "" {
				key = row.SiteKey + "::" + row.VideoID
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
				"siteKey":      row.SiteKey,
				"siteName":     row.SiteName,
				"spiderApi":    row.SpiderAPI,
				"videoId":      row.VideoID,
				"videoTitle":   row.VideoTitle,
				"videoPoster":  douban.RewriteVideoPosterURL(row.VideoPoster, doubanImgProxy, doubanImgCustom),
				"videoRemark":  row.VideoRemark,
				"tmdbId":       row.TMDBID,
				"tmdbType":     tmdbType,
				"tmdbSeasons":  "",
				"panLabel":     row.PanLabel,
				"playFlag":     row.PlayFlag,
				"episodeIndex": row.EpisodeIndex,
				"episodeName":  row.EpisodeName,
				"updatedAt":    row.UpdatedAt,
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
		getI64 := func(k string) int64 {
			v, ok := body[k]
			if !ok || v == nil {
				return 0
			}
			switch vv := v.(type) {
			case float64:
				return int64(vv)
			case string:
				n, _ := strconv.ParseInt(strings.TrimSpace(vv), 10, 64)
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
		tmdbID := getI("tmdbId")
		tmdbType := getS("tmdbType")
			tmdbSeasons := ""
		if tmdbType != "tv" && tmdbType != "movie" {
			tmdbType = ""
			tmdbID = 0
			}
			_ = tmdbSeasons
		panLabel := getS("panLabel")
		playFlag := getS("playFlag")
		episodeIndex := getI("episodeIndex")
		if episodeIndex < 0 {
			episodeIndex = 0
		}
		episodeName := getS("episodeName")

		// Playback ticks (Emby/Emby use 10,000,000 ticks per second).
		positionTicks := getI64("playbackPositionTicks")
		if positionTicks <= 0 {
			positionTicks = getI64("PlaybackPositionTicks")
		}
		if positionTicks <= 0 {
			// seconds -> ticks
			sec := getI64("playbackPositionSeconds")
			if sec > 0 {
				positionTicks = sec * 10_000_000
			}
		}
		if positionTicks < 0 {
			positionTicks = 0
		}
		runtimeTicks := getI64("playbackRuntimeTicks")
		if runtimeTicks <= 0 {
			runtimeTicks = getI64("RunTimeTicks")
		}
		if runtimeTicks <= 0 {
			sec := getI64("playbackDurationSeconds")
			if sec > 0 {
				runtimeTicks = sec * 10_000_000
			}
		}
		if runtimeTicks < 0 {
			runtimeTicks = 0
		}

		playbackItemID := strings.TrimSpace(getS("playbackItemId"))
		if playbackItemID == "" {
			playbackItemID = strings.TrimSpace(getS("playback_item_id"))
		}
		tmdbSeason := getI("tmdbSeason")
		tmdbEpisode := getI("tmdbEpisode")
		if tmdbSeason <= 0 {
			tmdbSeason = getI("season")
		}
		if tmdbEpisode <= 0 {
			tmdbEpisode = getI("episode")
		}

		if isNetDiskHistoryItem(videoID, playFlag) {
			writeJSON(w, 200, map[string]any{"success": true})
			return
		}

		forcePosterUpdate := false
		if v, ok := body["forcePosterUpdate"]; ok && v != nil {
			forcePosterUpdate = parseAnyBool(v, false)
		}

		// Allow client-provided contentKey to de-duplicate history across sources,
		// e.g. when titles differ only by quality / "更新至xx" noise that can be normalized on the client.
		contentKey := strings.TrimSpace(getS("contentKey"))
		if contentKey == "" {
			contentKey = strings.TrimSpace(getS("content_key"))
		}
		if contentKey != "" {
			contentKey = strings.ToLower(contentKey)
			if len(contentKey) > 200 {
				contentKey = contentKey[:200]
			}
		} else {
			contentKey = normalizeContentKey(videoTitle)
		}
		// Prefer a stable TMDB-based contentKey to sync across devices/clients.
		if tmdbID > 0 && (tmdbType == "tv" || tmdbType == "movie") {
			contentKey = strings.ToLower("tmdb:" + tmdbType + ":" + strconv.Itoa(tmdbID))
		}
		if contentKey == "" {
			contentKey = siteKey + "::" + videoID
		}

		// Derive a Emby item id for resume syncing when possible.
		if playbackItemID == "" && tmdbID > 0 {
			if tmdbType == "movie" {
				playbackItemID = emby.BuildMovieID(tmdbID)
			} else if tmdbType == "tv" {
				seasonNo := tmdbSeason
				epNo := tmdbEpisode
				if seasonNo <= 0 || epNo <= 0 {
					// Try to parse from episodeName / playFlag (e.g. "S01E04")
					hay := episodeName
					if hay == "" {
						hay = playFlag
					}
					m := regexp.MustCompile(`(?i)\bS(\d{1,2})E(\d{1,3})\b`).FindStringSubmatch(hay)
					if len(m) == 3 {
						seasonNo, _ = strconv.Atoi(m[1])
						epNo, _ = strconv.Atoi(m[2])
					}
				}
				if seasonNo > 0 && epNo > 0 {
					playbackItemID = emby.BuildEpisodeID(tmdbID, seasonNo, epNo)
				}
			}
		}

		lockedPoster, _ := database.GetPlayHistoryLatestPosterByContentKey(u.ID, contentKey)

		finalPoster := videoPoster
		if !forcePosterUpdate || strings.TrimSpace(videoPoster) == "" {
			if lockedPoster != "" {
				finalPoster = lockedPoster
			}
		}

		// Keep only one record per content (videoTitle) per user: always the latest played site.
		_, _ = database.DeletePlayHistoryDedupByContent(u.ID, contentKey, tmdbID, tmdbType)

		now := time.Now().Unix()
		_ = database.UpsertPlayHistory(db.PlayHistoryUpsert{
			UserID:                u.ID,
			ContentKey:            contentKey,
			SiteKey:               siteKey,
			SiteName:              siteName,
			SpiderAPI:             spiderAPI,
			VideoID:               videoID,
			VideoTitle:            videoTitle,
			VideoPoster:           finalPoster,
			VideoRemark:           videoRemark,
			TMDBID:                tmdbID,
			TMDBType:              tmdbType,
				PanLabel:              panLabel,
			PlayFlag:              playFlag,
			EpisodeIndex:          episodeIndex,
			EpisodeName:           episodeName,
			UpdatedAt:             now,
			PlaybackPositionTicks: positionTicks,
			PlaybackRuntimeTicks:  runtimeTicks,
			PlaybackItemID:        playbackItemID,
		})
		writeJSON(w, 200, map[string]any{"success": true})
	case http.MethodDelete:
		contentKey := strings.TrimSpace(r.URL.Query().Get("contentKey"))
		siteKey := strings.TrimSpace(r.URL.Query().Get("siteKey"))
		videoID := strings.TrimSpace(r.URL.Query().Get("videoId"))
		if contentKey == "" && (siteKey == "" || videoID == "") {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "参数不完整"})
			return
		}
		deleted := int64(0)
		if contentKey != "" {
			deleted, _ = database.DeletePlayHistoryByContentKey(u.ID, contentKey)
		} else {
			deleted, _ = database.DeletePlayHistoryBySiteVideo(u.ID, siteKey, videoID)
		}
		writeJSON(w, 200, map[string]any{"success": true, "deleted": deleted})
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
	cfg, _ := database.ReadAppConfig()
	doubanImgProxy := defaultString(cfg.DoubanImgProxy, "direct-browser")
	doubanImgCustom := cfg.DoubanImgCustom
	limit := parseIntQuery(strings.TrimSpace(r.URL.Query().Get("limit")), 200, 1, 200)
	rows, err := database.ListFavorites(u.ID, limit)
	if err != nil {
		writeJSON(w, 200, []any{})
		return
	}
	list := []map[string]any{}
	for _, row := range rows {
		list = append(list, map[string]any{
			"siteKey":     row.SiteKey,
			"siteName":    row.SiteName,
			"spiderApi":   row.SpiderAPI,
			"videoId":     row.VideoID,
			"videoTitle":  row.VideoTitle,
			"videoPoster": douban.RewriteVideoPosterURL(row.VideoPoster, doubanImgProxy, doubanImgCustom),
			"videoRemark": row.VideoRemark,
			"updatedAt":   row.UpdatedAt,
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
	ok, _ := database.IsFavorited(u.ID, siteKey, videoID)
	writeJSON(w, 200, map[string]any{"favorited": ok})
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
	exists, _ := database.IsFavorited(u.ID, siteKey, videoID)
	if exists {
		_, _ = database.DeleteFavorite(u.ID, siteKey, videoID)
		writeJSON(w, 200, map[string]any{"success": true, "favorited": false})
		return
	}
	now := time.Now().Unix()
	siteName := getS("siteName")
	videoPoster := getS("videoPoster")
	videoRemark := getS("videoRemark")
	_ = database.UpsertFavorite(db.FavoriteUpsert{
		UserID:      u.ID,
		SiteKey:     siteKey,
		SiteName:    siteName,
		SpiderAPI:   spiderAPI,
		VideoID:     videoID,
		VideoTitle:  videoTitle,
		VideoPoster: videoPoster,
		VideoRemark: videoRemark,
		UpdatedAt:   now,
	})
	writeJSON(w, 200, map[string]any{"success": true, "favorited": true})
}

func handleAPIUserSites(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	writeJSON(w, 200, map[string]any{
		"success":            true,
		"sites":              mergeVideoSourceSites(database),
		"requiresCatApiBase": false,
	})
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
	if !douban.IsAllowedImageHost(parsed.Hostname()) {
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
	static.WriteProxiedResponse(w, resp, body)
}

func minInt(a, b int) int {
	return mfnet.MinInt(a, b)
}

func maxInt(a, b int) int {
	return mfnet.MaxInt(a, b)
}
