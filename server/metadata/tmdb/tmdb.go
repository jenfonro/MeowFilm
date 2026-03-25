package tmdb

import (
	"encoding/json"
	"fmt"
	"io"
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
		SeasonNumber  int    `json:"season_number"`
		EpisodeNumber int    `json:"episode_number"`
		AirDate       string `json:"air_date"`
	} `json:"last_episode_to_air"`
	Name         string         `json:"name"`
	OriginalName string         `json:"original_name"`
	PosterPath   string         `json:"poster_path"`
	BackdropPath string         `json:"backdrop_path"`
	FirstAir     string         `json:"first_air_date"`
	Overview     string         `json:"overview"`
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

type tmdbTVSeasonDetailResponse struct {
	SeasonNumber int    `json:"season_number"`
	Name         string `json:"name"`
	PosterPath   string `json:"poster_path"`
	Episodes     []struct {
		EpisodeNumber int    `json:"episode_number"`
		AirDate       string `json:"air_date"`
		Name          string `json:"name"`
		Overview      string `json:"overview"`
		StillPath     string `json:"still_path"`
		Runtime       int    `json:"runtime"`
	} `json:"episodes"`
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

const tmdbSoonAirDays = 0 // only include episodes scheduled for "today" (local CN date)

var tmdbDetailRefreshInFlight = struct {
	sync.Mutex
	M map[string]bool
}{
	M: map[string]bool{},
}

func tmdbCNLocation() *time.Location {
	loc, err := time.LoadLocation("Asia/Shanghai")
	if err == nil && loc != nil {
		return loc
	}
	// Fallback: fixed UTC+8
	return time.FixedZone("CST", 8*3600)
}

func tmdbCNDayStart(t time.Time) time.Time {
	loc := tmdbCNLocation()
	tt := t.In(loc)
	y, m, d := tt.Date()
	return time.Date(y, m, d, 0, 0, 0, 0, loc)
}

func tmdbCNDayStartPlus(t time.Time, days int) time.Time {
	return tmdbCNDayStart(t).AddDate(0, 0, days)
}

func isTMDBAirDateInFuture(dateText string, now time.Time) bool {
	s := strings.TrimSpace(dateText)
	if s == "" {
		return false
	}
	t, err := time.Parse("2006-01-02", s)
	if err != nil {
		return false
	}
	loc := tmdbCNLocation()
	y, m, d := t.Date()
	airDay := time.Date(y, m, d, 0, 0, 0, 0, loc)
	return airDay.After(tmdbCNDayStart(now))
}

func isTMDBAirDateAiredOrToday(dateText string, now time.Time) bool {
	s := strings.TrimSpace(dateText)
	if s == "" {
		return false
	}
	t, err := time.Parse("2006-01-02", s)
	if err != nil {
		return false
	}
	loc := tmdbCNLocation()
	y, m, d := t.Date()
	airDay := time.Date(y, m, d, 0, 0, 0, 0, loc)
	return !airDay.After(tmdbCNDayStart(now))
}

func isTMDBAirDateNotAfter(dateText string, cutoff time.Time) bool {
	s := strings.TrimSpace(dateText)
	if s == "" {
		return false
	}
	t, err := time.Parse("2006-01-02", s)
	if err != nil {
		return false
	}
	loc := tmdbCNLocation()
	y, m, d := t.Date()
	airDay := time.Date(y, m, d, 0, 0, 0, 0, loc)
	return !airDay.After(tmdbCNDayStart(cutoff))
}

func pickLatestAiredFromSeasons(seasons []tmdbTVSeason, now time.Time) (int, int) {
	bestSeason := 0
	bestEpisodes := 0
	cutoff := tmdbCNDayStartPlus(now, tmdbSoonAirDays)
	for _, s := range seasons {
		if s.SeasonNumber <= 0 || s.EpisodeCount <= 0 {
			continue
		}
		air := strings.TrimSpace(s.AirDate)
		if air == "" {
			continue
		}
		if !isTMDBAirDateNotAfter(air, cutoff) {
			continue
		}
		if s.SeasonNumber > bestSeason {
			bestSeason = s.SeasonNumber
			bestEpisodes = s.EpisodeCount
		}
	}
	return bestSeason, bestEpisodes
}

func isNewerEpisodePair(seasonA, episodeA, seasonB, episodeB int) bool {
	if seasonA != seasonB {
		return seasonA > seasonB
	}
	return episodeA > episodeB
}

func fetchTMDBTVSeasonDetail(database *db.DB, tmdbID int, season int, language string) (*tmdbTVSeasonDetailResponse, error) {
	if database == nil || tmdbID <= 0 || season < 0 {
		return nil, fmt.Errorf("invalid args")
	}
	token, tokenKind := resolveTMDBToken(database)
	if token == "" || tokenKind == "" {
		return nil, fmt.Errorf("tmdb not configured")
	}
	apiBase := resolveTMDBAPIBase(database)
	u, _ := url.Parse(joinTMDBAPI(apiBase, fmt.Sprintf("tv/%d/season/%d", tmdbID, season)))
	params := u.Query()
	if strings.TrimSpace(language) != "" {
		params.Set("language", strings.TrimSpace(language))
	}
	if tokenKind == "v3" {
		params.Set("api_key", token)
	}
	u.RawQuery = params.Encode()

	req, err := http.NewRequest(http.MethodGet, u.String(), nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Accept", "application/json")
	if tokenKind == "v4" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	client := &http.Client{Timeout: 15 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, fmt.Errorf("tmdb http %d", resp.StatusCode)
	}
	var raw tmdbTVSeasonDetailResponse
	if err := json.NewDecoder(resp.Body).Decode(&raw); err != nil {
		return nil, err
	}
	return &raw, nil
}

func fetchAndStoreTMDBTVSeasonDetail(database *db.DB, tmdbID int, season int, language string) error {
	if database == nil || tmdbID <= 0 || season < 0 {
		return fmt.Errorf("invalid args")
	}
	raw, err := fetchTMDBTVSeasonDetail(database, tmdbID, season, language)
	if err != nil {
		return err
	}
	if raw == nil {
		return nil
	}
	episodes := make([]db.TMDBEpisode, 0, len(raw.Episodes))
	for _, e := range raw.Episodes {
		if e.EpisodeNumber <= 0 {
			continue
		}
		episodes = append(episodes, db.TMDBEpisode{
			SeasonNumber:  season,
			EpisodeNumber: e.EpisodeNumber,
			AirDate:       strings.TrimSpace(e.AirDate),
			Runtime:       e.Runtime,
			StillPath:     strings.TrimSpace(e.StillPath),
			Name:          strings.TrimSpace(e.Name),
			Overview:      strings.TrimSpace(e.Overview),
		})
	}
	_ = database.UpsertTMDBEpisodes(tmdbID, defaultString(language, "zh-CN"), episodes)
	_ = database.UpsertTMDBSeasons("tv", tmdbID, defaultString(language, "zh-CN"), []db.TMDBSeason{{
		SeasonNumber: season,
		PosterPath:   strings.TrimSpace(raw.PosterPath),
		Name:         strings.TrimSpace(raw.Name),
	}})
	return nil
}

func probeLatestAiredEpisodeFromSeasons(database *db.DB, tmdbID int, seasons []tmdbTVSeason, language string, now time.Time) (int, int) {
	if database == nil || tmdbID <= 0 || len(seasons) == 0 {
		return 0, 0
	}
	cutoff := tmdbCNDayStartPlus(now, tmdbSoonAirDays).Format("2006-01-02")

	type row struct {
		no   int
		date string
	}
	list := make([]row, 0, len(seasons))
	for _, s := range seasons {
		if s.SeasonNumber <= 0 {
			continue
		}
		ad := strings.TrimSpace(s.AirDate)
		if ad == "" || ad > cutoff {
			continue
		}
		list = append(list, row{no: s.SeasonNumber, date: ad})
	}
	for i := len(list) - 1; i >= 0; i-- {
		seasonNo := list[i].no
		detail, err := fetchTMDBTVSeasonDetail(database, tmdbID, seasonNo, language)
		if err != nil || detail == nil {
			continue
		}
		if len(detail.Episodes) > 0 {
			episodes := make([]db.TMDBEpisode, 0, len(detail.Episodes))
			for _, e := range detail.Episodes {
				if e.EpisodeNumber <= 0 {
					continue
				}
				episodes = append(episodes, db.TMDBEpisode{
					SeasonNumber:  seasonNo,
					EpisodeNumber: e.EpisodeNumber,
					AirDate:       strings.TrimSpace(e.AirDate),
					Runtime:       e.Runtime,
					StillPath:     strings.TrimSpace(e.StillPath),
					Name:          strings.TrimSpace(e.Name),
					Overview:      strings.TrimSpace(e.Overview),
				})
			}
			_ = database.UpsertTMDBEpisodes(tmdbID, defaultString(language, "zh-CN"), episodes)
			_ = database.UpsertTMDBSeasons("tv", tmdbID, defaultString(language, "zh-CN"), []db.TMDBSeason{{
				SeasonNumber: seasonNo,
				PosterPath:   strings.TrimSpace(detail.PosterPath),
				Name:         strings.TrimSpace(detail.Name),
			}})
		}

		latestEp := 0
		for _, e := range detail.Episodes {
			ad := strings.TrimSpace(e.AirDate)
			if ad == "" || ad > cutoff {
				continue
			}
			if e.EpisodeNumber > latestEp {
				latestEp = e.EpisodeNumber
			}
		}
		if latestEp > 0 {
			return seasonNo, latestEp
		}
	}
	return 0, 0
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

type UpstreamError struct {
	StatusCode int
	Body       string
	Message    string
}

func (e *UpstreamError) Error() string {
	if e == nil {
		return ""
	}
	if e.StatusCode > 0 {
		if e.Message != "" {
			return fmt.Sprintf("%s (http %d)", e.Message, e.StatusCode)
		}
		return fmt.Sprintf("tmdb http %d", e.StatusCode)
	}
	if e.Message != "" {
		return e.Message
	}
	return "tmdb upstream error"
}

func GetDetailPayload(database *db.DB, mediaType string, tmdbID int) (map[string]any, error) {
	if mediaType != "tv" && mediaType != "movie" {
		return nil, fmt.Errorf("invalid mediaType")
	}
	if tmdbID <= 0 || database == nil {
		return nil, fmt.Errorf("invalid tmdbID/db")
	}

	language := tmdbDetailLanguage(database)
	if cached, err := database.ReadTMDBDetailForAPI(mediaType, tmdbID, language); err == nil && cached != nil && strings.TrimSpace(cached.TMDBType) == mediaType {
		_ = database.TouchTMDBMediaAccess(mediaType, tmdbID, time.Now().Unix())
		out := buildTMDBDetailPayloadFromCache(database, cached)
		out["cache_status"] = true
		if mediaType == "tv" && shouldRefreshCachedTMDBTVDetail(database, cached, language, time.Now()) {
			if launchTMDBDetailRefresh(database, mediaType, tmdbID) {
				out["cache_status"] = "renew"
			}
		}
		return out, nil
	}

	out, err := fetchTMDBDetailForAPIUpstream(database, mediaType, tmdbID)
	if out != nil {
		out["cache_status"] = false
	}
	return out, err
}

func GetDetailForBackend(database *db.DB, mediaType string, tmdbID int) (*db.TMDBDetailForAPI, error) {
	if mediaType != "tv" && mediaType != "movie" {
		return nil, fmt.Errorf("invalid mediaType")
	}
	if database == nil || tmdbID <= 0 {
		return nil, fmt.Errorf("invalid tmdbID/db")
	}
	language := tmdbDetailLanguage(database)
	if cached, err := database.ReadTMDBDetailForAPI(mediaType, tmdbID, language); err == nil && cached != nil && strings.TrimSpace(cached.TMDBType) == mediaType {
		_ = database.TouchTMDBMediaAccess(mediaType, tmdbID, time.Now().Unix())
		if mediaType == "tv" && shouldRefreshCachedTMDBTVDetail(database, cached, language, time.Now()) {
			launchTMDBDetailRefresh(database, mediaType, tmdbID)
		}
		return cached, nil
	}
	if _, err := fetchTMDBDetailForAPIUpstream(database, mediaType, tmdbID); err != nil {
		return nil, err
	}
	return database.ReadTMDBDetailForAPI(mediaType, tmdbID, language)
}

func GetTVSeasonDetailForBackend(database *db.DB, tmdbID int, season int, minEpisodes int) (*db.TMDBSeasonDetailForAPI, error) {
	if database == nil || tmdbID <= 0 || season < 0 {
		return nil, fmt.Errorf("invalid args")
	}
	language := tmdbDetailLanguage(database)
	expectedEpisodes := 0
	if detail, err := database.ReadTMDBDetailForAPI("tv", tmdbID, language); err == nil && detail != nil {
		for _, s := range detail.Seasons {
			if s.SeasonNumber == season {
				expectedEpisodes = s.EpisodeCount
				break
			}
		}
	}
	if cached, err := database.ReadTMDBSeasonDetailForAPI(tmdbID, season, language); err == nil && cached != nil {
		cacheComplete := true
		if expectedEpisodes > 0 && len(cached.Episodes) == 0 {
			cacheComplete = false
		}
		if minEpisodes > 0 && len(cached.Episodes) < minEpisodes {
			cacheComplete = false
		}
		if cacheComplete {
			_ = database.TouchTMDBMediaAccess("tv", tmdbID, time.Now().Unix())
			if detail, err := database.ReadTMDBDetailForAPI("tv", tmdbID, language); err == nil && detail != nil && shouldRefreshCachedTMDBTVDetail(database, detail, language, time.Now()) {
				if detail.LatestSeason <= 0 || detail.LatestSeason == season {
					launchTMDBDetailRefresh(database, "tv", tmdbID)
				}
			}
			return cached, nil
		}
	}
	if err := fetchAndStoreTMDBTVSeasonDetail(database, tmdbID, season, language); err != nil {
		return nil, err
	}
	return database.ReadTMDBSeasonDetailForAPI(tmdbID, season, language)
}

func tmdbDetailLanguage(database *db.DB) string {
	if database != nil {
		if cfg, err := database.ReadAppConfig(); err == nil {
			if language := strings.TrimSpace(cfg.TMDBLanguage); language != "" {
				return language
			}
		}
	}
	return "zh-CN"
}

func buildTMDBDetailPayloadFromCache(database *db.DB, d *db.TMDBDetailForAPI) map[string]any {
	if d == nil {
		return nil
	}
	imgBase := resolveTMDBImageBase(database)
	if strings.TrimSpace(d.TMDBType) == "movie" {
		pic := strings.TrimSpace(d.PosterPath)
		if pic != "" && !strings.HasPrefix(pic, "http://") && !strings.HasPrefix(pic, "https://") {
			pic = joinTMDBImage(imgBase, "t/p/w500"+pic)
		}
		return map[string]any{
			"success":  true,
			"id":       d.TMDBID,
			"type":     "movie",
			"title":    strings.TrimSpace(d.Title),
			"year":     parseYearFromDate(d.Release),
			"poster":   pic,
			"overview": strings.TrimSpace(d.Overview),
			"badge":    "",
			"status":   strings.TrimSpace(d.Status),
		}
	}

	pic := strings.TrimSpace(d.PosterPath)
	if pic != "" && !strings.HasPrefix(pic, "http://") && !strings.HasPrefix(pic, "https://") {
		pic = joinTMDBImage(imgBase, "t/p/w500"+pic)
	}
	backdrop := strings.TrimSpace(d.Backdrop)
	if backdrop != "" && !strings.HasPrefix(backdrop, "http://") && !strings.HasPrefix(backdrop, "https://") {
		backdrop = joinTMDBImage(imgBase, "t/p/w780"+backdrop)
	}
	seasons := make([]map[string]any, 0, len(d.Seasons))
	seasonCount := 0
	for _, s := range d.Seasons {
		p := strings.TrimSpace(s.PosterPath)
		if p != "" && !strings.HasPrefix(p, "http://") && !strings.HasPrefix(p, "https://") {
			p = joinTMDBImage(imgBase, "t/p/w500"+p)
		}
		if s.SeasonNumber > 0 {
			seasonCount++
		}
		seasons = append(seasons, map[string]any{
			"season":   s.SeasonNumber,
			"episodes": s.EpisodeCount,
			"airDate":  strings.TrimSpace(s.AirDate),
			"poster":   p,
		})
	}
	status := strings.TrimSpace(d.Status)
	ended := strings.EqualFold(status, "Ended")
	badge := ""
	if d.LatestSeason > 0 && d.LatestEpisode > 0 {
		badge = "更新至 S" + strconv.Itoa(d.LatestSeason) + "E" + strconv.Itoa(d.LatestEpisode)
	} else if ended && d.EpisodeCount > 0 {
		badge = "共" + strconv.Itoa(d.EpisodeCount) + "集"
	}
	return map[string]any{
		"success":       true,
		"id":            d.TMDBID,
		"type":          "tv",
		"title":         strings.TrimSpace(d.Title),
		"year":          parseYearFromDate(d.FirstAir),
		"poster":        pic,
		"backdrop":      backdrop,
		"overview":      strings.TrimSpace(d.Overview),
		"badge":         badge,
		"status":        status,
		"latestSeason":  d.LatestSeason,
		"latestEpisode": d.LatestEpisode,
		"latestGlobal":  cachedTMDBLatestGlobal(d),
		"episodeCount":  d.EpisodeCount,
		"seasons":       seasons,
		"seasonCount":   seasonCount,
	}
}

func cachedTMDBLatestGlobal(d *db.TMDBDetailForAPI) int {
	if d == nil {
		return 0
	}
	if d.LatestSeason > 0 && d.LatestEpisode > 0 {
		sum := 0
		for _, s := range d.Seasons {
			if s.SeasonNumber <= 0 || s.EpisodeCount <= 0 {
				continue
			}
			if s.SeasonNumber < d.LatestSeason {
				sum += s.EpisodeCount
				continue
			}
			if s.SeasonNumber == d.LatestSeason {
				sum += d.LatestEpisode
				return sum
			}
		}
	}
	if strings.EqualFold(strings.TrimSpace(d.Status), "Ended") && d.EpisodeCount > 0 {
		return d.EpisodeCount
	}
	return 0
}

func shouldRefreshCachedTMDBTVDetail(database *db.DB, d *db.TMDBDetailForAPI, language string, now time.Time) bool {
	if database == nil || d == nil || d.TMDBID <= 0 || strings.TrimSpace(d.TMDBType) != "tv" {
		return false
	}
	if strings.EqualFold(strings.TrimSpace(d.Status), "Ended") {
		return false
	}
	if d.LatestSeason <= 0 || d.LatestEpisode <= 0 {
		return true
	}
	ok, err := database.HasTMDBEpisodeRealOverview(d.TMDBID, d.LatestSeason, d.LatestEpisode, language)
	if err != nil {
		return true
	}
	return !ok
}

func launchTMDBDetailRefresh(database *db.DB, mediaType string, tmdbID int) bool {
	if database == nil || tmdbID <= 0 {
		return false
	}
	cacheKey := strings.TrimSpace(mediaType) + ":" + strconv.Itoa(tmdbID)
	tmdbDetailRefreshInFlight.Lock()
	if tmdbDetailRefreshInFlight.M == nil {
		tmdbDetailRefreshInFlight.M = map[string]bool{}
	}
	if tmdbDetailRefreshInFlight.M[cacheKey] {
		tmdbDetailRefreshInFlight.Unlock()
		return false
	}
	tmdbDetailRefreshInFlight.M[cacheKey] = true
	tmdbDetailRefreshInFlight.Unlock()

	go func() {
		defer func() {
			tmdbDetailRefreshInFlight.Lock()
			delete(tmdbDetailRefreshInFlight.M, cacheKey)
			tmdbDetailRefreshInFlight.Unlock()
		}()
		_, _ = fetchTMDBDetailForAPIUpstream(database, mediaType, tmdbID)
	}()
	return true
}

func fetchTMDBDetailForAPIUpstream(database *db.DB, mediaType string, tmdbID int) (map[string]any, error) {
	if mediaType != "tv" && mediaType != "movie" {
		return nil, fmt.Errorf("invalid mediaType")
	}
	if tmdbID <= 0 || database == nil {
		return nil, fmt.Errorf("invalid tmdbID/db")
	}

	token, tokenKind := resolveTMDBToken(database)
	if token == "" || tokenKind == "" {
		return nil, fmt.Errorf("tmdb not configured")
	}

	language := tmdbDetailLanguage(database)

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
		return nil, err
	}
	req.Header.Set("Accept", "application/json")
	if tokenKind == "v4" {
		req.Header.Set("Authorization", "Bearer "+token)
	}

	client := &http.Client{Timeout: 15 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 4096))
		return nil, &UpstreamError{
			StatusCode: resp.StatusCode,
			Body:       strings.TrimSpace(string(body)),
			Message:    "tmdb request failed",
		}
	}

	if mediaType == "tv" {
		var detail tmdbTVDetailsResponse
		dec := json.NewDecoder(resp.Body)
		if err := dec.Decode(&detail); err != nil {
			return nil, err
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
			if isTMDBAirDateNotAfter(detail.LastEpisodeToAir.AirDate, time.Now().AddDate(0, 0, tmdbSoonAirDays)) {
				latestSeason = detail.LastEpisodeToAir.SeasonNumber
				latestEpisode = detail.LastEpisodeToAir.EpisodeNumber
			}
		}
		if sNo, eNo := probeLatestAiredEpisodeFromSeasons(database, tmdbID, detail.Seasons, language, time.Now().UTC()); sNo > 0 && eNo > 0 {
			if latestSeason <= 0 || latestEpisode <= 0 || isNewerEpisodePair(sNo, eNo, latestSeason, latestEpisode) {
				latestSeason = sNo
				latestEpisode = eNo
			}
		}
		ended := strings.EqualFold(strings.TrimSpace(detail.Status), "Ended")
		badge := ""
		if latestSeason > 0 && latestEpisode > 0 {
			badge = "更新至 S" + strconv.Itoa(latestSeason) + "E" + strconv.Itoa(latestEpisode)
		} else if ended && detail.NumberOfEpisodes > 0 {
			badge = "共" + strconv.Itoa(detail.NumberOfEpisodes) + "集"
		}

		seasons := make([]map[string]any, 0, len(detail.Seasons))
		seasonCount := 0
		latestGlobalEpisode := 0
		if latestSeason > 0 && latestEpisode > 0 {
			sum := 0
			for _, s := range detail.Seasons {
				if s.SeasonNumber <= 0 || s.EpisodeCount <= 0 {
					continue
				}
				if s.SeasonNumber < latestSeason {
					sum += s.EpisodeCount
				} else if s.SeasonNumber == latestSeason {
					sum += latestEpisode
					break
				}
			}
			if sum > 0 {
				latestGlobalEpisode = sum
			}
		} else if ended && detail.NumberOfEpisodes > 0 {
			latestGlobalEpisode = detail.NumberOfEpisodes
		}
		for _, s := range detail.Seasons {
			if s.SeasonNumber < 0 {
				continue
			}
			if s.SeasonNumber > 0 {
				seasonCount++
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
			"latestGlobal":  latestGlobalEpisode,
			"episodeCount":  detail.NumberOfEpisodes,
			"seasons":       seasons,
			"seasonCount":   seasonCount,
		}
		return out, nil
	}

	var detail tmdbMovieDetailsResponse
	dec := json.NewDecoder(resp.Body)
	if err := dec.Decode(&detail); err != nil {
		return nil, err
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
	return out, nil
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
