package api

import (
	"net/http"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/emby"
)

func handleAPITMDBSeasonHints(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	if database == nil {
		writeJSON(w, 200, map[string]any{"success": true, "seasons": []any{}})
		return
	}

	typ := strings.TrimSpace(strings.ToLower(r.URL.Query().Get("type")))
	if typ == "" {
		typ = "tv"
	}
	if typ != "tv" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "only tv supported"})
		return
	}

	id, _ := strconv.Atoi(strings.TrimSpace(r.URL.Query().Get("tmdbId")))
	if id <= 0 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "invalid tmdbId"})
		return
	}

	source := strings.TrimSpace(strings.ToLower(r.URL.Query().Get("source")))
	if source == "" {
		source = "douban"
	}
	if source != "douban" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "invalid source"})
		return
	}

	keyword := strings.TrimSpace(r.URL.Query().Get("keyword"))

	hints, _ := database.ListTMDBSeasonHints("tv", id, source)
	probed := false
	if len(hints) < 2 && keyword != "" {
		_, ok := emby.ProbeDoubanTMDBSeasonHints(database, id, keyword)
		if ok {
			probed = true
			hints, _ = database.ListTMDBSeasonHints("tv", id, source)
		}
	}

	seasons := make([]map[string]any, 0, len(hints))
	for _, h := range hints {
		if h.SeasonNumber <= 0 || h.EpisodeCount <= 0 {
			continue
		}
		seasons = append(seasons, map[string]any{
			"season":       h.SeasonNumber,
			"episodeCount": h.EpisodeCount,
		})
	}

	writeJSON(w, 200, map[string]any{
		"success": true,
		"tmdbId":  id,
		"type":    "tv",
		"source":  source,
		"probed":  probed,
		"seasons": seasons,
	})
}

