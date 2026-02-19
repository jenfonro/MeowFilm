package dashboard

import (
	"database/sql"
	"encoding/json"
	"net/http"
	"sort"
	"strings"

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
			"username":     r.Username,
			"role":         r.Role,
			"status":       r.Status,
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
		"success":    true,
		"username":   finalUsername,
		"role":       defaultString(finalRole, "user"),
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
