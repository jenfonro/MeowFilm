package dashboard

import (
	"database/sql"
	"encoding/json"
	"net/http"
	"sort"
	"strings"
	"time"

	"golang.org/x/crypto/bcrypt"

	"github.com/jenfonro/meowfilm/internal/auth"
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawopen"
	"github.com/jenfonro/meowfilm/server/config"
	"github.com/jenfonro/meowfilm/server/netdisk"
)

func Handler(database *db.DB, authMw *auth.Auth) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		path := strings.TrimPrefix(r.URL.Path, "/dashboard")
		switch path {
		case "/backup":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardBackup(w, r, database)
			})).ServeHTTP(w, r)
		case "/restore":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardRestore(w, r, database)
			})).ServeHTTP(w, r)
		case "/site/save":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardSiteSave(w, r, database)
			})).ServeHTTP(w, r)
		case "/catpawopen/save":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardCatPawOpenSave(w, r, database)
			})).ServeHTTP(w, r)
		case "/catpawopen/delete":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardCatPawOpenDelete(w, r, database)
			})).ServeHTTP(w, r)
		case "/site/settings":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardSiteSettings(w, r, database)
			})).ServeHTTP(w, r)
		case "/goproxy/save":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardGoProxySave(w, r, database)
			})).ServeHTTP(w, r)
		case "/pan/settings":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				netdisk.HandleDashboardPanSettings(w, r, database)
			})).ServeHTTP(w, r)
		case "/pan/baidu/start":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				netdisk.HandleDashboardBaiduStart(w, r, database)
			})).ServeHTTP(w, r)
		case "/pan/baidu/image":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				netdisk.HandleDashboardBaiduImage(w, r)
			})).ServeHTTP(w, r)
		case "/pan/baidu/cookie":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				netdisk.HandleDashboardBaiduCookie(w, r, database)
			})).ServeHTTP(w, r)
		case "/pan/quark/start":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				netdisk.HandleDashboardQuarkStart(w, r, database)
			})).ServeHTTP(w, r)
		case "/pan/quark/image":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				netdisk.HandleDashboardQuarkImage(w, r)
			})).ServeHTTP(w, r)
		case "/pan/quark/cookie":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				netdisk.HandleDashboardQuarkCookie(w, r, database)
			})).ServeHTTP(w, r)
		case "/pan/uc/start":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				netdisk.HandleDashboardUCStart(w, r, database)
			})).ServeHTTP(w, r)
		case "/pan/uc/image":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				netdisk.HandleDashboardUCImage(w, r)
			})).ServeHTTP(w, r)
		case "/pan/uc/cookie":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				netdisk.HandleDashboardUCCookie(w, r, database)
			})).ServeHTTP(w, r)
		case "/pan/115/start":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				netdisk.HandleDashboard115Start(w, r, database)
			})).ServeHTTP(w, r)
		case "/pan/115/image":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				netdisk.HandleDashboard115Image(w, r)
			})).ServeHTTP(w, r)
		case "/pan/115/cookie":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				netdisk.HandleDashboard115Cookie(w, r, database)
			})).ServeHTTP(w, r)
		case "/pan/bili/start":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				netdisk.HandleDashboardBiliStart(w, r, database)
			})).ServeHTTP(w, r)
		case "/pan/bili/image":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				netdisk.HandleDashboardBiliImage(w, r)
			})).ServeHTTP(w, r)
		case "/pan/bili/cookie":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				netdisk.HandleDashboardBiliCookie(w, r, database)
			})).ServeHTTP(w, r)
		case "/video/pans/list":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardVideoPansList(w, r, database)
			})).ServeHTTP(w, r)
		case "/video/source/save":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardVideoSourceSave(w, r, database)
			})).ServeHTTP(w, r)
		case "/video/source/settings":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.Method != http.MethodGet {
					methodNotAllowed(w)
					return
				}
				writeJSON(w, 200, map[string]any{"success": true, "videoSourceUrl": ""})
			})).ServeHTTP(w, r)
		case "/video/source/sites":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.Method != http.MethodGet {
					methodNotAllowed(w)
					return
				}
				sites := mergeVideoSourceSites(database)
				cfg, _ := database.ReadAppConfig()
				cover := resolveSearchCoverSite(sites, cfg.VideoSourceSearchCoverSite)
				writeJSON(w, 200, map[string]any{"success": true, "sites": sites, "coverSite": cover})
			})).ServeHTTP(w, r)
		case "/video/source/sites/status":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardVideoSourceSiteStatus(w, r, database)
			})).ServeHTTP(w, r)
		case "/video/source/sites/home":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardVideoSourceSiteHome(w, r, database)
			})).ServeHTTP(w, r)
		case "/video/source/sites/search":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardVideoSourceSiteSearch(w, r, database)
			})).ServeHTTP(w, r)
		case "/video/source/sites/cover":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardVideoSourceCoverSite(w, r, database)
			})).ServeHTTP(w, r)
		case "/video/source/sites/order":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardVideoSourceSiteOrder(w, r, database)
			})).ServeHTTP(w, r)
		case "/video/source/sites/check":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardVideoSourceSitesCheck(w, r, database)
			})).ServeHTTP(w, r)
		case "/video/source/sites/import":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardVideoSourceSitesImport(w, r, database)
			})).ServeHTTP(w, r)
		case "/magic/settings":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardMagicSettings(w, r, database)
			})).ServeHTTP(w, r)
		case "/smart/settings":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardSmartSettings(w, r, database)
			})).ServeHTTP(w, r)
		case "/metadata/settings":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardMetadataSettings(w, r, database)
			})).ServeHTTP(w, r)
		case "/user/list":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardUserList(w, r, database)
			})).ServeHTTP(w, r)
		case "/user/add":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardUserAdd(w, r, database)
			})).ServeHTTP(w, r)
		case "/user/ban":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardUserBan(w, r, database)
			})).ServeHTTP(w, r)
		case "/user/delete":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardUserDelete(w, r, database)
			})).ServeHTTP(w, r)
		case "/user/update":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardUserUpdate(w, r, database)
			})).ServeHTTP(w, r)
		default:
			http.NotFound(w, r)
		}
	})
}

func readBoolJSONBody(body map[string]any, key string) bool {
	if body == nil || key == "" {
		return false
	}
	v, ok := body[key]
	if !ok || v == nil {
		return false
	}
	b, _ := v.(bool)
	return b
}

func readStrJSONBody(body map[string]any, key string) string {
	if body == nil || key == "" {
		return ""
	}
	v, ok := body[key]
	if !ok || v == nil {
		return ""
	}
	s, _ := v.(string)
	return strings.TrimSpace(s)
}

func bool01(b bool) string {
	if b {
		return "1"
	}
	return "0"
}

func handleDashboardSmartSettings(w http.ResponseWriter, r *http.Request, database *db.DB) {
	writeOut := func() {
		cfg, _ := database.ReadAppConfig()
		sourceTokens, _ := database.ListSmartSourcePriorityTokens()
		panTokens, _ := database.ListSmartPanMatchTokens()
		writeJSON(w, 200, map[string]any{
			"success":                    true,
			"smartPlayEnabled":           cfg.SmartPlayEnabled,
			"smartListEnabled":           cfg.SmartListEnabled,
			"smartSourceExtractPriority": config.NormalizeSourceExtractPriority(cfg.SmartSourceExtractPriority),
			"smartSourcePriorityTokens":  defaultStringArray(sourceTokens),
			"smartPanMatchTokens":        defaultStringArray(panTokens),
		})
	}

	switch r.Method {
	case http.MethodGet:
		writeOut()
	case http.MethodPost:
		var body map[string]any
		_ = readJSONLoose(r, &body)

		if _, ok := body["smartPlayEnabled"]; ok {
			_ = database.UpdateAppConfig(func(c *db.AppConfig) {
				c.SmartPlayEnabled = readBoolJSONBody(body, "smartPlayEnabled")
			})
		}
		if _, ok := body["smartListEnabled"]; ok {
			_ = database.UpdateAppConfig(func(c *db.AppConfig) {
				c.SmartListEnabled = readBoolJSONBody(body, "smartListEnabled")
			})
		}
		if _, ok := body["smartSourceExtractPriority"]; ok {
			priority := config.NormalizeSourceExtractPriority(readStrJSONBody(body, "smartSourceExtractPriority"))
			_ = database.UpdateAppConfig(func(c *db.AppConfig) {
				c.SmartSourceExtractPriority = priority
			})
		}

		saveArr := func(set func([]string) error, list any) {
			switch vv := list.(type) {
			case []any:
				out := []string{}
				for _, it := range vv {
					s, _ := it.(string)
					s = strings.TrimSpace(s)
					if s != "" {
						out = append(out, s)
					}
				}
				_ = set(out)
			default:
				// ignore
			}
		}

		if v, ok := body["smartSourcePriorityTokens"]; ok && v != nil {
			saveArr(database.ReplaceSmartSourcePriorityTokens, v)
		}
		if v, ok := body["smartPanMatchTokens"]; ok && v != nil {
			saveArr(database.ReplaceSmartPanMatchTokens, v)
		}

		writeOut()
	default:
		methodNotAllowed(w)
	}
}

func defaultStringArray(v []string) []string {
	if v == nil {
		return []string{}
	}
	return v
}

func defaultStringArrayFromAny(v any) []string {
	out := []string{}
	switch vv := v.(type) {
	case []any:
		for _, it := range vv {
			s, _ := it.(string)
			s = strings.TrimSpace(s)
			if s != "" {
				out = append(out, s)
			}
		}
	case []string:
		for _, s := range vv {
			s = strings.TrimSpace(s)
			if s != "" {
				out = append(out, s)
			}
		}
	}
	return out
}

type dashboardBackupVideoSourceState struct {
	Enabled    bool `json:"enabled"`
	Home       bool `json:"home"`
	Search     bool `json:"search"`
	OrderIndex int  `json:"orderIndex"`
}

func handleDashboardBackup(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	cfg, _ := database.ReadAppConfig()

	catServersRaw, _ := database.ListCatPawOpenServers()
	catServers := make([]map[string]any, 0, len(catServersRaw))
	for _, s := range catServersRaw {
		catServers = append(catServers, map[string]any{"name": s.Name, "apiBase": s.APIBase})
	}
	catPansRaw, _ := database.ListCatPawOpenPans()
	catPans := make([]map[string]any, 0, len(catPansRaw))
	for _, p := range catPansRaw {
		catPans = append(catPans, map[string]any{"key": p.Key, "name": p.Name, "enable": p.Enabled})
	}

	goProxyServersRaw, _ := database.ListGoProxyServers()
	goProxyServers := make([]map[string]any, 0, len(goProxyServersRaw))
	for _, s := range goProxyServersRaw {
		goProxyServers = append(goProxyServers, map[string]any{
			"name":        s.Name,
			"displayName": s.DisplayName,
			"base":        s.Base,
			"pans":        map[string]any{"baidu": s.PansBaidu, "quark": s.PansQuark},
		})
	}

	panLogin, _ := database.ReadPanLoginSettings()

	videoSites, _ := database.ListVideoSourceSites()
	videoStatesRaw, _ := database.ReadVideoSourceSiteStates()
	videoStates := map[string]dashboardBackupVideoSourceState{}
	for k, st := range videoStatesRaw {
		if strings.TrimSpace(k) == "" {
			continue
		}
		videoStates[k] = dashboardBackupVideoSourceState{Enabled: st.Enabled, Home: st.Home, Search: st.Search, OrderIndex: st.OrderIndex}
	}
	type ordRow struct {
		Key string
		Ord int
	}
	ordRows := make([]ordRow, 0, len(videoStatesRaw))
	for k, st := range videoStatesRaw {
		if strings.TrimSpace(k) == "" {
			continue
		}
		ordRows = append(ordRows, ordRow{Key: k, Ord: st.OrderIndex})
	}
	sort.SliceStable(ordRows, func(i, j int) bool {
		if ordRows[i].Ord != ordRows[j].Ord {
			return ordRows[i].Ord < ordRows[j].Ord
		}
		return ordRows[i].Key < ordRows[j].Key
	})
	orderKeys := []string{}
	for _, row := range ordRows {
		orderKeys = append(orderKeys, row.Key)
	}

	magicEpisodeRules, _ := database.ListMagicEpisodeRules()
	magicEpisodeCleanRegexRules, _ := database.ListMagicEpisodeCleanRegexRules()
	magicMovieRules, _ := database.ListMagicMovieRules()
	magicAggregateRegexRules, _ := database.ListMagicAggregateRegexRules()

	smartSourcePriorityTokens, _ := database.ListSmartSourcePriorityTokens()
	smartPanMatchTokens, _ := database.ListSmartPanMatchTokens()

	writeJSON(w, 200, map[string]any{
		"success":    true,
		"version":    1,
		"exportedAt": time.Now().Unix(),
		"appConfig":  cfg,
		"catPawOpen": map[string]any{
			"active":  strings.TrimSpace(cfg.CatPawOpenActive),
			"servers": catServers,
			"pans":    catPans,
		},
		"goProxy": map[string]any{
			"enabled":    cfg.GoProxyEnabled,
			"autoSelect": cfg.GoProxyAutoSelect,
			"servers":    goProxyServers,
		},
		"pan": map[string]any{
			"loginSettings": panLogin,
		},
		"videoSource": map[string]any{
			"apiBase":         strings.TrimSpace(cfg.VideoSourceAPIBase),
			"searchCoverSite": strings.TrimSpace(cfg.VideoSourceSearchCoverSite),
			"sites":           videoSites,
			"states":          videoStates,
			"order":           orderKeys,
		},
		"magic": map[string]any{
			"episodeRules":           defaultStringArray(magicEpisodeRules),
			"episodeCleanRegexRules": defaultStringArray(magicEpisodeCleanRegexRules),
			"movieRules":             defaultStringArray(magicMovieRules),
			"aggregateRegexRules":    defaultStringArray(magicAggregateRegexRules),
		},
		"smart": map[string]any{
			"smartPlayEnabled":           cfg.SmartPlayEnabled,
			"smartListEnabled":           cfg.SmartListEnabled,
			"smartSourceExtractPriority": config.NormalizeSourceExtractPriority(strings.TrimSpace(cfg.SmartSourceExtractPriority)),
			"smartSourcePriorityTokens":  defaultStringArray(smartSourcePriorityTokens),
			"smartPanMatchTokens":        defaultStringArray(smartPanMatchTokens),
		},
		"metadata": map[string]any{
			"doubanDataProxy":    defaultString(cfg.DoubanDataProxy, "direct"),
			"doubanDataCustom":   cfg.DoubanDataCustom,
			"doubanImgProxy":     defaultString(cfg.DoubanImgProxy, "direct-browser"),
			"doubanImgCustom":    cfg.DoubanImgCustom,
			"tmdbApiToken":       strings.TrimSpace(cfg.TMDBAPIToken),
			"tmdbDataProxyBase":  strings.TrimSpace(cfg.TMDBAPIBase),
			"tmdbImageProxyBase": strings.TrimSpace(cfg.TMDBImgBase),
			"language":           defaultString(strings.TrimSpace(cfg.TMDBLanguage), "zh-CN"),
			"region":             defaultString(strings.TrimSpace(cfg.TMDBRegion), "CN"),
			"includeAdult":       cfg.TMDBIncludeAdult,
		},
	})
}

func handleDashboardRestore(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body map[string]any
	_ = readJSONLoose(r, &body)

	readObj := func(m map[string]any, key string) map[string]any {
		if m == nil {
			return nil
		}
		v, ok := m[key]
		if !ok || v == nil {
			return nil
		}
		o, _ := v.(map[string]any)
		return o
	}
	readArr := func(m map[string]any, key string) []any {
		if m == nil {
			return nil
		}
		v, ok := m[key]
		if !ok || v == nil {
			return nil
		}
		a, _ := v.([]any)
		return a
	}

	applied := map[string]any{}

	if cfgObj := readObj(body, "appConfig"); cfgObj != nil {
		readStr := func(keys ...string) (string, bool) {
			for _, k := range keys {
				v, ok := cfgObj[k]
				if !ok || v == nil {
					continue
				}
				s, _ := v.(string)
				return strings.TrimSpace(s), true
			}
			return "", false
		}
		readBool := func(keys ...string) (bool, bool) {
			for _, k := range keys {
				v, ok := cfgObj[k]
				if !ok || v == nil {
					continue
				}
				b, ok := v.(bool)
				if ok {
					return b, true
				}
			}
			return false, false
		}
		_ = database.UpdateAppConfig(func(c *db.AppConfig) {
			if s, ok := readStr("SiteName", "siteName"); ok && s != "" {
				c.SiteName = s
			}
			if s, ok := readStr("SearchDisplayMode", "searchDisplayMode"); ok && s != "" {
				c.SearchDisplayMode = s
			}
			if b, ok := readBool("SearchBadgePreferEpisode", "searchBadgePreferEpisode"); ok {
				c.SearchBadgePreferEpisode = b
			}
			if s, ok := readStr("VideoSourceAPIBase", "videoSourceApiBase"); ok {
				c.VideoSourceAPIBase = s
			}
			if s, ok := readStr("VideoSourceSearchCoverSite", "videoSourceSearchCoverSite"); ok {
				c.VideoSourceSearchCoverSite = s
			}
			if b, ok := readBool("SmartPlayEnabled", "smartPlayEnabled"); ok {
				c.SmartPlayEnabled = b
			}
			if b, ok := readBool("SmartListEnabled", "smartListEnabled"); ok {
				c.SmartListEnabled = b
			}
			if s, ok := readStr("SmartSourceExtractPriority", "smartSourceExtractPriority"); ok && s != "" {
				c.SmartSourceExtractPriority = s
			}
			if b, ok := readBool("GoProxyEnabled", "goProxyEnabled"); ok {
				c.GoProxyEnabled = b
			}
			if b, ok := readBool("GoProxyAutoSelect", "goProxyAutoSelect"); ok {
				c.GoProxyAutoSelect = b
			}
			if s, ok := readStr("DoubanDataProxy", "doubanDataProxy"); ok && s != "" {
				c.DoubanDataProxy = s
			}
			if s, ok := readStr("DoubanDataCustom", "doubanDataCustom"); ok {
				c.DoubanDataCustom = s
			}
			if s, ok := readStr("DoubanImgProxy", "doubanImgProxy"); ok && s != "" {
				c.DoubanImgProxy = s
			}
			if s, ok := readStr("DoubanImgCustom", "doubanImgCustom"); ok {
				c.DoubanImgCustom = s
			}
			if s, ok := readStr("TMDBAPIToken", "tmdbApiToken"); ok {
				c.TMDBAPIToken = s
			}
			if s, ok := readStr("TMDBAPIBase", "tmdbDataProxyBase"); ok {
				c.TMDBAPIBase = normalizeHTTPBase(s)
			}
			if s, ok := readStr("TMDBImgBase", "tmdbImageProxyBase"); ok {
				c.TMDBImgBase = normalizeHTTPBase(s)
			}
			if s, ok := readStr("TMDBLanguage", "language"); ok && s != "" {
				c.TMDBLanguage = s
			}
			if s, ok := readStr("TMDBRegion", "region"); ok && s != "" {
				c.TMDBRegion = s
			}
			if b, ok := readBool("TMDBIncludeAdult", "includeAdult"); ok {
				c.TMDBIncludeAdult = b
			}
			if s, ok := readStr("CatPawOpenActive", "catPawOpenActive"); ok {
				c.CatPawOpenActive = s
			}
		})
		applied["appConfig"] = true
	}

	if cat := readObj(body, "catPawOpen"); cat != nil {
		if arr := readArr(cat, "servers"); arr != nil {
			list := []db.CatPawOpenServer{}
			for _, it := range arr {
				row, _ := it.(map[string]any)
				if row == nil {
					continue
				}
				n, _ := row["name"].(string)
				a, _ := row["apiBase"].(string)
				n = strings.TrimSpace(n)
				a = strings.TrimSpace(a)
				if n == "" || a == "" {
					continue
				}
				list = append(list, db.CatPawOpenServer{Name: n, APIBase: a})
			}
			_ = database.ReplaceCatPawOpenServers(list)
		}
		if arr := readArr(cat, "pans"); arr != nil {
			list := []db.CatPawOpenPan{}
			for _, it := range arr {
				row, _ := it.(map[string]any)
				if row == nil {
					continue
				}
				key, _ := row["key"].(string)
				name, _ := row["name"].(string)
				en, _ := row["enable"].(bool)
				key = strings.TrimSpace(key)
				if key == "" {
					continue
				}
				list = append(list, db.CatPawOpenPan{Key: key, Name: strings.TrimSpace(name), Enabled: en})
			}
			_ = database.ReplaceCatPawOpenPans(list)
		}
		if v, ok := cat["active"]; ok && v != nil {
			if s, _ := v.(string); true {
				_ = database.UpdateAppConfig(func(c *db.AppConfig) { c.CatPawOpenActive = strings.TrimSpace(s) })
			}
		}
		applied["catPawOpen"] = true
	}

	if goProxy := readObj(body, "goProxy"); goProxy != nil {
		if v, ok := goProxy["enabled"]; ok {
			if b, _ := v.(bool); true {
				_ = database.UpdateAppConfig(func(c *db.AppConfig) { c.GoProxyEnabled = b })
			}
		}
		if v, ok := goProxy["autoSelect"]; ok {
			if b, _ := v.(bool); true {
				_ = database.UpdateAppConfig(func(c *db.AppConfig) { c.GoProxyAutoSelect = b })
			}
		}
		if arr := readArr(goProxy, "servers"); arr != nil {
			list := []db.GoProxyServer{}
			for _, it := range arr {
				row, _ := it.(map[string]any)
				if row == nil {
					continue
				}
				name, _ := row["name"].(string)
				displayName, _ := row["displayName"].(string)
				base, _ := row["base"].(string)
				pans, _ := row["pans"].(map[string]any)
				if strings.TrimSpace(name) == "" || strings.TrimSpace(base) == "" {
					continue
				}
				bd := false
				qk := false
				if pans != nil {
					bd, _ = pans["baidu"].(bool)
					qk, _ = pans["quark"].(bool)
				}
				list = append(list, db.GoProxyServer{
					Name:        strings.TrimSpace(name),
					DisplayName: strings.TrimSpace(displayName),
					Base:        strings.TrimSpace(base),
					PansBaidu:   bd,
					PansQuark:   qk,
				})
			}
			_ = database.ReplaceGoProxyServers(list)
		}
		applied["goProxy"] = true
	}

	if pan := readObj(body, "pan"); pan != nil {
		if v, ok := pan["loginSettings"]; ok && v != nil {
			root := map[string]map[string]any{}
			if m, ok := v.(map[string]any); ok {
				for provider, raw := range m {
					pk := strings.TrimSpace(provider)
					if pk == "" {
						continue
					}
					obj, _ := raw.(map[string]any)
					if obj == nil {
						continue
					}
					root[pk] = obj
				}
			}
			_ = database.ReplacePanLoginSettings(root)
			applied["panLoginSettings"] = true
		}
	}

	if vs := readObj(body, "videoSource"); vs != nil {
		if arr := readArr(vs, "sites"); arr != nil {
			list := []db.VideoSourceSite{}
			for _, it := range arr {
				row, _ := it.(map[string]any)
				if row == nil {
					continue
				}
				key, _ := row["Key"].(string)
				if key == "" {
					key, _ = row["key"].(string)
				}
				name, _ := row["Name"].(string)
				if name == "" {
					name, _ = row["name"].(string)
				}
				api, _ := row["API"].(string)
				if api == "" {
					api, _ = row["api"].(string)
				}
				key = strings.TrimSpace(key)
				api = strings.TrimSpace(api)
				if key == "" || api == "" {
					continue
				}
				var tptr *int
				if v, ok := row["Type"]; ok && v != nil {
					if f, ok := v.(float64); ok {
						n := int(f)
						tptr = &n
					}
				}
				if v, ok := row["type"]; ok && v != nil {
					if f, ok := v.(float64); ok {
						n := int(f)
						tptr = &n
					}
				}
				list = append(list, db.VideoSourceSite{Key: key, Name: strings.TrimSpace(name), API: api, Type: tptr})
			}
			_ = database.ReplaceVideoSourceSites(list)
		}
		if v, ok := vs["searchCoverSite"]; ok && v != nil {
			if s, _ := v.(string); true {
				_ = database.UpdateAppConfig(func(c *db.AppConfig) { c.VideoSourceSearchCoverSite = strings.TrimSpace(s) })
			}
		}
		if arr := readArr(vs, "order"); arr != nil {
			next := []string{}
			for _, it := range arr {
				s, _ := it.(string)
				s = strings.TrimSpace(s)
				if s != "" {
					next = append(next, s)
				}
			}
			_ = database.ReplaceVideoSourceSiteOrder(next)
		}
		if v, ok := vs["states"]; ok && v != nil {
			if m, ok := v.(map[string]any); ok {
				for k, raw := range m {
					key := strings.TrimSpace(k)
					obj, _ := raw.(map[string]any)
					if key == "" || obj == nil {
						continue
					}
					enabled, _ := obj["enabled"].(bool)
					home, _ := obj["home"].(bool)
					search, _ := obj["search"].(bool)
					orderIndex := 0
					if f, ok := obj["orderIndex"].(float64); ok {
						orderIndex = int(f)
					}
					_ = database.UpsertVideoSourceSiteState(key, func(s *db.VideoSourceSiteState) {
						s.Enabled = enabled
						s.Home = home
						s.Search = search
						s.OrderIndex = orderIndex
					})
				}
			}
		}
		applied["videoSource"] = true
	}

	if magic := readObj(body, "magic"); magic != nil {
		_ = database.ReplaceMagicEpisodeRules(defaultStringArrayFromAny(magic["episodeRules"]))
		_ = database.ReplaceMagicEpisodeCleanRegexRules(defaultStringArrayFromAny(magic["episodeCleanRegexRules"]))
		_ = database.ReplaceMagicMovieRules(defaultStringArrayFromAny(magic["movieRules"]))
		_ = database.ReplaceMagicAggregateRegexRules(defaultStringArrayFromAny(magic["aggregateRegexRules"]))
		applied["magic"] = true
	}

	if smart := readObj(body, "smart"); smart != nil {
		if v, ok := smart["smartPlayEnabled"]; ok {
			if b, _ := v.(bool); true {
				_ = database.UpdateAppConfig(func(c *db.AppConfig) { c.SmartPlayEnabled = b })
			}
		}
		if v, ok := smart["smartListEnabled"]; ok {
			if b, _ := v.(bool); true {
				_ = database.UpdateAppConfig(func(c *db.AppConfig) { c.SmartListEnabled = b })
			}
		}
		if v, ok := smart["smartSourceExtractPriority"]; ok && v != nil {
			if s, _ := v.(string); true {
				priority := config.NormalizeSourceExtractPriority(strings.TrimSpace(s))
				_ = database.UpdateAppConfig(func(c *db.AppConfig) { c.SmartSourceExtractPriority = priority })
			}
		}
		_ = database.ReplaceSmartSourcePriorityTokens(defaultStringArrayFromAny(smart["smartSourcePriorityTokens"]))
		_ = database.ReplaceSmartPanMatchTokens(defaultStringArrayFromAny(smart["smartPanMatchTokens"]))
		applied["smart"] = true
	}

	if meta := readObj(body, "metadata"); meta != nil {
		_ = database.UpdateAppConfig(func(c *db.AppConfig) {
			if v, ok := meta["doubanDataProxy"]; ok {
				if s, _ := v.(string); strings.TrimSpace(s) != "" {
					c.DoubanDataProxy = strings.TrimSpace(s)
				}
			}
			if v, ok := meta["doubanDataCustom"]; ok {
				if s, _ := v.(string); true {
					c.DoubanDataCustom = strings.TrimSpace(s)
				}
			}
			if v, ok := meta["doubanImgProxy"]; ok {
				if s, _ := v.(string); strings.TrimSpace(s) != "" {
					c.DoubanImgProxy = strings.TrimSpace(s)
				}
			}
			if v, ok := meta["doubanImgCustom"]; ok {
				if s, _ := v.(string); true {
					c.DoubanImgCustom = strings.TrimSpace(s)
				}
			}
			if v, ok := meta["tmdbApiToken"]; ok {
				if s, _ := v.(string); true {
					c.TMDBAPIToken = strings.TrimSpace(s)
				}
			}
			if v, ok := meta["tmdbDataProxyBase"]; ok {
				if s, _ := v.(string); true {
					c.TMDBAPIBase = normalizeHTTPBase(strings.TrimSpace(s))
				}
			}
			if v, ok := meta["tmdbImageProxyBase"]; ok {
				if s, _ := v.(string); true {
					c.TMDBImgBase = normalizeHTTPBase(strings.TrimSpace(s))
				}
			}
			if v, ok := meta["language"]; ok {
				if s, _ := v.(string); strings.TrimSpace(s) != "" {
					c.TMDBLanguage = strings.TrimSpace(s)
				}
			}
			if v, ok := meta["region"]; ok {
				if s, _ := v.(string); strings.TrimSpace(s) != "" {
					c.TMDBRegion = strings.TrimSpace(s)
				}
			}
			if v, ok := meta["includeAdult"]; ok {
				if b, _ := v.(bool); true {
					c.TMDBIncludeAdult = b
				}
			}
		})
		applied["metadata"] = true
	}

	writeJSON(w, 200, map[string]any{"success": true, "applied": applied})
}

func handleDashboardMetadataSettings(w http.ResponseWriter, r *http.Request, database *db.DB) {
	writeOut := func() {
		cfg, _ := database.ReadAppConfig()
		writeJSON(w, 200, map[string]any{
			"success":            true,
			"doubanDataProxy":    defaultString(cfg.DoubanDataProxy, "direct"),
			"doubanDataCustom":   cfg.DoubanDataCustom,
			"doubanImgProxy":     defaultString(cfg.DoubanImgProxy, "direct-browser"),
			"doubanImgCustom":    cfg.DoubanImgCustom,
			"tmdbApiToken":       strings.TrimSpace(cfg.TMDBAPIToken),
			"tmdbDataProxyBase":  strings.TrimSpace(cfg.TMDBAPIBase),
			"tmdbImageProxyBase": strings.TrimSpace(cfg.TMDBImgBase),
			"language":           defaultString(strings.TrimSpace(cfg.TMDBLanguage), "zh-CN"),
			"region":             defaultString(strings.TrimSpace(cfg.TMDBRegion), "CN"),
			"includeAdult":       cfg.TMDBIncludeAdult,
		})
	}

	switch r.Method {
	case http.MethodGet:
		writeOut()
	case http.MethodPost:
		var body map[string]any
		_ = readJSONLoose(r, &body)

		_ = database.UpdateAppConfig(func(c *db.AppConfig) {
			c.DoubanDataProxy = readStrJSONBody(body, "doubanDataProxy")
			c.DoubanDataCustom = readStrJSONBody(body, "doubanDataCustom")
			c.DoubanImgProxy = readStrJSONBody(body, "doubanImgProxy")
			c.DoubanImgCustom = readStrJSONBody(body, "doubanImgCustom")

			c.TMDBAPIToken = readStrJSONBody(body, "tmdbApiToken")
			c.TMDBAPIBase = normalizeHTTPBase(readStrJSONBody(body, "tmdbDataProxyBase"))
			c.TMDBImgBase = normalizeHTTPBase(readStrJSONBody(body, "tmdbImageProxyBase"))
			c.TMDBLanguage = readStrJSONBody(body, "language")
			c.TMDBRegion = readStrJSONBody(body, "region")
			c.TMDBIncludeAdult = readBoolJSONBody(body, "includeAdult")
		})

		writeOut()
	default:
		methodNotAllowed(w)
	}
}

func handleDashboardSiteSave(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	siteName := strings.TrimSpace(r.FormValue("siteName"))
	searchDisplayMode := strings.TrimSpace(r.FormValue("searchDisplayMode"))
	searchBadgePreferEpisode := boolFromForm(r.FormValue("searchBadgePreferEpisode"))
	switch searchDisplayMode {
	case "", "sites", "tmdb", "both":
		if searchDisplayMode == "" {
			searchDisplayMode = "sites"
		}
	default:
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "参数无效"})
		return
	}
	_ = database.UpdateAppConfig(func(c *db.AppConfig) {
		if siteName != "" {
			c.SiteName = siteName
		}
		c.SearchDisplayMode = searchDisplayMode
		c.SearchBadgePreferEpisode = searchBadgePreferEpisode
	})
	writeJSON(w, 200, map[string]any{"success": true})
}

func handleDashboardCatPawOpenSave(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	cfg, _ := database.ReadAppConfig()
	raw, _ := database.ListCatPawOpenServers()
	servers := make([]catpawopen.Server, 0, len(raw))
	for _, s := range raw {
		servers = append(servers, catpawopen.Server{Name: s.Name, APIBase: s.APIBase})
	}
	prevBase := catpawopen.ResolveActiveBase(servers, cfg.CatPawOpenActive)
	serverKey := strings.TrimSpace(r.FormValue("catPawOpenServerKey"))
	name := strings.TrimSpace(r.FormValue("catPawOpenName"))
	base := r.FormValue("catPawOpenApiBase")
	normalizedBase := normalizeCatPawOpenAPIBase(base)
	if normalizedBase == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "CatPawOpen 接口地址不是合法 URL"})
		return
	}

	if name == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "服务器名称不能为空"})
		return
	}

	// "__new__" (front-end draft) behaves like "append".
	key := serverKey
	if key == "__new__" {
		key = ""
	}

	existsName := func(n string, skipIdx int) bool {
		for i, s := range servers {
			if i == skipIdx {
				continue
			}
			if s.Name == n {
				return true
			}
		}
		return false
	}

	updatedIdx := -1
	if key != "" {
		for i, s := range servers {
			if s.Name == key {
				updatedIdx = i
				break
			}
		}
	}
	if updatedIdx >= 0 {
		if existsName(name, updatedIdx) {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "服务器名称已存在"})
			return
		}
		servers[updatedIdx] = catpawopen.Server{Name: name, APIBase: normalizedBase}
	} else {
		if existsName(name, -1) {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "服务器名称已存在"})
			return
		}
		servers = append(servers, catpawopen.Server{Name: name, APIBase: normalizedBase})
	}

	next := make([]db.CatPawOpenServer, 0, len(servers))
	for _, s := range servers {
		next = append(next, db.CatPawOpenServer{Name: s.Name, APIBase: s.APIBase})
	}
	_ = database.ReplaceCatPawOpenServers(next)
	_ = database.UpdateAppConfig(func(c *db.AppConfig) { c.CatPawOpenActive = name })
	writeJSON(w, 200, map[string]any{
		"success":        true,
		"apiBaseChanged": strings.TrimSpace(prevBase) != strings.TrimSpace(normalizedBase),
		"proxySync":      map[string]any{"ok": nil, "skipped": true},
		"goProxySync":    map[string]any{"ok": nil, "skipped": true},
	})
}

func handleDashboardCatPawOpenDelete(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	key := strings.TrimSpace(r.FormValue("catPawOpenServerKey"))
	if key == "" || key == "__new__" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "参数无效"})
		return
	}
	cfg, _ := database.ReadAppConfig()
	raw, _ := database.ListCatPawOpenServers()
	servers := make([]catpawopen.Server, 0, len(raw))
	for _, s := range raw {
		servers = append(servers, catpawopen.Server{Name: s.Name, APIBase: s.APIBase})
	}

	removed := false
	next := make([]catpawopen.Server, 0, len(servers))
	for _, s := range servers {
		if s.Name == key {
			removed = true
			continue
		}
		next = append(next, s)
	}
	if !removed {
		writeJSON(w, http.StatusNotFound, map[string]any{"success": false, "message": "服务器不存在"})
		return
	}

	// Persist list + pick active.
	out := make([]db.CatPawOpenServer, 0, len(next))
	for _, s := range next {
		out = append(out, db.CatPawOpenServer{Name: s.Name, APIBase: s.APIBase})
	}
	_ = database.ReplaceCatPawOpenServers(out)
	active := catpawopen.PickActiveName(next, cfg.CatPawOpenActive)
	_ = database.UpdateAppConfig(func(c *db.AppConfig) { c.CatPawOpenActive = active })

	writeJSON(w, 200, map[string]any{
		"success": true,
		"servers": next,
		"active":  active,
	})
}

func handleDashboardSiteSettings(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	cfg, _ := database.ReadAppConfig()
	raw, _ := database.ListCatPawOpenServers()
	servers := make([]catpawopen.Server, 0, len(raw))
	for _, s := range raw {
		servers = append(servers, catpawopen.Server{Name: s.Name, APIBase: s.APIBase})
	}
	active := catpawopen.PickActiveName(servers, cfg.CatPawOpenActive)
	mode := strings.TrimSpace(cfg.SearchDisplayMode)
	if mode != "tmdb" && mode != "both" && mode != "sites" {
		mode = "sites"
	}
	rawGoProxy, _ := database.ListGoProxyServers()
	goProxyForUI := make([]map[string]any, 0, len(rawGoProxy))
	for _, s := range rawGoProxy {
		goProxyForUI = append(goProxyForUI, map[string]any{
			"name":        s.Name,
			"displayName": s.DisplayName,
			"base":        s.Base,
			"pans":        map[string]any{"baidu": s.PansBaidu, "quark": s.PansQuark},
		})
	}
	goProxyJSON, _ := json.Marshal(goProxyForUI)
	writeJSON(w, 200, map[string]any{
		"success":                  true,
		"siteName":                 cfg.SiteName,
		"searchDisplayMode":        mode,
		"searchBadgePreferEpisode": cfg.SearchBadgePreferEpisode,
		"catPawOpenServers":        servers,
		"catPawOpenActive":         active,
		"goProxyEnabled":           cfg.GoProxyEnabled,
		"goProxyAutoSelect":        cfg.GoProxyAutoSelect,
		"goProxyServersJson":       defaultString(string(goProxyJSON), "[]"),
	})
}

func handleDashboardGoProxySave(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	enabled := boolFromForm(r.FormValue("goProxyEnabled"))
	autoSelect := boolFromForm(r.FormValue("goProxyAutoSelect"))
	serversJSON := r.FormValue("goProxyServersJson")
	servers := normalizeGoProxyServers(serversJSON)
	_ = database.UpdateAppConfig(func(c *db.AppConfig) {
		c.GoProxyEnabled = enabled
		c.GoProxyAutoSelect = autoSelect
	})
	out := make([]db.GoProxyServer, 0, len(servers))
	for _, s := range servers {
		out = append(out, db.GoProxyServer{
			Name:        s.Name,
			DisplayName: s.DisplayName,
			Base:        s.Base,
			PansBaidu:   s.Pans.Baidu,
			PansQuark:   s.Pans.Quark,
		})
	}
	_ = database.ReplaceGoProxyServers(out)
	writeJSON(w, 200, map[string]any{"success": true, "goProxySync": map[string]any{"ok": nil, "skipped": true}})
}

func handleDashboardVideoPansList(w http.ResponseWriter, r *http.Request, database *db.DB) {
	switch r.Method {
	case http.MethodGet:
		raw, _ := database.ListCatPawOpenPans()
		out := make([]map[string]any, 0, len(raw))
		for _, p := range raw {
			out = append(out, map[string]any{"key": p.Key, "name": p.Name, "enable": p.Enabled})
		}
		writeJSON(w, 200, map[string]any{"success": true, "pans": out})
	case http.MethodPost:
		parseForm(r)
		listRaw := r.FormValue("list")
		var list any
		if strings.TrimSpace(listRaw) != "" {
			_ = json.Unmarshal([]byte(listRaw), &list)
		}
		norm := normalizePansAny(list)
		pans := make([]db.CatPawOpenPan, 0, len(norm))
		for _, m := range norm {
			key, _ := m["key"].(string)
			name, _ := m["name"].(string)
			enable := parseAnyBool(m["enable"], false)
			pans = append(pans, db.CatPawOpenPan{Key: strings.TrimSpace(key), Name: name, Enabled: enable})
		}
		_ = database.ReplaceCatPawOpenPans(pans)
		writeJSON(w, 200, map[string]any{"success": true, "pans": norm})
	default:
		methodNotAllowed(w)
	}
}

func handleDashboardVideoSourceSave(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	rawPans, _ := database.ListCatPawOpenPans()
	pans := make([]map[string]any, 0, len(rawPans))
	for _, p := range rawPans {
		pans = append(pans, map[string]any{"key": p.Key, "name": p.Name, "enable": p.Enabled})
	}
	writeJSON(w, 200, map[string]any{
		"success":        true,
		"sites":          mergeVideoSourceSites(database),
		"sitesRefreshed": false,
		"pans":           pans,
		"panSync":        map[string]any{"ok": nil, "skipped": true},
	})
}

func handleDashboardVideoSourceSiteStatus(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	key := strings.TrimSpace(r.FormValue("key"))
	if key == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "key 不能为空"})
		return
	}
	enabled := boolFromForm(r.FormValue("enabled"))
	_ = database.UpsertVideoSourceSiteState(key, func(s *db.VideoSourceSiteState) { s.Enabled = enabled })
	writeJSON(w, 200, map[string]any{"success": true, "key": key, "enabled": enabled})
}

func handleDashboardVideoSourceSiteHome(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	key := strings.TrimSpace(r.FormValue("key"))
	if key == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "key 不能为空"})
		return
	}
	home := boolFromForm(r.FormValue("home"))
	_ = database.UpsertVideoSourceSiteState(key, func(s *db.VideoSourceSiteState) { s.Home = home })
	writeJSON(w, 200, map[string]any{"success": true, "key": key, "home": home})
}

func handleDashboardVideoSourceSiteSearch(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	key := strings.TrimSpace(r.FormValue("key"))
	if key == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "key 不能为空"})
		return
	}
	searchEnabled := boolFromForm(r.FormValue("search"))
	if strings.Contains(strings.ToLower(key), "baseset") {
		searchEnabled = false
	}
	_ = database.UpsertVideoSourceSiteState(key, func(s *db.VideoSourceSiteState) { s.Search = searchEnabled })
	writeJSON(w, 200, map[string]any{"success": true, "key": key, "search": searchEnabled})
}

func resolveSearchCoverSite(sites []map[string]any, preferredRaw string) string {
	preferred := strings.TrimSpace(preferredRaw)
	keySet := map[string]struct{}{}
	enabledFirst := ""
	first := ""
	for _, s := range sites {
		k, _ := s["key"].(string)
		k = strings.TrimSpace(k)
		if k == "" {
			continue
		}
		if first == "" {
			first = k
		}
		keySet[k] = struct{}{}
		if enabledFirst == "" {
			enabled, _ := s["enabled"].(bool)
			if enabled {
				enabledFirst = k
			}
		}
	}
	if preferred != "" {
		if _, ok := keySet[preferred]; ok {
			return preferred
		}
	}
	if enabledFirst != "" {
		return enabledFirst
	}
	return first
}

func handleDashboardVideoSourceCoverSite(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	key := strings.TrimSpace(r.FormValue("key"))
	sites := mergeVideoSourceSites(database)
	cover := resolveSearchCoverSite(sites, key)
	_ = database.UpdateAppConfig(func(c *db.AppConfig) { c.VideoSourceSearchCoverSite = cover })
	writeJSON(w, 200, map[string]any{"success": true, "coverSite": cover})
}

func handleDashboardVideoSourceSiteOrder(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	orderRaw := strings.TrimSpace(r.FormValue("order"))
	var order []string
	_ = json.Unmarshal([]byte(orderRaw), &order)
	uniq := []string{}
	seen := map[string]struct{}{}
	for _, k := range order {
		k = strings.TrimSpace(k)
		if k == "" {
			continue
		}
		if _, ok := seen[k]; ok {
			continue
		}
		seen[k] = struct{}{}
		uniq = append(uniq, k)
	}
	_ = database.ReplaceVideoSourceSiteOrder(uniq)
	writeJSON(w, 200, map[string]any{"success": true, "order": uniq})
}

func handleDashboardVideoSourceSitesCheck(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	raw := strings.TrimSpace(r.FormValue("results"))
	if raw == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "results 参数无效"})
		return
	}
	var input map[string]any
	if err := json.Unmarshal([]byte(raw), &input); err != nil || input == nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "results 参数无效"})
		return
	}
	results := map[string]string{}
	for k, v := range input {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		s, _ := v.(string)
		results[key] = normalizeAvailability(s)
	}
	if len(results) == 0 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "results 参数无效"})
		return
	}

	errorInput := map[string]string{}
	rawErrors := strings.TrimSpace(r.FormValue("errors"))
	if rawErrors != "" {
		var errMap map[string]any
		if err := json.Unmarshal([]byte(rawErrors), &errMap); err == nil && errMap != nil {
			for k, v := range errMap {
				key := strings.TrimSpace(k)
				if key == "" {
					continue
				}
				s, _ := v.(string)
				s = strings.TrimSpace(s)
				if s == "" {
					continue
				}
				errorInput[key] = s
			}
		}
	}

	for k, v := range results {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		avail := normalizeAvailability(v)
		msg := strings.TrimSpace(errorInput[key])
		_ = database.UpsertVideoSourceSiteState(key, func(s *db.VideoSourceSiteState) {
			s.Availability = avail
			if msg != "" {
				s.Error = msg
			} else {
				s.Error = ""
			}
			if avail == "invalid" {
				s.Enabled = false
			}
			if avail == "category_error" {
				s.Home = false
			}
			if avail == "search_error" {
				s.Search = false
			}
		})
	}

	sites := mergeVideoSourceSites(database)
	cfg, _ := database.ReadAppConfig()
	cover := resolveSearchCoverSite(sites, cfg.VideoSourceSearchCoverSite)
	writeJSON(w, 200, map[string]any{
		"success":   true,
		"results":   results,
		"sites":     sites,
		"coverSite": cover,
	})
}

func handleDashboardVideoSourceSitesImport(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	raw := strings.TrimSpace(r.FormValue("sites"))
	if raw == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "sites 参数无效"})
		return
	}
	var input []site
	if err := json.Unmarshal([]byte(raw), &input); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "sites 参数无效"})
		return
	}
	normalized := normalizeSitesSlice(input)
	if len(normalized) == 0 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "sites 参数无效"})
		return
	}

	prevStates, _ := database.ReadVideoSourceSiteStates()
	prevStatus := map[string]bool{}
	prevHome := map[string]bool{}
	prevSearch := map[string]bool{}
	prevAvailability := map[string]string{}
	prevErrors := map[string]string{}
	for k, st := range prevStates {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		prevStatus[key] = st.Enabled
		prevHome[key] = st.Home
		prevSearch[key] = st.Search
		if strings.TrimSpace(st.Availability) != "" {
			prevAvailability[key] = st.Availability
		}
		if strings.TrimSpace(st.Error) != "" {
			prevErrors[key] = st.Error
		}
	}
	// Use current merged order as prevOrder input.
	prevMerged := mergeVideoSourceSites(database)
	prevOrder := []string{}
	for _, s := range prevMerged {
		k, _ := s["key"].(string)
		k = strings.TrimSpace(k)
		if k != "" {
			prevOrder = append(prevOrder, k)
		}
	}
	reconciled := reconcileSites(normalized, prevStatus, prevHome, prevSearch, prevOrder, prevAvailability)

	for _, s := range reconciled.Sites {
		if isConfigCenterSite(s) {
			reconciled.Search[s.Key] = false
		}
	}

	nextErrors := map[string]string{}
	for _, s := range reconciled.Sites {
		if msg, ok := prevErrors[s.Key]; ok && strings.TrimSpace(msg) != "" {
			nextErrors[s.Key] = strings.TrimSpace(msg)
		}
	}

	nextSites := make([]db.VideoSourceSite, 0, len(reconciled.Sites))
	for _, s := range reconciled.Sites {
		nextSites = append(nextSites, db.VideoSourceSite{Key: s.Key, Name: s.Name, API: s.API, Type: s.Type})
	}
	_ = database.ReplaceVideoSourceSites(nextSites)

	orderIndex := map[string]int{}
	for i, k := range reconciled.Order {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		if _, ok := orderIndex[key]; ok {
			continue
		}
		orderIndex[key] = i
	}
	for _, s := range reconciled.Sites {
		key := strings.TrimSpace(s.Key)
		if key == "" {
			continue
		}
		ord, ok := orderIndex[key]
		if !ok {
			ord = 1_000_000_000
		}
		errMsg := strings.TrimSpace(nextErrors[key])
		_ = database.UpsertVideoSourceSiteState(key, func(st *db.VideoSourceSiteState) {
			st.Enabled = reconciled.Status[key]
			st.Home = reconciled.Home[key]
			st.Search = reconciled.Search[key]
			st.Availability = reconciled.Availability[key]
			st.Error = errMsg
			st.OrderIndex = ord
		})
	}

	sites := mergeVideoSourceSites(database)
	cfg, _ := database.ReadAppConfig()
	cover := resolveSearchCoverSite(sites, cfg.VideoSourceSearchCoverSite)
	writeJSON(w, 200, map[string]any{"success": true, "sites": sites, "coverSite": cover})
}

func handleDashboardMagicSettings(w http.ResponseWriter, r *http.Request, database *db.DB) {
	switch r.Method {
	case http.MethodGet:
		cfg, _ := database.ReadAppConfig()
		cleanRules, _ := database.ListMagicEpisodeCleanRegexRules()
		episodeCleanRegex := ""
		if len(cleanRules) > 0 {
			episodeCleanRegex = cleanRules[0]
		}
		smartSourcePriorityTokens, _ := database.ListSmartSourcePriorityTokens()
		smartPanMatchTokens, _ := database.ListSmartPanMatchTokens()
		smartSourceExtractPriority := config.NormalizeSourceExtractPriority(strings.TrimSpace(cfg.SmartSourceExtractPriority))
		episodeRules, _ := database.ListMagicEpisodeRules()
		movieRules, _ := database.ListMagicMovieRules()
		aggregateRegexRules, _ := database.ListMagicAggregateRegexRules()
		writeJSON(w, 200, map[string]any{
			"success":                    true,
			"episodeCleanRegex":          episodeCleanRegex,
			"episodeCleanRegexRules":     defaultStringArray(cleanRules),
			"episodeRules":               defaultStringArray(episodeRules),
			"movieRules":                 defaultStringArray(movieRules),
			"aggregateRules":             []string{},
			"aggregateRegexRules":        defaultStringArray(aggregateRegexRules),
			"smartSourcePriorityTokens":  defaultStringArray(smartSourcePriorityTokens),
			"smartPanMatchTokens":        defaultStringArray(smartPanMatchTokens),
			"smartSourceExtractPriority": smartSourceExtractPriority,
		})
	case http.MethodPost:
		var body map[string]any
		_ = readJSONLoose(r, &body)

		episodeCleanRegex, _ := body["episodeCleanRegex"].(string)

		cleanRules := []string{}
		if v, ok := body["episodeCleanRegexRules"]; ok && v != nil {
			switch vv := v.(type) {
			case []any:
				for _, it := range vv {
					s, _ := it.(string)
					s = strings.TrimSpace(s)
					if s != "" {
						cleanRules = append(cleanRules, s)
					}
				}
			case string:
				cleanRules = parseJSONStringArray(vv)
			}
		}
		if len(cleanRules) == 0 && strings.TrimSpace(episodeCleanRegex) != "" {
			cleanRules = []string{strings.TrimSpace(episodeCleanRegex)}
		}

		readList := func(key string) []string {
			v, ok := body[key]
			if !ok || v == nil {
				return []string{}
			}
			switch vv := v.(type) {
			case []any:
				out := []string{}
				for _, it := range vv {
					s, _ := it.(string)
					s = strings.TrimSpace(s)
					if s != "" {
						out = append(out, s)
					}
				}
				return out
			case string:
				return parseJSONStringArray(vv)
			default:
				return []string{}
			}
		}

		_ = database.ReplaceMagicEpisodeCleanRegexRules(cleanRules)
		_ = database.ReplaceMagicEpisodeRules(readList("episodeRules"))
		_ = database.ReplaceMagicMovieRules(readList("movieRules"))
		_ = database.ReplaceMagicAggregateRegexRules(readList("aggregateRegexRules"))

		readCommaTokens := func(key string) []string {
			raw, ok := body[key]
			if !ok || raw == nil {
				return []string{}
			}
			switch vv := raw.(type) {
			case []any:
				out := []string{}
				for _, it := range vv {
					s, _ := it.(string)
					s = strings.TrimSpace(s)
					if s != "" {
						out = append(out, s)
					}
				}
				return normalizeSmartCommaTokens(strings.Join(out, ","))
			case string:
				return normalizeSmartCommaTokens(vv)
			default:
				return []string{}
			}
		}

		_ = database.ReplaceSmartSourcePriorityTokens(readCommaTokens("smartSourcePriorityTokens"))
		_ = database.ReplaceSmartPanMatchTokens(readCommaTokens("smartPanMatchTokens"))

		priorityRaw, _ := body["smartSourceExtractPriority"].(string)
		_ = database.UpdateAppConfig(func(c *db.AppConfig) {
			c.SmartSourceExtractPriority = config.NormalizeSourceExtractPriority(priorityRaw)
		})

		outClean, _ := database.ListMagicEpisodeCleanRegexRules()
		outEpisodeClean := ""
		if len(outClean) > 0 {
			outEpisodeClean = outClean[0]
		}
		cfg, _ := database.ReadAppConfig()
		smartSourcePriorityTokens, _ := database.ListSmartSourcePriorityTokens()
		smartPanMatchTokens, _ := database.ListSmartPanMatchTokens()
		smartSourceExtractPriority := config.NormalizeSourceExtractPriority(strings.TrimSpace(cfg.SmartSourceExtractPriority))
		episodeRules, _ := database.ListMagicEpisodeRules()
		movieRules, _ := database.ListMagicMovieRules()
		aggregateRegexRules, _ := database.ListMagicAggregateRegexRules()
		writeJSON(w, 200, map[string]any{
			"success":                    true,
			"episodeCleanRegex":          outEpisodeClean,
			"episodeCleanRegexRules":     defaultStringArray(outClean),
			"episodeRules":               defaultStringArray(episodeRules),
			"movieRules":                 defaultStringArray(movieRules),
			"aggregateRules":             []string{},
			"aggregateRegexRules":        defaultStringArray(aggregateRegexRules),
			"smartSourcePriorityTokens":  defaultStringArray(smartSourcePriorityTokens),
			"smartPanMatchTokens":        defaultStringArray(smartPanMatchTokens),
			"smartSourceExtractPriority": smartSourceExtractPriority,
		})
	default:
		methodNotAllowed(w)
	}
}

func normalizeSmartCommaTokens(input string) []string {
	raw := strings.TrimSpace(input)
	if raw == "" {
		return []string{}
	}
	raw = strings.ReplaceAll(raw, "，", ",")
	parts := strings.Split(raw, ",")
	out := []string{}
	seen := map[string]struct{}{}
	for _, p := range parts {
		t := strings.TrimSpace(p)
		if t == "" {
			continue
		}
		key := strings.ToLower(t)
		if _, ok := seen[key]; ok {
			continue
		}
		seen[key] = struct{}{}
		out = append(out, t)
	}
	return out
}

func handleDashboardUserList(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	rows, err := database.ListUsers()
	if err != nil {
		writeJSON(w, 200, map[string]any{"success": true, "users": []any{}})
		return
	}
	users := make([]map[string]any, 0, len(rows))
	for _, r := range rows {
		users = append(users, map[string]any{
			"username": r.Username,
			"role":     r.Role,
			"status":   r.Status,
		})
	}
	writeJSON(w, 200, map[string]any{"success": true, "users": users, "userCount": len(users)})
}

func handleDashboardUserAdd(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	username := strings.TrimSpace(r.FormValue("username"))
	password := strings.TrimSpace(r.FormValue("password"))

	if username == "" || password == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "添加用户失败，可能是用户名已存在或参数无效"})
		return
	}

	hashed, err := bcrypt.GenerateFromPassword([]byte(password), 10)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "添加用户失败，可能是用户名已存在或参数无效"})
		return
	}
	userID, err := database.CreateUser(username, string(hashed), "user")
	if err != nil || userID <= 0 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "添加用户失败，可能是用户名已存在或参数无效"})
		return
	}
	writeJSON(w, 200, map[string]any{"success": true})
}

func handleDashboardUserBan(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	username := strings.TrimSpace(r.FormValue("username"))
	if username == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "用户名不能为空"})
		return
	}
	next, err := database.ToggleUserStatusByUsername(username)
	if err != nil || next == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "操作失败"})
		return
	}
	writeJSON(w, 200, map[string]any{"success": true, "status": next})
}

func handleDashboardUserDelete(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	username := strings.TrimSpace(r.FormValue("username"))
	if username == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "用户名不能为空"})
		return
	}
	stats, err := database.DeleteUserCascadeByUsername(username)
	if err != nil || stats.UserDeleted <= 0 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "删除失败"})
		return
	}
	writeJSON(w, 200, map[string]any{
		"success": true,
		"deleted": map[string]any{
			"tokenDeleted":       stats.TokenDeleted,
			"historyDeleted":     stats.HistoryDeleted,
			"playHistoryDeleted": stats.PlayHistoryDeleted,
			"favoritesDeleted":   stats.FavoritesDeleted,
			"userDeleted":        stats.UserDeleted,
		},
	})
}

func handleDashboardUserUpdate(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	username := strings.TrimSpace(r.FormValue("username"))
	newUsername := strings.TrimSpace(r.FormValue("newUsername"))
	newPassword := strings.TrimSpace(r.FormValue("newPassword"))

	if username == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "用户名不能为空"})
		return
	}
	if newUsername == "" && newPassword == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "未提供修改内容"})
		return
	}

	id, err := database.GetUserIDByUsername(username)
	if err != nil {
		if err == sql.ErrNoRows {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "用户不存在"})
			return
		}
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "用户不存在"})
		return
	}

	cur, err := database.GetUserAuthByUsername(username)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "用户不存在"})
		return
	}

	finalUsername := cur.Username
	finalRole := cur.Role
	if newUsername != "" && newUsername != finalUsername {
		if err := database.UpdateUserUsernameByID(id, newUsername); err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "用户名已存在或不合法"})
			return
		}
		finalUsername = newUsername
	}

	if newPassword != "" {
		hashed, err := bcrypt.GenerateFromPassword([]byte(newPassword), 10)
		if err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "修改失败"})
			return
		}
		if err := database.UpdateUserPasswordByID(id, string(hashed)); err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "修改失败"})
			return
		}
	}

	latest, _ := database.GetUserAuthByUsername(finalUsername)
	if strings.TrimSpace(latest.Role) != "" {
		finalRole = latest.Role
	}

	writeJSON(w, 200, map[string]any{
		"success":  true,
		"username": finalUsername,
		"role":     defaultString(finalRole, "user"),
	})
}

func normalizePansAny(list any) []map[string]any {
	arr, ok := list.([]any)
	if !ok {
		return []map[string]any{}
	}
	out := []map[string]any{}
	seen := map[string]struct{}{}
	for _, it := range arr {
		m, ok := it.(map[string]any)
		if !ok {
			continue
		}
		key, _ := m["key"].(string)
		name, _ := m["name"].(string)
		enable := parseAnyBool(m["enable"], false)
		key = strings.TrimSpace(key)
		if key == "" {
			continue
		}
		if _, ok := seen[key]; ok {
			continue
		}
		seen[key] = struct{}{}
		out = append(out, map[string]any{"key": key, "name": name, "enable": enable})
	}
	return out
}

func mergeVideoSourceSites(database *db.DB) []map[string]any {
	if database == nil {
		return []map[string]any{}
	}
	rawSites, err := database.ListVideoSourceSites()
	if err != nil || len(rawSites) == 0 {
		return []map[string]any{}
	}
	states, _ := database.ReadVideoSourceSiteStates()

	sites := make([]site, 0, len(rawSites))
	for _, s := range rawSites {
		sites = append(sites, site{Key: s.Key, Name: s.Name, API: s.API, Type: s.Type})
	}

	statusMap := map[string]bool{}
	homeMap := map[string]bool{}
	searchMap := map[string]bool{}
	availabilityAny := map[string]any{}
	errorMap := map[string]string{}

	type decorated struct {
		k string
		o int
		i int
	}
	ds := make([]decorated, 0, len(sites))
	for i, s := range sites {
		st, ok := states[s.Key]
		if ok {
			statusMap[s.Key] = st.Enabled
			homeMap[s.Key] = st.Home
			searchMap[s.Key] = st.Search
			if strings.TrimSpace(st.Availability) != "" {
				availabilityAny[s.Key] = st.Availability
			}
			if strings.TrimSpace(st.Error) != "" {
				errorMap[s.Key] = st.Error
			}
			ds = append(ds, decorated{k: s.Key, o: st.OrderIndex, i: i})
		} else {
			statusMap[s.Key] = true
			homeMap[s.Key] = defaultHomeForSite(s)
			searchMap[s.Key] = false
			ds = append(ds, decorated{k: s.Key, o: 1_000_000_000, i: i})
		}
	}
	sort.Slice(ds, func(i, j int) bool {
		if ds[i].o != ds[j].o {
			return ds[i].o < ds[j].o
		}
		return ds[i].i < ds[j].i
	})
	order := make([]string, 0, len(ds))
	for _, d := range ds {
		order = append(order, d.k)
	}

	return mergeSitesWithState(sites, statusMap, homeMap, order, availabilityAny, searchMap, errorMap)
}
