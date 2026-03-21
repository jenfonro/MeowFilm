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

type UpstreamRawError struct {
	StatusCode  int
	ContentType string
	Body        []byte
	Message     string
}

func (e *UpstreamRawError) Error() string {
	if e == nil {
		return ""
	}
	if strings.TrimSpace(e.Message) != "" {
		return e.Message
	}
	if e.StatusCode > 0 {
		return fmt.Sprintf("upstream http %d", e.StatusCode)
	}
	return "upstream request failed"
}

var rexxarSearchCache = cache.NewTwoTierTTLInflightCache[rexxarJSONCacheValue](6*time.Hour, 2048, 2*time.Minute, 4096)
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
		return nil, resp.StatusCode, &UpstreamRawError{
			StatusCode:  resp.StatusCode,
			ContentType: strings.TrimSpace(resp.Header.Get("Content-Type")),
			Body:        append([]byte(nil), body...),
			Message:     fmt.Sprintf("upstream http %d", resp.StatusCode),
		}
	}

	var payload any
	if err := json.Unmarshal(body, &payload); err != nil {
		return nil, http.StatusBadGateway, fmt.Errorf("upstream json decode failed")
	}
	if statusCode, message, ok := inspectDoubanBusinessError(payload); ok {
		return nil, statusCode, &UpstreamRawError{
			StatusCode:  statusCode,
			ContentType: "application/json; charset=utf-8",
			Body:        append([]byte(nil), body...),
			Message:     message,
		}
	}
	return body, http.StatusOK, nil
}

func inspectDoubanBusinessError(payload any) (statusCode int, message string, ok bool) {
	obj, ok := payload.(map[string]any)
	if !ok {
		return 0, "", false
	}

	code, hasCode, codeInt := doubanErrorCode(obj["code"])
	msg := strings.TrimSpace(asString(obj["msg"]))
	localized := strings.TrimSpace(asString(obj["localized_message"]))
	errText := strings.TrimSpace(asString(obj["error"]))

	if hasCode && codeInt != 0 {
		message = firstNonEmpty(localized, msg, errText, fmt.Sprintf("douban business error code %d", code))
		return mapDoubanBusinessStatus(codeInt), message, true
	}
	if errText != "" {
		return http.StatusBadGateway, errText, true
	}
	switch strings.ToLower(msg) {
	case "need_login", "permission_denied", "forbidden", "rate_limit", "rate_limited", "invalid_request":
		return mapDoubanBusinessStatus(codeInt), firstNonEmpty(localized, msg), true
	default:
		return 0, "", false
	}
}

func doubanErrorCode(v any) (raw int64, has bool, normalized int) {
	switch x := v.(type) {
	case nil:
		return 0, false, 0
	case int:
		return int64(x), true, x
	case int8:
		return int64(x), true, int(x)
	case int16:
		return int64(x), true, int(x)
	case int32:
		return int64(x), true, int(x)
	case int64:
		return x, true, int(x)
	case float32:
		return int64(x), true, int(x)
	case float64:
		return int64(x), true, int(x)
	case json.Number:
		n, err := x.Int64()
		if err != nil {
			return 0, false, 0
		}
		return n, true, int(n)
	case string:
		s := strings.TrimSpace(x)
		if s == "" {
			return 0, false, 0
		}
		n, err := strconv.Atoi(s)
		if err != nil {
			return 0, false, 0
		}
		return int64(n), true, n
	default:
		return 0, false, 0
	}
}

func mapDoubanBusinessStatus(code int) int {
	switch code {
	case 103:
		return http.StatusUnauthorized
	case 403:
		return http.StatusForbidden
	case 404:
		return http.StatusNotFound
	case 429:
		return http.StatusTooManyRequests
	default:
		return http.StatusBadGateway
	}
}

func asString(v any) string {
	switch x := v.(type) {
	case string:
		return x
	case json.Number:
		return x.String()
	case fmt.Stringer:
		return x.String()
	default:
		if v == nil {
			return ""
		}
		return fmt.Sprint(v)
	}
}

func firstNonEmpty(values ...string) string {
	for _, value := range values {
		if strings.TrimSpace(value) != "" {
			return value
		}
	}
	return ""
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

func WriteRawBody(w http.ResponseWriter, statusCode int, contentType string, body []byte) {
	if statusCode <= 0 {
		statusCode = http.StatusBadGateway
	}
	if strings.TrimSpace(contentType) == "" {
		contentType = "application/octet-stream"
	}
	w.Header().Set("Content-Type", contentType)
	w.Header().Set("Content-Length", strconv.Itoa(len(body)))
	w.WriteHeader(statusCode)
	if len(body) > 0 {
		_, _ = w.Write(body)
	}
}
