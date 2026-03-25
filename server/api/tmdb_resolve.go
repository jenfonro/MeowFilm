package api

import (
	"net/http"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/metadata/tmdb"
)

func handleAPITMDBResolve(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}

	mediaType := strings.TrimSpace(r.URL.Query().Get("type"))
	if mediaType == "" {
		mediaType = strings.TrimSpace(r.URL.Query().Get("mediaType"))
	}
	if mediaType != "tv" && mediaType != "movie" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "参数无效"})
		return
	}

	title := strings.TrimSpace(r.URL.Query().Get("title"))
	if title == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "参数无效"})
		return
	}

	year := 0
	if rawYear := strings.TrimSpace(r.URL.Query().Get("year")); rawYear != "" {
		parsed, err := strconv.Atoi(rawYear)
		if err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]any{"error": "参数无效"})
			return
		}
		year = parsed
	}

	id, _, err := tmdb.ResolveByTitleFromCache(database, mediaType, title, year, "zh-CN")
	if err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{"error": "TMDB 解析失败"})
		return
	}

	writeJSON(w, http.StatusOK, map[string]any{
		"id":    id,
		"type":  mediaType,
		"title": title,
		"year":  year,
	})
}
