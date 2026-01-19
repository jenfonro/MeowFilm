package routes

import (
	"database/sql"
	"encoding/json"
	"net/http"
	"strings"

	"golang.org/x/crypto/bcrypt"

	"github.com/jenfonro/TV_Server/internal/auth"
	"github.com/jenfonro/TV_Server/internal/db"
)

func DashboardHandler(database *db.DB, authMw *auth.Auth) http.Handler {
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
		case "/site/settings":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardSiteSettings(w, r, database)
			})).ServeHTTP(w, r)
		case "/openlist/save":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardOpenListSave(w, r, database)
			})).ServeHTTP(w, r)
		case "/goproxy/save":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardGoProxySave(w, r, database)
			})).ServeHTTP(w, r)
		case "/pan/settings":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardPanSettings(w, r, database)
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
				writeJSON(w, 200, map[string]any{"success": true, "videoSourceUrl": database.GetSetting("video_source_url")})
			})).ServeHTTP(w, r)
		case "/video/source/sites":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.Method != http.MethodGet {
					methodNotAllowed(w)
					return
				}
				writeJSON(w, 200, map[string]any{"success": true, "sites": mergeVideoSourceSites(database)})
			})).ServeHTTP(w, r)
		case "/video/source/sites/status":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardVideoSourceSiteStatus(w, r, database)
			})).ServeHTTP(w, r)
		case "/video/source/sites/home":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardVideoSourceSiteHome(w, r, database)
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
		case "/search/settings":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardSearchSettings(w, r, database)
			})).ServeHTTP(w, r)
		case "/magic/settings":
			authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleDashboardMagicSettings(w, r, database)
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

func handleDashboardSiteSave(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	siteName := strings.TrimSpace(r.FormValue("siteName"))
	doubanDataProxy := strings.TrimSpace(r.FormValue("doubanDataProxy"))
	doubanDataCustom := strings.TrimSpace(r.FormValue("doubanDataCustom"))
	doubanImgProxy := strings.TrimSpace(r.FormValue("doubanImgProxy"))
	doubanImgCustom := strings.TrimSpace(r.FormValue("doubanImgCustom"))
	if doubanDataProxy == "" || doubanImgProxy == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "参数无效"})
		return
	}
	if siteName != "" {
		_ = database.SetSetting("site_name", siteName)
	}
	_ = database.SetSetting("douban_data_proxy", doubanDataProxy)
	_ = database.SetSetting("douban_data_custom", doubanDataCustom)
	_ = database.SetSetting("douban_img_proxy", doubanImgProxy)
	_ = database.SetSetting("douban_img_custom", doubanImgCustom)
	writeJSON(w, 200, map[string]any{"success": true})
}

func handleDashboardCatPawOpenSave(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	prev := database.GetSetting("catpawopen_api_base")
	base := r.FormValue("catPawOpenApiBase")
	normalized := normalizeCatPawOpenAPIBase(base)
	if normalized == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "CatPawOpen 接口地址不是合法 URL"})
		return
	}
	_ = database.SetSetting("catpawopen_api_base", normalized)
	writeJSON(w, 200, map[string]any{
		"success":        true,
		"apiBaseChanged": strings.TrimSpace(prev) != strings.TrimSpace(normalized),
		"proxySync":      map[string]any{"ok": nil, "skipped": true},
		"goProxySync":    map[string]any{"ok": nil, "skipped": true},
	})
}

func handleDashboardSiteSettings(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	writeJSON(w, 200, map[string]any{
		"success":             true,
		"siteName":            database.GetSetting("site_name"),
		"catPawOpenApiBase":   database.GetSetting("catpawopen_api_base"),
		"openListApiBase":     database.GetSetting("openlist_api_base"),
		"openListToken":       database.GetSetting("openlist_token"),
		"openListQuarkTvMode": strings.TrimSpace(database.GetSetting("openlist_quark_tv_mode")) == "1",
		"openListQuarkTvMount": database.GetSetting("openlist_quark_tv_mount"),
		"goProxyEnabled":      strings.TrimSpace(database.GetSetting("goproxy_enabled")) == "1",
		"goProxyAutoSelect":   strings.TrimSpace(database.GetSetting("goproxy_auto_select")) == "1",
		"goProxyServersJson":  defaultString(database.GetSetting("goproxy_servers"), "[]"),
		"doubanDataProxy":     defaultString(database.GetSetting("douban_data_proxy"), "direct"),
		"doubanDataCustom":    database.GetSetting("douban_data_custom"),
		"doubanImgProxy":      defaultString(database.GetSetting("douban_img_proxy"), "direct-browser"),
		"doubanImgCustom":     database.GetSetting("douban_img_custom"),
	})
}

func handleDashboardOpenListSave(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	apiBase := strings.TrimSpace(r.FormValue("openListApiBase"))
	token := strings.TrimSpace(r.FormValue("openListToken"))
	quarkTv := boolFromForm(r.FormValue("openListQuarkTvMode"))
	mount := strings.TrimSpace(r.FormValue("openListQuarkTvMount"))

	if apiBase != "" {
		normalized := normalizeHTTPBase(apiBase)
		if normalized == "" {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "OpenList 服务器地址不是合法 URL"})
			return
		}
		if !strings.HasSuffix(normalized, "/") {
			normalized += "/"
		}
		_ = database.SetSetting("openlist_api_base", normalized)
	} else {
		_ = database.SetSetting("openlist_api_base", "")
	}
	_ = database.SetSetting("openlist_token", token)
	if quarkTv {
		_ = database.SetSetting("openlist_quark_tv_mode", "1")
	} else {
		_ = database.SetSetting("openlist_quark_tv_mode", "0")
	}
	if mount != "" {
		mount = normalizeMountPath(mount)
	}
	_ = database.SetSetting("openlist_quark_tv_mount", mount)
	writeJSON(w, 200, map[string]any{"success": true})
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
	if enabled {
		_ = database.SetSetting("goproxy_enabled", "1")
	} else {
		_ = database.SetSetting("goproxy_enabled", "0")
	}
	if autoSelect {
		_ = database.SetSetting("goproxy_auto_select", "1")
	} else {
		_ = database.SetSetting("goproxy_auto_select", "0")
	}
	b, _ := json.Marshal(servers)
	_ = database.SetSetting("goproxy_servers", string(b))
	writeJSON(w, 200, map[string]any{"success": true, "goProxySync": map[string]any{"ok": nil, "skipped": true}})
}

func handleDashboardPanSettings(w http.ResponseWriter, r *http.Request, database *db.DB) {
	parseForm(r)
	switch r.Method {
	case http.MethodGet:
		key := strings.TrimSpace(r.URL.Query().Get("key"))
		store := parseJSONMap(database.GetSetting("pan_login_settings"))
		if key != "" {
			v, ok := store[key]
			if !ok {
				v = map[string]any{}
			}
			writeJSON(w, 200, map[string]any{"success": true, "settings": map[string]any{key: v}})
			return
		}
		writeJSON(w, 200, map[string]any{"success": true, "settings": store})
	case http.MethodPost:
		key := strings.TrimSpace(r.FormValue("key"))
		typ := strings.TrimSpace(r.FormValue("type"))
		if key == "" {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "key 不能为空"})
			return
		}
		if typ != "cookie" && typ != "account" {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "type 参数无效"})
			return
		}
		store := parseJSONMap(database.GetSetting("pan_login_settings"))
		cur, _ := store[key].(map[string]any)
		if cur == nil {
			cur = map[string]any{}
		}
		var payload any
		if typ == "cookie" {
			cookie := r.FormValue("cookie")
			cur["cookie"] = cookie
			payload = map[string]any{"cookie": cookie}
		} else {
			username := r.FormValue("username")
			password := r.FormValue("password")
			cur["username"] = username
			cur["password"] = password
			payload = map[string]any{"username": username, "password": password}
		}
		store[key] = cur
		b, _ := json.Marshal(store)
		_ = database.SetSetting("pan_login_settings", string(b))
		writeJSON(w, 200, map[string]any{"success": true, "settings": store, "sync": map[string]any{"ok": nil, "skipped": true}, "payload": payload})
	default:
		methodNotAllowed(w)
	}
}

func handleDashboardVideoPansList(w http.ResponseWriter, r *http.Request, database *db.DB) {
	switch r.Method {
	case http.MethodGet:
		writeJSON(w, 200, map[string]any{"success": true, "pans": normalizePansList(database.GetSetting("catpawopen_pans_list"))})
	case http.MethodPost:
		parseForm(r)
		listRaw := r.FormValue("list")
		var list any
		if strings.TrimSpace(listRaw) != "" {
			_ = json.Unmarshal([]byte(listRaw), &list)
		}
		norm := normalizePansAny(list)
		b, _ := json.Marshal(norm)
		_ = database.SetSetting("catpawopen_pans_list", string(b))
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
	parseForm(r)
	rawURL := strings.TrimSpace(r.FormValue("videoSourceUrl"))
	_ = database.SetSetting("video_source_url", rawURL)
	writeJSON(w, 200, map[string]any{
		"success":        true,
		"sites":          mergeVideoSourceSites(database),
		"sitesRefreshed": false,
		"pans":           normalizePansList(database.GetSetting("catpawopen_pans_list")),
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
	m := parseJSONBoolMap(database.GetSetting("video_source_site_status"))
	m[key] = enabled
	b, _ := json.Marshal(m)
	_ = database.SetSetting("video_source_site_status", string(b))
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
	m := parseJSONBoolMap(database.GetSetting("video_source_site_home"))
	m[key] = home
	b, _ := json.Marshal(m)
	_ = database.SetSetting("video_source_site_home", string(b))
	writeJSON(w, 200, map[string]any{"success": true, "key": key, "home": home})
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
	b, _ := json.Marshal(uniq)
	_ = database.SetSetting("video_source_site_order", string(b))
	writeJSON(w, 200, map[string]any{"success": true})
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

	availabilityMap := parseAvailabilityJSON(database.GetSetting("video_source_site_availability"))
	for k, v := range results {
		availabilityMap[k] = normalizeAvailability(v)
	}
	_ = database.SetSetting("video_source_site_availability", marshalJSON(availabilityMap))

	statusMap := parseJSONBoolMap(database.GetSetting("video_source_site_status"))
	for k, v := range results {
		if v == "invalid" {
			statusMap[k] = false
		}
	}
	_ = database.SetSetting("video_source_site_status", marshalJSON(statusMap))

	writeJSON(w, 200, map[string]any{
		"success": true,
		"results": results,
		"sites":   mergeVideoSourceSites(database),
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

	prevStatus := parseJSONBoolMap(database.GetSetting("video_source_site_status"))
	prevHome := parseJSONBoolMap(database.GetSetting("video_source_site_home"))
	prevOrder := parseJSONStringArray(database.GetSetting("video_source_site_order"))
	prevAvailability := parseAvailabilityJSON(database.GetSetting("video_source_site_availability"))
	reconciled := reconcileSites(normalized, prevStatus, prevHome, prevOrder, prevAvailability)

	_ = database.SetSetting("video_source_sites", marshalJSON(reconciled.Sites))
	_ = database.SetSetting("video_source_site_status", marshalJSON(reconciled.Status))
	_ = database.SetSetting("video_source_site_home", marshalJSON(reconciled.Home))
	_ = database.SetSetting("video_source_site_order", marshalJSON(reconciled.Order))
	_ = database.SetSetting("video_source_site_availability", marshalJSON(reconciled.Availability))

	writeJSON(w, 200, map[string]any{"success": true, "sites": mergeVideoSourceSites(database)})
}

func handleDashboardSearchSettings(w http.ResponseWriter, r *http.Request, database *db.DB) {
	switch r.Method {
	case http.MethodGet:
		sites := mergeVideoSourceSites(database)
		keys := []string{}
		keySet := map[string]struct{}{}
		for _, s := range sites {
			k, _ := s["key"].(string)
			k = strings.TrimSpace(k)
			if k == "" {
				continue
			}
			keys = append(keys, k)
			keySet[k] = struct{}{}
		}

		orderRaw := parseJSONStringArray(database.GetSetting("video_source_search_order"))
		uniq := []string{}
		seen := map[string]struct{}{}
		for _, k := range orderRaw {
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
			uniq = append(uniq, key)
		}
		for _, k := range keys {
			if _, ok := seen[k]; ok {
				continue
			}
			seen[k] = struct{}{}
			uniq = append(uniq, k)
		}

		coverRaw := strings.TrimSpace(database.GetSetting("video_source_search_cover_site"))
		cover := ""
		if coverRaw != "" {
			if _, ok := keySet[coverRaw]; ok {
				cover = coverRaw
			}
		}

		enabledFirst := ""
		for _, s := range sites {
			enabled, _ := s["enabled"].(bool)
			if enabled {
				k, _ := s["key"].(string)
				enabledFirst = strings.TrimSpace(k)
				break
			}
		}
		fallbackCover := cover
		if fallbackCover == "" {
			if enabledFirst != "" {
				fallbackCover = enabledFirst
			} else if len(uniq) > 0 {
				fallbackCover = uniq[0]
			}
		}

		writeJSON(w, 200, map[string]any{
			"success": true,
			"sites":   sites,
			"search":  map[string]any{"order": uniq, "coverSite": fallbackCover},
		})
	case http.MethodPost:
		var body struct {
			Order     []string `json:"order"`
			CoverSite string   `json:"coverSite"`
		}
		_ = readJSONLoose(r, &body)

		sites := mergeVideoSourceSites(database)
		keys := []string{}
		keySet := map[string]struct{}{}
		for _, s := range sites {
			k, _ := s["key"].(string)
			k = strings.TrimSpace(k)
			if k == "" {
				continue
			}
			keys = append(keys, k)
			keySet[k] = struct{}{}
		}

		orderIn := body.Order
		uniq := []string{}
		seen := map[string]struct{}{}
		for _, k := range orderIn {
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
			uniq = append(uniq, key)
		}
		for _, k := range keys {
			if _, ok := seen[k]; ok {
				continue
			}
			seen[k] = struct{}{}
			uniq = append(uniq, k)
		}

		coverRaw := strings.TrimSpace(body.CoverSite)
		cover := ""
		if coverRaw != "" {
			if _, ok := keySet[coverRaw]; ok {
				cover = coverRaw
			}
		}
		enabledFirst := ""
		for _, s := range sites {
			enabled, _ := s["enabled"].(bool)
			if enabled {
				k, _ := s["key"].(string)
				enabledFirst = strings.TrimSpace(k)
				break
			}
		}
		fallbackCover := cover
		if fallbackCover == "" {
			if enabledFirst != "" {
				fallbackCover = enabledFirst
			} else if len(uniq) > 0 {
				fallbackCover = uniq[0]
			}
		}

		_ = database.SetSetting("video_source_search_order", marshalJSON(uniq))
		_ = database.SetSetting("video_source_search_cover_site", fallbackCover)
		writeJSON(w, 200, map[string]any{"success": true, "search": map[string]any{"order": uniq, "coverSite": fallbackCover}})
	default:
		methodNotAllowed(w)
	}
}

func handleDashboardMagicSettings(w http.ResponseWriter, r *http.Request, database *db.DB) {
	switch r.Method {
	case http.MethodGet:
		cleanRules := parseJSONStringArray(database.GetSetting("magic_episode_clean_regex_rules"))
		episodeCleanRegex := ""
		if len(cleanRules) > 0 {
			episodeCleanRegex = cleanRules[0]
		}
		writeJSON(w, 200, map[string]any{
			"success":                true,
			"episodeCleanRegex":      episodeCleanRegex,
			"episodeCleanRegexRules": cleanRules,
			"episodeRules":           parseJSONStringArray(database.GetSetting("magic_episode_rules")),
			"aggregateRules":         parseJSONStringArray(database.GetSetting("magic_aggregate_rules")),
			"aggregateRegexRules":    parseJSONStringArray(database.GetSetting("magic_aggregate_regex_rules")),
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

		saveStrArrSetting(database, "magic_episode_clean_regex_rules", cleanRules)
		saveStrArrSetting(database, "magic_episode_rules", readList("episodeRules"))
		saveStrArrSetting(database, "magic_aggregate_rules", readList("aggregateRules"))
		saveStrArrSetting(database, "magic_aggregate_regex_rules", readList("aggregateRegexRules"))

		outClean := parseJSONStringArray(database.GetSetting("magic_episode_clean_regex_rules"))
		outEpisodeClean := ""
		if len(outClean) > 0 {
			outEpisodeClean = outClean[0]
		}
		writeJSON(w, 200, map[string]any{
			"success":                true,
			"episodeCleanRegex":      outEpisodeClean,
			"episodeCleanRegexRules": outClean,
			"episodeRules":           parseJSONStringArray(database.GetSetting("magic_episode_rules")),
			"aggregateRules":         parseJSONStringArray(database.GetSetting("magic_aggregate_rules")),
			"aggregateRegexRules":    parseJSONStringArray(database.GetSetting("magic_aggregate_regex_rules")),
		})
	default:
		methodNotAllowed(w)
	}
}

func handleDashboardUserList(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	rows, err := database.SQL().Query(`
		SELECT username, role, status, cat_api_base, cat_proxy
		FROM users
		ORDER BY CASE WHEN role = 'admin' THEN 0 ELSE 1 END, username
	`)
	if err != nil {
		writeJSON(w, 200, map[string]any{"success": true, "users": []any{}})
		return
	}
	defer rows.Close()
	users := []map[string]any{}
	for rows.Next() {
		var username, role, status, catAPIBase, catProxy string
		_ = rows.Scan(&username, &role, &status, &catAPIBase, &catProxy)
		users = append(users, map[string]any{
			"username":    username,
			"role":        role,
			"status":      status,
			"cat_api_base": catAPIBase,
			"cat_proxy":   catProxy,
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
	roleRaw := strings.TrimSpace(r.FormValue("role"))
	role := "user"
	if roleRaw == "shared" {
		role = "shared"
	}
	catAPIBase := strings.TrimSpace(r.FormValue("catApiBase"))
	catProxy := strings.TrimSpace(r.FormValue("catProxy"))

	if username == "" || password == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "添加用户失败，可能是用户名已存在或参数无效"})
		return
	}

	hashed, err := bcrypt.GenerateFromPassword([]byte(password), 10)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "添加用户失败，可能是用户名已存在或参数无效"})
		return
	}
	_, err = database.SQL().Exec(
		`INSERT INTO users(username, password, role, status, cat_api_base, cat_proxy) VALUES (?, ?, ?, 'active', ?, ?)`,
		username,
		string(hashed),
		role,
		catAPIBase,
		catProxy,
	)
	if err != nil {
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
	var role, status string
	if err := database.SQL().QueryRow(`SELECT role, status FROM users WHERE username = ? LIMIT 1`, username).Scan(&role, &status); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "操作失败"})
		return
	}
	if role == "admin" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "操作失败"})
		return
	}
	next := "active"
	if status == "active" {
		next = "banned"
	}
	res, _ := database.SQL().Exec(`UPDATE users SET status = ? WHERE username = ? AND role <> 'admin'`, next, username)
	changed, _ := res.RowsAffected()
	if changed <= 0 {
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
	var id int64
	var role string
	if err := database.SQL().QueryRow(`SELECT id, role FROM users WHERE username = ? LIMIT 1`, username).Scan(&id, &role); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "删除失败"})
		return
	}
	if role == "admin" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "删除失败"})
		return
	}

	tx, err := database.SQL().Begin()
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "删除失败"})
		return
	}
	defer func() { _ = tx.Rollback() }()

	tokens, _ := tx.Exec(`DELETE FROM auth_tokens WHERE user_id = ?`, id)
	history, _ := tx.Exec(`DELETE FROM search_history WHERE user_id = ?`, id)
	playHistory, _ := tx.Exec(`DELETE FROM play_history WHERE user_id = ?`, id)
	favorites, _ := tx.Exec(`DELETE FROM favorites WHERE user_id = ?`, id)
	userRes, _ := tx.Exec(`DELETE FROM users WHERE id = ? AND role <> 'admin'`, id)

	userDeleted, _ := userRes.RowsAffected()
	if userDeleted <= 0 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "删除失败"})
		return
	}
	_ = tx.Commit()

	tokenDeleted, _ := tokens.RowsAffected()
	historyDeleted, _ := history.RowsAffected()
	playHistoryDeleted, _ := playHistory.RowsAffected()
	favoritesDeleted, _ := favorites.RowsAffected()
	writeJSON(w, 200, map[string]any{
		"success": true,
		"deleted": map[string]any{
			"tokenDeleted":       tokenDeleted,
			"historyDeleted":     historyDeleted,
			"playHistoryDeleted": playHistoryDeleted,
			"favoritesDeleted":   favoritesDeleted,
			"userDeleted":        userDeleted,
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
	roleRaw := strings.TrimSpace(r.FormValue("role"))
	_, hasCatAPIBase := r.PostForm["catApiBase"]
	_, hasCatProxy := r.PostForm["catProxy"]
	catAPIBase := ""
	catProxy := ""
	if hasCatAPIBase {
		catAPIBase = strings.TrimSpace(r.FormValue("catApiBase"))
	}
	if hasCatProxy {
		catProxy = strings.TrimSpace(r.FormValue("catProxy"))
	}

	if username == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "用户名不能为空"})
		return
	}
	if newUsername == "" && newPassword == "" && roleRaw == "" && !hasCatAPIBase && !hasCatProxy {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "未提供修改内容"})
		return
	}

	var id int64
	if err := database.SQL().QueryRow(`SELECT id FROM users WHERE username = ? LIMIT 1`, username).Scan(&id); err != nil {
		if err == sql.ErrNoRows {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "用户不存在"})
			return
		}
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "用户不存在"})
		return
	}

	var curUsername, curRole string
	if err := database.SQL().QueryRow(`SELECT username, role FROM users WHERE id = ? LIMIT 1`, id).Scan(&curUsername, &curRole); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "用户不存在"})
		return
	}

	finalUsername := curUsername
	finalRole := curRole
	if newUsername != "" && newUsername != finalUsername {
		if _, err := database.SQL().Exec(`UPDATE users SET username = ? WHERE id = ?`, newUsername, id); err != nil {
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
		res, _ := database.SQL().Exec(`UPDATE users SET password = ? WHERE id = ?`, string(hashed), id)
		changed, _ := res.RowsAffected()
		if changed <= 0 {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "修改失败"})
			return
		}
	}

	if roleRaw != "" {
		if finalRole == "admin" {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "管理员角色不可修改"})
			return
		}
		nextRole := ""
		if roleRaw == "shared" {
			nextRole = "shared"
		} else if roleRaw == "user" {
			nextRole = "user"
		}
		if nextRole == "" {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "角色无效"})
			return
		}
		res, _ := database.SQL().Exec(`UPDATE users SET role = ? WHERE id = ?`, nextRole, id)
		changed, _ := res.RowsAffected()
		if changed <= 0 {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "修改失败"})
			return
		}
		finalRole = nextRole
	}

	if hasCatAPIBase {
		_, _ = database.SQL().Exec(`UPDATE users SET cat_api_base = ? WHERE id = ?`, catAPIBase, id)
	}
	if hasCatProxy {
		_, _ = database.SQL().Exec(`UPDATE users SET cat_proxy = ? WHERE id = ?`, catProxy, id)
	}

	var rowRole, rowCatBase, rowCatProxy string
	_ = database.SQL().QueryRow(`SELECT role, cat_api_base, cat_proxy FROM users WHERE id = ? LIMIT 1`, id).Scan(&rowRole, &rowCatBase, &rowCatProxy)
	if strings.TrimSpace(rowRole) != "" {
		finalRole = rowRole
	}

	writeJSON(w, 200, map[string]any{
		"success":   true,
		"username":  finalUsername,
		"role":      defaultString(finalRole, "user"),
		"catApiBase": rowCatBase,
		"catProxy":   rowCatProxy,
	})
}

func saveStrArrSetting(database *db.DB, key string, values []string) {
	uniq := []string{}
	seen := map[string]struct{}{}
	for _, v := range values {
		v = strings.TrimSpace(v)
		if v == "" || len(v) > 1000 {
			continue
		}
		if _, ok := seen[v]; ok {
			continue
		}
		seen[v] = struct{}{}
		uniq = append(uniq, v)
	}
	b, _ := json.Marshal(uniq)
	_ = database.SetSetting(key, string(b))
}

func normalizePansList(text string) []map[string]any {
	var raw []any
	if err := json.Unmarshal([]byte(text), &raw); err != nil {
		return []map[string]any{}
	}
	return normalizePansAny(raw)
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
	sites := normalizeSitesFromJSON(database.GetSetting("video_source_sites"))
	statusMap := parseJSONBoolMap(database.GetSetting("video_source_site_status"))
	homeMap := parseJSONBoolMap(database.GetSetting("video_source_site_home"))
	order := parseJSONStringArray(database.GetSetting("video_source_site_order"))
	availability := parseJSONMap(database.GetSetting("video_source_site_availability"))
	return mergeSitesWithState(sites, statusMap, homeMap, order, availability)
}
