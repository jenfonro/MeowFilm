package douban

import (
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

const (
	seasonMetaCacheTTL        = 24 * time.Hour
	seasonMetaCacheMaxEntries = 2000
)

type seasonMetaCacheEntry struct {
	ExpireAt time.Time
	Seasons  []db.TMDBSeasonHint
}

var seasonMetaCache = struct {
	sync.Mutex
	M map[int]seasonMetaCacheEntry
}{
	M: map[int]seasonMetaCacheEntry{},
}

type probeInFlightEntry struct {
	done    chan struct{}
	ok      bool
	seasons []db.TMDBSeasonHint
}

var probeInFlight = struct {
	mu sync.Mutex
	m  map[int]*probeInFlightEntry
}{
	m: map[int]*probeInFlightEntry{},
}

func ProbeTVSeasonHints(database *db.DB, tmdbID int, keyword string, wantGlobal int, hintSource string) ([]db.TMDBSeasonHint, bool) {
	id := tmdbID
	q := strings.TrimSpace(keyword)
	if id <= 0 || q == "" {
		return nil, false
	}
	if seasons, ok, waited := probeWaitIfAny(id); waited {
		return seasons, ok
	}
	inFlightEntry, started := probeStart(id)
	if !started {
		if seasons, ok, waited := probeWaitIfAny(id); waited {
			return seasons, ok
		}
	}
	finished := false
	defer func() {
		if finished || inFlightEntry == nil {
			return
		}
		probeFinish(id, inFlightEntry, nil, false)
	}()

	if hit, ok := seasonMetaCacheGet(id); ok {
		if v := sanitizeSeasonHints(hit); hasValidSeasonHints(v) {
			if inFlightEntry != nil {
				probeFinish(id, inFlightEntry, v, true)
				finished = true
			}
			return v, true
		}
		seasonMetaCacheDelete(id)
	}
	if database != nil {
		if hints, err := database.ListTMDBSeasonHints("tv", id, hintSource); err == nil {
			if v := sanitizeSeasonHints(hints); hasValidSeasonHints(v) {
				seasonMetaCacheSet(id, v)
				if inFlightEntry != nil {
					probeFinish(id, inFlightEntry, v, true)
					finished = true
				}
				return v, true
			}
			if len(hints) > 0 {
				deleteSeasonHints(database, id, hintSource)
			}
		}
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
		s = strings.ToLower(s)
		re := regexp.MustCompile(`[\s\-_—–·|:：/\\()[\]{}<>【】（）「」『』《》、，,。.！!？?~～]+`)
		s = re.ReplaceAllString(s, "")
		s = strings.TrimSpace(s)
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
		// Accept aliases after season markers, e.g. "第一季呪術廻戦".
		if regexp.MustCompile(`^第(?:[0-9]{1,3}|[一二三四五六七八九十百千两零〇]{1,10})季`).MatchString(tail) {
			return parseSeasonNoFromTitle(t, false) > 0
		}
		if regexp.MustCompile(`^年番(?:[0-9]{1,3}|[一二三四五六七八九十百千两零〇]{1,10})`).MatchString(tail) {
			return parseSeasonNoFromTitle(t, false) > 0
		}
		return false
	}
	push := func(typ string, id any, title string) {
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
		did := anyIDToString(id)
		if did == "" {
			return
		}
		items = append(items, cand{ID: did, Title: t})
	}

	rows, _, err := FetchSearchSubjects(database, q)
	if err != nil {
		if inFlightEntry != nil {
			probeFinish(id, inFlightEntry, nil, false)
			finished = true
		}
		return nil, false
	}
	for _, it := range rows {
		push(it.TargetType, it.TargetID, it.Title)
	}
	if len(items) == 0 {
		if inFlightEntry != nil {
			probeFinish(id, inFlightEntry, nil, false)
			finished = true
		}
		return nil, false
	}

	baseHasSeason1 := false
	for _, it := range items {
		if hasExplicitFirstSeasonMarker(it.Title) {
			baseHasSeason1 = true
			break
		}
	}

	bySeason := map[int]cand{}
	for _, it := range items {
		seasonNo := parseSeasonNoFromTitle(it.Title, baseHasSeason1)
		if seasonNo <= 0 && baseHasSeason1 && normalizeForMatch(it.Title) == baseKey {
			continue
		}
		if seasonNo <= 0 {
			continue
		}
		if _, ok := bySeason[seasonNo]; ok {
			continue
		}
		it.Season = seasonNo
		bySeason[seasonNo] = it
	}

	list := make([]cand, 0, maxInt(len(bySeason), len(items)))
	for _, v := range bySeason {
		list = append(list, v)
	}
	if len(list) == 0 {
		if len(items) == 1 {
			list = append(list, cand{
				Season: 1,
				ID:     items[0].ID,
				Title:  items[0].Title,
				Year:   items[0].Year,
				Sub:    items[0].Sub,
			})
		} else {
			if inFlightEntry != nil {
				probeFinish(id, inFlightEntry, nil, false)
				finished = true
			}
			return nil, false
		}
	} else {
		sort.Slice(list, func(i, j int) bool { return list[i].Season < list[j].Season })
		if len(list) > 10 {
			list = list[:10]
		}
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
			detail, err := FetchTVDetail(database, it.ID)
			if err != nil || detail == nil {
				return
			}
			epCount := parseIntValue(detail.EpisodesCount)
			if epCount <= 0 {
				return
			}
			resCh <- seasonResult{Season: it.Season, EpCount: epCount}
		}()
	}
	go func() {
		wg.Wait()
		close(resCh)
	}()

	seasons := make([]db.TMDBSeasonHint, 0, len(list))
	for r := range resCh {
		seasons = append(seasons, db.TMDBSeasonHint{SeasonNumber: r.Season, EpisodeCount: r.EpCount})
	}
	seasons = sanitizeSeasonHints(seasons)
	sort.Slice(seasons, func(i, j int) bool { return seasons[i].SeasonNumber < seasons[j].SeasonNumber })
	if !hasValidSeasonHints(seasons) {
		if inFlightEntry != nil {
			probeFinish(id, inFlightEntry, nil, false)
			finished = true
		}
		return nil, false
	}
	seasonMetaCacheSet(id, seasons)
	if inFlightEntry != nil {
		probeFinish(id, inFlightEntry, seasons, true)
		finished = true
	}
	return seasons, true
}

func probeWaitIfAny(tmdbID int) ([]db.TMDBSeasonHint, bool, bool) {
	if tmdbID <= 0 {
		return nil, false, false
	}
	probeInFlight.mu.Lock()
	e := probeInFlight.m[tmdbID]
	probeInFlight.mu.Unlock()
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
	if !hasValidSeasonHints(e.seasons) {
		return nil, false, true
	}
	out := make([]db.TMDBSeasonHint, 0, len(e.seasons))
	out = append(out, e.seasons...)
	return out, true, true
}

func probeStart(tmdbID int) (*probeInFlightEntry, bool) {
	if tmdbID <= 0 {
		return nil, false
	}
	probeInFlight.mu.Lock()
	defer probeInFlight.mu.Unlock()
	if cur := probeInFlight.m[tmdbID]; cur != nil && cur.done != nil {
		return cur, false
	}
	e := &probeInFlightEntry{done: make(chan struct{}), ok: false}
	probeInFlight.m[tmdbID] = e
	return e, true
}

func probeFinish(tmdbID int, e *probeInFlightEntry, seasons []db.TMDBSeasonHint, ok bool) {
	if tmdbID <= 0 || e == nil {
		return
	}
	probeInFlight.mu.Lock()
	if cur := probeInFlight.m[tmdbID]; cur == e {
		delete(probeInFlight.m, tmdbID)
	}
	probeInFlight.mu.Unlock()
	e.ok = ok
	if seasons != nil {
		out := make([]db.TMDBSeasonHint, 0, len(seasons))
		out = append(out, seasons...)
		e.seasons = out
	}
	defer func() { recover() }()
	close(e.done)
}

func seasonMetaCacheGet(tmdbID int) ([]db.TMDBSeasonHint, bool) {
	if tmdbID <= 0 {
		return nil, false
	}
	now := time.Now()
	seasonMetaCache.Lock()
	defer seasonMetaCache.Unlock()
	if seasonMetaCache.M == nil {
		seasonMetaCache.M = map[int]seasonMetaCacheEntry{}
		return nil, false
	}
	hit, ok := seasonMetaCache.M[tmdbID]
	if !ok || len(hit.Seasons) == 0 {
		return nil, false
	}
	if !hit.ExpireAt.IsZero() && hit.ExpireAt.Before(now) {
		delete(seasonMetaCache.M, tmdbID)
		return nil, false
	}
	out := make([]db.TMDBSeasonHint, 0, len(hit.Seasons))
	out = append(out, hit.Seasons...)
	return out, true
}

func seasonMetaCacheDelete(tmdbID int) {
	if tmdbID <= 0 {
		return
	}
	seasonMetaCache.Lock()
	if seasonMetaCache.M != nil {
		delete(seasonMetaCache.M, tmdbID)
	}
	seasonMetaCache.Unlock()
}

func seasonMetaCacheSet(tmdbID int, seasons []db.TMDBSeasonHint) {
	if tmdbID <= 0 || len(seasons) == 0 {
		return
	}
	now := time.Now()
	seasonMetaCache.Lock()
	if seasonMetaCache.M == nil {
		seasonMetaCache.M = map[int]seasonMetaCacheEntry{}
	}
	seasonMetaCache.M[tmdbID] = seasonMetaCacheEntry{
		ExpireAt: now.Add(seasonMetaCacheTTL),
		Seasons:  append([]db.TMDBSeasonHint(nil), seasons...),
	}
	if len(seasonMetaCache.M) > seasonMetaCacheMaxEntries {
		for k, v := range seasonMetaCache.M {
			if len(seasonMetaCache.M) <= seasonMetaCacheMaxEntries {
				break
			}
			if !v.ExpireAt.IsZero() && v.ExpireAt.Before(now) {
				delete(seasonMetaCache.M, k)
			}
		}
		for k := range seasonMetaCache.M {
			if len(seasonMetaCache.M) <= seasonMetaCacheMaxEntries {
				break
			}
			delete(seasonMetaCache.M, k)
		}
	}
	seasonMetaCache.Unlock()
}

func deleteSeasonHints(database *db.DB, tmdbID int, source string) {
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

func sanitizeSeasonHints(list []db.TMDBSeasonHint) []db.TMDBSeasonHint {
	if len(list) == 0 {
		return nil
	}
	out := make([]db.TMDBSeasonHint, 0, len(list))
	seen := map[int]struct{}{}
	for _, s := range list {
		if s.SeasonNumber <= 0 || s.EpisodeCount <= 0 {
			continue
		}
		if _, ok := seen[s.SeasonNumber]; ok {
			continue
		}
		seen[s.SeasonNumber] = struct{}{}
		out = append(out, db.TMDBSeasonHint{
			SeasonNumber: s.SeasonNumber,
			EpisodeCount: s.EpisodeCount,
		})
	}
	return out
}

func hasValidSeasonHints(list []db.TMDBSeasonHint) bool {
	for _, s := range list {
		if s.SeasonNumber > 0 && s.EpisodeCount > 0 {
			return true
		}
	}
	return false
}

func anyIDToString(v any) string {
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

func parseSeasonNoFromTitle(title string, baseHasSeason1 bool) int {
	s := strings.TrimSpace(title)
	if s == "" {
		return 0
	}
	reSeason := regexp.MustCompile(`第\s*([0-9０-９]{1,3}|[一二三四五六七八九十百千两零〇]{1,10})\s*季`)
	if m := reSeason.FindStringSubmatch(s); len(m) >= 2 && m[1] != "" {
		return parseChineseSeasonNo(m[1])
	}
	reYear := regexp.MustCompile(`年番\s*([0-9０-９]{1,3}|[一二三四五六七八九十百千两零〇]{1,10})`)
	if m := reYear.FindStringSubmatch(s); len(m) >= 2 && m[1] != "" {
		n := parseChineseSeasonNo(m[1])
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

func hasExplicitFirstSeasonMarker(title string) bool {
	s := strings.TrimSpace(title)
	if s == "" {
		return false
	}
	if regexp.MustCompile(`第\s*(?:1|01|一)\s*季`).MatchString(s) {
		return true
	}
	if regexp.MustCompile(`年番\s*(?:1|01|一)`).MatchString(s) {
		return true
	}
	return false
}

func parseChineseSeasonNo(raw string) int {
	s := strings.TrimSpace(raw)
	if s == "" {
		return 0
	}
	if n, err := strconv.Atoi(strings.NewReplacer("０", "0", "１", "1", "２", "2", "３", "3", "４", "4", "５", "5", "６", "6", "７", "7", "８", "8", "９", "9").Replace(s)); err == nil {
		if n > 0 && n <= 999 {
			return n
		}
	}
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
		if v, ok := m[[]rune(s)[0]]; ok && v > 0 && v <= 999 {
			return v
		}
	}
	return 0
}

func parseIntValue(v any) int {
	switch vv := v.(type) {
	case float64:
		if vv > 0 {
			return int(vv)
		}
	case int:
		if vv > 0 {
			return vv
		}
	case string:
		if n, err := strconv.Atoi(strings.TrimSpace(vv)); err == nil && n > 0 {
			return n
		}
	}
	return 0
}

func asBoolPtr(v any) *bool {
	switch vv := v.(type) {
	case bool:
		out := vv
		return &out
	case *bool:
		return vv
	default:
		return nil
	}
}

func asStringSlice(v any) []string {
	switch vv := v.(type) {
	case []string:
		out := make([]string, 0, len(vv))
		for _, row := range vv {
			if text := strings.TrimSpace(row); text != "" {
				out = append(out, text)
			}
		}
		return out
	case []any:
		out := make([]string, 0, len(vv))
		for _, row := range vv {
			if text := strings.TrimSpace(anyIDToString(row)); text != "" {
				out = append(out, text)
			}
		}
		return out
	default:
		return nil
	}
}

func maxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}
