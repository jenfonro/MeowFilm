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

	rawBody, raw, err := fetchTMDBSearchResponse(database, q)
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
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write(rawBody)
}

func fetchTMDBSearchResponse(database *db.DB, query string) ([]byte, tmdbMultiSearchResponse, error) {
	return fetchTMDBSearchResponseOnce(database, query)
}

func fetchTMDBSearchResponseOnce(database *db.DB, query string) ([]byte, tmdbMultiSearchResponse, error) {
	rawBody, err := fetchMultiSearchRaw(database, query)
	if err != nil {
		return nil, tmdbMultiSearchResponse{}, err
	}
	var raw tmdbMultiSearchResponse
	if err := json.Unmarshal(rawBody, &raw); err != nil {
		return nil, tmdbMultiSearchResponse{}, err
	}
	return rawBody, raw, nil
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
