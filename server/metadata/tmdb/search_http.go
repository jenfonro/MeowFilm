package tmdb

import (
	"encoding/json"
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	mfnet "github.com/jenfonro/meowfilm/server/net"
)

type MultiSearchResponse = tmdbMultiSearchResponse

func HandleSearch(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}

	q := strings.TrimSpace(r.URL.Query().Get("q"))
	if q == "" {
		q = strings.TrimSpace(r.URL.Query().Get("query"))
	}
	if q == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "参数无效"})
		return
	}

	token, tokenKind := resolveTMDBToken(database)
	if token == "" || tokenKind == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{
			"error": "TMDB 未配置",
			"code":  "TMDB_TOKEN_INVALID",
		})
		return
	}

	rawBody, raw, fromCache, err := fetchTMDBSearchResponse(database, q)
	if err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{
			"error": "TMDB 请求失败",
			"code":  "TMDB_REQUEST_FAILED",
		})
		return
	}
	if _, err := rememberMultiSearchResults(database, &raw); err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{
			"error": "TMDB 缓存失败",
			"code":  "TMDB_CACHE_FAILED",
		})
		return
	}
	var payload map[string]any
	if err := json.Unmarshal(rawBody, &payload); err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{
			"error": "TMDB 解析失败",
			"code":  "TMDB_PARSE_FAILED",
		})
		return
	}
	payload["cache"] = fromCache
	writeJSON(w, http.StatusOK, payload)
}

func fetchTMDBSearchResponse(database *db.DB, query string) ([]byte, tmdbMultiSearchResponse, bool, error) {
	return fetchTMDBSearchResponseOnce(database, query)
}

func fetchTMDBSearchResponseOnce(database *db.DB, query string) ([]byte, tmdbMultiSearchResponse, bool, error) {
	rawBody, fromCache, err := fetchMultiSearchRaw(database, query)
	if err != nil {
		return nil, tmdbMultiSearchResponse{}, false, err
	}
	var raw tmdbMultiSearchResponse
	if err := json.Unmarshal(rawBody, &raw); err != nil {
		return nil, tmdbMultiSearchResponse{}, false, err
	}
	return rawBody, raw, fromCache, nil
}

func boolToStr(v bool) string {
	if v {
		return "true"
	}
	return "false"
}

func BoolToStr(v bool) string { return boolToStr(v) }

func writeJSON(w http.ResponseWriter, status int, payload any) {
	mfnet.WriteJSON(w, status, payload)
}

func methodNotAllowed(w http.ResponseWriter) {
	mfnet.MethodNotAllowed(w)
}
