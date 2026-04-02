package api

import (
	"errors"
	"net/http"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/metadata/tmdb"
)

func handleAPITMDBSeason(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}

	idStr := strings.TrimSpace(r.URL.Query().Get("id"))
	if idStr == "" {
		idStr = strings.TrimSpace(r.URL.Query().Get("tvId"))
	}
	tmdbID, _ := strconv.Atoi(idStr)
	seasonStr := strings.TrimSpace(r.URL.Query().Get("season"))
	seasonNo, _ := strconv.Atoi(seasonStr)
	if tmdbID <= 0 || seasonStr == "" || seasonNo < 0 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "参数无效"})
		return
	}

	data, fromCache, err := tmdb.GetRawTVSeasonPayloadWithCache(database, tmdbID, seasonNo)
	if data == nil {
		payload := map[string]any{
			"error": "TMDB 请求失败",
			"code":  "TMDB_REQUEST_FAILED",
		}
		if err != nil {
			payload["raw"] = err.Error()
			var upstreamErr *tmdb.UpstreamError
			if errors.As(err, &upstreamErr) && upstreamErr != nil {
				if upstreamErr.StatusCode > 0 {
					payload["upstreamStatus"] = upstreamErr.StatusCode
				}
				if upstreamErr.Body != "" {
					payload["upstreamBody"] = upstreamErr.Body
				}
			}
		} else {
			payload["raw"] = "tmdb season payload missing"
		}
		writeJSON(w, http.StatusBadGateway, payload)
		return
	}
	data["cache"] = fromCache

	writeJSON(w, http.StatusOK, data)
}
