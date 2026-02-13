package routes

import (
	"encoding/json"
	"net/http"
	"net/url"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
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

type tmdbTVDetailsResponse struct {
	Status           string `json:"status"`
	NumberOfEpisodes int    `json:"number_of_episodes"`
	LastEpisodeToAir *struct {
		SeasonNumber  int `json:"season_number"`
		EpisodeNumber int `json:"episode_number"`
	} `json:"last_episode_to_air"`
	Name         string `json:"name"`
	PosterPath   string `json:"poster_path"`
	BackdropPath string `json:"backdrop_path"`
	FirstAir     string `json:"first_air_date"`
	Overview     string `json:"overview"`
	Seasons      []struct {
		SeasonNumber int    `json:"season_number"`
		EpisodeCount int    `json:"episode_count"`
		AirDate      string `json:"air_date"`
		PosterPath   string `json:"poster_path"`
	} `json:"seasons"`
}

type tmdbMovieDetailsResponse struct {
	Title       string `json:"title"`
	PosterPath  string `json:"poster_path"`
	ReleaseDate string `json:"release_date"`
	Overview    string `json:"overview"`
	Status      string `json:"status"`
}

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

func resolveTMDBAPIBase(database *db.DB) string {
	raw := ""
	if database != nil {
		raw = strings.TrimSpace(database.GetSetting("tmdb_api_base"))
	}
	base := normalizeHTTPBase(raw)
	if base == "" {
		return "https://api.themoviedb.org/3"
	}
	return base
}

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

func handleAPITMDBSearch(w http.ResponseWriter, r *http.Request, database *db.DB) {
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

	v4 := strings.TrimSpace(database.GetSetting("tmdb_v4_token"))
	v3 := strings.TrimSpace(database.GetSetting("tmdb_v3_key"))
	if v4 == "" && v3 == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{
			"error": "TMDB 未配置",
			"code":  "TMDB_TOKEN_INVALID",
		})
		return
	}

	language := strings.TrimSpace(database.GetSetting("tmdb_language"))
	if language == "" {
		language = "zh-CN"
	}
	region := strings.TrimSpace(database.GetSetting("tmdb_region"))
	if region == "" {
		region = "CN"
	}
	includeAdult := strings.TrimSpace(database.GetSetting("tmdb_include_adult")) == "1"
	lite := strings.TrimSpace(r.URL.Query().Get("lite")) == "1" ||
		strings.TrimSpace(r.URL.Query().Get("simple")) == "1" ||
		strings.TrimSpace(r.URL.Query().Get("noDetail")) == "1"

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
	if v3 != "" {
		params.Set("api_key", v3)
	}
	u.RawQuery = params.Encode()

	req, err := http.NewRequest(http.MethodGet, u.String(), nil)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "请求构造失败"})
		return
	}
	req.Header.Set("Accept", "application/json")
	if v4 != "" {
		req.Header.Set("Authorization", "Bearer "+v4)
	}

	client := &http.Client{Timeout: 8 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{
			"error": "TMDB 请求失败",
			"code":  "TMDB_CONNECT_FAILED",
		})
		return
	}
	defer resp.Body.Close()

	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		writeJSON(w, http.StatusBadGateway, map[string]any{
			"error":  "TMDB 返回异常",
			"code":   "TMDB_UPSTREAM_BAD_STATUS",
			"status": resp.StatusCode,
		})
		return
	}

	var data tmdbMultiSearchResponse
	if err := json.NewDecoder(resp.Body).Decode(&data); err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{
			"error": "TMDB 解析失败",
			"code":  "TMDB_PARSE_FAILED",
		})
		return
	}

	type outItem struct {
		ID        string
		TMDBID    int
		MediaType string
		Name      string
		Pic       string
		Year      int
		Badge     string
		SeasonCnt int
	}
	items := []outItem{}
	for _, it := range data.Results {
		typ := strings.TrimSpace(it.MediaType)
		if typ != "movie" && typ != "tv" {
			continue
		}
		name := strings.TrimSpace(it.Title)
		if name == "" {
			name = strings.TrimSpace(it.Name)
		}
		if name == "" || it.ID <= 0 {
			continue
		}
		year := parseYear(it.ReleaseDate)
		if year == 0 {
			year = parseYear(it.FirstAir)
		}
		pic := ""
		if strings.TrimSpace(it.PosterPath) != "" {
			pic = "https://image.tmdb.org/t/p/w342" + strings.TrimSpace(it.PosterPath)
		}
		badge := ""
		if typ == "movie" {
			badge = "电影"
		} else if typ == "tv" {
			badge = "剧集"
		}
		items = append(items, outItem{
			ID:        typ + ":" + strconv.Itoa(it.ID),
			TMDBID:    it.ID,
			MediaType: typ,
			Name:      name,
			Pic:       pic,
			Year:      year,
			Badge:     badge,
			SeasonCnt: 0,
		})
	}

	if lite {
		list := make([]map[string]any, 0, len(items))
		for _, it := range items {
			row := map[string]any{
				"id":        it.ID,
				"tmdbId":    it.TMDBID,
				"mediaType": it.MediaType,
				"name":      it.Name,
				"pic":       it.Pic,
				"year":      it.Year,
				"badge":     strings.TrimSpace(it.Badge),
			}
			list = append(list, row)
		}
		writeJSON(w, 200, map[string]any{"success": true, "list": list})
		return
	}

	fetchTVBadge := func(id int) (badge string, seasonCount int, _ bool) {
		if id <= 0 {
			return "", 0, false
		}
		cacheKey := apiBase + "::tv::" + strconv.Itoa(id) + "::" + language
		now := time.Now().Unix()
		if hit, ok := tmdbDetailCacheGet(cacheKey, now); ok && hit != nil {
			if b, ok := hit["badge"].(string); ok && strings.TrimSpace(b) != "" {
				if seasonsAny, ok := hit["seasons"].([]any); ok && len(seasonsAny) > 0 {
					for _, v := range seasonsAny {
						m, _ := v.(map[string]any)
						if m == nil {
							continue
						}
						if sn, ok := m["season"]; ok {
							switch vv := sn.(type) {
							case float64:
								if int(vv) > 0 {
									seasonCount += 1
								}
							case int:
								if vv > 0 {
									seasonCount += 1
								}
							}
						}
					}
				}
				return strings.TrimSpace(b), seasonCount, true
			}
		}

		u, _ := url.Parse(joinTMDBAPI(apiBase, "tv/"+strconv.Itoa(id)))
		q := u.Query()
		if language != "" {
			q.Set("language", language)
		}
		if v3 != "" {
			q.Set("api_key", v3)
		}
		u.RawQuery = q.Encode()

		req, err := http.NewRequest(http.MethodGet, u.String(), nil)
		if err != nil {
			return "", 0, false
		}
		req.Header.Set("Accept", "application/json")
		if v4 != "" {
			req.Header.Set("Authorization", "Bearer "+v4)
		}

		resp, err := client.Do(req)
		if err != nil {
			return "", 0, false
		}
		defer resp.Body.Close()
		if resp.StatusCode < 200 || resp.StatusCode >= 300 {
			return "", 0, false
		}

		var d tmdbTVDetailsResponse
		if err := json.NewDecoder(resp.Body).Decode(&d); err != nil {
			return "", 0, false
		}
		status := strings.TrimSpace(d.Status)
		last := 0
		if d.LastEpisodeToAir != nil && d.LastEpisodeToAir.EpisodeNumber > 0 {
			last = d.LastEpisodeToAir.EpisodeNumber
		}
		total := 0
		if d.NumberOfEpisodes > 0 {
			total = d.NumberOfEpisodes
		}
		seasonCount = 0
		seasons := []map[string]any{}
		for _, s := range d.Seasons {
			if s.SeasonNumber > 0 {
				seasonCount += 1
			}
			if s.SeasonNumber < 0 || s.EpisodeCount < 0 {
				continue
			}
			seasons = append(seasons, map[string]any{
				"season":       s.SeasonNumber,
				"episodeCount": s.EpisodeCount,
				"airDate":      strings.TrimSpace(s.AirDate),
			})
		}

		ended := status == "Ended"
		if ended {
			if total > 0 {
				if seasonCount >= 2 {
					badge = "共" + strconv.Itoa(seasonCount) + "季" + strconv.Itoa(total) + "集"
				} else {
					badge = "共" + strconv.Itoa(total) + "集"
				}
			} else if last > 0 {
				badge = "共" + strconv.Itoa(last) + "集"
			} else {
				badge = "完结"
			}
		} else {
			if last > 0 {
				badge = "更新至" + strconv.Itoa(last) + "集"
			} else if total > 0 {
				badge = "更新至" + strconv.Itoa(total) + "集"
			} else {
				badge = "更新中"
			}
		}

		ttl := tmdbDetailCacheTTL
		if ended {
			ttl = tmdbDetailCacheTTLEnded
		}
		out := map[string]any{
			"success":       true,
			"tmdbId":        id,
			"mediaType":     "tv",
			"title":         strings.TrimSpace(d.Name),
			"year":          parseYear(d.FirstAir),
			"pic":           "",
			"overview":      strings.TrimSpace(d.Overview),
			"badge":         strings.TrimSpace(badge),
			"status":        status,
			"latestSeason":  0,
			"latestEpisode": 0,
			"episodeCount":  total,
			"seasons":       seasons,
		}
		if strings.TrimSpace(d.PosterPath) != "" {
			out["pic"] = "https://image.tmdb.org/t/p/w500" + strings.TrimSpace(d.PosterPath)
		}
		if d.LastEpisodeToAir != nil {
			if d.LastEpisodeToAir.SeasonNumber > 0 {
				out["latestSeason"] = d.LastEpisodeToAir.SeasonNumber
			}
			if d.LastEpisodeToAir.EpisodeNumber > 0 {
				out["latestEpisode"] = d.LastEpisodeToAir.EpisodeNumber
			}
		}
		tmdbDetailCacheSet(cacheKey, out, ttl, now)

		return strings.TrimSpace(badge), seasonCount, true
	}

	// Fill TV badges with a bounded parallelism so search remains responsive.
	workerCount := 4
	if n := runtime.GOMAXPROCS(0); n > 0 && n < workerCount {
		workerCount = n
	}
	sem := make(chan struct{}, workerCount)
	var wg sync.WaitGroup
	for i := range items {
		if items[i].MediaType != "tv" || items[i].TMDBID <= 0 {
			continue
		}
		wg.Add(1)
		sem <- struct{}{}
		go func(idx int) {
			defer wg.Done()
			defer func() { <-sem }()
			if b, sc, ok := fetchTVBadge(items[idx].TMDBID); ok && strings.TrimSpace(b) != "" {
				items[idx].Badge = b
				if sc > 0 {
					items[idx].SeasonCnt = sc
				}
			}
		}(i)
	}
	wg.Wait()

	list := make([]map[string]any, 0, len(items))
	for _, it := range items {
		row := map[string]any{
			"id":        it.ID,
			"tmdbId":    it.TMDBID,
			"mediaType": it.MediaType,
			"name":      it.Name,
			"pic":       it.Pic,
			"year":      it.Year,
			"badge":     strings.TrimSpace(it.Badge),
		}
		if it.MediaType == "tv" && it.SeasonCnt > 0 {
			row["seasonCount"] = it.SeasonCnt
		}
		list = append(list, row)
	}

	writeJSON(w, 200, map[string]any{"success": true, "list": list})
}

func handleAPITMDBDetail(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}

	rawID := strings.TrimSpace(r.URL.Query().Get("id"))
	if rawID == "" {
		rawID = strings.TrimSpace(r.URL.Query().Get("tmdbId"))
	}
	id, _ := strconv.Atoi(rawID)
	if id <= 0 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "参数无效"})
		return
	}
	typ := strings.TrimSpace(r.URL.Query().Get("type"))
	if typ == "" {
		typ = strings.TrimSpace(r.URL.Query().Get("mediaType"))
	}
	if typ != "tv" && typ != "movie" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "参数无效"})
		return
	}

	v4 := strings.TrimSpace(database.GetSetting("tmdb_v4_token"))
	v3 := strings.TrimSpace(database.GetSetting("tmdb_v3_key"))
	if v4 == "" && v3 == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{
			"error": "TMDB 未配置",
			"code":  "TMDB_TOKEN_INVALID",
		})
		return
	}

	language := strings.TrimSpace(database.GetSetting("tmdb_language"))
	if language == "" {
		language = "zh-CN"
	}

	apiBase := resolveTMDBAPIBase(database)
	cacheKey := apiBase + "::" + typ + "::" + strconv.Itoa(id) + "::" + language
	now := time.Now().Unix()
	if hit, ok := tmdbDetailCacheGet(cacheKey, now); ok && hit != nil {
		writeJSON(w, 200, hit)
		return
	}

	u, _ := url.Parse(joinTMDBAPI(apiBase, typ+"/"+strconv.Itoa(id)))
	params := u.Query()
	if language != "" {
		params.Set("language", language)
	}
	if v3 != "" {
		params.Set("api_key", v3)
	}
	u.RawQuery = params.Encode()

	req, err := http.NewRequest(http.MethodGet, u.String(), nil)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "请求构造失败"})
		return
	}
	req.Header.Set("Accept", "application/json")
	if v4 != "" {
		req.Header.Set("Authorization", "Bearer "+v4)
	}

	client := &http.Client{Timeout: 8 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{
			"error": "TMDB 请求失败",
			"code":  "TMDB_CONNECT_FAILED",
		})
		return
	}
	defer resp.Body.Close()

	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		writeJSON(w, http.StatusBadGateway, map[string]any{
			"error":  "TMDB 返回异常",
			"code":   "TMDB_UPSTREAM_BAD_STATUS",
			"status": resp.StatusCode,
		})
		return
	}

	pic := ""
	title := ""
	year := 0
	overview := ""
	badge := ""
	latestEpisode := 0
	latestSeason := 0
	episodeCount := 0
	statusRaw := ""
	seasons := []map[string]any{}

	if typ == "tv" {
		var d tmdbTVDetailsResponse
		if err := json.NewDecoder(resp.Body).Decode(&d); err != nil {
			writeJSON(w, http.StatusBadGateway, map[string]any{
				"error": "TMDB 解析失败",
				"code":  "TMDB_PARSE_FAILED",
			})
			return
		}
		title = strings.TrimSpace(d.Name)
		year = parseYear(d.FirstAir)
		overview = strings.TrimSpace(d.Overview)
		statusRaw = strings.TrimSpace(d.Status)
		if strings.TrimSpace(d.PosterPath) != "" {
			pic = "https://image.tmdb.org/t/p/w500" + strings.TrimSpace(d.PosterPath)
		}
		if d.LastEpisodeToAir != nil && d.LastEpisodeToAir.EpisodeNumber > 0 {
			latestEpisode = d.LastEpisodeToAir.EpisodeNumber
			if d.LastEpisodeToAir.SeasonNumber > 0 {
				latestSeason = d.LastEpisodeToAir.SeasonNumber
			}
		}
		if d.NumberOfEpisodes > 0 {
			episodeCount = d.NumberOfEpisodes
		}
		ended := statusRaw == "Ended"
		seasonCount := 0
		for _, s := range d.Seasons {
			if s.SeasonNumber < 0 {
				continue
			}
			if s.EpisodeCount < 0 {
				continue
			}
			if s.SeasonNumber > 0 {
				seasonCount += 1
			}
			seasons = append(seasons, map[string]any{
				"season":       s.SeasonNumber,
				"episodeCount": s.EpisodeCount,
				"airDate":      strings.TrimSpace(s.AirDate),
			})
		}
		if ended {
			if episodeCount > 0 {
				if seasonCount >= 2 {
					badge = "共" + strconv.Itoa(seasonCount) + "季" + strconv.Itoa(episodeCount) + "集"
				} else {
					badge = "共" + strconv.Itoa(episodeCount) + "集"
				}
			} else if latestEpisode > 0 {
				badge = "共" + strconv.Itoa(latestEpisode) + "集"
			} else {
				badge = "完结"
			}
		} else {
			if latestEpisode > 0 {
				badge = "更新至" + strconv.Itoa(latestEpisode) + "集"
			} else {
				badge = "更新中"
			}
		}
	} else {
		var d tmdbMovieDetailsResponse
		if err := json.NewDecoder(resp.Body).Decode(&d); err != nil {
			writeJSON(w, http.StatusBadGateway, map[string]any{
				"error": "TMDB 解析失败",
				"code":  "TMDB_PARSE_FAILED",
			})
			return
		}
		title = strings.TrimSpace(d.Title)
		year = parseYear(d.ReleaseDate)
		overview = strings.TrimSpace(d.Overview)
		if strings.TrimSpace(d.PosterPath) != "" {
			pic = "https://image.tmdb.org/t/p/w500" + strings.TrimSpace(d.PosterPath)
		}
		badge = "电影"
		latestEpisode = 1
		latestSeason = 1
		episodeCount = 1
		statusRaw = strings.TrimSpace(d.Status)
	}

	out := map[string]any{
		"success":       true,
		"tmdbId":        id,
		"mediaType":     typ,
		"title":         title,
		"year":          year,
		"pic":           pic,
		"overview":      overview,
		"badge":         badge,
		"status":        statusRaw,
		"latestSeason":  latestSeason,
		"latestEpisode": latestEpisode,
		"episodeCount":  episodeCount,
		"seasons":       seasons,
	}

	ttl := tmdbDetailCacheTTL
	if typ == "tv" {
		if sr, ok := out["status"].(string); ok && strings.TrimSpace(sr) == "Ended" {
			ttl = tmdbDetailCacheTTLEnded
		}
	}

	tmdbDetailCacheSet(cacheKey, out, ttl, now)

	writeJSON(w, 200, out)
}

func boolToStr(v bool) string {
	if v {
		return "true"
	}
	return "false"
}

func parseYear(s string) int {
	raw := strings.TrimSpace(s)
	if len(raw) < 4 {
		return 0
	}
	y, err := strconv.Atoi(raw[:4])
	if err != nil {
		return 0
	}
	if y < 1900 || y > 2100 {
		return 0
	}
	return y
}
