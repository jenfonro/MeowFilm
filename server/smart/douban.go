package smart

import (
	"encoding/json"
	"fmt"
	"net/url"
	"regexp"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/metadata/douban"
)

type embyDoubanHotItem struct {
	DoubanID string
	Title    string
	Year     int
	Rate     string
}

func smartDoubanAPIBase(database *db.DB) (base string, proxyBase string) {
	return douban.APIBase(database)
}

func smartDoubanToProxiedURL(targetURL string, proxyBase string) string {
	return douban.ToProxiedURL(targetURL, proxyBase)
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

	base, _ := smartDoubanAPIBase(database)
	u, _ := url.Parse(strings.TrimRight(base, "/") + "/rexxar/api/v2/subject/recent_hot/" + k)
	q := u.Query()
	q.Set("start", strconv.Itoa(start))
	q.Set("limit", strconv.Itoa(limit))
	q.Set("category", strings.TrimSpace(category))
	q.Set("type", strings.TrimSpace(hotType))

	body, err := douban.FetchRexxarJSON(database, u.Path, q)
	if err != nil {
		smartDebugPrintf("[emby][douban] recent_hot %s %s %s -> request error: %v", k, category, hotType, err)
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
		smartDebugPrintf("[emby][douban] recent_hot %s %s %s -> decode error: %v", k, category, hotType, err)
		return nil, err
	}
	if smartDebugLogEnabled() && len(raw.Items) == 0 {
		snippet := strings.TrimSpace(string(body))
		if len(snippet) > 200 {
			snippet = snippet[:200]
		}
		smartDebugPrintf("[emby][douban] recent_hot %s %s %s -> empty items body=%q", k, category, hotType, snippet)
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
	if smartDebugLogEnabled() && len(raw.Items) > 0 && len(out) == 0 {
		smartDebugPrintf("[emby][douban] recent_hot %s %s %s -> parsed 0/%d usable items (unexpected)", k, category, hotType, len(raw.Items))
	}
	return out, nil
}

var yearRegex = regexp.MustCompile(`(\d{4})`)
