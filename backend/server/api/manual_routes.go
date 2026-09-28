package api

import (
	"net/http"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func normalizeManualTMDBType(value string) string {
	typ := strings.TrimSpace(strings.ToLower(value))
	if typ == "tv" || typ == "movie" {
		return typ
	}
	return ""
}

func handleAPIManualTMDBManualList(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	items, err := database.ListSmartManualTMDBItems()
	if err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error()})
		return
	}
	out := make([]map[string]any, 0, len(items))
	for _, item := range items {
		title := strings.TrimSpace(item.Title)
		if item.TMDBType == "" || item.TMDBID <= 0 || title == "" {
			continue
		}
		count := item.Count
		if count < 0 {
			count = 0
		}
		out = append(out, map[string]any{
			"tmdbType": item.TMDBType,
			"tmdbId":   item.TMDBID,
			"title":    title,
			"count":    count,
		})
	}
	writeJSON(w, 200, map[string]any{"success": true, "items": out})
}

func handleAPIManualTMDBAdd(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		TMDBType string `json:"tmdbType"`
		TMDBID   int    `json:"tmdbId"`
		Title    string `json:"title"`
	}
	if err := readJSONLoose(r, &body); err != nil {
		writeJSON(w, 400, map[string]any{"success": false, "message": "invalid json"})
		return
	}
	typ := normalizeManualTMDBType(body.TMDBType)
	id := body.TMDBID
	title := strings.TrimSpace(body.Title)
	if typ == "" || id <= 0 || title == "" {
		writeJSON(w, 400, map[string]any{"success": false, "message": "invalid params"})
		return
	}
	if err := database.UpsertSmartManualTMDBItem(typ, id, title); err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"success": true})
}

func handleAPIManualTMDBDelete(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		TMDBType string `json:"tmdbType"`
		TMDBID   int    `json:"tmdbId"`
	}
	if err := readJSONLoose(r, &body); err != nil {
		writeJSON(w, 400, map[string]any{"success": false, "message": "invalid json"})
		return
	}
	typ := normalizeManualTMDBType(body.TMDBType)
	id := body.TMDBID
	if typ == "" || id <= 0 {
		writeJSON(w, 400, map[string]any{"success": false, "message": "invalid params"})
		return
	}
	if err := database.DeleteSmartManualTMDBItem(typ, id); err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"success": true})
}

func handleAPIManualItemManualList(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	tmdbType := normalizeManualTMDBType(r.URL.Query().Get("tmdbType"))
	tmdbID := 0
	enabledOnly := strings.TrimSpace(r.URL.Query().Get("enabled")) == "1"
	if raw := strings.TrimSpace(r.URL.Query().Get("tmdbId")); raw != "" {
		if v, err := strconv.Atoi(raw); err == nil && v > 0 {
			tmdbID = v
		}
	}
	if tmdbType == "" || tmdbID <= 0 {
		writeJSON(w, 400, map[string]any{"success": false, "message": "invalid params"})
		return
	}
	rows, err := database.ListSmartManualItems(tmdbType, tmdbID)
	if err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error()})
		return
	}
	out := make([]map[string]any, 0, len(rows))
	for _, row := range rows {
		if row.ID <= 0 || row.TMDBType == "" || row.TMDBID <= 0 {
			continue
		}
		if enabledOnly && !row.Enabled {
			continue
		}
		out = append(out, map[string]any{
			"id":          row.ID,
			"tmdbType":    row.TMDBType,
			"tmdbId":      row.TMDBID,
			"siteKey":     strings.TrimSpace(row.SiteKey),
			"spiderApi":   strings.TrimSpace(row.SpiderAPI),
			"siteDetail":  strings.TrimSpace(row.SiteDetail),
			"panFlag":     strings.TrimSpace(row.PanFlag),
			"seasonHint":  strings.TrimSpace(row.SeasonHint),
			"errorCount":  row.ErrorCount,
			"autoDisable": row.AutoDisable,
			"enabled":     row.Enabled,
		})
	}
	writeJSON(w, 200, map[string]any{"success": true, "items": out})
}

func handleAPIManualItemAdd(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		TMDBType    string `json:"tmdbType"`
		TMDBID      int    `json:"tmdbId"`
		SiteKey     string `json:"siteKey"`
		SpiderAPI   string `json:"spiderApi"`
		SiteDetail  string `json:"siteDetail"`
		PanFlag     string `json:"panFlag"`
		SeasonHint  string `json:"seasonHint"`
		AutoDisable *bool  `json:"autoDisable"`
	}
	if err := readJSONLoose(r, &body); err != nil {
		writeJSON(w, 400, map[string]any{"success": false, "message": "invalid json"})
		return
	}
	typ := normalizeManualTMDBType(body.TMDBType)
	id := body.TMDBID
	if typ == "" || id <= 0 {
		writeJSON(w, 400, map[string]any{"success": false, "message": "invalid params"})
		return
	}
	autoDisable := true
	if body.AutoDisable != nil {
		autoDisable = *body.AutoDisable
	}
	if err := database.AddSmartManualItem(typ, id, body.SiteKey, body.SpiderAPI, body.SiteDetail, body.PanFlag, body.SeasonHint, autoDisable); err != nil {
		writeJSON(w, 400, map[string]any{"success": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"success": true})
}

func handleAPIManualItemUpdate(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		ID          int64  `json:"id"`
		SiteKey     string `json:"siteKey"`
		SpiderAPI   string `json:"spiderApi"`
		SiteDetail  string `json:"siteDetail"`
		PanFlag     string `json:"panFlag"`
		SeasonHint  string `json:"seasonHint"`
		AutoDisable *bool  `json:"autoDisable"`
	}
	if err := readJSONLoose(r, &body); err != nil {
		writeJSON(w, 400, map[string]any{"success": false, "message": "invalid json"})
		return
	}
	if body.ID <= 0 {
		writeJSON(w, 400, map[string]any{"success": false, "message": "invalid id"})
		return
	}
	autoDisable := true
	if body.AutoDisable != nil {
		autoDisable = *body.AutoDisable
	}
	if err := database.UpdateSmartManualItem(body.ID, body.SiteKey, body.SpiderAPI, body.SiteDetail, body.PanFlag, body.SeasonHint, autoDisable); err != nil {
		writeJSON(w, 400, map[string]any{"success": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"success": true})
}

func handleAPIManualItemDelete(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		ID int64 `json:"id"`
	}
	if err := readJSONLoose(r, &body); err != nil {
		writeJSON(w, 400, map[string]any{"success": false, "message": "invalid json"})
		return
	}
	id := body.ID
	if id <= 0 {
		writeJSON(w, 400, map[string]any{"success": false, "message": "invalid id"})
		return
	}
	if err := database.DeleteSmartManualItem(id); err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"success": true})
}

func handleAPIManualItemReportResult(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		ID      int64 `json:"id"`
		Success *bool `json:"success"`
	}
	if err := readJSONLoose(r, &body); err != nil {
		writeJSON(w, 400, map[string]any{"success": false, "message": "invalid json"})
		return
	}
	id := body.ID
	if id <= 0 || body.Success == nil {
		writeJSON(w, 400, map[string]any{"success": false, "message": "invalid params"})
		return
	}
	if err := database.ReportSmartManualItemResult(id, *body.Success); err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"success": true})
}
