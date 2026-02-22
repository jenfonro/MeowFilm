package emby

import (
	"encoding/json"
	"io"
	"net/http"
	"net/url"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

type doubanSeasonMetaCacheEntry struct {
	ExpireAt time.Time
	Seasons  []embyTMDBSeason
}

var doubanSeasonMetaCache = struct {
	sync.Mutex
	M map[int]doubanSeasonMetaCacheEntry
}{
	M: map[int]doubanSeasonMetaCacheEntry{},
}

const doubanSeasonMetaCacheTTL = 24 * time.Hour
const doubanSeasonMetaCacheMaxEntries = 2000

func doubanAnyIDToString(v any) string {
	switch vv := v.(type) {
	case string:
		return strings.TrimSpace(vv)
	case float64:
		if vv <= 0 {
			return ""
		}
		return strconv.FormatInt(int64(vv), 10)
	case int:
		if vv <= 0 {
			return ""
		}
		return strconv.Itoa(vv)
	default:
		return ""
	}
}

func doubanParseChineseSeasonNo(raw string) int {
	s := strings.TrimSpace(raw)
	if s == "" {
		return 0
	}
	// Arabic digits (including full-width digits)
	if n, err := strconv.Atoi(strings.NewReplacer("０", "0", "１", "1", "２", "2", "３", "3", "４", "4", "５", "5", "６", "6", "７", "7", "８", "8", "９", "9").Replace(s)); err == nil {
		if n > 0 && n <= 999 {
			return n
		}
	}
	// Basic Chinese numerals (good enough for seasons)
	m := map[rune]int{'零': 0, '〇': 0, '一': 1, '二': 2, '两': 2, '三': 3, '四': 4, '五': 5, '六': 6, '七': 7, '八': 8, '九': 9, '十': 10}
	if s == "十" {
		return 10
	}
	if strings.Contains(s, "十") {
		parts := strings.SplitN(s, "十", 2)
		tens := 1
		if parts[0] != "" {
			if v, ok := m[[]rune(parts[0])[0]]; ok && v > 0 {
				tens = v
			}
		}
		ones := 0
		if len(parts) >= 2 && parts[1] != "" {
			if v, ok := m[[]rune(parts[1])[0]]; ok && v >= 0 {
				ones = v
			}
		}
		n := tens*10 + ones
		if n > 0 && n <= 999 {
			return n
		}
	}
	if len([]rune(s)) == 1 {
		if v, ok := m[[]rune(s)[0]]; ok {
			if v > 0 && v <= 999 {
				return v
			}
		}
	}
	return 0
}

func doubanParseSeasonNoFromTitle(title string, baseHasSeason1 bool) int {
	s := strings.TrimSpace(title)
	if s == "" {
		return 0
	}
	reSeason := regexp.MustCompile(`第\s*([0-9０-９]{1,3}|[一二三四五六七八九十百千两零〇]{1,10})\s*季`)
	if m := reSeason.FindStringSubmatch(s); len(m) >= 2 && m[1] != "" {
		return doubanParseChineseSeasonNo(m[1])
	}
	reYear := regexp.MustCompile(`年番\s*([0-9０-９]{1,3}|[一二三四五六七八九十百千两零〇]{1,10})`)
	if m := reYear.FindStringSubmatch(s); len(m) >= 2 && m[1] != "" {
		n := doubanParseChineseSeasonNo(m[1])
		if n <= 0 {
			return 0
		}
		if baseHasSeason1 {
			return n + 1
		}
		return n
	}
	return 0
}

func doubanParseEpisodeCountFromInfo(text string) int {
	s := strings.TrimSpace(text)
	if s == "" {
		return 0
	}
	reAll := regexp.MustCompile(`(\d{1,4})\s*集\s*全`)
	if m := reAll.FindStringSubmatch(s); len(m) >= 2 {
		if n, err := strconv.Atoi(m[1]); err == nil && n > 0 {
			return n
		}
	}
	reUp := regexp.MustCompile(`更新至\s*第?\s*(\d{1,4})\s*(?:集|话)`)
	if m := reUp.FindStringSubmatch(s); len(m) >= 2 {
		if n, err := strconv.Atoi(m[1]); err == nil && n > 0 {
			return n
		}
	}
	reCnt := regexp.MustCompile(`(\d{1,4})\s*集`)
	if m := reCnt.FindStringSubmatch(s); len(m) >= 2 {
		if n, err := strconv.Atoi(m[1]); err == nil && n > 0 {
			return n
		}
	}
	return 0
}

func doubanParseUpdatedEpisodeCountFromInfo(text string) int {
	s := strings.TrimSpace(text)
	if s == "" {
		return 0
	}
	reUp := regexp.MustCompile(`更新至\s*第?\s*(\d{1,4})\s*(?:集|话)`)
	if m := reUp.FindStringSubmatch(s); len(m) >= 2 {
		if n, err := strconv.Atoi(m[1]); err == nil && n > 0 {
			return n
		}
	}
	return 0
}

func doubanSeasonMetaCacheGet(tmdbID int) ([]embyTMDBSeason, bool) {
	if tmdbID <= 0 {
		return nil, false
	}
	now := time.Now()
	doubanSeasonMetaCache.Lock()
	defer doubanSeasonMetaCache.Unlock()
	if doubanSeasonMetaCache.M == nil {
		doubanSeasonMetaCache.M = map[int]doubanSeasonMetaCacheEntry{}
		return nil, false
	}
	hit, ok := doubanSeasonMetaCache.M[tmdbID]
	if !ok || len(hit.Seasons) == 0 {
		return nil, false
	}
	if !hit.ExpireAt.IsZero() && hit.ExpireAt.Before(now) {
		delete(doubanSeasonMetaCache.M, tmdbID)
		return nil, false
	}
	out := make([]embyTMDBSeason, 0, len(hit.Seasons))
	out = append(out, hit.Seasons...)
	return out, true
}

func doubanSeasonMetaCacheSet(tmdbID int, seasons []embyTMDBSeason) {
	if tmdbID <= 0 || len(seasons) == 0 {
		return
	}
	now := time.Now()
	doubanSeasonMetaCache.Lock()
	if doubanSeasonMetaCache.M == nil {
		doubanSeasonMetaCache.M = map[int]doubanSeasonMetaCacheEntry{}
	}
	doubanSeasonMetaCache.M[tmdbID] = doubanSeasonMetaCacheEntry{
		ExpireAt: now.Add(doubanSeasonMetaCacheTTL),
		Seasons:  seasons,
	}
	if len(doubanSeasonMetaCache.M) > doubanSeasonMetaCacheMaxEntries {
		cut := len(doubanSeasonMetaCache.M) - doubanSeasonMetaCacheMaxEntries
		if cut < 1 {
			cut = 1
		}
		for k, v := range doubanSeasonMetaCache.M {
			if cut <= 0 {
				break
			}
			if !v.ExpireAt.IsZero() && v.ExpireAt.Before(now) {
				delete(doubanSeasonMetaCache.M, k)
				cut--
			}
		}
		for k := range doubanSeasonMetaCache.M {
			if len(doubanSeasonMetaCache.M) <= doubanSeasonMetaCacheMaxEntries {
				break
			}
			delete(doubanSeasonMetaCache.M, k)
		}
	}
	doubanSeasonMetaCache.Unlock()
}

func doubanProbeSeasons(database *db.DB, tmdbID int, keyword string) ([]embyTMDBSeason, bool) {
	id := tmdbID
	q := strings.TrimSpace(keyword)
	if id <= 0 || q == "" {
		return nil, false
	}
	if hit, ok := doubanSeasonMetaCacheGet(id); ok && len(hit) >= 2 {
		return hit, true
	}
	// Persistent hint cache (SQLite): stored as tmdb_season_hint source='douban'
	if database != nil {
		if hints, err := database.ListTMDBSeasonHints("tv", id, "douban"); err == nil && len(hints) >= 2 {
			out := make([]embyTMDBSeason, 0, len(hints))
			for _, h := range hints {
				out = append(out, embyTMDBSeason{Season: h.SeasonNumber, EpisodeCount: h.EpisodeCount})
			}
			doubanSeasonMetaCacheSet(id, out)
			return out, true
		}
	}

	base, proxyBase := embyDoubanAPIBase(database)
	u, _ := url.Parse(strings.TrimRight(base, "/") + "/rexxar/api/v2/search")
	params := u.Query()
	params.Set("q", q)
	params.Set("type", "tv")
	params.Set("start", "0")
	params.Set("count", "20")
	u.RawQuery = params.Encode()
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
	if err != nil || resp == nil {
		return nil, false
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, false
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil || len(body) == 0 {
		return nil, false
	}

	var raw struct {
		Subjects struct {
			Items []struct {
				TargetType string `json:"target_type"`
				TargetID   any    `json:"target_id"`
				Target     struct {
					ID    any    `json:"id"`
					Title string `json:"title"`
				} `json:"target"`
			} `json:"items"`
		} `json:"subjects"`
		SmartBox []struct {
			TargetType string `json:"target_type"`
			TargetID   any    `json:"target_id"`
			Target     struct {
				ID    any    `json:"id"`
				Title string `json:"title"`
			} `json:"target"`
		} `json:"smart_box"`
	}
	if err := json.Unmarshal(body, &raw); err != nil {
		return nil, false
	}

	type cand struct {
		Season int
		ID     string
		Title  string
	}
	items := make([]cand, 0, 64)
	push := func(typ string, id any, targetID any, title string) {
		if strings.TrimSpace(typ) != "tv" {
			return
		}
		t := strings.TrimSpace(title)
		if t == "" {
			return
		}
		did := doubanAnyIDToString(id)
		if did == "" {
			did = doubanAnyIDToString(targetID)
		}
		if did == "" {
			return
		}
		items = append(items, cand{ID: did, Title: t})
	}
	for _, it := range raw.Subjects.Items {
		push(it.TargetType, it.Target.ID, it.TargetID, it.Target.Title)
	}
	for _, it := range raw.SmartBox {
		push(it.TargetType, it.Target.ID, it.TargetID, it.Target.Title)
	}
	if len(items) == 0 {
		return nil, false
	}

	baseHasSeason1 := false
	reSeason1 := regexp.MustCompile(`第\s*(?:1|01|一)\s*季`)
	for _, it := range items {
		if reSeason1.MatchString(it.Title) {
			baseHasSeason1 = true
			break
		}
	}

	bySeason := map[int]cand{}
	for _, it := range items {
		seasonNo := doubanParseSeasonNoFromTitle(it.Title, baseHasSeason1)
		if seasonNo <= 0 {
			continue
		}
		if _, ok := bySeason[seasonNo]; ok {
			continue
		}
		bySeason[seasonNo] = cand{Season: seasonNo, ID: it.ID, Title: it.Title}
	}
	if len(bySeason) < 2 {
		return nil, false
	}

	list := make([]cand, 0, len(bySeason))
	for _, v := range bySeason {
		list = append(list, v)
	}
	sort.Slice(list, func(i, j int) bool { return list[i].Season < list[j].Season })
	if len(list) > 10 {
		list = list[:10]
	}

	seasons := make([]embyTMDBSeason, 0, len(list))
	for _, it := range list {
		detailURL := strings.TrimRight(base, "/") + "/rexxar/api/v2/tv/" + url.PathEscape(it.ID)
		if proxyBase != "" {
			detailURL = embyDoubanToProxiedURL(detailURL, proxyBase)
		}
		dreq, _ := http.NewRequest(http.MethodGet, detailURL, nil)
		dreq.Header.Set("Accept", "application/json, text/plain, */*")
		dreq.Header.Set("User-Agent", "Mozilla/5.0 (compatible; MeowFilm/1.0; +https://github.com/jenfonro/meowfilm)")
		dreq.Header.Set("Referer", "https://m.douban.com/")
		dreq.Header.Set("Origin", "https://m.douban.com")

		dresp, err := client.Do(dreq)
		if err != nil || dresp == nil {
			continue
		}
		func() {
			defer dresp.Body.Close()
			if dresp.StatusCode < 200 || dresp.StatusCode >= 300 {
				return
			}
			b, err := io.ReadAll(io.LimitReader(dresp.Body, 1<<20))
			if err != nil || len(b) == 0 {
				return
			}
			var d struct {
				EpisodesCount any    `json:"episodes_count"`
				EpisodesInfo  string `json:"episodes_info"`
			}
			if err := json.Unmarshal(b, &d); err != nil {
				return
			}
			epCount := doubanParseUpdatedEpisodeCountFromInfo(d.EpisodesInfo)
			switch vv := d.EpisodesCount.(type) {
			case float64:
				if epCount <= 0 && vv > 0 {
					epCount = int(vv)
				}
			case int:
				if epCount <= 0 && vv > 0 {
					epCount = vv
				}
			case string:
				if epCount <= 0 {
					if n, err := strconv.Atoi(strings.TrimSpace(vv)); err == nil && n > 0 {
						epCount = n
					}
				}
			}
			if epCount <= 0 {
				epCount = doubanParseEpisodeCountFromInfo(d.EpisodesInfo)
			}
			if epCount <= 0 {
				return
			}
			seasons = append(seasons, embyTMDBSeason{Season: it.Season, EpisodeCount: epCount})
		}()
	}
	sort.Slice(seasons, func(i, j int) bool { return seasons[i].Season < seasons[j].Season })
	if len(seasons) < 2 {
		return nil, false
	}
	// Persist season hints (best-effort)
	if database != nil {
		hints := make([]db.TMDBSeasonHint, 0, len(seasons))
		for _, s := range seasons {
			if s.Season > 0 && s.EpisodeCount > 0 {
				hints = append(hints, db.TMDBSeasonHint{SeasonNumber: s.Season, EpisodeCount: s.EpisodeCount})
			}
		}
		_ = database.UpsertTMDBSeasonHints("tv", id, "douban", hints)
	}
	doubanSeasonMetaCacheSet(id, seasons)
	return seasons, true
}
