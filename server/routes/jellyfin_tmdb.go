package routes

import (
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

type jellyfinTMDBSearchItem struct {
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

type jellyfinTMDBTVDetail struct {
	ID       int
	Title    string
	Overview string
	Year     int
	Poster   string
	Seasons  []jellyfinTMDBSeason
}

type jellyfinTMDBSeason struct {
	Season       int
	EpisodeCount int
	Poster       string
}

type jellyfinTMDBSeasonEpisode struct {
	Episode  int
	Name     string
	Overview string
	Still    string
	AirDate  string
}

type jellyfinTMDBMovieDetail struct {
	ID       int
	Title    string
	Overview string
	Year     int
	Poster   string
}

func jellyfinTMDBImageURL(path string, size string) string {
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

func jellyfinTMDBClient(database *db.DB) (*http.Client, string, string, string, string, bool) {
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

func jellyfinTMDBSearchMulti(database *db.DB, query string) ([]jellyfinTMDBSearchItem, error) {
	q := strings.TrimSpace(query)
	if q == "" {
		return nil, errors.New("empty query")
	}
	client, v4, v3, lang, region, includeAdult := jellyfinTMDBClient(database)
	if v4 == "" && v3 == "" {
		return nil, errors.New("TMDB not configured")
	}

	searchOnce := func(query string) ([]jellyfinTMDBSearchItem, error) {
		qq := strings.TrimSpace(query)
		if qq == "" {
			return nil, errors.New("empty query")
		}

		doTV := func(language string) ([]jellyfinTMDBSearchItem, error) {
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
			out := make([]jellyfinTMDBSearchItem, 0, len(data.Results))
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
				out = append(out, jellyfinTMDBSearchItem{
					ID:         it.ID,
					MediaType:  "tv",
					Title:      title,
					PosterPath: strings.TrimSpace(it.PosterPath),
					Year:       year,
				})
			}
			return out, nil
		}

		doMovie := func(language string) ([]jellyfinTMDBSearchItem, error) {
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
			out := make([]jellyfinTMDBSearchItem, 0, len(data.Results))
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
				out = append(out, jellyfinTMDBSearchItem{
					ID:         it.ID,
					MediaType:  "movie",
					Title:      title,
					PosterPath: strings.TrimSpace(it.PosterPath),
					Year:       year,
				})
			}
			return out, nil
		}

		doReq := func(language string, region string) ([]jellyfinTMDBSearchItem, error) {
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

			out := make([]jellyfinTMDBSearchItem, 0, len(data.Results))
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
				out = append(out, jellyfinTMDBSearchItem{
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
		merged := make([]jellyfinTMDBSearchItem, 0, len(tv)+len(mv))
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

func jellyfinTMDBGetTVDetail(database *db.DB, tmdbID int) (*jellyfinTMDBTVDetail, error) {
	id := tmdbID
	if id <= 0 {
		return nil, errors.New("invalid tmdb id")
	}
	client, v4, v3, lang, _, _ := jellyfinTMDBClient(database)
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
	seasons := make([]jellyfinTMDBSeason, 0, len(data.Seasons))
	for _, s := range data.Seasons {
		if s.SeasonNumber < 0 || s.EpisodeCount <= 0 {
			continue
		}
		seasons = append(seasons, jellyfinTMDBSeason{
			Season:       s.SeasonNumber,
			EpisodeCount: s.EpisodeCount,
			Poster:       "",
		})
	}
	return &jellyfinTMDBTVDetail{
		ID:       id,
		Title:    strings.TrimSpace(data.Name),
		Overview: strings.TrimSpace(data.Overview),
		Year:     year,
		Poster:   strings.TrimSpace(data.PosterPath),
		Seasons:  seasons,
	}, nil
}

func jellyfinTMDBGetTVSeasonEpisodes(database *db.DB, tmdbID int, season int) ([]jellyfinTMDBSeasonEpisode, error) {
	if tmdbID <= 0 || season < 0 {
		return nil, errors.New("invalid args")
	}
	client, v4, v3, lang, _, _ := jellyfinTMDBClient(database)
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
		Episodes []struct {
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
	out := make([]jellyfinTMDBSeasonEpisode, 0, len(raw.Episodes))
	for _, e := range raw.Episodes {
		if e.EpisodeNumber <= 0 {
			continue
		}
		out = append(out, jellyfinTMDBSeasonEpisode{
			Episode:  e.EpisodeNumber,
			Name:     strings.TrimSpace(e.Name),
			Overview: strings.TrimSpace(e.Overview),
			Still:    strings.TrimSpace(e.StillPath),
			AirDate:  strings.TrimSpace(e.AirDate),
		})
	}
	return out, nil
}

func jellyfinTMDBGetMovieDetail(database *db.DB, tmdbID int) (*jellyfinTMDBMovieDetail, error) {
	id := tmdbID
	if id <= 0 {
		return nil, errors.New("invalid tmdb id")
	}
	client, v4, v3, lang, _, _ := jellyfinTMDBClient(database)
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
	return &jellyfinTMDBMovieDetail{
		ID:       id,
		Title:    strings.TrimSpace(data.Title),
		Overview: strings.TrimSpace(data.Overview),
		Year:     year,
		Poster:   strings.TrimSpace(data.PosterPath),
	}, nil
}
