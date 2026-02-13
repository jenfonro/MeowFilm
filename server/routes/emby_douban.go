package routes

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

type embyDoubanHotItem struct {
	DoubanID string
	Title    string
	Year     int
	Rate     string
}

func embyDoubanAPIBase(database *db.DB) (base string, proxyBase string) {
	if database == nil {
		return "https://m.douban.com", ""
	}
	mode := strings.TrimSpace(database.GetSetting("douban_data_proxy"))
	custom := strings.TrimSpace(database.GetSetting("douban_data_custom"))
	switch mode {
	case "cdn-tx", "cmliussss-cdn-tencent":
		return "https://m.douban.cmliussss.net", ""
	case "cdn-ali", "cmliussss-cdn-ali":
		return "https://m.douban.cmliussss.com", ""
	case "cors", "cors-proxy-zwei", "ciao-cors":
		return "https://m.douban.com", "https://ciao-cors.is-an.org/"
	case "cors-anywhere":
		return "https://m.douban.com", "https://cors-anywhere.com/"
	case "custom":
		if custom != "" {
			return strings.TrimRight(custom, "/"), ""
		}
		return "https://m.douban.com", ""
	default:
		return "https://m.douban.com", ""
	}
}

func embyDoubanToProxiedURL(targetURL string, proxyBase string) string {
	t := strings.TrimSpace(targetURL)
	p := strings.TrimSpace(proxyBase)
	if t == "" || p == "" {
		return t
	}
	if !strings.HasSuffix(p, "/") && !strings.HasSuffix(p, "?") && !strings.HasSuffix(p, "&") && !strings.HasSuffix(p, "=") {
		p = p + "/"
	}
	// Match frontend behavior:
	// - cors-anywhere: proxyBase + targetUrl
	// - others: proxyBase + encodeURIComponent(targetUrl)
	if strings.Contains(p, "cors-anywhere.com/") {
		return p + t
	}
	return p + url.PathEscape(t)
}

func embyDoubanFetchRecentHot(database *db.DB, kind string, category string, hotType string, start int, limit int) ([]embyDoubanHotItem, error) {
	k := strings.TrimSpace(kind)
	if k != "movie" && k != "tv" {
		return nil, fmt.Errorf("invalid kind %q", kind)
	}
	if start < 0 {
		start = 0
	}
	if limit <= 0 {
		limit = 20
	}
	if limit > 60 {
		limit = 60
	}

	base, proxyBase := embyDoubanAPIBase(database)
	u, _ := url.Parse(strings.TrimRight(base, "/") + "/rexxar/api/v2/subject/recent_hot/" + k)
	q := u.Query()
	q.Set("start", strconv.Itoa(start))
	q.Set("limit", strconv.Itoa(limit))
	q.Set("category", strings.TrimSpace(category))
	q.Set("type", strings.TrimSpace(hotType))
	u.RawQuery = q.Encode()

	target := u.String()
	if proxyBase != "" {
		target = embyDoubanToProxiedURL(target, proxyBase)
	}

	client := &http.Client{Timeout: 12 * time.Second}
	req, _ := http.NewRequest(http.MethodGet, target, nil)
	req.Header.Set("Accept", "application/json, text/plain, */*")
	req.Header.Set("User-Agent", "Mozilla/5.0 (compatible; MeowFilm/1.0; +https://github.com/jenfonro/meowfilm)")
	req.Header.Set("Referer", "https://m.douban.com/")
	req.Header.Set("Origin", "https://m.douban.com")
	resp, err := client.Do(req)
	if err != nil {
		embyDebugPrintf("[emby][douban] recent_hot %s %s %s -> request error: %v", k, category, hotType, err)
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		embyDebugPrintf("[emby][douban] recent_hot %s %s %s -> http %d", k, category, hotType, resp.StatusCode)
		return nil, fmt.Errorf("douban http %d", resp.StatusCode)
	}

	body, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		embyDebugPrintf("[emby][douban] recent_hot %s %s %s -> read error: %v", k, category, hotType, err)
		return nil, err
	}

	var raw struct {
		Items []struct {
			ID           any    `json:"id"`
			Title        string `json:"title"`
			CardSubtitle string `json:"card_subtitle"`
			Rating       struct {
				Value any `json:"value"`
			} `json:"rating"`
		} `json:"items"`
	}
	if err := json.Unmarshal(body, &raw); err != nil {
		embyDebugPrintf("[emby][douban] recent_hot %s %s %s -> decode error: %v", k, category, hotType, err)
		return nil, err
	}
	if embyDebugLogEnabled() && len(raw.Items) == 0 {
		snippet := strings.TrimSpace(string(body))
		if len(snippet) > 200 {
			snippet = snippet[:200]
		}
		ct := strings.TrimSpace(resp.Header.Get("Content-Type"))
		embyDebugPrintf("[emby][douban] recent_hot %s %s %s -> empty items (ct=%q) url=%q body=%q", k, category, hotType, ct, target, snippet)
	}

	out := make([]embyDoubanHotItem, 0, len(raw.Items))
	for _, it := range raw.Items {
		id := strings.TrimSpace(fmt.Sprintf("%v", it.ID))
		if id == "" || id == "<nil>" {
			continue
		}
		title := strings.TrimSpace(it.Title)
		if title == "" {
			continue
		}
		year := 0
		if m := yearRegex.FindStringSubmatch(it.CardSubtitle); len(m) >= 2 {
			if y, err := strconv.Atoi(m[1]); err == nil && y > 0 {
				year = y
			}
		}
		rate := ""
		if it.Rating.Value != nil {
			switch vv := it.Rating.Value.(type) {
			case float64:
				if vv > 0 {
					rate = fmt.Sprintf("%.1f", vv)
				}
			case string:
				rate = strings.TrimSpace(vv)
			default:
				rate = strings.TrimSpace(fmt.Sprintf("%v", vv))
			}
		}
		out = append(out, embyDoubanHotItem{
			DoubanID: id,
			Title:    title,
			Year:     year,
			Rate:     rate,
		})
	}
	if embyDebugLogEnabled() && len(raw.Items) > 0 && len(out) == 0 {
		embyDebugPrintf("[emby][douban] recent_hot %s %s %s -> parsed 0/%d usable items (unexpected)", k, category, hotType, len(raw.Items))
	}
	return out, nil
}

var yearRegex = regexp.MustCompile(`(\d{4})`)
