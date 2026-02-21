package dashboard

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleDashboardSmartMatchBlockKeywords(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	rows, err := database.ListSmartMatchBlockKeywords()
	if err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error()})
		return
	}
	out := make([]map[string]any, 0, len(rows))
	for _, it := range rows {
		kw := strings.TrimSpace(it.Keyword)
		if kw == "" {
			continue
		}
		out = append(out, map[string]any{
			"keyword":   kw,
			"count":     it.Count,
			"updatedAt": it.UpdatedAt,
		})
	}
	writeJSON(w, 200, map[string]any{"success": true, "keywords": out})
}

func handleDashboardSmartMatchBlockItems(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	keyword := strings.TrimSpace(r.URL.Query().Get("keyword"))
	if keyword == "" {
		writeJSON(w, 200, map[string]any{"success": true, "keyword": "", "items": []any{}})
		return
	}
	rows, err := database.ListSmartMatchBlockItems(keyword)
	if err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error()})
		return
	}
	siteNameMap := map[string]string{}
	siteAPIMap := map[string]string{}
	if sites, _ := database.ListVideoSourceSites(); len(sites) > 0 {
		for _, s := range sites {
			k := strings.TrimSpace(s.Key)
			if k == "" {
				continue
			}
			siteNameMap[k] = strings.TrimSpace(s.Name)
			siteAPIMap[k] = strings.TrimSpace(s.API)
		}
	}
	out := make([]map[string]any, 0, len(rows))
	for _, it := range rows {
		sk := strings.TrimSpace(it.SiteKey)
		sapi := strings.TrimSpace(it.SpiderAPI)
		vid := strings.TrimSpace(it.VideoID)
		poster := strings.TrimSpace(it.Poster)
		if sk == "" || vid == "" {
			continue
		}
		if sapi == "" {
			sapi = siteAPIMap[sk]
		}
		out = append(out, map[string]any{
			"keyword":   keyword,
			"siteKey":   sk,
			"siteName":  siteNameMap[sk],
			"spiderApi": sapi,
			"videoId":   vid,
			"poster":    poster,
			"updatedAt": it.UpdatedAt,
		})
	}
	writeJSON(w, 200, map[string]any{"success": true, "keyword": keyword, "items": out})
}

func handleDashboardSmartMatchBlockDelete(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body map[string]any
	if err := readJSONLoose(r, &body); err != nil {
		writeJSON(w, 400, map[string]any{"success": false, "message": "invalid json"})
		return
	}
	keyword := readStrJSONBody(body, "keyword")
	siteKey := readStrJSONBody(body, "siteKey")
	videoID := readStrJSONBody(body, "videoId")
	if keyword == "" || siteKey == "" || videoID == "" {
		writeJSON(w, 400, map[string]any{"success": false, "message": "invalid params"})
		return
	}
	if err := database.DeleteSmartMatchBlockItem(keyword, siteKey, videoID); err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"success": true})
}

func handleDashboardSmartMatchBlockKeywordDelete(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body map[string]any
	if err := readJSONLoose(r, &body); err != nil {
		writeJSON(w, 400, map[string]any{"success": false, "message": "invalid json"})
		return
	}
	keyword := readStrJSONBody(body, "keyword")
	if keyword == "" {
		writeJSON(w, 400, map[string]any{"success": false, "message": "invalid params"})
		return
	}
	if err := database.DeleteSmartMatchBlockKeyword(keyword); err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"success": true})
}
