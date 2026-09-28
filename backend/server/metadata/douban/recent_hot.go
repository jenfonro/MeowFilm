package douban

import (
	"encoding/json"
	"fmt"
	"net/url"
	"regexp"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

type RecentHotItem struct {
	DoubanID string
	Title    string
	Year     int
	Rate     string
	Subtitle string
	Poster   string
}

var recentHotYearRegex = regexp.MustCompile(`(\d{4})`)

func FetchRecentHot(database *db.DB, kind string, category string, hotType string, start int, limit int) ([]RecentHotItem, error) {
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

	base, _ := APIBase(database)
	u, _ := url.Parse(strings.TrimRight(base, "/") + "/rexxar/api/v2/subject/recent_hot/" + k)
	q := u.Query()
	q.Set("start", strconv.Itoa(start))
	q.Set("limit", strconv.Itoa(limit))
	q.Set("category", strings.TrimSpace(category))
	q.Set("type", strings.TrimSpace(hotType))

	body, err := FetchRexxarJSON(database, u.Path, q)
	if err != nil {
		return nil, err
	}
	var raw struct {
		Items []struct {
			ID           any    `json:"id"`
			Title        string `json:"title"`
			CardSubtitle string `json:"card_subtitle"`
			CoverURL     string `json:"cover_url"`
			Pic          struct {
				Normal string `json:"normal"`
				Large  string `json:"large"`
			} `json:"pic"`
			Rating struct {
				Value any `json:"value"`
			} `json:"rating"`
		} `json:"items"`
	}
	if err := json.Unmarshal(body, &raw); err != nil {
		return nil, err
	}

	out := make([]RecentHotItem, 0, len(raw.Items))
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
		if m := recentHotYearRegex.FindStringSubmatch(it.CardSubtitle); len(m) >= 2 {
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
		poster := strings.TrimSpace(it.CoverURL)
		if poster == "" {
			poster = strings.TrimSpace(it.Pic.Normal)
		}
		if poster == "" {
			poster = strings.TrimSpace(it.Pic.Large)
		}
		out = append(out, RecentHotItem{
			DoubanID: id,
			Title:    title,
			Year:     year,
			Rate:     rate,
			Subtitle: strings.TrimSpace(it.CardSubtitle),
			Poster:   poster,
		})
	}
	return out, nil
}
