package dashboard

import (
	"encoding/json"
	"net/http"
	"net/url"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

func handleDashboardThirdPartySettings(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	sections, _ := database.ReadThirdPartyClientHomeSections()
	writeJSON(w, 200, map[string]any{
		"success":                      true,
		"thirdPartyClientHomeSections": sections,
	})
}

func handleDashboardThirdPartySave(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	raw := strings.TrimSpace(r.FormValue("thirdPartyClientHomeSectionsJson"))
	if raw == "" {
		_ = database.ReplaceThirdPartyClientHomeSections(db.DefaultThirdPartyClientHomeSections())
		writeJSON(w, 200, map[string]any{"success": true})
		return
	}
	var sections []db.ThirdPartyClientHomeSection
	if err := json.Unmarshal([]byte(raw), &sections); err != nil {
		writeJSON(w, 200, map[string]any{"success": false, "message": "JSON 格式错误"})
		return
	}
	_ = database.ReplaceThirdPartyClientHomeSections(sections)
	writeJSON(w, 200, map[string]any{"success": true})
}

func handleDashboardThirdPartySiteCategories(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	if database == nil {
		writeJSON(w, 200, map[string]any{"success": true, "categories": []any{}})
		return
	}

	siteKey := strings.TrimSpace(r.URL.Query().Get("siteKey"))
	if siteKey == "" {
		writeJSON(w, 200, map[string]any{"success": false, "message": "缺少站点 key"})
		return
	}

	sites := mergeVideoSourceSites(database)
	spiderAPI := ""
	for _, s := range sites {
		if s == nil {
			continue
		}
		k, _ := s["key"].(string)
		if strings.TrimSpace(k) != siteKey {
			continue
		}
		api, _ := s["api"].(string)
		spiderAPI = strings.TrimSpace(api)
		break
	}
	if spiderAPI == "" {
		writeJSON(w, 200, map[string]any{"success": false, "message": "站点不存在"})
		return
	}

	cfg, _ := database.ReadAppConfig()
	rawServers, _ := database.ListcatpawrunnerServers()
	servers := make([]catpawrunner.Server, 0, len(rawServers))
	for _, s := range rawServers {
		servers = append(servers, catpawrunner.Server{Name: s.Name, APIBase: s.APIBase})
	}
	apiBase := catpawrunner.ResolveActiveBase(servers, cfg.CatpawrunnerActive)
	if strings.TrimSpace(apiBase) == "" {
		writeJSON(w, 200, map[string]any{"success": false, "message": "catpawrunner 接口地址未设置"})
		return
	}
	if _, err := url.ParseRequestURI(apiBase); err != nil {
		writeJSON(w, 200, map[string]any{"success": false, "message": "catpawrunner 接口地址不是合法 URL"})
		return
	}

	raw, err := catpawrunner.RequestSpider(apiBase, spiderAPI, "home", map[string]any{})
	if err != nil || raw == nil {
		msg := "获取分类失败"
		if err != nil && strings.TrimSpace(err.Error()) != "" {
			msg = err.Error()
		}
		writeJSON(w, 200, map[string]any{"success": false, "message": msg})
		return
	}

	classAny, _ := raw["class"].([]any)
	out := make([]map[string]any, 0, len(classAny))
	seen := map[string]struct{}{}
	for _, it := range classAny {
		m, ok := it.(map[string]any)
		if !ok {
			continue
		}
		id, _ := m["type_id"].(string)
		name, _ := m["type_name"].(string)
		id = strings.TrimSpace(id)
		name = strings.TrimSpace(name)
		if id == "" || name == "" {
			continue
		}
		if _, ok := seen[id]; ok {
			continue
		}
		seen[id] = struct{}{}
		out = append(out, map[string]any{"id": id, "name": name})
		if len(out) >= 200 {
			break
		}
	}

	writeJSON(w, 200, map[string]any{"success": true, "categories": out})
}
