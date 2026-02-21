package api

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/auth"
	"github.com/jenfonro/meowfilm/internal/db"
)

func requireAdminUser(w http.ResponseWriter, r *http.Request) *auth.User {
	u := auth.CurrentUser(r)
	if u == nil || u.Status != "active" {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"success": false, "message": "未登录"})
		return nil
	}
	if u.Role != "admin" {
		writeJSON(w, http.StatusForbidden, map[string]any{"success": false, "message": "无权限"})
		return nil
	}
	return u
}

func handleAPISmartMatchBlockItems(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	if requireAdminUser(w, r) == nil {
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
			"keyword": it.Keyword,
			"siteKey": it.SiteKey,
			"spiderApi": it.SpiderAPI,
			"videoId": it.VideoID,
			"poster": it.Poster,
		})
	}
	writeJSON(w, 200, map[string]any{"success": true, "items": out})
}

func handleAPISmartMatchBlockAdd(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	if requireAdminUser(w, r) == nil {
		return
	}
	var body map[string]any
	_ = readJSONLoose(r, &body)
	keyword := strings.TrimSpace(readStrJSONBody(body, "keyword"))
	siteKey := strings.TrimSpace(readStrJSONBody(body, "siteKey"))
	spiderAPI := strings.TrimSpace(readStrJSONBody(body, "spiderApi"))
	videoID := strings.TrimSpace(readStrJSONBody(body, "videoId"))
	poster := strings.TrimSpace(readStrJSONBody(body, "poster"))
	if keyword == "" || siteKey == "" || videoID == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "参数无效"})
		return
	}
	if err := database.UpsertSmartMatchBlockItem(keyword, siteKey, spiderAPI, videoID, poster); err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{"success": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"success": true})
}

func handleAPISmartMatchBlockDelete(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	if requireAdminUser(w, r) == nil {
		return
	}
	var body map[string]any
	_ = readJSONLoose(r, &body)
	keyword := strings.TrimSpace(readStrJSONBody(body, "keyword"))
	siteKey := strings.TrimSpace(readStrJSONBody(body, "siteKey"))
	videoID := strings.TrimSpace(readStrJSONBody(body, "videoId"))
	if keyword == "" || siteKey == "" || videoID == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "参数无效"})
		return
	}
	if err := database.DeleteSmartMatchBlockItem(keyword, siteKey, videoID); err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{"success": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"success": true})
}
