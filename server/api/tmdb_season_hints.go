package api

import (
	"fmt"
	"net/http"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/metadata/tvmeta"
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

	meta, err := tvmeta.GetTVMeta(database, id)
	if err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{"success": false, "message": err.Error()})
		return
	}

	seasons := make([]map[string]any, 0, len(meta.DoubanSeasons))
	for _, h := range meta.DoubanSeasons {
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
		"probed":  len(meta.DoubanSeasons) > 0,
		"seasons": seasons,
	})
}

func buildPlaybackMovieID(tmdbID int) string {
	return fmt.Sprintf("tmdb_movie_%d", tmdbID)
}

func buildPlaybackEpisodeID(tmdbID int, season int, episode int) string {
	return fmt.Sprintf("tmdb_tv_%d_s%02d_e%03d", tmdbID, season, episode)
}
