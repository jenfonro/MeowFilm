package tmdb

import (
	"encoding/json"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	mfnet "github.com/jenfonro/meowfilm/server/net"
)

type tmdbMultiSearchResponse struct {
	Results []struct {
		ID          int    `json:"id"`
		MediaType   string `json:"media_type"`
		Title       string `json:"title"`
		Name        string `json:"name"`
		PosterPath  string `json:"poster_path"`
		ReleaseDate string `json:"release_date"`
		FirstAir    string `json:"first_air_date"`
	} `json:"results"`
}

type MultiSearchResponse = tmdbMultiSearchResponse

type tmdbTVDetailsResponse struct {
	Status           string `json:"status"`
	NumberOfEpisodes int    `json:"number_of_episodes"`
	LastEpisodeToAir *struct {
		SeasonNumber  int `json:"season_number"`
		EpisodeNumber int `json:"episode_number"`
		AirDate       string `json:"air_date"`
	} `json:"last_episode_to_air"`
	Name         string `json:"name"`
	OriginalName string `json:"original_name"`
	PosterPath   string `json:"poster_path"`
	BackdropPath string `json:"backdrop_path"`
	FirstAir     string `json:"first_air_date"`
	Overview     string `json:"overview"`
	Seasons      []tmdbTVSeason `json:"seasons"`
}

type TVDetailsResponse = tmdbTVDetailsResponse

type tmdbTVSeason struct {
	SeasonNumber int    `json:"season_number"`
	Name         string `json:"name"`
	EpisodeCount int    `json:"episode_count"`
	AirDate      string `json:"air_date"`
	PosterPath   string `json:"poster_path"`
	Overview     string `json:"overview"`
}

type tmdbMovieDetailsResponse struct {
	Title       string `json:"title"`
	Original    string `json:"original_title"`
	PosterPath  string `json:"poster_path"`
	ReleaseDate string `json:"release_date"`
	Overview    string `json:"overview"`
	Status      string `json:"status"`
	Tagline     string `json:"tagline"`
	Runtime     int    `json:"runtime"`
	Backdrop    string `json:"backdrop_path"`
}

type MovieDetailsResponse = tmdbMovieDetailsResponse

type tmdbDetailCacheEntry struct {
	At       int64
	ExpireAt int64
	Data     map[string]any
}

var tmdbDetailCache = struct {
	sync.Mutex
	M map[string]tmdbDetailCacheEntry
}{
	M: map[string]tmdbDetailCacheEntry{},
}

const tmdbDetailCacheTTL = 10 * time.Minute
const tmdbDetailCacheTTLEnded = 24 * time.Hour
const tmdbDetailCacheMaxEntries = 2000

func isTMDBAirDateInFuture(dateText string, now time.Time) bool {
	s := strings.TrimSpace(dateText)
	if s == "" {
		return false
	}
	t, err := time.Parse("2006-01-02", s)
	if err != nil {
		return false
	}
	// Compare by date only.
	n := now
	y, m, d := n.Date()
	today := time.Date(y, m, d, 0, 0, 0, 0, time.UTC)
	return t.After(today)
}

func pickLatestAiredFromSeasons(seasons []tmdbTVSeason, now time.Time) (int, int) {
	bestSeason := 0
	bestEpisodes := 0
	for _, s := range seasons {
		if s.SeasonNumber <= 0 || s.EpisodeCount <= 0 {
			continue
		}
		air := strings.TrimSpace(s.AirDate)
		if air == "" {
			continue
		}
		if isTMDBAirDateInFuture(air, now) {
			continue
		}
		if s.SeasonNumber > bestSeason {
			bestSeason = s.SeasonNumber
			bestEpisodes = s.EpisodeCount
		}
	}
	return bestSeason, bestEpisodes
}

func resolveTMDBAPIBase(database *db.DB) string {
	raw := ""
	if database != nil {
		if cfg, err := database.ReadAppConfig(); err == nil {
			raw = strings.TrimSpace(cfg.TMDBAPIBase)
		}
	}
	base := normalizeHTTPBase(raw)
	if base == "" {
		return "https://api.themoviedb.org/3"
	}
	return base
}

func ResolveAPIBase(database *db.DB) string { return resolveTMDBAPIBase(database) }

func joinTMDBAPI(base, path string) string {
	b := strings.TrimRight(strings.TrimSpace(base), "/")
	p := strings.TrimLeft(strings.TrimSpace(path), "/")
	if b == "" {
		b = "https://api.themoviedb.org/3"
	}
	if p == "" {
		return b
	}
	return b + "/" + p
}

func JoinAPI(base, path string) string { return joinTMDBAPI(base, path) }

func resolveTMDBImageBase(database *db.DB) string {
	raw := ""
	if database != nil {
		if cfg, err := database.ReadAppConfig(); err == nil {
			raw = strings.TrimSpace(cfg.TMDBImgBase)
		}
	}
	base := normalizeHTTPBase(raw)
	if base == "" {
		return "https://image.tmdb.org"
	}
	return base
}

func joinTMDBImage(base, path string) string {
	b := strings.TrimRight(strings.TrimSpace(base), "/")
	p := strings.TrimLeft(strings.TrimSpace(path), "/")
	if b == "" {
		b = "https://image.tmdb.org"
	}
	if p == "" {
		return b
	}
	return b + "/" + p
}

func resolveTMDBToken(database *db.DB) (token string, kind string) {
	if database == nil {
		return "", ""
	}
	cfg, err := database.ReadAppConfig()
	if err != nil {
		return "", ""
	}
	raw := strings.TrimSpace(cfg.TMDBAPIToken)
	if raw == "" {
		return "", ""
	}
	raw = strings.TrimSpace(strings.TrimPrefix(raw, "Bearer "))
	if raw == "" {
		return "", ""
	}
	// Heuristic:
	// - v4 read access tokens are JWT-like (contain '.' segments)
	// - v3 api keys are plain strings (commonly 32 chars)
	if strings.Contains(raw, ".") {
		return raw, "v4"
	}
	return raw, "v3"
}

func ResolveToken(database *db.DB) (token string, kind string) { return resolveTMDBToken(database) }

func ResolveImageBase(database *db.DB) string { return resolveTMDBImageBase(database) }

func JoinImage(base, path string) string { return joinTMDBImage(base, path) }

func tmdbDetailCacheGet(cacheKey string, now int64) (map[string]any, bool) {
	cacheKey = strings.TrimSpace(cacheKey)
	if cacheKey == "" {
		return nil, false
	}
	tmdbDetailCache.Lock()
	defer tmdbDetailCache.Unlock()
	if tmdbDetailCache.M == nil {
		tmdbDetailCache.M = map[string]tmdbDetailCacheEntry{}
		return nil, false
	}
	hit, ok := tmdbDetailCache.M[cacheKey]
	if !ok || hit.Data == nil || hit.ExpireAt <= 0 {
		return nil, false
	}
	if now >= hit.ExpireAt {
		delete(tmdbDetailCache.M, cacheKey)
		return nil, false
	}
	return hit.Data, true
}

func tmdbDetailCacheSet(cacheKey string, out map[string]any, ttl time.Duration, now int64) {
	cacheKey = strings.TrimSpace(cacheKey)
	if cacheKey == "" || out == nil {
		return
	}
	if ttl <= 0 {
		ttl = tmdbDetailCacheTTL
	}
	exp := now + int64(ttl.Seconds())
	tmdbDetailCache.Lock()
	if tmdbDetailCache.M == nil {
		tmdbDetailCache.M = map[string]tmdbDetailCacheEntry{}
	}
	tmdbDetailCache.M[cacheKey] = tmdbDetailCacheEntry{At: now, ExpireAt: exp, Data: out}
	if len(tmdbDetailCache.M) > tmdbDetailCacheMaxEntries {
		cut := len(tmdbDetailCache.M) - tmdbDetailCacheMaxEntries
		if cut < 1 {
			cut = 1
		}
		for k, v := range tmdbDetailCache.M {
			if cut <= 0 {
				break
			}
			if v.ExpireAt > 0 && now >= v.ExpireAt {
				delete(tmdbDetailCache.M, k)
				cut -= 1
			}
		}
		for k := range tmdbDetailCache.M {
			if len(tmdbDetailCache.M) <= tmdbDetailCacheMaxEntries {
				break
			}
			delete(tmdbDetailCache.M, k)
		}
	}
	tmdbDetailCache.Unlock()
}

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

	cfg, _ := database.ReadAppConfig()
	language := strings.TrimSpace(cfg.TMDBLanguage)
	if language == "" {
		language = "zh-CN"
	}
	region := strings.TrimSpace(cfg.TMDBRegion)
	if region == "" {
		region = "CN"
	}
	includeAdult := cfg.TMDBIncludeAdult

	apiBase := resolveTMDBAPIBase(database)
	u, _ := url.Parse(joinTMDBAPI(apiBase, "search/multi"))
	params := u.Query()
	params.Set("query", q)
	params.Set("page", "1")
	if language != "" {
		params.Set("language", language)
	}
	if region != "" {
		params.Set("region", region)
	}
	params.Set("include_adult", boolToStr(includeAdult))
	if tokenKind == "v3" {
		params.Set("api_key", token)
	}
	u.RawQuery = params.Encode()

	req, err := http.NewRequest(http.MethodGet, u.String(), nil)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "请求构造失败"})
		return
	}
	req.Header.Set("Accept", "application/json")
	if tokenKind == "v4" {
		req.Header.Set("Authorization", "Bearer "+token)
	}

	client := &http.Client{Timeout: 15 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{
			"error": "TMDB 请求失败",
			"code":  "TMDB_REQUEST_FAILED",
		})
		return
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		writeJSON(w, http.StatusBadGateway, map[string]any{
			"error": "TMDB 响应异常",
			"code":  "TMDB_RESPONSE_ERROR",
		})
		return
	}

	var out tmdbMultiSearchResponse
	dec := json.NewDecoder(resp.Body)
	if err := dec.Decode(&out); err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{
			"error": "TMDB 解析失败",
			"code":  "TMDB_DECODE_FAILED",
		})
		return
	}

	list := make([]map[string]any, 0, len(out.Results))
	for _, it := range out.Results {
		typ := strings.TrimSpace(it.MediaType)
		if typ != "movie" && typ != "tv" {
			continue
		}
		title := strings.TrimSpace(it.Title)
		if title == "" {
			title = strings.TrimSpace(it.Name)
		}
		if title == "" || it.ID <= 0 {
			continue
		}
		poster := strings.TrimSpace(it.PosterPath)
		if poster != "" && !strings.HasPrefix(poster, "http://") && !strings.HasPrefix(poster, "https://") {
			poster = joinTMDBImage(resolveTMDBImageBase(database), "t/p/w500"+poster)
		}
		release := strings.TrimSpace(it.ReleaseDate)
		if release == "" {
			release = strings.TrimSpace(it.FirstAir)
		}
		year := parseYearFromDate(release)
		list = append(list, map[string]any{
			"id":          it.ID,
			"type":        typ,
			"title":       title,
			"poster":      poster,
			"year":        year,
			"badge":       "",
			"seasonCount": 0,
		})
	}

	writeJSON(w, 200, map[string]any{"success": true, "list": list})
}

func HandleDetail(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}

	t := strings.TrimSpace(r.URL.Query().Get("type"))
	if t == "" {
		t = strings.TrimSpace(r.URL.Query().Get("mediaType"))
	}
	if t != "tv" && t != "movie" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "参数无效"})
		return
	}

	idStr := strings.TrimSpace(r.URL.Query().Get("id"))
	if idStr == "" {
		idStr = strings.TrimSpace(r.URL.Query().Get("tmdbId"))
	}
	id, _ := strconv.Atoi(idStr)
	if id <= 0 {
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

	now := time.Now().Unix()
	cacheKey := t + ":" + strconv.Itoa(id)
	if hit, ok := tmdbDetailCacheGet(cacheKey, now); ok {
		writeJSON(w, 200, hit)
		return
	}

	data := fetchTMDBDetailForAPI(database, t, id)
	if data == nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{
			"error": "TMDB 请求失败",
			"code":  "TMDB_REQUEST_FAILED",
		})
		return
	}

	ttl := tmdbDetailCacheTTL
	if s, ok := data["status"].(string); ok && strings.EqualFold(strings.TrimSpace(s), "Ended") {
		ttl = tmdbDetailCacheTTLEnded
	}
	tmdbDetailCacheSet(cacheKey, data, ttl, now)
	writeJSON(w, 200, data)
}

func fetchTMDBDetailForAPI(database *db.DB, mediaType string, tmdbID int) map[string]any {
	if mediaType != "tv" && mediaType != "movie" {
		return nil
	}
	if tmdbID <= 0 || database == nil {
		return nil
	}

	token, tokenKind := resolveTMDBToken(database)
	if token == "" || tokenKind == "" {
		return nil
	}

	cfg, _ := database.ReadAppConfig()
	language := strings.TrimSpace(cfg.TMDBLanguage)
	if language == "" {
		language = "zh-CN"
	}

	// Persistent normalized cache (SQLite)
	if d, err := database.ReadTMDBDetailForAPI(mediaType, tmdbID, language); err == nil && d != nil {
		imgBase := resolveTMDBImageBase(database)
		if d.TMDBType == "tv" {
			pic := strings.TrimSpace(d.PosterPath)
			if pic != "" && !strings.HasPrefix(pic, "http://") && !strings.HasPrefix(pic, "https://") {
				pic = joinTMDBImage(imgBase, "t/p/w500"+pic)
			}
			backdrop := strings.TrimSpace(d.Backdrop)
			if backdrop != "" && !strings.HasPrefix(backdrop, "http://") && !strings.HasPrefix(backdrop, "https://") {
				backdrop = joinTMDBImage(imgBase, "t/p/w780"+backdrop)
			}
			year := parseYearFromDate(d.FirstAir)
			seasons := make([]map[string]any, 0, len(d.Seasons))
			for _, s := range d.Seasons {
				p := strings.TrimSpace(s.PosterPath)
				if p != "" && !strings.HasPrefix(p, "http://") && !strings.HasPrefix(p, "https://") {
					p = joinTMDBImage(imgBase, "t/p/w500"+p)
				}
				seasons = append(seasons, map[string]any{
					"season":   s.SeasonNumber,
					"episodes": s.EpisodeCount,
					"airDate":  strings.TrimSpace(s.AirDate),
					"poster":   p,
				})
			}
			badge := ""
			if d.LatestSeason > 0 && d.LatestEpisode > 0 {
				badge = "更新至 S" + strconv.Itoa(d.LatestSeason) + "E" + strconv.Itoa(d.LatestEpisode)
			} else if d.EpisodeCount > 0 {
				badge = "共" + strconv.Itoa(d.EpisodeCount) + "集"
			}
			out := map[string]any{
				"success":     true,
				"id":          tmdbID,
				"type":        "tv",
				"title":       strings.TrimSpace(d.Title),
				"year":        year,
				"poster":      pic,
				"backdrop":    backdrop,
				"overview":    strings.TrimSpace(d.Overview),
				"badge":       badge,
				"status":      strings.TrimSpace(d.Status),
				"latestSeason":  d.LatestSeason,
				"latestEpisode": d.LatestEpisode,
				"episodeCount":  d.EpisodeCount,
				"seasons":     seasons,
				"seasonCount": len(seasons),
			}
			return out
		}
		pic := strings.TrimSpace(d.PosterPath)
		if pic != "" && !strings.HasPrefix(pic, "http://") && !strings.HasPrefix(pic, "https://") {
			pic = joinTMDBImage(imgBase, "t/p/w500"+pic)
		}
		year := parseYearFromDate(d.Release)
		out := map[string]any{
			"success":  true,
			"id":       tmdbID,
			"type":     "movie",
			"title":    strings.TrimSpace(d.Title),
			"year":     year,
			"poster":   pic,
			"overview": strings.TrimSpace(d.Overview),
			"badge":    "",
			"status":   strings.TrimSpace(d.Status),
		}
		return out
	}

	apiBase := resolveTMDBAPIBase(database)
	u, _ := url.Parse(joinTMDBAPI(apiBase, mediaType+"/"+strconv.Itoa(tmdbID)))
	params := u.Query()
	if language != "" {
		params.Set("language", language)
	}
	if tokenKind == "v3" {
		params.Set("api_key", token)
	}
	u.RawQuery = params.Encode()

	req, err := http.NewRequest(http.MethodGet, u.String(), nil)
	if err != nil {
		return nil
	}
	req.Header.Set("Accept", "application/json")
	if tokenKind == "v4" {
		req.Header.Set("Authorization", "Bearer "+token)
	}

	client := &http.Client{Timeout: 15 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return nil
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil
	}

	if mediaType == "tv" {
		var detail tmdbTVDetailsResponse
		dec := json.NewDecoder(resp.Body)
		if err := dec.Decode(&detail); err != nil {
			return nil
		}
		// Upsert normalized TMDB library
		_, _ = database.UpsertTMDBMedia(db.TMDBUpsertMedia{
			Type:         "tv",
			ID:           tmdbID,
			Lang:         language,
			Title:        strings.TrimSpace(detail.Name),
			Original:     strings.TrimSpace(detail.OriginalName),
			Overview:     strings.TrimSpace(detail.Overview),
			Status:       strings.TrimSpace(detail.Status),
			PosterPath:   strings.TrimSpace(detail.PosterPath),
			BackdropPath: strings.TrimSpace(detail.BackdropPath),
			FirstAirDate: strings.TrimSpace(detail.FirstAir),
		})
		seasonRows := make([]db.TMDBSeason, 0, len(detail.Seasons))
		for _, s := range detail.Seasons {
			seasonRows = append(seasonRows, db.TMDBSeason{
				SeasonNumber: s.SeasonNumber,
				EpisodeCount: s.EpisodeCount,
				AirDate:      strings.TrimSpace(s.AirDate),
				PosterPath:   strings.TrimSpace(s.PosterPath),
				Name:         strings.TrimSpace(s.Name),
				Overview:     strings.TrimSpace(s.Overview),
			})
		}
		_ = database.UpsertTMDBSeasons("tv", tmdbID, language, seasonRows)

		pic := strings.TrimSpace(detail.PosterPath)
		if pic != "" && !strings.HasPrefix(pic, "http://") && !strings.HasPrefix(pic, "https://") {
			pic = joinTMDBImage(resolveTMDBImageBase(database), "t/p/w500"+pic)
		}
		backdrop := strings.TrimSpace(detail.BackdropPath)
		if backdrop != "" && !strings.HasPrefix(backdrop, "http://") && !strings.HasPrefix(backdrop, "https://") {
			backdrop = joinTMDBImage(resolveTMDBImageBase(database), "t/p/w780"+backdrop)
		}
		year := parseYearFromDate(detail.FirstAir)
		latestSeason := 0
		latestEpisode := 0
		if detail.LastEpisodeToAir != nil {
			if !isTMDBAirDateInFuture(detail.LastEpisodeToAir.AirDate, time.Now().UTC()) {
				latestSeason = detail.LastEpisodeToAir.SeasonNumber
				latestEpisode = detail.LastEpisodeToAir.EpisodeNumber
			}
		}
		if latestSeason <= 0 || latestEpisode <= 0 {
			if sNo, eNo := pickLatestAiredFromSeasons(detail.Seasons, time.Now().UTC()); sNo > 0 && eNo > 0 {
				latestSeason = sNo
				latestEpisode = eNo
			}
		}
		badge := ""
		if latestSeason > 0 && latestEpisode > 0 {
			badge = "更新至 S" + strconv.Itoa(latestSeason) + "E" + strconv.Itoa(latestEpisode)
		} else if detail.NumberOfEpisodes > 0 {
			badge = "共" + strconv.Itoa(detail.NumberOfEpisodes) + "集"
		}

		seasons := make([]map[string]any, 0, len(detail.Seasons))
		for _, s := range detail.Seasons {
			if s.SeasonNumber < 0 {
				continue
			}
				p := strings.TrimSpace(s.PosterPath)
				if p != "" && !strings.HasPrefix(p, "http://") && !strings.HasPrefix(p, "https://") {
					p = joinTMDBImage(resolveTMDBImageBase(database), "t/p/w500"+p)
				}
			seasons = append(seasons, map[string]any{
				"season":   s.SeasonNumber,
				"episodes": s.EpisodeCount,
				"airDate":  strings.TrimSpace(s.AirDate),
				"poster":   p,
			})
		}
		out := map[string]any{
			"success":       true,
			"id":            tmdbID,
			"type":          "tv",
			"title":         strings.TrimSpace(detail.Name),
			"year":          year,
			"poster":        pic,
			"backdrop":      backdrop,
			"overview":      strings.TrimSpace(detail.Overview),
			"badge":         badge,
			"status":        strings.TrimSpace(detail.Status),
			"latestSeason":  latestSeason,
			"latestEpisode": latestEpisode,
			"episodeCount":  detail.NumberOfEpisodes,
			"seasons":       seasons,
			"seasonCount":   len(seasons),
		}
		return out
	}

	var detail tmdbMovieDetailsResponse
	dec := json.NewDecoder(resp.Body)
	if err := dec.Decode(&detail); err != nil {
		return nil
	}
	_, _ = database.UpsertTMDBMedia(db.TMDBUpsertMedia{
		Type:         "movie",
		ID:           tmdbID,
		Lang:         language,
		Title:        strings.TrimSpace(detail.Title),
		Original:     strings.TrimSpace(detail.Original),
		Overview:     strings.TrimSpace(detail.Overview),
		Tagline:      strings.TrimSpace(detail.Tagline),
		Status:       strings.TrimSpace(detail.Status),
		PosterPath:   strings.TrimSpace(detail.PosterPath),
		BackdropPath: strings.TrimSpace(detail.Backdrop),
		ReleaseDate:  strings.TrimSpace(detail.ReleaseDate),
		Runtime:      detail.Runtime,
	})
	pic := strings.TrimSpace(detail.PosterPath)
	if pic != "" && !strings.HasPrefix(pic, "http://") && !strings.HasPrefix(pic, "https://") {
		pic = joinTMDBImage(resolveTMDBImageBase(database), "t/p/w500"+pic)
	}
	year := parseYearFromDate(detail.ReleaseDate)
	out := map[string]any{
		"success":  true,
		"id":       tmdbID,
		"type":     "movie",
		"title":    strings.TrimSpace(detail.Title),
		"year":     year,
		"poster":   pic,
		"overview": strings.TrimSpace(detail.Overview),
		"badge":    "",
		"status":   strings.TrimSpace(detail.Status),
	}
	return out
}

func defaultString(v, def string) string {
	return mfnet.DefaultString(v, def)
}

func normalizeHTTPBase(value string) string {
	return mfnet.NormalizeHTTPBase(value)
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

func parseYearFromDate(v string) int {
	s := strings.TrimSpace(v)
	if len(s) < 4 {
		return 0
	}
	yyyy := s[:4]
	n, err := strconv.Atoi(yyyy)
	if err != nil || n < 1800 || n > 2500 {
		return 0
	}
	return n
}
