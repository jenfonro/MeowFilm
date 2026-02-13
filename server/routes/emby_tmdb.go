package routes

import (
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

type embyTMDBSearchItem struct {
	ID         int
	MediaType  string // "tv" | "movie"
	Title      string
	PosterPath string
	Year       int
}

type tmdbSearchTVResponse struct {
	Results []struct {
		ID         int    `json:"id"`
		Name       string `json:"name"`
		PosterPath string `json:"poster_path"`
		FirstAir   string `json:"first_air_date"`
	} `json:"results"`
}

type tmdbSearchMovieResponse struct {
	Results []struct {
		ID          int    `json:"id"`
		Title       string `json:"title"`
		PosterPath  string `json:"poster_path"`
		ReleaseDate string `json:"release_date"`
	} `json:"results"`
}

type tmdbDiscoverResponse struct {
	Page       int `json:"page"`
	TotalPages int `json:"total_pages"`
	Total      int `json:"total_results"`
	Results    []struct {
		ID           int    `json:"id"`
		Title        string `json:"title"`          // movie
		Name         string `json:"name"`           // tv
		PosterPath   string `json:"poster_path"`    // both
		ReleaseDate  string `json:"release_date"`   // movie
		FirstAirDate string `json:"first_air_date"` // tv
	} `json:"results"`
}

type embyTMDBTVDetail struct {
	ID       int
	Title    string
	Overview string
	Year     int
	Poster   string
	Backdrop string
	Seasons  []embyTMDBSeason
}

type embyTMDBSeason struct {
	Season       int
	EpisodeCount int
	Poster       string
}

type embyTMDBSeasonEpisode struct {
	Episode  int
	Name     string
	Overview string
	Still    string
	AirDate  string
}

type embyTMDBMovieDetail struct {
	ID       int
	Title    string
	Overview string
	Year     int
	Poster   string
}

type embyTMDBCredits struct {
	MediaType string // "movie" | "tv"
	ID        int
	Cast      []embyTMDBCast
	Crew      []embyTMDBCrew
}

type embyTMDBCast struct {
	ID      int
	Name    string
	Role    string
	Profile string
	Order   int
}

type embyTMDBCrew struct {
	ID      int
	Name    string
	Job     string
	Dept    string
	Profile string
}

type embyPersonProfileCacheEntry struct {
	At     time.Time
	Expire time.Time
	Path   string
}

var embyPersonProfileCache = struct {
	sync.Mutex
	M map[int]embyPersonProfileCacheEntry
}{
	M: map[int]embyPersonProfileCacheEntry{},
}

const embyPersonProfileCacheTTL = 24 * time.Hour

func embyRememberPersonProfile(personID int, profilePath string) {
	if personID <= 0 {
		return
	}
	p := strings.TrimSpace(profilePath)
	if p == "" {
		return
	}
	now := time.Now()
	embyPersonProfileCache.Lock()
	if embyPersonProfileCache.M == nil {
		embyPersonProfileCache.M = map[int]embyPersonProfileCacheEntry{}
	}
	embyPersonProfileCache.M[personID] = embyPersonProfileCacheEntry{
		At:     now,
		Expire: now.Add(embyPersonProfileCacheTTL),
		Path:   p,
	}
	embyPersonProfileCache.Unlock()
}

func embyCachedPersonProfile(personID int) string {
	if personID <= 0 {
		return ""
	}
	now := time.Now()
	embyPersonProfileCache.Lock()
	defer embyPersonProfileCache.Unlock()
	if embyPersonProfileCache.M == nil {
		return ""
	}
	hit, ok := embyPersonProfileCache.M[personID]
	if !ok || strings.TrimSpace(hit.Path) == "" {
		return ""
	}
	if !hit.Expire.IsZero() && hit.Expire.Before(now) {
		delete(embyPersonProfileCache.M, personID)
		return ""
	}
	return strings.TrimSpace(hit.Path)
}

func embyTMDBImageURL(path string, size string) string {
	p := strings.TrimSpace(path)
	if p == "" {
		return ""
	}
	if !strings.HasPrefix(p, "/") {
		p = "/" + p
	}
	sz := strings.TrimSpace(size)
	if sz == "" {
		sz = "w500"
	}
	return "https://image.tmdb.org/t/p/" + sz + p
}

func embyTMDBClient(database *db.DB) (*http.Client, string, string, string, string, bool) {
	v4 := strings.TrimSpace(database.GetSetting("tmdb_v4_token"))
	v3 := strings.TrimSpace(database.GetSetting("tmdb_v3_key"))
	lang := strings.TrimSpace(database.GetSetting("tmdb_language"))
	if lang == "" {
		lang = "zh-CN"
	}
	region := strings.TrimSpace(database.GetSetting("tmdb_region"))
	if region == "" {
		region = "CN"
	}
	includeAdult := strings.TrimSpace(database.GetSetting("tmdb_include_adult")) == "1"
	return &http.Client{Timeout: 10 * time.Second}, v4, v3, lang, region, includeAdult
}

func embyTMDBSearchMulti(database *db.DB, query string) ([]embyTMDBSearchItem, error) {
	q := strings.TrimSpace(query)
	if q == "" {
		return nil, errors.New("empty query")
	}
	client, v4, v3, lang, region, includeAdult := embyTMDBClient(database)
	if v4 == "" && v3 == "" {
		return nil, errors.New("TMDB not configured")
	}

	searchOnce := func(query string) ([]embyTMDBSearchItem, error) {
		qq := strings.TrimSpace(query)
		if qq == "" {
			return nil, errors.New("empty query")
		}

		doTV := func(language string) ([]embyTMDBSearchItem, error) {
			u, _ := url.Parse("https://api.themoviedb.org/3/search/tv")
			params := u.Query()
			params.Set("query", qq)
			params.Set("page", "1")
			params.Set("include_adult", boolToStr(includeAdult))
			if strings.TrimSpace(language) != "" {
				params.Set("language", strings.TrimSpace(language))
			}
			if v3 != "" {
				params.Set("api_key", v3)
			}
			u.RawQuery = params.Encode()
			req, _ := http.NewRequest(http.MethodGet, u.String(), nil)
			req.Header.Set("Accept", "application/json")
			if v4 != "" {
				req.Header.Set("Authorization", "Bearer "+v4)
			}
			resp, err := client.Do(req)
			if err != nil {
				return nil, err
			}
			defer resp.Body.Close()
			if resp.StatusCode < 200 || resp.StatusCode >= 300 {
				return nil, fmt.Errorf("tmdb http %d", resp.StatusCode)
			}
			var data tmdbSearchTVResponse
			if err := json.NewDecoder(resp.Body).Decode(&data); err != nil {
				return nil, err
			}
			out := make([]embyTMDBSearchItem, 0, len(data.Results))
			for _, it := range data.Results {
				if it.ID <= 0 {
					continue
				}
				title := strings.TrimSpace(it.Name)
				if title == "" {
					continue
				}
				year := 0
				if len(strings.TrimSpace(it.FirstAir)) >= 4 {
					if y, err := strconv.Atoi(strings.TrimSpace(it.FirstAir)[:4]); err == nil && y > 0 {
						year = y
					}
				}
				out = append(out, embyTMDBSearchItem{
					ID:         it.ID,
					MediaType:  "tv",
					Title:      title,
					PosterPath: strings.TrimSpace(it.PosterPath),
					Year:       year,
				})
			}
			return out, nil
		}

		doMovie := func(language string) ([]embyTMDBSearchItem, error) {
			u, _ := url.Parse("https://api.themoviedb.org/3/search/movie")
			params := u.Query()
			params.Set("query", qq)
			params.Set("page", "1")
			params.Set("include_adult", boolToStr(includeAdult))
			if strings.TrimSpace(language) != "" {
				params.Set("language", strings.TrimSpace(language))
			}
			if v3 != "" {
				params.Set("api_key", v3)
			}
			u.RawQuery = params.Encode()
			req, _ := http.NewRequest(http.MethodGet, u.String(), nil)
			req.Header.Set("Accept", "application/json")
			if v4 != "" {
				req.Header.Set("Authorization", "Bearer "+v4)
			}
			resp, err := client.Do(req)
			if err != nil {
				return nil, err
			}
			defer resp.Body.Close()
			if resp.StatusCode < 200 || resp.StatusCode >= 300 {
				return nil, fmt.Errorf("tmdb http %d", resp.StatusCode)
			}
			var data tmdbSearchMovieResponse
			if err := json.NewDecoder(resp.Body).Decode(&data); err != nil {
				return nil, err
			}
			out := make([]embyTMDBSearchItem, 0, len(data.Results))
			for _, it := range data.Results {
				if it.ID <= 0 {
					continue
				}
				title := strings.TrimSpace(it.Title)
				if title == "" {
					continue
				}
				year := 0
				if len(strings.TrimSpace(it.ReleaseDate)) >= 4 {
					if y, err := strconv.Atoi(strings.TrimSpace(it.ReleaseDate)[:4]); err == nil && y > 0 {
						year = y
					}
				}
				out = append(out, embyTMDBSearchItem{
					ID:         it.ID,
					MediaType:  "movie",
					Title:      title,
					PosterPath: strings.TrimSpace(it.PosterPath),
					Year:       year,
				})
			}
			return out, nil
		}

		doReq := func(language string, region string) ([]embyTMDBSearchItem, error) {
			u, _ := url.Parse("https://api.themoviedb.org/3/search/multi")
			params := u.Query()
			params.Set("query", qq)
			params.Set("page", "1")
			params.Set("include_adult", boolToStr(includeAdult))
			if strings.TrimSpace(language) != "" {
				params.Set("language", strings.TrimSpace(language))
			}
			if strings.TrimSpace(region) != "" {
				params.Set("region", strings.TrimSpace(region))
			}
			if v3 != "" {
				params.Set("api_key", v3)
			}
			u.RawQuery = params.Encode()

			req, _ := http.NewRequest(http.MethodGet, u.String(), nil)
			req.Header.Set("Accept", "application/json")
			if v4 != "" {
				req.Header.Set("Authorization", "Bearer "+v4)
			}

			resp, err := client.Do(req)
			if err != nil {
				return nil, err
			}
			defer resp.Body.Close()
			if resp.StatusCode < 200 || resp.StatusCode >= 300 {
				return nil, fmt.Errorf("tmdb http %d", resp.StatusCode)
			}

			var data tmdbMultiSearchResponse
			if err := json.NewDecoder(resp.Body).Decode(&data); err != nil {
				return nil, err
			}

			out := make([]embyTMDBSearchItem, 0, len(data.Results))
			for _, it := range data.Results {
				mt := strings.TrimSpace(it.MediaType)
				if mt != "tv" && mt != "movie" {
					continue
				}
				title := strings.TrimSpace(it.Title)
				if title == "" {
					title = strings.TrimSpace(it.Name)
				}
				if title == "" || it.ID <= 0 {
					continue
				}
				year := 0
				date := strings.TrimSpace(it.ReleaseDate)
				if date == "" {
					date = strings.TrimSpace(it.FirstAir)
				}
				if len(date) >= 4 {
					if y, err := strconv.Atoi(date[:4]); err == nil && y > 0 {
						year = y
					}
				}
				out = append(out, embyTMDBSearchItem{
					ID:         it.ID,
					MediaType:  mt,
					Title:      title,
					PosterPath: strings.TrimSpace(it.PosterPath),
					Year:       year,
				})
			}
			return out, nil
		}

		primary, err := doReq(lang, region)
		if err != nil {
			return nil, err
		}
		if len(primary) > 0 {
			return primary, nil
		}

		// Fallbacks: some queries (esp. CJK) may return empty under certain language/region settings.
		// Try several relaxed variants, and finally try tv/movie endpoints directly.
		if alt, err := doReq("zh-CN", ""); err == nil && len(alt) > 0 {
			return alt, nil
		}
		if alt, err := doReq("", ""); err == nil && len(alt) > 0 {
			return alt, nil
		}
		if alt, err := doReq("en-US", ""); err == nil && len(alt) > 0 {
			return alt, nil
		}

		// As a last resort, search tv/movie separately and merge a small list.
		tv, _ := doTV("zh-CN")
		if len(tv) == 0 {
			tv, _ = doTV("")
		}
		mv, _ := doMovie("zh-CN")
		if len(mv) == 0 {
			mv, _ = doMovie("")
		}
		merged := make([]embyTMDBSearchItem, 0, len(tv)+len(mv))
		seen := map[string]bool{}
		for _, it := range tv {
			k := it.MediaType + ":" + strconv.Itoa(it.ID)
			if seen[k] {
				continue
			}
			seen[k] = true
			merged = append(merged, it)
			if len(merged) >= 20 {
				break
			}
		}
		for _, it := range mv {
			if len(merged) >= 20 {
				break
			}
			k := it.MediaType + ":" + strconv.Itoa(it.ID)
			if seen[k] {
				continue
			}
			seen[k] = true
			merged = append(merged, it)
		}
		if len(merged) > 0 {
			return merged, nil
		}
		return primary, nil
	}

	items, err := searchOnce(q)
	if err != nil {
		return nil, err
	}
	if len(items) > 0 {
		return items, nil
	}

	return items, nil
}

func embyTMDBDiscover(database *db.DB, mediaType string, yearStart int, yearEnd int, sortBy string, page int) (items []embyTMDBSearchItem, total int, err error) {
	mt := strings.TrimSpace(strings.ToLower(mediaType))
	if mt != "movie" && mt != "tv" {
		return nil, 0, errors.New("invalid mediaType")
	}
	if yearStart <= 0 {
		return nil, 0, errors.New("invalid year")
	}
	if yearEnd <= 0 {
		yearEnd = yearStart
	}
	if yearEnd < yearStart {
		yearStart, yearEnd = yearEnd, yearStart
	}
	if page <= 0 {
		page = 1
	}

	client, v4, v3, lang, region, includeAdult := embyTMDBClient(database)
	if v4 == "" && v3 == "" {
		return nil, 0, errors.New("TMDB not configured")
	}

	sort := strings.TrimSpace(sortBy)
	if sort == "" {
		sort = "popularity.desc"
	}

	endpoint := "https://api.themoviedb.org/3/discover/" + mt
	u, _ := url.Parse(endpoint)
	params := u.Query()
	params.Set("page", strconv.Itoa(page))
	params.Set("include_adult", boolToStr(includeAdult))
	params.Set("sort_by", sort)
	if strings.TrimSpace(lang) != "" {
		params.Set("language", strings.TrimSpace(lang))
	}
	if mt == "movie" {
		if strings.TrimSpace(region) != "" {
			params.Set("region", strings.TrimSpace(region))
		}
		if yearStart == yearEnd {
			params.Set("primary_release_year", strconv.Itoa(yearStart))
		} else {
			params.Set("primary_release_date.gte", fmt.Sprintf("%04d-01-01", yearStart))
			params.Set("primary_release_date.lte", fmt.Sprintf("%04d-12-31", yearEnd))
		}
	} else {
		if yearStart == yearEnd {
			params.Set("first_air_date_year", strconv.Itoa(yearStart))
		} else {
			params.Set("first_air_date.gte", fmt.Sprintf("%04d-01-01", yearStart))
			params.Set("first_air_date.lte", fmt.Sprintf("%04d-12-31", yearEnd))
		}
	}
	if v3 != "" {
		params.Set("api_key", v3)
	}
	u.RawQuery = params.Encode()

	req, _ := http.NewRequest(http.MethodGet, u.String(), nil)
	req.Header.Set("Accept", "application/json")
	if v4 != "" {
		req.Header.Set("Authorization", "Bearer "+v4)
	}
	resp, err := client.Do(req)
	if err != nil {
		return nil, 0, err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, 0, fmt.Errorf("tmdb http %d", resp.StatusCode)
	}
	var data tmdbDiscoverResponse
	if err := json.NewDecoder(resp.Body).Decode(&data); err != nil {
		return nil, 0, err
	}
	out := make([]embyTMDBSearchItem, 0, len(data.Results))
	for _, it := range data.Results {
		if it.ID <= 0 {
			continue
		}
		title := strings.TrimSpace(it.Title)
		if mt == "tv" {
			title = strings.TrimSpace(it.Name)
		}
		if title == "" {
			continue
		}
		y := 0
		date := strings.TrimSpace(it.ReleaseDate)
		if mt == "tv" {
			date = strings.TrimSpace(it.FirstAirDate)
		}
		if len(date) >= 4 {
			if yy, err := strconv.Atoi(date[:4]); err == nil && yy > 0 {
				y = yy
			}
		}
		out = append(out, embyTMDBSearchItem{
			ID:         it.ID,
			MediaType:  mt,
			Title:      title,
			PosterPath: strings.TrimSpace(it.PosterPath),
			Year:       y,
		})
	}
	return out, data.Total, nil
}

func embyTMDBGetTVDetail(database *db.DB, tmdbID int) (*embyTMDBTVDetail, error) {
	id := tmdbID
	if id <= 0 {
		return nil, errors.New("invalid tmdb id")
	}
	client, v4, v3, lang, _, _ := embyTMDBClient(database)
	if v4 == "" && v3 == "" {
		return nil, errors.New("TMDB not configured")
	}

	u, _ := url.Parse(fmt.Sprintf("https://api.themoviedb.org/3/tv/%d", id))
	params := u.Query()
	if lang != "" {
		params.Set("language", lang)
	}
	if v3 != "" {
		params.Set("api_key", v3)
	}
	u.RawQuery = params.Encode()

	req, _ := http.NewRequest(http.MethodGet, u.String(), nil)
	req.Header.Set("Accept", "application/json")
	if v4 != "" {
		req.Header.Set("Authorization", "Bearer "+v4)
	}

	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, fmt.Errorf("tmdb http %d", resp.StatusCode)
	}

	var data tmdbTVDetailsResponse
	if err := json.NewDecoder(resp.Body).Decode(&data); err != nil {
		return nil, err
	}
	year := 0
	if len(strings.TrimSpace(data.FirstAir)) >= 4 {
		if y, err := strconv.Atoi(strings.TrimSpace(data.FirstAir)[:4]); err == nil {
			year = y
		}
	}
	seasons := make([]embyTMDBSeason, 0, len(data.Seasons))
	for _, s := range data.Seasons {
		if s.SeasonNumber < 0 || s.EpisodeCount <= 0 {
			continue
		}
		seasons = append(seasons, embyTMDBSeason{
			Season:       s.SeasonNumber,
			EpisodeCount: s.EpisodeCount,
			Poster:       strings.TrimSpace(s.PosterPath),
		})
	}
	return &embyTMDBTVDetail{
		ID:       id,
		Title:    strings.TrimSpace(data.Name),
		Overview: strings.TrimSpace(data.Overview),
		Year:     year,
		Poster:   strings.TrimSpace(data.PosterPath),
		Backdrop: strings.TrimSpace(data.BackdropPath),
		Seasons:  seasons,
	}, nil
}

type embyTMDBTVSeasonDetail struct {
	ID       int
	Season   int
	Name     string
	Poster   string
	Episodes []embyTMDBSeasonEpisode
}

type embySeasonDetailCacheEntry struct {
	At     time.Time
	Expire time.Time
	Data   *embyTMDBTVSeasonDetail
}

var embySeasonDetailCache = struct {
	sync.Mutex
	M map[string]embySeasonDetailCacheEntry
}{
	M: map[string]embySeasonDetailCacheEntry{},
}

const embySeasonDetailCacheTTL = 10 * time.Minute

func embyTMDBGetTVSeasonDetail(database *db.DB, tmdbID int, season int) (*embyTMDBTVSeasonDetail, error) {
	if tmdbID <= 0 || season < 0 {
		return nil, errors.New("invalid args")
	}

	cacheKey := fmt.Sprintf("tv:%d:s:%d", tmdbID, season)
	now := time.Now()
	embySeasonDetailCache.Lock()
	if embySeasonDetailCache.M != nil {
		if hit, ok := embySeasonDetailCache.M[cacheKey]; ok && hit.Data != nil && hit.Expire.After(now) {
			d := hit.Data
			embySeasonDetailCache.Unlock()
			return d, nil
		}
	}
	embySeasonDetailCache.Unlock()

	client, v4, v3, lang, _, _ := embyTMDBClient(database)
	if v4 == "" && v3 == "" {
		return nil, errors.New("TMDB not configured")
	}
	u, _ := url.Parse(fmt.Sprintf("https://api.themoviedb.org/3/tv/%d/season/%d", tmdbID, season))
	params := u.Query()
	if lang != "" {
		params.Set("language", lang)
	}
	if v3 != "" {
		params.Set("api_key", v3)
	}
	u.RawQuery = params.Encode()

	req, _ := http.NewRequest(http.MethodGet, u.String(), nil)
	req.Header.Set("Accept", "application/json")
	if v4 != "" {
		req.Header.Set("Authorization", "Bearer "+v4)
	}

	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, fmt.Errorf("tmdb http %d", resp.StatusCode)
	}
	var raw struct {
		Name       string `json:"name"`
		PosterPath string `json:"poster_path"`
		Episodes   []struct {
			EpisodeNumber int    `json:"episode_number"`
			Name          string `json:"name"`
			Overview      string `json:"overview"`
			StillPath     string `json:"still_path"`
			AirDate       string `json:"air_date"`
		} `json:"episodes"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&raw); err != nil {
		return nil, err
	}
	out := make([]embyTMDBSeasonEpisode, 0, len(raw.Episodes))
	for _, e := range raw.Episodes {
		if e.EpisodeNumber <= 0 {
			continue
		}
		out = append(out, embyTMDBSeasonEpisode{
			Episode:  e.EpisodeNumber,
			Name:     strings.TrimSpace(e.Name),
			Overview: strings.TrimSpace(e.Overview),
			Still:    strings.TrimSpace(e.StillPath),
			AirDate:  strings.TrimSpace(e.AirDate),
		})
	}
	outDetail := &embyTMDBTVSeasonDetail{
		ID:       tmdbID,
		Season:   season,
		Name:     strings.TrimSpace(raw.Name),
		Poster:   strings.TrimSpace(raw.PosterPath),
		Episodes: out,
	}

	embySeasonDetailCache.Lock()
	if embySeasonDetailCache.M == nil {
		embySeasonDetailCache.M = map[string]embySeasonDetailCacheEntry{}
	}
	embySeasonDetailCache.M[cacheKey] = embySeasonDetailCacheEntry{
		At:     now,
		Expire: now.Add(embySeasonDetailCacheTTL),
		Data:   outDetail,
	}
	embySeasonDetailCache.Unlock()

	return outDetail, nil
}

func embyTMDBGetTVSeasonEpisodes(database *db.DB, tmdbID int, season int) ([]embyTMDBSeasonEpisode, error) {
	if tmdbID <= 0 || season < 0 {
		return nil, errors.New("invalid args")
	}
	d, err := embyTMDBGetTVSeasonDetail(database, tmdbID, season)
	if err != nil {
		return nil, err
	}
	if d == nil {
		return []embyTMDBSeasonEpisode{}, nil
	}
	return d.Episodes, nil
}

func embyTMDBGetMovieDetail(database *db.DB, tmdbID int) (*embyTMDBMovieDetail, error) {
	id := tmdbID
	if id <= 0 {
		return nil, errors.New("invalid tmdb id")
	}
	client, v4, v3, lang, _, _ := embyTMDBClient(database)
	if v4 == "" && v3 == "" {
		return nil, errors.New("TMDB not configured")
	}
	u, _ := url.Parse(fmt.Sprintf("https://api.themoviedb.org/3/movie/%d", id))
	params := u.Query()
	if lang != "" {
		params.Set("language", lang)
	}
	if v3 != "" {
		params.Set("api_key", v3)
	}
	u.RawQuery = params.Encode()

	req, _ := http.NewRequest(http.MethodGet, u.String(), nil)
	req.Header.Set("Accept", "application/json")
	if v4 != "" {
		req.Header.Set("Authorization", "Bearer "+v4)
	}
	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, fmt.Errorf("tmdb http %d", resp.StatusCode)
	}
	var data tmdbMovieDetailsResponse
	if err := json.NewDecoder(resp.Body).Decode(&data); err != nil {
		return nil, err
	}
	year := 0
	if len(strings.TrimSpace(data.ReleaseDate)) >= 4 {
		if y, err := strconv.Atoi(strings.TrimSpace(data.ReleaseDate)[:4]); err == nil {
			year = y
		}
	}
	return &embyTMDBMovieDetail{
		ID:       id,
		Title:    strings.TrimSpace(data.Title),
		Overview: strings.TrimSpace(data.Overview),
		Year:     year,
		Poster:   strings.TrimSpace(data.PosterPath),
	}, nil
}

type embyCreditsCacheEntry struct {
	At     time.Time
	Expire time.Time
	Data   *embyTMDBCredits
}

var embyCreditsCache = struct {
	sync.Mutex
	M map[string]embyCreditsCacheEntry
}{
	M: map[string]embyCreditsCacheEntry{},
}

const embyCreditsCacheTTL = 10 * time.Minute

func embyTMDBGetCredits(database *db.DB, mediaType string, tmdbID int) (*embyTMDBCredits, error) {
	typ := strings.ToLower(strings.TrimSpace(mediaType))
	if (typ != "movie" && typ != "tv") || tmdbID <= 0 {
		return nil, errors.New("invalid args")
	}

	cacheKey := typ + ":" + strconv.Itoa(tmdbID) + ":credits"
	now := time.Now()
	embyCreditsCache.Lock()
	if embyCreditsCache.M != nil {
		if hit, ok := embyCreditsCache.M[cacheKey]; ok && hit.Data != nil && hit.Expire.After(now) {
			d := hit.Data
			embyCreditsCache.Unlock()
			return d, nil
		}
	}
	embyCreditsCache.Unlock()

	client, v4, v3, lang, _, _ := embyTMDBClient(database)
	if v4 == "" && v3 == "" {
		return nil, errors.New("TMDB not configured")
	}

	u, _ := url.Parse(fmt.Sprintf("https://api.themoviedb.org/3/%s/%d/credits", typ, tmdbID))
	params := u.Query()
	if strings.TrimSpace(lang) != "" {
		params.Set("language", strings.TrimSpace(lang))
	}
	if v3 != "" {
		params.Set("api_key", v3)
	}
	u.RawQuery = params.Encode()

	req, _ := http.NewRequest(http.MethodGet, u.String(), nil)
	req.Header.Set("Accept", "application/json")
	if v4 != "" {
		req.Header.Set("Authorization", "Bearer "+v4)
	}

	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, fmt.Errorf("tmdb http %d", resp.StatusCode)
	}

	var raw struct {
		Cast []struct {
			ID          int    `json:"id"`
			Name        string `json:"name"`
			Character   string `json:"character"`
			Order       int    `json:"order"`
			ProfilePath string `json:"profile_path"`
		} `json:"cast"`
		Crew []struct {
			ID          int    `json:"id"`
			Name        string `json:"name"`
			Job         string `json:"job"`
			Department  string `json:"department"`
			ProfilePath string `json:"profile_path"`
		} `json:"crew"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&raw); err != nil {
		return nil, err
	}

	out := &embyTMDBCredits{MediaType: typ, ID: tmdbID}
	if len(raw.Cast) > 0 {
		out.Cast = make([]embyTMDBCast, 0, len(raw.Cast))
		for _, c := range raw.Cast {
			name := strings.TrimSpace(c.Name)
			if c.ID <= 0 || name == "" {
				continue
			}
			embyRememberPersonProfile(c.ID, c.ProfilePath)
			out.Cast = append(out.Cast, embyTMDBCast{
				ID:      c.ID,
				Name:    name,
				Role:    strings.TrimSpace(c.Character),
				Profile: strings.TrimSpace(c.ProfilePath),
				Order:   c.Order,
			})
		}
	}
	if len(raw.Crew) > 0 {
		out.Crew = make([]embyTMDBCrew, 0, len(raw.Crew))
		for _, c := range raw.Crew {
			name := strings.TrimSpace(c.Name)
			if c.ID <= 0 || name == "" {
				continue
			}
			embyRememberPersonProfile(c.ID, c.ProfilePath)
			out.Crew = append(out.Crew, embyTMDBCrew{
				ID:      c.ID,
				Name:    name,
				Job:     strings.TrimSpace(c.Job),
				Dept:    strings.TrimSpace(c.Department),
				Profile: strings.TrimSpace(c.ProfilePath),
			})
		}
	}

	embyCreditsCache.Lock()
	if embyCreditsCache.M == nil {
		embyCreditsCache.M = map[string]embyCreditsCacheEntry{}
	}
	embyCreditsCache.M[cacheKey] = embyCreditsCacheEntry{
		At:     now,
		Expire: now.Add(embyCreditsCacheTTL),
		Data:   out,
	}
	embyCreditsCache.Unlock()

	return out, nil
}

func embyTMDBGetPersonProfile(database *db.DB, personID int) (string, error) {
	if personID <= 0 {
		return "", errors.New("invalid person id")
	}
	if hit := embyCachedPersonProfile(personID); hit != "" {
		return hit, nil
	}

	client, v4, v3, lang, _, _ := embyTMDBClient(database)
	if v4 == "" && v3 == "" {
		return "", errors.New("TMDB not configured")
	}

	u, _ := url.Parse(fmt.Sprintf("https://api.themoviedb.org/3/person/%d", personID))
	params := u.Query()
	if strings.TrimSpace(lang) != "" {
		params.Set("language", strings.TrimSpace(lang))
	}
	if v3 != "" {
		params.Set("api_key", v3)
	}
	u.RawQuery = params.Encode()

	req, _ := http.NewRequest(http.MethodGet, u.String(), nil)
	req.Header.Set("Accept", "application/json")
	if v4 != "" {
		req.Header.Set("Authorization", "Bearer "+v4)
	}

	resp, err := client.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return "", fmt.Errorf("tmdb http %d", resp.StatusCode)
	}

	var raw struct {
		ProfilePath string `json:"profile_path"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&raw); err != nil {
		return "", err
	}
	p := strings.TrimSpace(raw.ProfilePath)
	if p != "" {
		embyRememberPersonProfile(personID, p)
	}
	return p, nil
}
