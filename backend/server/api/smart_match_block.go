package api

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleAPISmartMatchBlockItems(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	keyword := strings.TrimSpace(r.URL.Query().Get("keyword"))
	items, err := database.ListSmartMatchBlockItems(keyword)
	if err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{"success": false, "message": err.Error()})
		return
	}
	out := make([]map[string]any, 0, len(items))
	for _, it := range items {
		out = append(out, map[string]any{
			"keyword":    it.Keyword,
			"siteKey":    it.SiteKey,
			"spiderApi":  it.SpiderAPI,
			"siteDetail": it.SiteDetail,
			"poster":     it.Poster,
			"panFlag":    it.PanFlag,
			"source":     it.Source,
		})
	}
	writeJSON(w, 200, map[string]any{"success": true, "items": out})
}

func handleAPISmartMatchBlockAdd(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body map[string]any
	_ = readJSONLoose(r, &body)
	keyword := strings.TrimSpace(readStrJSONBody(body, "keyword"))
	siteKey := strings.TrimSpace(readStrJSONBody(body, "siteKey"))
	spiderAPI := strings.TrimSpace(readStrJSONBody(body, "spiderApi"))
	siteDetail := strings.TrimSpace(readStrJSONBody(body, "siteDetail"))
	poster := strings.TrimSpace(readStrJSONBody(body, "poster"))
	source := strings.TrimSpace(readStrJSONBody(body, "source"))
	panFlagRaw := body["panFlag"]
	if keyword == "" || siteKey == "" || siteDetail == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "参数无效"})
		return
	}
	panFlags := []string{}
	switch v := panFlagRaw.(type) {
	case []any:
		for _, it := range v {
			if it == nil {
				continue
			}
			s := strings.TrimSpace(readStrJSONBody(map[string]any{"_": it}, "_"))
			if s == "" {
				continue
			}
			panFlags = append(panFlags, s)
		}
	default:
		s := strings.TrimSpace(readStrJSONBody(body, "panFlag"))
		if s != "" {
			panFlags = append(panFlags, s)
		}
	}
	if len(panFlags) == 0 {
		panFlags = []string{""}
	}
	if source == "search" && len(panFlags) == 1 && panFlags[0] == "" {
		// Search blocks override any prior play-specific blocks for the same detail.
		_ = database.DeleteSmartMatchBlockItem(keyword, siteKey, siteDetail, "", "")
	}
	if source == "play" {
		// Keep only one play-origin record per site/video.
		_ = database.DeleteSmartMatchBlockItem(keyword, siteKey, siteDetail, "", "play")
	}
	for _, pf := range panFlags {
		if err := database.UpsertSmartMatchBlockItem(keyword, siteKey, spiderAPI, siteDetail, poster, pf, source); err != nil {
			writeJSON(w, http.StatusBadGateway, map[string]any{"success": false, "message": err.Error()})
			return
		}
	}
	writeJSON(w, 200, map[string]any{"success": true})
}

func handleAPISmartMatchBlockDelete(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body map[string]any
	_ = readJSONLoose(r, &body)
	keyword := strings.TrimSpace(readStrJSONBody(body, "keyword"))
	siteKey := strings.TrimSpace(readStrJSONBody(body, "siteKey"))
	siteDetail := strings.TrimSpace(readStrJSONBody(body, "siteDetail"))
	panFlag := strings.TrimSpace(readStrJSONBody(body, "panFlag"))
	source := strings.TrimSpace(readStrJSONBody(body, "source"))
	if keyword == "" || siteKey == "" || siteDetail == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "参数无效"})
		return
	}
	if err := database.DeleteSmartMatchBlockItem(keyword, siteKey, siteDetail, panFlag, source); err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{"success": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"success": true})
}
