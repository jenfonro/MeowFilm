package douban

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/cache"
)

type rexxarJSONCacheValue struct {
	JSON []byte
}

var rexxarSearchCache = cache.NewTwoTierTTLInflightCache[rexxarJSONCacheValue](10*time.Minute, 2048, 2*time.Minute, 4096)
var rexxarRecentHotCache = cache.NewTwoTierTTLInflightCache[rexxarJSONCacheValue](1*time.Hour, 512, 5*time.Minute, 1024)

func FetchRexxarJSON(database *db.DB, upstreamPath string, query url.Values) ([]byte, error) {
	payload, _, err := FetchRexxarJSONWithStatus(database, upstreamPath, query)
	return payload, err
}

func FetchRexxarJSONWithStatus(database *db.DB, upstreamPath string, query url.Values) ([]byte, int, error) {
	path := strings.TrimSpace(upstreamPath)
	if !strings.HasPrefix(path, "/rexxar/api/v2/") {
		return nil, http.StatusBadRequest, fmt.Errorf("unsupported rexxar path")
	}

	finalURL, err := buildRexxarFinalURL(database, path, query)
	if err != nil {
		return nil, http.StatusBadRequest, err
	}

	if detailKind, detailID, ok := parseRexxarDetailPath(path); ok {
		return fetchRexxarDetail(database, detailKind, detailID, finalURL)
	}
	if isRexxarSearchPath(path) {
		return fetchRexxarSearch(finalURL)
	}
	if isRexxarRecentHotPath(path) {
		return fetchRexxarRecentHot(finalURL)
	}
	return fetchRexxarProxyJSON(finalURL)
}

func buildRexxarFinalURL(database *db.DB, upstreamPath string, query url.Values) (string, error) {
	base, proxyBase := APIBase(database)
	apiBase := strings.TrimRight(strings.TrimSpace(base), "/")
	if apiBase == "" {
		apiBase = "https://m.douban.com"
	}

	u, err := url.Parse(apiBase + upstreamPath)
	if err != nil {
		return "", fmt.Errorf("invalid douban path")
	}
	if query != nil {
		u.RawQuery = query.Encode()
	}
	return ToProxiedURL(u.String(), proxyBase), nil
}

func fetchRexxarDetail(database *db.DB, detailKind string, detailID string, finalURL string) ([]byte, int, error) {
	if database == nil {
		return nil, http.StatusBadGateway, fmt.Errorf("database unavailable")
	}
	if cached, err := database.ReadDoubanDetailCache(detailKind, detailID); err == nil && cached != nil && strings.TrimSpace(cached.PayloadJSON) != "" {
		_ = database.TouchDoubanDetailCacheAccess(detailKind, detailID, time.Now().Unix())
		return []byte(cached.PayloadJSON), http.StatusOK, nil
	}
	payload, statusCode, err := fetchRexxarProxyJSON(finalURL)
	if err != nil {
		return nil, statusCode, err
	}
	_ = database.UpsertDoubanDetailCache(detailKind, detailID, string(payload), time.Now().Unix())
	return payload, http.StatusOK, nil
}

func fetchRexxarSearch(finalURL string) ([]byte, int, error) {
	val, _, err := rexxarSearchCache.Do(finalURL, func() (rexxarJSONCacheValue, error) {
		payload, _, fetchErr := fetchRexxarProxyJSON(finalURL)
		if fetchErr != nil {
			return rexxarJSONCacheValue{}, fetchErr
		}
		return rexxarJSONCacheValue{JSON: payload}, nil
	})
	if err != nil {
		return nil, http.StatusBadGateway, err
	}
	return val.JSON, http.StatusOK, nil
}

func fetchRexxarRecentHot(finalURL string) ([]byte, int, error) {
	val, _, err := rexxarRecentHotCache.Do(finalURL, func() (rexxarJSONCacheValue, error) {
		payload, _, fetchErr := fetchRexxarProxyJSON(finalURL)
		if fetchErr != nil {
			return rexxarJSONCacheValue{}, fetchErr
		}
		return rexxarJSONCacheValue{JSON: payload}, nil
	})
	if err != nil {
		return nil, http.StatusBadGateway, err
	}
	return val.JSON, http.StatusOK, nil
}

func fetchRexxarProxyJSON(finalURL string) ([]byte, int, error) {
	const maxBytes = 4 * 1024 * 1024

	parsed, err := url.Parse(finalURL)
	if err != nil || parsed.Scheme == "" || parsed.Host == "" {
		return nil, http.StatusBadRequest, fmt.Errorf("invalid douban url")
	}

	client := &http.Client{
		Timeout: 15 * time.Second,
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			if len(via) >= 5 {
				return http.ErrUseLastResponse
			}
			return nil
		},
	}

	req, err := http.NewRequest(http.MethodGet, parsed.String(), nil)
	if err != nil {
		return nil, http.StatusBadRequest, fmt.Errorf("invalid request")
	}
	req.Header.Set("User-Agent", "Mozilla/5.0")
	req.Header.Set("Accept", "application/json, text/plain, */*")
	req.Header.Set("Referer", "https://m.douban.com/")
	req.Header.Set("Origin", "https://m.douban.com")

	resp, err := client.Do(req)
	if err != nil || resp == nil {
		return nil, http.StatusBadGateway, fmt.Errorf("upstream request failed")
	}
	defer func() { _ = resp.Body.Close() }()

	body, err := io.ReadAll(io.LimitReader(resp.Body, maxBytes+1))
	if err != nil {
		return nil, http.StatusBadGateway, fmt.Errorf("upstream response invalid")
	}
	if len(body) > maxBytes {
		return nil, http.StatusRequestEntityTooLarge, fmt.Errorf("upstream response too large")
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, resp.StatusCode, fmt.Errorf("upstream http %d", resp.StatusCode)
	}

	var payload any
	if err := json.Unmarshal(body, &payload); err != nil {
		return nil, http.StatusBadGateway, fmt.Errorf("upstream json decode failed")
	}
	return body, http.StatusOK, nil
}

func isRexxarSearchPath(upstreamPath string) bool {
	return strings.TrimSpace(upstreamPath) == "/rexxar/api/v2/search"
}

func isRexxarRecentHotPath(upstreamPath string) bool {
	path := strings.TrimSpace(upstreamPath)
	return strings.HasPrefix(path, "/rexxar/api/v2/subject/recent_hot/")
}

func parseRexxarDetailPath(upstreamPath string) (kind string, doubanID string, ok bool) {
	path := strings.TrimSpace(upstreamPath)
	if !strings.HasPrefix(path, "/rexxar/api/v2/") {
		return "", "", false
	}
	rel := strings.TrimPrefix(path, "/rexxar/api/v2/")
	parts := strings.Split(rel, "/")
	if len(parts) != 2 {
		return "", "", false
	}
	k := strings.TrimSpace(parts[0])
	id := strings.TrimSpace(parts[1])
	if k == "" || id == "" {
		return "", "", false
	}
	switch k {
	case "tv", "movie":
		return k, id, true
	default:
		return "", "", false
	}
}

func WriteRawJSON(w http.ResponseWriter, statusCode int, body []byte) {
	if statusCode <= 0 {
		statusCode = http.StatusOK
	}
	if len(body) == 0 {
		body = []byte("{}")
	}
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Content-Length", strconv.Itoa(len(body)))
	w.WriteHeader(statusCode)
	_, _ = w.Write(body)
}
