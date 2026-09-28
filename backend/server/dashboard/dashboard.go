package dashboard

import (
	"database/sql"
	"encoding/json"
	"net/http"
	"sort"
	"strconv"
	"strings"
	"time"

	"golang.org/x/crypto/bcrypt"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/metadata/douban"
	"github.com/jenfonro/meowfilm/server/netdisk"
	"github.com/jenfonro/meowfilm/server/sites"
)

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

func readSmartSourceRuleRowsJSONBody(body map[string]any, key string) []db.SmartSourceRuleRow {
	if body == nil || key == "" {
		return nil
	}
	v, ok := body[key]
	if !ok || v == nil {
		return nil
	}
	arr, ok := v.([]any)
	if !ok {
		return nil
	}
	rows := make([]db.SmartSourceRuleRow, 0, len(arr))
	for _, it := range arr {
		m, ok := it.(map[string]any)
		if !ok || m == nil {
			continue
		}
		keyValue, _ := m["key"].(string)
		order := len(rows) + 1
		if f, ok := m["order"].(float64); ok {
			order = int(f)
		}
		rows = append(rows, db.SmartSourceRuleRow{
			Key:   strings.TrimSpace(keyValue),
			Order: order,
		})
	}
	return db.NormalizeSmartSourceRuleRows(rows)
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
		panAliasMappings, _ := database.ListSmartPanAliasMappings()
		panAliasOut := make([]map[string]string, 0, len(panAliasMappings))
		for _, it := range panAliasMappings {
			panAliasOut = append(panAliasOut, map[string]string{
				"pan":     strings.TrimSpace(it.Pan),
				"aliases": strings.TrimSpace(it.Aliases),
			})
		}
		writeJSON(w, 200, map[string]any{
			"success":                   true,
			"smartSourceRuleRows":       cfg.SmartSourceRuleRows,
			"siteCleanKeywords":         strings.TrimSpace(cfg.SmartSiteCleanKeywords),
			"smartSourcePriorityTokens": defaultStringArray(sourceTokens),
			"smartPanMatchTokens":       defaultStringArray(panTokens),
			"smartPanAliasMappings":     panAliasOut,
		})
	}

	switch r.Method {
	case http.MethodGet:
		writeOut()
	case http.MethodPost:
		var body map[string]any
		_ = readJSONLoose(r, &body)

		rows := readSmartSourceRuleRowsJSONBody(body, "smartSourceRuleRows")
		if len(rows) > 0 {
			_ = database.UpdateAppConfig(func(c *db.AppConfig) {
				c.SmartSourceRuleRows = rows
			})
		}
		if _, ok := body["siteCleanKeywords"]; ok {
			raw := strings.TrimSpace(readStrJSONBody(body, "siteCleanKeywords"))
			_ = database.UpdateAppConfig(func(c *db.AppConfig) {
				c.SmartSiteCleanKeywords = raw
			})
			_ = database.RecomputeSmartSkipSites()
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
		if v, ok := body["smartPanAliasMappings"]; ok && v != nil {
			if arr, ok := v.([]any); ok {
				out := make([]db.SmartPanAliasMapping, 0, len(arr))
				for _, it := range arr {
					m, ok := it.(map[string]any)
					if !ok || m == nil {
						continue
					}
					pan, _ := m["pan"].(string)
					aliases, _ := m["aliases"].(string)
					pan = strings.TrimSpace(pan)
					aliases = strings.TrimSpace(aliases)
					if pan == "" {
						continue
					}
					out = append(out, db.SmartPanAliasMapping{Pan: pan, Aliases: aliases})
				}
				_ = database.ReplaceSmartPanAliasMappings(out)
			}
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

	catServersRaw, _ := database.ListcatpawrunnerServers()
	catServers := make([]map[string]any, 0, len(catServersRaw))
	for _, s := range catServersRaw {
		catServers = append(catServers, map[string]any{"name": s.Name, "apiBase": s.APIBase})
	}
	catPansRaw, _ := database.ListcatpawrunnerPans()
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
	relayServersRaw, _ := database.ListRelayServers()
	relayServers := make([]map[string]any, 0, len(relayServersRaw))
	for _, s := range relayServersRaw {
		relayServers = append(relayServers, map[string]any{
			"name":        s.Name,
			"displayName": s.DisplayName,
			"base":        s.Base,
			"secret":      s.Secret,
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
	thirdPartyClientHomeSections, _ := database.ReadThirdPartyClientHomeSections()

	smartSourcePriorityTokens, _ := database.ListSmartSourcePriorityTokens()
	smartPanMatchTokens, _ := database.ListSmartPanMatchTokens()
	smartPanAliasMappings, _ := database.ListSmartPanAliasMappings()
	smartPanAliasOut := make([]map[string]string, 0, len(smartPanAliasMappings))
	for _, it := range smartPanAliasMappings {
		smartPanAliasOut = append(smartPanAliasOut, map[string]string{
			"pan":     strings.TrimSpace(it.Pan),
			"aliases": strings.TrimSpace(it.Aliases),
		})
	}

	writeJSON(w, 200, map[string]any{
		"success":    true,
		"format":     "meowfilm-dashboard-settings",
		"version":    1,
		"exportedAt": time.Now().Unix(),
		"site": map[string]any{
			"siteName":            strings.TrimSpace(cfg.SiteName),
			"searchDisplayMode":   defaultString(strings.TrimSpace(cfg.SearchDisplayMode), "sites"),
			"netdiskProxyEnabled": cfg.NetdiskProxyEnabled,
			"netdiskProxyUrl":     strings.TrimSpace(cfg.NetdiskProxyURL),
		},
		"catpawrunner": map[string]any{
			"active":  strings.TrimSpace(cfg.CatpawrunnerActive),
			"servers": catServers,
			"pans":    catPans,
		},
		"goProxy": map[string]any{
			"enabled":    cfg.GoProxyEnabled,
			"autoSelect": cfg.GoProxyAutoSelect,
			"servers":    goProxyServers,
		},
		"relay": map[string]any{
			"enabled": cfg.RelayEnabled,
			"auth":    strings.TrimSpace(cfg.RelayAuthToken),
			"servers": relayServers,
		},
		"pan": map[string]any{
			"loginSettings": panLogin,
		},
		"videoSource": map[string]any{
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
			"smartSourceRuleRows":       cfg.SmartSourceRuleRows,
			"siteCleanKeywords":         strings.TrimSpace(cfg.SmartSiteCleanKeywords),
			"smartSourcePriorityTokens": defaultStringArray(smartSourcePriorityTokens),
			"smartPanMatchTokens":       defaultStringArray(smartPanMatchTokens),
			"smartPanAliasMappings":     smartPanAliasOut,
		},
		"metadata": map[string]any{
			"doubanDataProxy":    douban.CanonicalDataProxyMode(cfg.DoubanDataProxy),
			"doubanDataCustom":   cfg.DoubanDataCustom,
			"doubanImgProxy":     douban.CanonicalImageProxyMode(cfg.DoubanImgProxy),
			"doubanImgCustom":    cfg.DoubanImgCustom,
			"doubanSearchCookie": strings.TrimSpace(cfg.DoubanSearchCookie),
			"tmdbApiToken":       strings.TrimSpace(cfg.TMDBAPIToken),
			"tmdbDataProxyBase":  strings.TrimSpace(cfg.TMDBAPIBase),
			"tmdbImageProxyBase": strings.TrimSpace(cfg.TMDBImgBase),
			"language":           defaultString(strings.TrimSpace(cfg.TMDBLanguage), "zh-CN"),
			"region":             defaultString(strings.TrimSpace(cfg.TMDBRegion), "CN"),
			"includeAdult":       cfg.TMDBIncludeAdult,
		},
		"thirdParty": map[string]any{
			"thirdPartyClientHomeSections": thirdPartyClientHomeSections,
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
	readTrimmedString := func(m map[string]any, key string) (string, bool) {
		if m == nil || key == "" {
			return "", false
		}
		v, ok := m[key]
		if !ok || v == nil {
			return "", false
		}
		s, _ := v.(string)
		return strings.TrimSpace(s), true
	}
	readBool := func(m map[string]any, key string) (bool, bool) {
		if m == nil || key == "" {
			return false, false
		}
		v, ok := m[key]
		if !ok || v == nil {
			return false, false
		}
		b, ok := v.(bool)
		return b, ok
	}
	applied := map[string]any{}

	if siteObj := readObj(body, "site"); siteObj != nil {
		_ = database.UpdateAppConfig(func(c *db.AppConfig) {
			if s, ok := readTrimmedString(siteObj, "siteName"); ok && s != "" {
				c.SiteName = s
			}
			if s, ok := readTrimmedString(siteObj, "searchDisplayMode"); ok {
				switch s {
				case "", "sites", "tmdb", "both":
					if s == "" {
						s = "sites"
					}
					c.SearchDisplayMode = s
				}
			}
			if b, ok := readBool(siteObj, "netdiskProxyEnabled"); ok {
				c.NetdiskProxyEnabled = b
			}
			if s, ok := readTrimmedString(siteObj, "netdiskProxyUrl"); ok {
				c.NetdiskProxyURL = s
			}
		})
		applied["site"] = true
	}

	if cat := readObj(body, "catpawrunner"); cat != nil {
		if arr := readArr(cat, "servers"); arr != nil {
			list := []db.CatpawrunnerServer{}
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
				list = append(list, db.CatpawrunnerServer{Name: n, APIBase: a})
			}
			_ = database.ReplacecatpawrunnerServers(list)
		}
		if arr := readArr(cat, "pans"); arr != nil {
			list := []db.CatpawrunnerPan{}
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
				list = append(list, db.CatpawrunnerPan{Key: key, Name: strings.TrimSpace(name), Enabled: en})
			}
			_ = database.ReplacecatpawrunnerPans(list)
		}
		if v, ok := cat["active"]; ok && v != nil {
			if s, _ := v.(string); true {
				_ = database.UpdateAppConfig(func(c *db.AppConfig) { c.CatpawrunnerActive = strings.TrimSpace(s) })
			}
		}
		applied["catpawrunner"] = true
	}

	if goProxy := readObj(body, "goProxy"); goProxy != nil {
		if enabled, ok := readBool(goProxy, "enabled"); ok {
			_ = database.UpdateAppConfig(func(c *db.AppConfig) { c.GoProxyEnabled = enabled })
		}
		if autoSelect, ok := readBool(goProxy, "autoSelect"); ok {
			_ = database.UpdateAppConfig(func(c *db.AppConfig) { c.GoProxyAutoSelect = autoSelect })
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

	if relay := readObj(body, "relay"); relay != nil {
		_ = database.UpdateAppConfig(func(c *db.AppConfig) {
			if enabled, ok := readBool(relay, "enabled"); ok {
				c.RelayEnabled = enabled
			}
			if authToken, ok := readTrimmedString(relay, "auth"); ok {
				c.RelayAuthToken = authToken
			}
		})
		if arr := readArr(relay, "servers"); arr != nil {
			list := []db.RelayServer{}
			for _, it := range arr {
				row, _ := it.(map[string]any)
				if row == nil {
					continue
				}
				name, _ := row["name"].(string)
				displayName, _ := row["displayName"].(string)
				base, _ := row["base"].(string)
				secret, _ := row["secret"].(string)
				if strings.TrimSpace(name) == "" || strings.TrimSpace(base) == "" {
					continue
				}
				list = append(list, db.RelayServer{
					Name:        strings.TrimSpace(name),
					DisplayName: strings.TrimSpace(displayName),
					Base:        strings.TrimSpace(base),
					Secret:      strings.TrimSpace(secret),
				})
			}
			_ = database.ReplaceRelayServers(list)
		}
		applied["relay"] = true
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
		rows := readSmartSourceRuleRowsJSONBody(smart, "smartSourceRuleRows")
		if len(rows) > 0 {
			_ = database.UpdateAppConfig(func(c *db.AppConfig) { c.SmartSourceRuleRows = rows })
		}
		if v, ok := smart["siteCleanKeywords"]; ok && v != nil {
			if s, _ := v.(string); true {
				_ = database.UpdateAppConfig(func(c *db.AppConfig) { c.SmartSiteCleanKeywords = strings.TrimSpace(s) })
			}
		}
		_ = database.ReplaceSmartSourcePriorityTokens(defaultStringArrayFromAny(smart["smartSourcePriorityTokens"]))
		_ = database.ReplaceSmartPanMatchTokens(defaultStringArrayFromAny(smart["smartPanMatchTokens"]))
		if v, ok := smart["smartPanAliasMappings"]; ok && v != nil {
			if arr, ok := v.([]any); ok {
				out := make([]db.SmartPanAliasMapping, 0, len(arr))
				for _, it := range arr {
					m, ok := it.(map[string]any)
					if !ok || m == nil {
						continue
					}
					pan, _ := m["pan"].(string)
					aliases, _ := m["aliases"].(string)
					pan = strings.TrimSpace(pan)
					aliases = strings.TrimSpace(aliases)
					if pan == "" {
						continue
					}
					out = append(out, db.SmartPanAliasMapping{Pan: pan, Aliases: aliases})
				}
				_ = database.ReplaceSmartPanAliasMappings(out)
			}
		}
		applied["smart"] = true
	}

	if meta := readObj(body, "metadata"); meta != nil {
		_ = database.UpdateAppConfig(func(c *db.AppConfig) {
			if v, ok := meta["doubanDataProxy"]; ok {
				if s, _ := v.(string); strings.TrimSpace(s) != "" {
					c.DoubanDataProxy = douban.CanonicalDataProxyMode(s)
				}
			}
			if v, ok := meta["doubanDataCustom"]; ok {
				if s, _ := v.(string); true {
					c.DoubanDataCustom = strings.TrimSpace(s)
				}
			}
			if v, ok := meta["doubanImgProxy"]; ok {
				if s, _ := v.(string); strings.TrimSpace(s) != "" {
					c.DoubanImgProxy = douban.CanonicalImageProxyMode(s)
				}
			}
			if v, ok := meta["doubanImgCustom"]; ok {
				if s, _ := v.(string); true {
					c.DoubanImgCustom = strings.TrimSpace(s)
				}
			}
			if v, ok := meta["doubanSearchCookie"]; ok {
				if s, _ := v.(string); true {
					c.DoubanSearchCookie = strings.TrimSpace(s)
				}
			}
			if v, ok := meta["tmdbApiToken"]; ok {
				if s, _ := v.(string); true {
					c.TMDBAPIToken = strings.TrimSpace(s)
				}
			}
			if v, ok := meta["tmdbDataProxyBase"]; ok {
				if s, _ := v.(string); true {
					c.TMDBAPIBase = catpawrunner.NormalizeHTTPBase(strings.TrimSpace(s))
				}
			}
			if v, ok := meta["tmdbImageProxyBase"]; ok {
				if s, _ := v.(string); true {
					c.TMDBImgBase = catpawrunner.NormalizeHTTPBase(strings.TrimSpace(s))
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

	thirdPartyObj := readObj(body, "thirdParty")
	if thirdPartyObj == nil {
		thirdPartyObj = readObj(body, "thirdparty")
	}
	if thirdPartyObj != nil {
		arr := readArr(thirdPartyObj, "thirdPartyClientHomeSections")
		if arr != nil {
			raw, _ := json.Marshal(arr)
			var sections []db.ThirdPartyClientHomeSection
			if err := json.Unmarshal(raw, &sections); err == nil {
				_ = database.ReplaceThirdPartyClientHomeSections(sections)
			}
		}
		applied["thirdParty"] = true
	}

	netdisk.InitNetdiskProxyFromDB(database)
	_ = database.RecomputeSmartSkipSites()

	writeJSON(w, 200, map[string]any{"success": true, "applied": applied})
}

func handleDashboardMetadataSettings(w http.ResponseWriter, r *http.Request, database *db.DB) {
	writeOut := func() {
		cfg, _ := database.ReadAppConfig()
		writeJSON(w, 200, map[string]any{
			"success":            true,
			"doubanDataProxy":    douban.CanonicalDataProxyMode(cfg.DoubanDataProxy),
			"doubanDataCustom":   cfg.DoubanDataCustom,
			"doubanImgProxy":     douban.CanonicalImageProxyMode(cfg.DoubanImgProxy),
			"doubanImgCustom":    cfg.DoubanImgCustom,
			"doubanSearchCookie": strings.TrimSpace(cfg.DoubanSearchCookie),
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
			c.DoubanDataProxy = douban.CanonicalDataProxyMode(readStrJSONBody(body, "doubanDataProxy"))
			c.DoubanDataCustom = readStrJSONBody(body, "doubanDataCustom")
			c.DoubanImgProxy = douban.CanonicalImageProxyMode(readStrJSONBody(body, "doubanImgProxy"))
			c.DoubanImgCustom = readStrJSONBody(body, "doubanImgCustom")
			c.DoubanSearchCookie = readStrJSONBody(body, "doubanSearchCookie")

			c.TMDBAPIToken = readStrJSONBody(body, "tmdbApiToken")
			c.TMDBAPIBase = catpawrunner.NormalizeHTTPBase(readStrJSONBody(body, "tmdbDataProxyBase"))
			c.TMDBImgBase = catpawrunner.NormalizeHTTPBase(readStrJSONBody(body, "tmdbImageProxyBase"))
			c.TMDBLanguage = readStrJSONBody(body, "language")
			c.TMDBRegion = readStrJSONBody(body, "region")
			c.TMDBIncludeAdult = readBoolJSONBody(body, "includeAdult")
		})

		writeOut()
	default:
		methodNotAllowed(w)
	}
}

func handleDashboardMetadataCacheClear(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body map[string]any
	_ = readJSONLoose(r, &body)
	scope := strings.ToLower(strings.TrimSpace(readStrJSONBody(body, "scope")))
	if scope != "douban" && scope != "tmdb" && scope != "all" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "缓存清理类型无效"})
		return
	}

	var err error
	switch scope {
	case "douban":
		err = database.ClearDoubanMetadataCache()
	case "tmdb":
		err = database.ClearTMDBMetadataCache()
	case "all":
		err = database.ClearAllMetadataCache()
	}
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"success": false, "message": err.Error()})
		return
	}

	message := "缓存已清理"
	switch scope {
	case "douban":
		message = "豆瓣缓存已清理"
	case "tmdb":
		message = "TMDB缓存已清理"
	case "all":
		message = "所有缓存已清理"
	}
	writeJSON(w, 200, map[string]any{"success": true, "scope": scope, "message": message})
}

func handleDashboardSiteSave(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	siteName := strings.TrimSpace(r.FormValue("siteName"))
	searchDisplayMode := strings.TrimSpace(r.FormValue("searchDisplayMode"))
	netdiskProxyEnabled := boolFromForm(r.FormValue("netdiskProxyEnabled"))
	netdiskProxyURLRaw := strings.TrimSpace(r.FormValue("netdiskProxyUrl"))
	netdiskProxyURL := strings.TrimSpace(netdiskProxyURLRaw)
	if netdiskProxyEnabled {
		norm, err := normalizeNetdiskProxyURL(netdiskProxyURLRaw)
		if err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": err.Error()})
			return
		}
		if strings.TrimSpace(norm) == "" {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "代理地址不能为空"})
			return
		}
		netdiskProxyURL = norm
	} else {
		// Normalize if possible, but keep invalid drafts so the user can fix later.
		if norm, err := normalizeNetdiskProxyURL(netdiskProxyURLRaw); err == nil && strings.TrimSpace(norm) != "" {
			netdiskProxyURL = norm
		}
	}
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
		c.NetdiskProxyEnabled = netdiskProxyEnabled
		c.NetdiskProxyURL = netdiskProxyURL
	})
	netdisk.SetNetdiskProxySettings(netdiskProxyEnabled, netdiskProxyURL)
	writeJSON(w, 200, map[string]any{"success": true})
}

func handleDashboardcatpawrunnerSave(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	cfg, _ := database.ReadAppConfig()
	raw, _ := database.ListcatpawrunnerServers()
	servers := make([]catpawrunner.Server, 0, len(raw))
	for _, s := range raw {
		servers = append(servers, catpawrunner.Server{Name: s.Name, APIBase: s.APIBase})
	}
	prevBase := catpawrunner.ResolveActiveBase(servers, cfg.CatpawrunnerActive)
	serverKey := strings.TrimSpace(r.FormValue("catpawrunnerServerKey"))
	name := strings.TrimSpace(r.FormValue("catpawrunnerName"))
	base := r.FormValue("catpawrunnerApiBase")
	normalizedBase := normalizecatpawrunnerAPIBase(base)
	if normalizedBase == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "catpawrunner 接口地址不是合法 URL"})
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
		servers[updatedIdx] = catpawrunner.Server{Name: name, APIBase: normalizedBase}
	} else {
		if existsName(name, -1) {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "服务器名称已存在"})
			return
		}
		servers = append(servers, catpawrunner.Server{Name: name, APIBase: normalizedBase})
	}

	next := make([]db.CatpawrunnerServer, 0, len(servers))
	for _, s := range servers {
		next = append(next, db.CatpawrunnerServer{Name: s.Name, APIBase: s.APIBase})
	}
	_ = database.ReplacecatpawrunnerServers(next)
	_ = database.UpdateAppConfig(func(c *db.AppConfig) { c.CatpawrunnerActive = name })
	writeJSON(w, 200, map[string]any{
		"success":        true,
		"apiBaseChanged": strings.TrimSpace(prevBase) != strings.TrimSpace(normalizedBase),
		"proxySync":      map[string]any{"ok": nil, "skipped": true},
		"goProxySync":    map[string]any{"ok": nil, "skipped": true},
	})
}

func handleDashboardcatpawrunnerDelete(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	key := strings.TrimSpace(r.FormValue("catpawrunnerServerKey"))
	if key == "" || key == "__new__" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "参数无效"})
		return
	}
	cfg, _ := database.ReadAppConfig()
	raw, _ := database.ListcatpawrunnerServers()
	servers := make([]catpawrunner.Server, 0, len(raw))
	for _, s := range raw {
		servers = append(servers, catpawrunner.Server{Name: s.Name, APIBase: s.APIBase})
	}

	removed := false
	next := make([]catpawrunner.Server, 0, len(servers))
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
	out := make([]db.CatpawrunnerServer, 0, len(next))
	for _, s := range next {
		out = append(out, db.CatpawrunnerServer{Name: s.Name, APIBase: s.APIBase})
	}
	_ = database.ReplacecatpawrunnerServers(out)
	active := catpawrunner.PickActiveName(next, cfg.CatpawrunnerActive)
	_ = database.UpdateAppConfig(func(c *db.AppConfig) { c.CatpawrunnerActive = active })

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
	raw, _ := database.ListcatpawrunnerServers()
	servers := make([]catpawrunner.Server, 0, len(raw))
	for _, s := range raw {
		servers = append(servers, catpawrunner.Server{Name: s.Name, APIBase: s.APIBase})
	}
	active := catpawrunner.PickActiveName(servers, cfg.CatpawrunnerActive)
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
	rawRelay, _ := database.ListRelayServers()
	relayForUI := make([]map[string]any, 0, len(rawRelay))
	for _, s := range rawRelay {
		relayForUI = append(relayForUI, map[string]any{
			"name":        s.Name,
			"displayName": s.DisplayName,
			"base":        s.Base,
			"secret":      s.Secret,
		})
	}
	relayJSON, _ := json.Marshal(relayForUI)
	writeJSON(w, 200, map[string]any{
		"success":             true,
		"siteName":            cfg.SiteName,
		"searchDisplayMode":   mode,
		"netdiskProxyEnabled": cfg.NetdiskProxyEnabled,
		"netdiskProxyUrl":     strings.TrimSpace(cfg.NetdiskProxyURL),
		"catpawrunnerServers": servers,
		"CatpawrunnerActive":  active,
		"goProxyEnabled":      cfg.GoProxyEnabled,
		"goProxyAutoSelect":   cfg.GoProxyAutoSelect,
		"goProxyServersJson":  defaultString(string(goProxyJSON), "[]"),
		"relayEnabled":        cfg.RelayEnabled,
		"auth":                cfg.RelayAuthToken,
		"relayServersJson":    defaultString(string(relayJSON), "[]"),
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
	if err := database.UpdateAppConfig(func(c *db.AppConfig) {
		c.GoProxyEnabled = enabled
		c.GoProxyAutoSelect = autoSelect
	}); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"success": false, "message": err.Error()})
		return
	}
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
	if err := database.ReplaceGoProxyServers(out); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"success": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"success": true, "goProxySync": map[string]any{"ok": nil, "skipped": true}})
}

func handleDashboardRelaySave(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	enabled := boolFromForm(r.FormValue("relayEnabled"))
	relayAuthToken := strings.TrimSpace(r.FormValue("auth"))
	serversJSON := r.FormValue("relayServersJson")
	servers := normalizeRelayServers(serversJSON)
	if err := database.UpdateAppConfig(func(c *db.AppConfig) {
		c.RelayEnabled = enabled
		c.RelayAuthToken = relayAuthToken
	}); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"success": false, "message": err.Error()})
		return
	}
	out := make([]db.RelayServer, 0, len(servers))
	for _, s := range servers {
		out = append(out, db.RelayServer{
			Name:        s.Name,
			DisplayName: s.DisplayName,
			Base:        s.Base,
			Secret:      s.Secret,
		})
	}
	if err := database.ReplaceRelayServers(out); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"success": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"success": true})
}

func parseNonNegativeInt(raw string) int {
	v, err := strconv.Atoi(strings.TrimSpace(raw))
	if err != nil || v < 0 {
		return 0
	}
	return v
}

func maxInt(minimum, value int) int {
	if value < minimum {
		return minimum
	}
	return value
}

func handleDashboardVideoPansList(w http.ResponseWriter, r *http.Request, database *db.DB) {
	switch r.Method {
	case http.MethodGet:
		raw, _ := database.ListcatpawrunnerPans()
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
		pans := make([]db.CatpawrunnerPan, 0, len(norm))
		for _, m := range norm {
			key, _ := m["key"].(string)
			name, _ := m["name"].(string)
			enable := parseAnyBool(m["enable"], false)
			pans = append(pans, db.CatpawrunnerPan{Key: strings.TrimSpace(key), Name: name, Enabled: enable})
		}
		_ = database.ReplacecatpawrunnerPans(pans)
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
	rawPans, _ := database.ListcatpawrunnerPans()
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
		results[key] = sites.NormalizeAvailability(s)
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
		avail := sites.NormalizeAvailability(v)
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
	var input []sites.Site
	if err := json.Unmarshal([]byte(raw), &input); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "sites 参数无效"})
		return
	}
	normalized := sites.NormalizeSitesSlice(input)
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
	reconciled := sites.ReconcileSites(normalized, prevStatus, prevHome, prevSearch, prevOrder, prevAvailability)

	for _, s := range reconciled.Sites {
		if sites.IsConfigCenterSite(s) {
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
		cleanRules, _ := database.ListMagicEpisodeCleanRegexRules()
		episodeCleanRegex := ""
		if len(cleanRules) > 0 {
			episodeCleanRegex = cleanRules[0]
		}
		smartSourcePriorityTokens, _ := database.ListSmartSourcePriorityTokens()
		smartPanMatchTokens, _ := database.ListSmartPanMatchTokens()
		smartPanAliasMappings, _ := database.ListSmartPanAliasMappings()
		smartPanAliasOut := make([]map[string]string, 0, len(smartPanAliasMappings))
		for _, it := range smartPanAliasMappings {
			smartPanAliasOut = append(smartPanAliasOut, map[string]string{
				"pan":     strings.TrimSpace(it.Pan),
				"aliases": strings.TrimSpace(it.Aliases),
			})
		}
		episodeRules, _ := database.ListMagicEpisodeRules()
		movieRules, _ := database.ListMagicMovieRules()
		aggregateRegexRules, _ := database.ListMagicAggregateRegexRules()
		writeJSON(w, 200, map[string]any{
			"success":                   true,
			"episodeCleanRegex":         episodeCleanRegex,
			"episodeCleanRegexRules":    defaultStringArray(cleanRules),
			"episodeRules":              defaultStringArray(episodeRules),
			"movieRules":                defaultStringArray(movieRules),
			"aggregateRules":            []string{},
			"aggregateRegexRules":       defaultStringArray(aggregateRegexRules),
			"smartSourcePriorityTokens": defaultStringArray(smartSourcePriorityTokens),
			"smartPanMatchTokens":       defaultStringArray(smartPanMatchTokens),
			"smartPanAliasMappings":     smartPanAliasOut,
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
		if v, ok := body["smartPanAliasMappings"]; ok && v != nil {
			if arr, ok := v.([]any); ok {
				out := make([]db.SmartPanAliasMapping, 0, len(arr))
				for _, it := range arr {
					m, ok := it.(map[string]any)
					if !ok || m == nil {
						continue
					}
					pan, _ := m["pan"].(string)
					aliases, _ := m["aliases"].(string)
					pan = strings.TrimSpace(pan)
					aliases = strings.TrimSpace(aliases)
					if pan == "" {
						continue
					}
					out = append(out, db.SmartPanAliasMapping{Pan: pan, Aliases: aliases})
				}
				_ = database.ReplaceSmartPanAliasMappings(out)
			}
		}

		outClean, _ := database.ListMagicEpisodeCleanRegexRules()
		outEpisodeClean := ""
		if len(outClean) > 0 {
			outEpisodeClean = outClean[0]
		}
		smartSourcePriorityTokens, _ := database.ListSmartSourcePriorityTokens()
		smartPanMatchTokens, _ := database.ListSmartPanMatchTokens()
		smartPanAliasMappings, _ := database.ListSmartPanAliasMappings()
		smartPanAliasOut := make([]map[string]string, 0, len(smartPanAliasMappings))
		for _, it := range smartPanAliasMappings {
			smartPanAliasOut = append(smartPanAliasOut, map[string]string{
				"pan":     strings.TrimSpace(it.Pan),
				"aliases": strings.TrimSpace(it.Aliases),
			})
		}
		episodeRules, _ := database.ListMagicEpisodeRules()
		movieRules, _ := database.ListMagicMovieRules()
		aggregateRegexRules, _ := database.ListMagicAggregateRegexRules()
		writeJSON(w, 200, map[string]any{
			"success":                   true,
			"episodeCleanRegex":         outEpisodeClean,
			"episodeCleanRegexRules":    defaultStringArray(outClean),
			"episodeRules":              defaultStringArray(episodeRules),
			"movieRules":                defaultStringArray(movieRules),
			"aggregateRules":            []string{},
			"aggregateRegexRules":       defaultStringArray(aggregateRegexRules),
			"smartSourcePriorityTokens": defaultStringArray(smartSourcePriorityTokens),
			"smartPanMatchTokens":       defaultStringArray(smartPanMatchTokens),
			"smartPanAliasMappings":     smartPanAliasOut,
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

	sitesList := make([]sites.Site, 0, len(rawSites))
	for _, s := range rawSites {
		sitesList = append(sitesList, sites.Site{Key: s.Key, Name: s.Name, API: s.API, Type: s.Type})
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
	ds := make([]decorated, 0, len(sitesList))
	for i, s := range sitesList {
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
			homeMap[s.Key] = sites.DefaultHomeForSite(s)
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

	return sites.MergeSitesWithState(sitesList, statusMap, homeMap, order, availabilityAny, searchMap, errorMap)
}
