package api

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/metadata/douban"
)

func handleAPIDoubanRexxarProxy(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}

	// Expect: /api/douban/rexxar/api/v2/...
	path := strings.TrimPrefix(r.URL.Path, "/api")
	if !strings.HasPrefix(path, "/douban/rexxar/api/v2/") {
		http.NotFound(w, r)
		return
	}
	upstreamPath := strings.TrimPrefix(path, "/douban")
	if !strings.HasPrefix(upstreamPath, "/rexxar/api/v2/") {
		http.NotFound(w, r)
		return
	}

	base, proxyBase := douban.APIBase(database)
	apiBase := strings.TrimRight(strings.TrimSpace(base), "/")
	if apiBase == "" {
		apiBase = "https://m.douban.com"
	}

	target := apiBase + upstreamPath
	if strings.TrimSpace(r.URL.RawQuery) != "" {
		target = target + "?" + r.URL.RawQuery
	}
	finalURL := douban.ToProxiedURL(target, proxyBase)

	const maxBytes = 4 * 1024 * 1024
	client := &http.Client{
		Timeout: 15 * time.Second,
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			if len(via) >= 5 {
				return http.ErrUseLastResponse
			}
			return nil
		},
	}

	parsed, err := url.Parse(finalURL)
	if err != nil || parsed.Scheme == "" || parsed.Host == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "URL 无效"})
		return
	}

	req, err := http.NewRequest(http.MethodGet, parsed.String(), nil)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "请求无效"})
		return
	}
	req.Header.Set("User-Agent", "Mozilla/5.0")
	req.Header.Set("Accept", "application/json, text/plain, */*")
	req.Header.Set("Referer", "https://m.douban.com/")

	resp, err := client.Do(req)
	if err != nil || resp == nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{"success": false, "message": "上游请求失败"})
		return
	}
	defer func() { _ = resp.Body.Close() }()

	body, err := io.ReadAll(io.LimitReader(resp.Body, maxBytes+1))
	if err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{"success": false, "message": "上游响应无效"})
		return
	}
	if len(body) > maxBytes {
		writeJSON(w, http.StatusRequestEntityTooLarge, map[string]any{"success": false, "message": "上游响应过大"})
		return
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		writeJSON(w, resp.StatusCode, map[string]any{"success": false, "message": fmt.Sprintf("上游 HTTP %d", resp.StatusCode)})
		return
	}

	var payload any
	if err := json.Unmarshal(body, &payload); err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{"success": false, "message": "上游 JSON 解析失败"})
		return
	}
	writeJSON(w, http.StatusOK, payload)
}

