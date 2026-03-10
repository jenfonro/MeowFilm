package smart

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
	Seasons  []TMDBSeason
}

var doubanSeasonMetaCache = struct {
	sync.Mutex
	M map[int]doubanSeasonMetaCacheEntry
}{
	M: map[int]doubanSeasonMetaCacheEntry{},
}

const doubanSeasonMetaCacheTTL = 24 * time.Hour
const doubanSeasonMetaCacheMaxEntries = 2000
const doubanSeasonHintSource = "douban"

type doubanProbeInFlightEntry struct {
	done    chan struct{}
	ok      bool
	seasons []TMDBSeason
}

var doubanProbeInFlight = struct {
	mu sync.Mutex
	m  map[int]*doubanProbeInFlightEntry // key: tmdbID
}{
	m: map[int]*doubanProbeInFlightEntry{},
}

func doubanSeasonSum(list []TMDBSeason) int {
	sum := 0
	for _, s := range list {
		if s.Season > 0 && s.EpisodeCount > 0 {
			sum += s.EpisodeCount
		}
	}
	return sum
}

func doubanSeasonSumEnough(list []TMDBSeason, wantGlobal int) bool {
	want := wantGlobal
	if want <= 0 {
		return true
	}
	return doubanSeasonSum(list) >= want
}

func doubanProbeWaitIfAny(tmdbID int, wantGlobal int) ([]TMDBSeason, bool, bool) {
	id := tmdbID
	if id <= 0 {
		return nil, false, false
	}
	doubanProbeInFlight.mu.Lock()
	e := doubanProbeInFlight.m[id]
	doubanProbeInFlight.mu.Unlock()
	if e == nil || e.done == nil {
		return nil, false, false
	}
	select {
	case <-e.done:
	case <-time.After(14 * time.Second):
		return nil, false, true
	}
	if !e.ok || len(e.seasons) == 0 {
		return nil, false, true
	}
	if !doubanSeasonSumEnough(e.seasons, wantGlobal) {
		return nil, false, true
	}
	out := make([]TMDBSeason, 0, len(e.seasons))
	out = append(out, e.seasons...)
	return out, true, true
}

func doubanProbeStart(tmdbID int) (*doubanProbeInFlightEntry, bool) {
	id := tmdbID
	if id <= 0 {
		return nil, false
	}
	doubanProbeInFlight.mu.Lock()
	defer doubanProbeInFlight.mu.Unlock()
	if cur := doubanProbeInFlight.m[id]; cur != nil && cur.done != nil {
		return cur, false
	}
	e := &doubanProbeInFlightEntry{done: make(chan struct{}), ok: false, seasons: nil}
	doubanProbeInFlight.m[id] = e
	return e, true
}

func doubanProbeFinish(tmdbID int, e *doubanProbeInFlightEntry, seasons []TMDBSeason, ok bool) {
	id := tmdbID
	if id <= 0 || e == nil {
		return
	}
	doubanProbeInFlight.mu.Lock()
	if cur := doubanProbeInFlight.m[id]; cur == e {
		delete(doubanProbeInFlight.m, id)
	}
	doubanProbeInFlight.mu.Unlock()
	e.ok = ok
	if seasons != nil {
		out := make([]TMDBSeason, 0, len(seasons))
		out = append(out, seasons...)
		e.seasons = out
	}
	defer func() { recover() }()
	close(e.done)
}

func doubanDeleteSeasonHints(database *db.DB, tmdbID int, source string) {
	if database == nil || database.SQL() == nil || tmdbID <= 0 {
		return
	}
	src := strings.TrimSpace(strings.ToLower(source))
	if src == "" {
		return
	}
	var mediaRowID int64
	_ = database.SQL().QueryRow(`SELECT id FROM tmdb_media WHERE tmdb_type='tv' AND tmdb_id=? LIMIT 1`, tmdbID).Scan(&mediaRowID)
	if mediaRowID <= 0 {
		return
	}
	_, _ = database.SQL().Exec(`DELETE FROM tmdb_season_hint WHERE media_id=? AND source=?`, mediaRowID, src)
}

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

func doubanSeasonMetaCacheGet(tmdbID int) ([]TMDBSeason, bool) {
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
	out := make([]TMDBSeason, 0, len(hit.Seasons))
	out = append(out, hit.Seasons...)
	return out, true
}

func doubanSeasonMetaCacheDelete(tmdbID int) {
	if tmdbID <= 0 {
		return
	}
	doubanSeasonMetaCache.Lock()
	if doubanSeasonMetaCache.M != nil {
		delete(doubanSeasonMetaCache.M, tmdbID)
	}
	doubanSeasonMetaCache.Unlock()
}

func doubanSeasonMetaCacheSet(tmdbID int, seasons []TMDBSeason) {
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

func doubanProbeSeasons(database *db.DB, tmdbID int, keyword string, wantGlobal int) ([]TMDBSeason, bool) {
	id := tmdbID
	q := strings.TrimSpace(keyword)
	if id <= 0 || q == "" {
		return nil, false
	}
	// Deduplicate concurrent probes for the same TMDB ID.
	if seasons, ok, waited := doubanProbeWaitIfAny(id, wantGlobal); waited {
		return seasons, ok
	}
	inFlightEntry, started := doubanProbeStart(id)
	if !started {
		if seasons, ok, waited := doubanProbeWaitIfAny(id, wantGlobal); waited {
			return seasons, ok
		}
	}
	finished := false
	defer func() {
		if finished || inFlightEntry == nil {
			return
		}
		doubanProbeFinish(id, inFlightEntry, nil, false)
	}()

	validate := func(list []TMDBSeason) []TMDBSeason {
		out := make([]TMDBSeason, 0, len(list))
		for _, s := range list {
			if s.Season > 0 && s.EpisodeCount > 0 {
				out = append(out, s)
			}
		}
		return out
	}
	if hit, ok := doubanSeasonMetaCacheGet(id); ok && len(hit) >= 2 {
		if v := validate(hit); len(v) >= 2 {
			if doubanSeasonSumEnough(v, wantGlobal) {
				if smartDebugLogEnabled() {
					smartDebugPrintf("[smart][douban_probe] tmdbId=%d hit=mem_cache seasons=%v", id, v)
				}
				if inFlightEntry != nil {
					doubanProbeFinish(id, inFlightEntry, v, true)
					finished = true
				}
				return v, true
			}
			if smartDebugLogEnabled() {
				smartDebugPrintf("[smart][douban_probe] tmdbId=%d miss=mem_cache_sum<want want=%d seasons=%v", id, wantGlobal, v)
			}
			// Avoid reusing partial/incorrect cache (e.g. "更新至xx集" interpreted as total).
			doubanSeasonMetaCacheDelete(id)
		}
	}
	// Persistent hint cache (SQLite): stored as tmdb_season_hint source=doubanSeasonHintSource.
	if database != nil {
		if hints, err := database.ListTMDBSeasonHints("tv", id, doubanSeasonHintSource); err == nil && len(hints) >= 2 {
			out := make([]TMDBSeason, 0, len(hints))
			for _, h := range hints {
				out = append(out, TMDBSeason{Season: h.SeasonNumber, EpisodeCount: h.EpisodeCount})
			}
			if v := validate(out); len(v) >= 2 {
				if doubanSeasonSumEnough(v, wantGlobal) {
					if smartDebugLogEnabled() {
						smartDebugPrintf("[smart][douban_probe] tmdbId=%d hit=sqlite_hint seasons=%v", id, v)
					}
					doubanSeasonMetaCacheSet(id, v)
					if inFlightEntry != nil {
						doubanProbeFinish(id, inFlightEntry, v, true)
						finished = true
					}
					return v, true
				}
				if smartDebugLogEnabled() {
					smartDebugPrintf("[smart][douban_probe] tmdbId=%d miss=sqlite_hint_sum<want want=%d seasons=%v", id, wantGlobal, v)
				}
				doubanDeleteSeasonHints(database, id, doubanSeasonHintSource)
			}
		}
	}

	base, proxyBase := smartDoubanAPIBase(database)
	u, _ := url.Parse(strings.TrimRight(base, "/") + "/rexxar/api/v2/search")
	params := u.Query()
	params.Set("q", q)
	params.Set("type", "tv")
	params.Set("start", "0")
	params.Set("count", "20")
	u.RawQuery = params.Encode()
	target := u.String()
	if proxyBase != "" {
		target = smartDoubanToProxiedURL(target, proxyBase)
	}

	client := &http.Client{Timeout: 12 * time.Second}
	req, _ := http.NewRequest(http.MethodGet, target, nil)
	req.Header.Set("Accept", "application/json, text/plain, */*")
	req.Header.Set("User-Agent", "Mozilla/5.0 (compatible; MeowFilm/1.0; +https://github.com/jenfonro/meowfilm)")
	req.Header.Set("Referer", "https://m.douban.com/")
	req.Header.Set("Origin", "https://m.douban.com")
	resp, err := client.Do(req)
	if err != nil || resp == nil {
		if inFlightEntry != nil {
			doubanProbeFinish(id, inFlightEntry, nil, false)
			finished = true
		}
		return nil, false
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		if inFlightEntry != nil {
			doubanProbeFinish(id, inFlightEntry, nil, false)
			finished = true
		}
		return nil, false
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil || len(body) == 0 {
		if inFlightEntry != nil {
			doubanProbeFinish(id, inFlightEntry, nil, false)
			finished = true
		}
		return nil, false
	}

	var raw struct {
		Subjects struct {
			Items []struct {
				TargetType string `json:"target_type"`
				TargetID   any    `json:"target_id"`
				Target     struct {
					ID               any      `json:"id"`
					Title            string   `json:"title"`
					Year             any      `json:"year"`
					CardSubtitle     string   `json:"card_subtitle"`
					CardSubTitle     string   `json:"cardSubTitle"`
					Subtitle         string   `json:"subtitle"`
					SubTitle         string   `json:"sub_title"`
					NullRatingReason string   `json:"null_rating_reason"`
					IsReleased       *bool    `json:"is_released"`
					CanRate          *bool    `json:"can_rate"`
					VendorCount      any      `json:"vendor_count"`
					Pubdate          []string `json:"pubdate"`
				} `json:"target"`
			} `json:"items"`
		} `json:"subjects"`
		SmartBox []struct {
			TargetType string `json:"target_type"`
			TargetID   any    `json:"target_id"`
			Target     struct {
				ID               any      `json:"id"`
				Title            string   `json:"title"`
				Year             any      `json:"year"`
				CardSubtitle     string   `json:"card_subtitle"`
				CardSubTitle     string   `json:"cardSubTitle"`
				Subtitle         string   `json:"subtitle"`
				SubTitle         string   `json:"sub_title"`
				NullRatingReason string   `json:"null_rating_reason"`
				IsReleased       *bool    `json:"is_released"`
				CanRate          *bool    `json:"can_rate"`
				VendorCount      any      `json:"vendor_count"`
				Pubdate          []string `json:"pubdate"`
			} `json:"target"`
		} `json:"smart_box"`
	}
	if err := json.Unmarshal(body, &raw); err != nil {
		if inFlightEntry != nil {
			doubanProbeFinish(id, inFlightEntry, nil, false)
			finished = true
		}
		return nil, false
	}

	type cand struct {
		Season int
		ID     string
		Title  string
		Year   string
		Sub    string
	}
	items := make([]cand, 0, 64)
	normalizeForMatch := func(text string) string {
		s := strings.TrimSpace(text)
		if s == "" {
			return ""
		}
		// Keep CJK + alnum; remove spaces and common separators.
		s = strings.ToLower(s)
		re := regexp.MustCompile(`[\s\-_—–·|:：/\\()[\]{}<>【】（）「」『』《》、，,。.！!？?~～]+`)
		s = re.ReplaceAllString(s, "")
		s = strings.TrimSpace(s)
		// Drop trailing year-like digits.
		s = regexp.MustCompile(`(19|20)\d{2}$`).ReplaceAllString(s, "")
		return s
	}
	keywordBase := strings.TrimSpace(keyword)
	keywordBase = regexp.MustCompile(`[(（]\s*(19|20)\d{2}\s*[)）]`).ReplaceAllString(keywordBase, "")
	keywordBase = strings.TrimSpace(keywordBase)
	if seg := strings.Split(keywordBase, " "); len(seg) > 0 && strings.TrimSpace(seg[0]) != "" {
		keywordBase = strings.TrimSpace(seg[0])
	}
	baseKey := normalizeForMatch(keywordBase)
	isLikelyUnreleased := func(sub string) bool {
		s := strings.TrimSpace(sub)
		if s == "" {
			return false
		}
		return regexp.MustCompile(`(尚未上映|尚未播出|即将上映|即将播出)`).MatchString(s)
	}
	isLikelyUnreleasedByDetail := func(nullReason string, isReleased *bool, canRate *bool, vendorCount any, pubdate []string) bool {
		if strings.TrimSpace(nullReason) != "" && regexp.MustCompile(`(尚未上映|尚未播出)`).MatchString(nullReason) {
			return true
		}
		if isReleased != nil && !*isReleased {
			return true
		}
		if canRate != nil && !*canRate {
			vc := 0
			if n, err := strconv.Atoi(strings.TrimSpace(doubanAnyIDToString(vendorCount))); err == nil {
				vc = n
			}
			if vc <= 0 {
				return true
			}
		}
		for _, p := range pubdate {
			ps := strings.TrimSpace(p)
			if ps == "" {
				continue
			}
			if regexp.MustCompile(`(尚未上映|尚未播出|即将上映|即将播出|未定)`).MatchString(ps) {
				return true
			}
			if m := regexp.MustCompile(`\b(19|20)\d{2}-\d{2}-\d{2}\b`).FindString(ps); m != "" {
				if d, err := time.Parse("2006-01-02", m); err == nil {
					if d.After(time.Now()) {
						return true
					}
				}
			}
		}
		return false
	}
	parseYear := func(v any) int {
		s := strings.TrimSpace(doubanAnyIDToString(v))
		if s == "" {
			return 0
		}
		if m := regexp.MustCompile(`\b(19|20)\d{2}\b`).FindString(s); m != "" {
			if n, err := strconv.Atoi(m); err == nil {
				return n
			}
		}
		return 0
	}
	isStrictTitleMatch := func(title string) bool {
		t := strings.TrimSpace(title)
		if t == "" {
			return false
		}
		if baseKey == "" || len([]rune(baseKey)) < 2 {
			return true
		}
		key := normalizeForMatch(t)
		if !strings.HasPrefix(key, baseKey) {
			return false
		}
		tail := strings.TrimPrefix(key, baseKey)
		if tail == "" {
			return true
		}
		if regexp.MustCompile(`^第(?:[0-9]{1,3}|[一二三四五六七八九十百千两零〇]{1,10})季$`).MatchString(tail) {
			return true
		}
		if regexp.MustCompile(`^年番(?:[0-9]{1,3}|[一二三四五六七八九十百千两零〇]{1,10})$`).MatchString(tail) {
			return true
		}
		return false
	}

	push := func(typ string, id any, targetID any, title string, year any, sub string, nullReason string, isReleased *bool, canRate *bool, vendorCount any, pubdate []string) {
		if strings.TrimSpace(typ) != "tv" {
			return
		}
		t := strings.TrimSpace(title)
		if t == "" {
			return
		}
		if !isStrictTitleMatch(t) {
			return
		}
		if isLikelyUnreleased(sub) {
			return
		}
		if isLikelyUnreleasedByDetail(nullReason, isReleased, canRate, vendorCount, pubdate) {
			return
		}
		nowY := time.Now().Year()
		if y := parseYear(year); y > 0 && y > nowY {
			return
		}
		did := doubanAnyIDToString(id)
		if did == "" {
			did = doubanAnyIDToString(targetID)
		}
		if did == "" {
			return
		}
		items = append(items, cand{ID: did, Title: t, Year: doubanAnyIDToString(year), Sub: strings.TrimSpace(sub)})
	}
	for _, it := range raw.Subjects.Items {
		sub := strings.TrimSpace(it.Target.CardSubtitle)
		if sub == "" {
			sub = strings.TrimSpace(it.Target.CardSubTitle)
		}
		if sub == "" {
			sub = strings.TrimSpace(it.Target.Subtitle)
		}
		if sub == "" {
			sub = strings.TrimSpace(it.Target.SubTitle)
		}
		push(it.TargetType, it.Target.ID, it.TargetID, it.Target.Title, it.Target.Year, sub, it.Target.NullRatingReason, it.Target.IsReleased, it.Target.CanRate, it.Target.VendorCount, it.Target.Pubdate)
	}
	for _, it := range raw.SmartBox {
		sub := strings.TrimSpace(it.Target.CardSubtitle)
		if sub == "" {
			sub = strings.TrimSpace(it.Target.CardSubTitle)
		}
		if sub == "" {
			sub = strings.TrimSpace(it.Target.Subtitle)
		}
		if sub == "" {
			sub = strings.TrimSpace(it.Target.SubTitle)
		}
		push(it.TargetType, it.Target.ID, it.TargetID, it.Target.Title, it.Target.Year, sub, it.Target.NullRatingReason, it.Target.IsReleased, it.Target.CanRate, it.Target.VendorCount, it.Target.Pubdate)
	}
	if len(items) == 0 {
		if smartDebugLogEnabled() {
			smartDebugPrintf("[smart][douban_probe] tmdbId=%d miss=search_empty q=%q", id, q)
		}
		if inFlightEntry != nil {
			doubanProbeFinish(id, inFlightEntry, nil, false)
			finished = true
		}
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
		if smartDebugLogEnabled() {
			smartDebugPrintf("[smart][douban_probe] tmdbId=%d miss=season_candidates<2 n=%d q=%q", id, len(bySeason), q)
		}
		if inFlightEntry != nil {
			doubanProbeFinish(id, inFlightEntry, nil, false)
			finished = true
		}
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

	type seasonResult struct {
		Season  int
		EpCount int
	}
	resCh := make(chan seasonResult, len(list))
	var wg sync.WaitGroup

	for _, it := range list {
		it := it
		wg.Add(1)
		go func() {
			defer wg.Done()

			detailURL := strings.TrimRight(base, "/") + "/rexxar/api/v2/tv/" + url.PathEscape(it.ID)
			if proxyBase != "" {
				detailURL = smartDoubanToProxiedURL(detailURL, proxyBase)
			}
			dreq, _ := http.NewRequest(http.MethodGet, detailURL, nil)
			dreq.Header.Set("Accept", "application/json, text/plain, */*")
			dreq.Header.Set("User-Agent", "Mozilla/5.0 (compatible; MeowFilm/1.0; +https://github.com/jenfonro/meowfilm)")
			dreq.Header.Set("Referer", "https://m.douban.com/")
			dreq.Header.Set("Origin", "https://m.douban.com")

			dresp, err := client.Do(dreq)
			if err != nil || dresp == nil {
				return
			}
			defer dresp.Body.Close()
			if dresp.StatusCode < 200 || dresp.StatusCode >= 300 {
				if smartDebugLogEnabled() {
					smartDebugPrintf("[smart][douban_probe] tmdbId=%d season=%d doubanId=%s detail=http_%d", id, it.Season, it.ID, dresp.StatusCode)
				}
				return
			}
			b, err := io.ReadAll(io.LimitReader(dresp.Body, 1<<20))
			if err != nil || len(b) == 0 {
				if smartDebugLogEnabled() {
					smartDebugPrintf("[smart][douban_probe] tmdbId=%d season=%d doubanId=%s detail=empty_body", id, it.Season, it.ID)
				}
				return
			}
			var d struct {
				EpisodesCount any    `json:"episodes_count"`
				EpisodesInfo  string `json:"episodes_info"`
			}
			if err := json.Unmarshal(b, &d); err != nil {
				if smartDebugLogEnabled() {
					smartDebugPrintf("[smart][douban_probe] tmdbId=%d season=%d doubanId=%s detail=bad_json", id, it.Season, it.ID)
				}
				return
			}

			// Prefer total episode count for season mapping (stable across "更新至").
			epCount := 0
			epCountSource := ""
			switch vv := d.EpisodesCount.(type) {
			case float64:
				if vv > 0 {
					epCount = int(vv)
					epCountSource = "episodes_count"
				}
			case int:
				if vv > 0 {
					epCount = vv
					epCountSource = "episodes_count"
				}
			case string:
				if n, err := strconv.Atoi(strings.TrimSpace(vv)); err == nil && n > 0 {
					epCount = n
					epCountSource = "episodes_count"
				}
			}
			if epCount <= 0 {
				if smartDebugLogEnabled() {
					smartDebugPrintf(
						"[smart][douban_probe] tmdbId=%d season=%d doubanId=%s epCount=0 episodes_count=%s episodes_info=%q",
						id,
						it.Season,
						it.ID,
						strings.TrimSpace(doubanAnyIDToString(d.EpisodesCount)),
						strings.TrimSpace(d.EpisodesInfo),
					)
				}
				return
			}
			if smartDebugLogEnabled() {
				smartDebugPrintf(
					"[smart][douban_probe] tmdbId=%d season=%d doubanId=%s epCount=%d src=%s episodes_count=%s episodes_info=%q",
					id,
					it.Season,
					it.ID,
					epCount,
					epCountSource,
					strings.TrimSpace(doubanAnyIDToString(d.EpisodesCount)),
					strings.TrimSpace(d.EpisodesInfo),
				)
			}
			resCh <- seasonResult{Season: it.Season, EpCount: epCount}
		}()
	}

	go func() {
		wg.Wait()
		close(resCh)
	}()

	seasons := make([]TMDBSeason, 0, len(list))
	for r := range resCh {
		seasons = append(seasons, TMDBSeason{Season: r.Season, EpisodeCount: r.EpCount})
	}
	sort.Slice(seasons, func(i, j int) bool { return seasons[i].Season < seasons[j].Season })
	if len(seasons) < 2 {
		if smartDebugLogEnabled() {
			smartDebugPrintf("[smart][douban_probe] tmdbId=%d miss=detail_seasons<2 seasons=%v q=%q", id, seasons, q)
		}
		if inFlightEntry != nil {
			doubanProbeFinish(id, inFlightEntry, nil, false)
			finished = true
		}
		return nil, false
	}
	if smartDebugLogEnabled() {
		smartDebugPrintf("[smart][douban_probe] tmdbId=%d ok seasons=%v q=%q", id, seasons, q)
	}
	// Persist season hints (best-effort)
	if database != nil {
		hints := make([]db.TMDBSeasonHint, 0, len(seasons))
		for _, s := range seasons {
			if s.Season > 0 && s.EpisodeCount > 0 {
				hints = append(hints, db.TMDBSeasonHint{SeasonNumber: s.Season, EpisodeCount: s.EpisodeCount})
			}
		}
		_ = database.UpsertTMDBSeasonHints("tv", id, doubanSeasonHintSource, hints)
	}
	doubanSeasonMetaCacheSet(id, seasons)
	if inFlightEntry != nil {
		doubanProbeFinish(id, inFlightEntry, seasons, true)
		finished = true
	}
	return seasons, true
}

func smartDoubanProbeSeasons(database *db.DB, tmdbID int, keyword string, wantGlobal int) ([]TMDBSeason, bool) {
	return doubanProbeSeasons(database, tmdbID, keyword, wantGlobal)
}
