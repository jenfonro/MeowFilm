package api

import (
	"errors"
	"net/http"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/metadata/tmdb"
)

func handleAPITMDBDetail(w http.ResponseWriter, r *http.Request, database *db.DB) {
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

	idStr := strings.TrimSpace(r.URL.Query().Get("id"))
	if idStr == "" {
		idStr = strings.TrimSpace(r.URL.Query().Get("tmdbId"))
	}
	id, _ := strconv.Atoi(idStr)
	if id <= 0 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "参数无效"})
		return
	}

	data, err := tmdb.GetDetailPayload(database, mediaType, id)
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
			payload["raw"] = "tmdb detail fetch returned empty result"
		}
		writeJSON(w, http.StatusBadGateway, payload)
		return
	}

	writeJSON(w, http.StatusOK, data)
}
