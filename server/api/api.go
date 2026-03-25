package api

import (
	"bytes"
	"fmt"
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
	"github.com/jenfonro/meowfilm/internal/buildinfo"
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/internal/limit"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
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
		if exceeded, err := limit.GuardAPI(database); err == nil && exceeded {
			w.Header().Set(limit.HeaderErrKey(), limit.Code())
			if wm := buildinfo.WatermarkTrim(); wm != "" {
				w.Header().Set(limit.HeaderWatermarkKey(), wm)
			}
			writeJSON(w, http.StatusServiceUnavailable, map[string]any{
				"success": false,
				"code":    limit.PublicCode(),
			})
			return
		}

		path := strings.TrimPrefix(r.URL.Path, "/api")

		// Douban API proxy (rexxar v2): frontend uses same params; backend chooses upstream base.
		if strings.HasPrefix(path, "/douban/rexxar/api/v2/") {
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIDoubanRexxarProxy(w, r, database)
			})).ServeHTTP(w, r)
			return
		}

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
		case "/douban/search":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIDoubanSearch(w, r, database)
			})).ServeHTTP(w, r)
		case "/tmdb/search":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				tmdb.HandleSearch(w, r, database)
			})).ServeHTTP(w, r)
		case "/tmdb/detail":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPITMDBDetail(w, r, database)
			})).ServeHTTP(w, r)
		case "/smart/matchblock/items":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPISmartMatchBlockItems(w, r, database)
			})).ServeHTTP(w, r)
		case "/smart/matchblock/add":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPISmartMatchBlockAdd(w, r, database)
			})).ServeHTTP(w, r)
		case "/smart/matchblock/delete":
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPISmartMatchBlockDelete(w, r, database)
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
		case "/pan/relay/resolve":
			http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				netdisk.HandleAPIRelayResolve(w, r, database)
			}).ServeHTTP(w, r)
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
			rawCat, _ := database.ListcatpawrunnerServers()
			catServers := make([]catpawrunner.Server, 0, len(rawCat))
			for _, s := range rawCat {
				catServers = append(catServers, catpawrunner.Server{Name: s.Name, APIBase: s.APIBase})
			}
			settings["catpawrunnerApiBase"] = catpawrunner.ResolveActiveBase(catServers, cfg.CatpawrunnerActive)

			// Index page includes Douban browse sections.
			if page == "index" {
				settings["doubanDataProxy"] = douban.CanonicalDataProxyMode(cfg.DoubanDataProxy)
				settings["doubanDataCustom"] = cfg.DoubanDataCustom
				settings["doubanImgProxy"] = douban.CanonicalImageProxyMode(cfg.DoubanImgProxy)
				settings["doubanImgCustom"] = cfg.DoubanImgCustom
				settings["tmdbImageProxyBase"] = strings.TrimSpace(cfg.TMDBImgBase)
			}

			// Search / playback: settings used by front-end to talk to catpawrunner and to normalize titles.
			if page == "search" || page == "play" || page == "site" {
				settings["doubanDataProxy"] = douban.CanonicalDataProxyMode(cfg.DoubanDataProxy)
				settings["doubanDataCustom"] = cfg.DoubanDataCustom
				settings["doubanImgProxy"] = douban.CanonicalImageProxyMode(cfg.DoubanImgProxy)
				settings["doubanImgCustom"] = cfg.DoubanImgCustom
				settings["tmdbImageProxyBase"] = strings.TrimSpace(cfg.TMDBImgBase)
				settings["doubanSearchCookieConfigured"] = strings.TrimSpace(cfg.DoubanSearchCookie) != ""

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
				settings["relayGoProxyThresholdGB"] = cfg.RelayGoProxyThresholdGB
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
				settings["relayEnabled"] = cfg.RelayEnabled
				rawRelay, _ := database.ListRelayServers()
				settings["relayServers"] = normalizeRelayServers(rawRelay)

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
				if v, _ := database.ListSmartPanAliasMappings(); v != nil {
					out := make([]map[string]string, 0, len(v))
					for _, it := range v {
						out = append(out, map[string]string{
							"pan":     strings.TrimSpace(it.Pan),
							"aliases": strings.TrimSpace(it.Aliases),
						})
					}
					settings["smartPanAliasMappings"] = out
				} else {
					settings["smartPanAliasMappings"] = []map[string]string{}
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

	if page == "index" || page == "play" || page == "site" {
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
	doubanImgProxy := douban.CanonicalImageProxyMode(cfg.DoubanImgProxy)
	doubanImgCustom := cfg.DoubanImgCustom

	out := map[string]any{"success": true}

	if includePlayHistory {
		rows, err := database.ListPlayHistory(u.ID, playHistoryLimit)
		if err == nil {
			out["playHistory"] = buildNormalizedPlayHistoryList(rows, playHistoryLimit)
		}
	}

	if includeFavorites {
		rows, err := database.ListFavorites(u.ID, favoritesLimit)
		if err == nil {
			list := []map[string]any{}
			for _, row := range rows {
				list = append(list, map[string]any{
					"siteKey":    row.SiteKey,
					"siteName":   row.SiteName,
					"spiderApi":  row.SpiderAPI,
					"siteDetail": row.SiteDetail,
					"contentKey": row.ContentKey,
					"Poster":     douban.RewriteVideoPosterURL(row.Poster, doubanImgProxy, doubanImgCustom),
					"Remark":     row.Remark,
					"updatedAt":  row.UpdatedAt,
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

func isNetDiskHistoryItem(siteDetail string, playFlag string) bool {
	id := strings.ToLower(strings.TrimSpace(siteDetail))
	if strings.HasSuffix(id, "######wodepan") {
		return true
	}
	return false
}

func normalizeRawHistoryPoster(poster string) string {
	raw := strings.TrimSpace(poster)
	if raw == "" {
		return ""
	}
	normalized := strings.ReplaceAll(raw, "\\", "/")
	const marker = "/pan/tmdb-img/"
	if idx := strings.Index(strings.ToLower(normalized), marker); idx >= 0 {
		path := strings.TrimSpace(normalized[idx+len(marker):])
		if path != "" {
			return "https://image.tmdb.org/" + strings.TrimLeft(path, "/")
		}
	}
	if strings.HasPrefix(strings.ToLower(normalized), "/tmdb-img/") {
		path := strings.TrimSpace(normalized[len("/tmdb-img/"):])
		if path != "" {
			return "https://image.tmdb.org/" + strings.TrimLeft(path, "/")
		}
	}
	return raw
}

func buildNormalizedPlayHistoryList(rows []db.PlayHistoryRow, limit int) []map[string]any {
	if limit <= 0 {
		limit = 20
	}
	out := make([]map[string]any, 0, minInt(limit, len(rows)))

	for i := range rows {
		if len(out) >= limit {
			break
		}
		row := rows[i]
		contentKey := strings.TrimSpace(row.ContentKey)
		tmdbType := strings.ToLower(strings.TrimSpace(row.TMDBType))
		tmdbID := row.TMDBID
		tmdbOK := tmdbID > 0 && (tmdbType == "tv" || tmdbType == "movie")
		if contentKey == "" {
			continue
		}

		poster := strings.TrimSpace(row.Poster)
		remark := strings.TrimSpace(row.Remark)
		playbackItemID := strings.TrimSpace(row.PlaybackItemID)
		tmdbSeason := row.TMDBSeason
		tmdbEpisode := row.TMDBEpisode
		if tmdbSeason <= 0 || tmdbEpisode <= 0 {
			if typ, tid, s, e, ok := parseTMDBFromPlaybackItemID(playbackItemID); ok && typ == "tv" && tid > 0 {
				tmdbSeason = s
				tmdbEpisode = e
			}
		}

		siteKey := strings.TrimSpace(row.SiteKey)
		spiderAPI := strings.TrimSpace(row.SpiderAPI)
		siteDetail := strings.TrimSpace(row.SiteDetail)
		siteName := strings.TrimSpace(row.SiteName)

		// If this record isn't directly playable via web (no spider api / emby-only), emit a TMDB-only card.
		if tmdbOK && (spiderAPI == "" || strings.EqualFold(siteKey, "emby") || siteDetail == "") {
			siteKey = ""
			spiderAPI = ""
			siteDetail = ""
			siteName = ""
		}

		out = append(out, map[string]any{
			"contentKey":            contentKey,
			"siteKey":               siteKey,
			"siteName":              siteName,
			"spiderApi":             spiderAPI,
			"siteDetail":            siteDetail,
			"Poster":                poster,
			"Remark":                remark,
			"tmdbId":                tmdbID,
			"tmdbType":              tmdbType,
			"tmdbSeason":            tmdbSeason,
			"tmdbEpisode":           tmdbEpisode,
			"playFlag":              strings.TrimSpace(row.PlayFlag),
			"siteEpisodeIndex":      row.SiteEpisodeIndex,
			"siteEpisodeFile":       strings.TrimSpace(row.SiteEpisodeFile),
			"playbackItemId":        playbackItemID,
			"playbackPositionTicks": row.PlaybackPositionTicks,
			"playbackRuntimeTicks":  row.PlaybackRuntimeTicks,
			"updatedAt":             row.UpdatedAt,
		})
	}
	return out
}

func parseTMDBFromPlaybackItemID(playbackItemID string) (tmdbType string, tmdbID int, season int, episode int, ok bool) {
	id := strings.TrimSpace(playbackItemID)
	if id == "" {
		return "", 0, 0, 0, false
	}
	// tmdb_movie_{id}
	if m := regexp.MustCompile(`^tmdb_movie_(\d+)$`).FindStringSubmatch(id); len(m) == 2 {
		n, _ := strconv.Atoi(m[1])
		if n > 0 {
			return "movie", n, 0, 0, true
		}
	}
	// tmdb_tv_{id}_s{ss}_e{eee}
	if m := regexp.MustCompile(`^tmdb_tv_(\d+)_s(\d{2})_e(\d{3})$`).FindStringSubmatch(id); len(m) == 4 {
		tid, _ := strconv.Atoi(m[1])
		s, _ := strconv.Atoi(m[2])
		e, _ := strconv.Atoi(m[3])
		if tid > 0 && s > 0 && e > 0 {
			return "tv", tid, s, e, true
		}
	}
	return "", 0, 0, 0, false
}

func handleAPIPlayHistoryOne(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	u := auth.CurrentUser(r)
	siteKey := strings.TrimSpace(r.URL.Query().Get("siteKey"))
	siteDetail := strings.TrimSpace(r.URL.Query().Get("siteDetail"))
	if siteKey == "" || siteDetail == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "Invalid params"})
		return
	}
	var (
		contentKey       string
		siteName         string
		spiderAPI        string
		poster           string
		remark           string
		tmdbID           int
		tmdbType         string
		playFlag         string
		siteEpisodeIndex int
		siteEpisodeFile  string
		updatedAt        int64
	)
	row, err := database.GetPlayHistoryLatestBySiteVideo(u.ID, siteKey, siteDetail)
	if err != nil || row == nil {
		writeJSON(w, 200, nil)
		return
	}
	contentKey = row.ContentKey
	siteName = row.SiteName
	spiderAPI = row.SpiderAPI
	poster = row.Poster
	remark = row.Remark
	tmdbID = row.TMDBID
	tmdbType = row.TMDBType
	playFlag = row.PlayFlag
	siteEpisodeIndex = row.SiteEpisodeIndex
	siteEpisodeFile = row.SiteEpisodeFile
	updatedAt = row.UpdatedAt
	tmdbType = strings.TrimSpace(strings.ToLower(tmdbType))
	tmdbSeason := row.TMDBSeason
	tmdbEpisode := row.TMDBEpisode
	if tmdbSeason <= 0 || tmdbEpisode <= 0 {
		if typ, tid, s, e, ok := parseTMDBFromPlaybackItemID(strings.TrimSpace(row.PlaybackItemID)); ok && typ == "tv" && tid > 0 {
			tmdbSeason = s
			tmdbEpisode = e
		}
	}
	writeJSON(w, 200, map[string]any{
		"contentKey":            contentKey,
		"siteKey":               siteKey,
		"siteName":              siteName,
		"spiderApi":             spiderAPI,
		"siteDetail":            siteDetail,
		"Poster":                poster,
		"Remark":                remark,
		"tmdbId":                tmdbID,
		"tmdbType":              tmdbType,
		"tmdbSeason":            tmdbSeason,
		"tmdbEpisode":           tmdbEpisode,
		"playFlag":              playFlag,
		"siteEpisodeIndex":      siteEpisodeIndex,
		"siteEpisodeFile":       siteEpisodeFile,
		"playbackItemId":        row.PlaybackItemID,
		"playbackPositionTicks": row.PlaybackPositionTicks,
		"playbackRuntimeTicks":  row.PlaybackRuntimeTicks,
		"updatedAt":             updatedAt,
	})
}

func handleAPIPlayHistory(w http.ResponseWriter, r *http.Request, database *db.DB) {
	u := auth.CurrentUser(r)
	switch r.Method {
	case http.MethodGet:
		limit := parseIntQuery(r.URL.Query().Get("limit"), 20, 1, 50)
		rows, err := database.ListPlayHistory(u.ID, limit)
		if err != nil {
			writeJSON(w, 200, []any{})
			return
		}
		writeJSON(w, 200, buildNormalizedPlayHistoryList(rows, limit))
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
		siteDetail := getS("siteDetail")
		contentKey := getS("contentKey")
		if siteKey == "" || spiderAPI == "" || siteDetail == "" {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "参数不完整"})
			return
		}
		siteName := getS("siteName")
		poster := getS("Poster")
		remark := getS("Remark")
		tmdbID := getI("tmdbId")
		tmdbType := getS("tmdbType")
		if tmdbType != "tv" && tmdbType != "movie" {
			tmdbType = ""
			tmdbID = 0
		}
		playFlag := getS("playFlag")
		siteEpisodeIndex := getI("siteEpisodeIndex")
		if siteEpisodeIndex < 0 {
			siteEpisodeIndex = 0
		}
		siteEpisodeFile := getS("siteEpisodeFile")

		// Playback ticks (Emby/Emby use 10,000,000 ticks per second).
		positionTicks := getI64("playbackPositionTicks")
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
			sec := getI64("playbackDurationSeconds")
			if sec > 0 {
				runtimeTicks = sec * 10_000_000
			}
		}
		if runtimeTicks < 0 {
			runtimeTicks = 0
		}

		playbackItemID := strings.TrimSpace(getS("playbackItemId"))
		tmdbSeason := getI("tmdbSeason")
		tmdbEpisode := getI("tmdbEpisode")

		// If client doesn't send tmdbId/tmdbType (or sends partial), derive them from playbackItemId.
		if typ, id, s, e, ok := parseTMDBFromPlaybackItemID(playbackItemID); ok {
			if tmdbID <= 0 || tmdbType == "" {
				tmdbType = typ
				tmdbID = id
			}
			if tmdbSeason <= 0 {
				tmdbSeason = s
			}
			if tmdbEpisode <= 0 {
				tmdbEpisode = e
			}
		}

		if isNetDiskHistoryItem(siteDetail, playFlag) {
			// still persist (netdisk items are valid history entries for quick start)
		}

		contentKey = strings.TrimSpace(contentKey)
		if len(contentKey) > 200 {
			contentKey = contentKey[:200]
		}
		if contentKey == "" {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "contentKey不能为空"})
			return
		}
		poster = normalizeRawHistoryPoster(poster)

		// Derive a Emby item id for resume syncing when possible.
		if playbackItemID == "" && tmdbID > 0 {
			if tmdbType == "movie" {
				playbackItemID = emby.BuildMovieID(tmdbID)
			} else if tmdbType == "tv" {
				seasonNo := tmdbSeason
				epNo := tmdbEpisode
				if seasonNo <= 0 || epNo <= 0 {
					// Try to parse from siteEpisodeFile / playFlag (e.g. "S01E04")
					hay := siteEpisodeFile
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

		now := time.Now().Unix()
		_ = database.UpsertPlayHistory(db.PlayHistoryUpsert{
			UserID:                u.ID,
			ContentKey:            contentKey,
			SiteKey:               siteKey,
			SiteName:              siteName,
			SpiderAPI:             spiderAPI,
			SiteDetail:            siteDetail,
			Poster:                poster,
			Remark:                remark,
			TMDBID:                tmdbID,
			TMDBType:              tmdbType,
			PlayFlag:              playFlag,
			SiteEpisodeIndex:      siteEpisodeIndex,
			SiteEpisodeFile:       siteEpisodeFile,
			TMDBSeason:            tmdbSeason,
			TMDBEpisode:           tmdbEpisode,
			UpdatedAt:             now,
			PlaybackPositionTicks: positionTicks,
			PlaybackRuntimeTicks:  runtimeTicks,
			PlaybackItemID:        playbackItemID,
		})
		writeJSON(w, 200, map[string]any{"success": true})
	case http.MethodDelete:
		contentKey := strings.TrimSpace(r.URL.Query().Get("contentKey"))
		siteKey := strings.TrimSpace(r.URL.Query().Get("siteKey"))
		siteDetail := strings.TrimSpace(r.URL.Query().Get("siteDetail"))
		if contentKey == "" && (siteKey == "" || siteDetail == "") {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "参数不完整"})
			return
		}
		deleted := int64(0)
		if contentKey != "" {
			deleted, _ = database.DeletePlayHistoryByContentKey(u.ID, contentKey)
		} else {
			deleted, _ = database.DeletePlayHistoryBySiteVideo(u.ID, siteKey, siteDetail)
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
	doubanImgProxy := douban.CanonicalImageProxyMode(cfg.DoubanImgProxy)
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
			"siteKey":    row.SiteKey,
			"siteName":   row.SiteName,
			"spiderApi":  row.SpiderAPI,
			"siteDetail": row.SiteDetail,
			"contentKey": row.ContentKey,
			"Poster":     douban.RewriteVideoPosterURL(row.Poster, doubanImgProxy, doubanImgCustom),
			"Remark":     row.Remark,
			"updatedAt":  row.UpdatedAt,
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
	siteDetail := strings.TrimSpace(r.URL.Query().Get("siteDetail"))
	if siteKey == "" || siteDetail == "" {
		writeJSON(w, 200, map[string]any{"favorited": false})
		return
	}
	ok, _ := database.IsFavorited(u.ID, siteKey, siteDetail)
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
	siteDetail := getS("siteDetail")
	contentKey := getS("contentKey")
	if siteKey == "" || spiderAPI == "" || siteDetail == "" || contentKey == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "参数无效"})
		return
	}
	exists, _ := database.IsFavorited(u.ID, siteKey, siteDetail)
	if exists {
		_, _ = database.DeleteFavorite(u.ID, siteKey, siteDetail)
		writeJSON(w, 200, map[string]any{"success": true, "favorited": false})
		return
	}
	now := time.Now().Unix()
	siteName := getS("siteName")
	poster := getS("Poster")
	remark := getS("Remark")
	_ = database.UpsertFavorite(db.FavoriteUpsert{
		UserID:     u.ID,
		SiteKey:    siteKey,
		SiteName:   siteName,
		SpiderAPI:  spiderAPI,
		SiteDetail: siteDetail,
		ContentKey: contentKey,
		Poster:     poster,
		Remark:     remark,
		UpdatedAt:  now,
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
	const doubanReferer = "https://movie.douban.com"
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

	applyHeaders := func(req *http.Request, cookie string) {
		if req == nil {
			return
		}
		// Keep headers minimal; Douban may return 418 if Host/Referer is missing.
		req.Header = make(http.Header)
		req.Host = req.URL.Host
		req.Header.Set("Referer", doubanReferer)
		req.Header.Set("Cache-Control", "no-cache")
		if strings.TrimSpace(cookie) != "" {
			req.Header.Set("Cookie", strings.TrimSpace(cookie))
		}
		// Use curl-like headers to avoid EO bot challenge pages (Go default UA is empty).
		req.Header.Set("User-Agent", "curl/8.5.0")
		req.Header.Set("Accept", "*/*")
	}

	client := &http.Client{
		Timeout: 10 * time.Second,
		// Handle redirects manually to keep headers consistent.
		CheckRedirect: func(req *http.Request, via []*http.Request) error { return http.ErrUseLastResponse },
	}

	fetch := func(target *url.URL, cookie string) (*http.Response, []byte, error) {
		if target == nil {
			return nil, nil, http.ErrNoLocation
		}
		cur := target
		for i := 0; i < 5; i++ {
			req, _ := http.NewRequest(http.MethodGet, cur.String(), nil)
			applyHeaders(req, cookie)
			resp, err := client.Do(req)
			if err != nil || resp == nil {
				if resp != nil && resp.Body != nil {
					_ = resp.Body.Close()
				}
				return nil, nil, err
			}

			if resp.StatusCode >= 300 && resp.StatusCode < 400 {
				loc := strings.TrimSpace(resp.Header.Get("Location"))
				_ = resp.Body.Close()
				if loc == "" {
					return resp, []byte{}, nil
				}
				next, err := url.Parse(loc)
				if err != nil {
					return resp, []byte{}, nil
				}
				cur = cur.ResolveReference(next)
				continue
			}

			defer resp.Body.Close()
			body, readErr := io.ReadAll(io.LimitReader(resp.Body, maxBytes+1))
			if readErr != nil {
				return resp, nil, readErr
			}
			return resp, body, nil
		}
		return nil, nil, http.ErrUseLastResponse
	}

	isBotHTML := func(resp *http.Response, body []byte) bool {
		if resp == nil || len(body) == 0 {
			return false
		}
		ct := strings.ToLower(strings.TrimSpace(resp.Header.Get("Content-Type")))
		if strings.HasPrefix(ct, "text/html") || strings.Contains(ct, "text/html") {
			return true
		}
		// EO bot challenge script markers.
		if bytes.Contains(body, []byte("EO_Bot_Ssid")) || bytes.Contains(body, []byte("__tst_status")) {
			return true
		}
		return false
	}

	parseBotCookies := func(html string) (string, bool) {
		s := html
		if s == "" {
			return "", false
		}
		low := strings.ToLower(s)
		if !strings.Contains(low, "eo_bot_ssid") || !strings.Contains(low, "__tst_status") {
			return "", false
		}

		findNum := func(re string) int64 {
			rx := regexp.MustCompile(re)
			m := rx.FindStringSubmatch(s)
			if len(m) < 2 {
				return 0
			}
			v, _ := strconv.ParseInt(m[1], 10, 64)
			if v < 0 {
				return 0
			}
			return v
		}

		wtk := findNum(`WTKkN:\s*(\d+)`)
		boy := findNum(`bOYDu:\s*(\d+)`)
		wye := findNum(`wyeCN:\s*(\d+)`)
		if wtk == 0 || boy == 0 || wye == 0 {
			return "", false
		}
		tst := wtk + boy + wye

		idx := strings.Index(s, "EO_Bot_Ssid")
		if idx < 0 {
			idx = strings.Index(low, "eo_bot_ssid")
		}
		if idx < 0 {
			return "", false
		}
		window := s[idx:]
		if len(window) > 400 {
			window = window[:400]
		}
		rxSsid := regexp.MustCompile(`\b(\d{6,})\b`)
		m2 := rxSsid.FindStringSubmatch(window)
		if len(m2) < 2 {
			return "", false
		}
		ssid, _ := strconv.ParseInt(m2[1], 10, 64)
		if ssid <= 0 {
			return "", false
		}

		return fmt.Sprintf("__tst_status=%d#; EO_Bot_Ssid=%d", tst, ssid), true
	}

	resp, body, err := fetch(parsed, "")
	if err != nil || resp == nil {
		w.WriteHeader(http.StatusBadGateway)
		return
	}

	// If upstream returns EO bot-challenge HTML (often 200 + text/html), parse cookies and retry once.
	if isBotHTML(resp, body) {
		if cookie, ok := parseBotCookies(string(body)); ok {
			if resp2, body2, err2 := fetch(parsed, cookie); err2 == nil && resp2 != nil && len(body2) > 0 {
				resp = resp2
				body = body2
			}
		}
	}

	if len(body) > maxBytes {
		w.WriteHeader(http.StatusRequestEntityTooLarge)
		return
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		w.WriteHeader(resp.StatusCode)
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
