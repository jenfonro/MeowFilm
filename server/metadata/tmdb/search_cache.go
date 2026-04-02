package tmdb

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/cache"
)

type tmdbMultiSearchResponse struct {
	Results []struct {
		Adult            bool     `json:"adult"`
		ID               int      `json:"id"`
		MediaType        string   `json:"media_type"`
		Title            string   `json:"title"`
		Name             string   `json:"name"`
		OriginalTitle    string   `json:"original_title"`
		OriginalName     string   `json:"original_name"`
		Overview         string   `json:"overview"`
		PosterPath       string   `json:"poster_path"`
		BackdropPath     string   `json:"backdrop_path"`
		OriginalLanguage string   `json:"original_language"`
		GenreIDs         []int    `json:"genre_ids"`
		Popularity       float64  `json:"popularity"`
		ReleaseDate      string   `json:"release_date"`
		FirstAir         string   `json:"first_air_date"`
		VoteAverage      float64  `json:"vote_average"`
		VoteCount        int      `json:"vote_count"`
		OriginCountry    []string `json:"origin_country"`
	} `json:"results"`
}

const tmdbSearchRawCacheTTL = 6 * time.Hour

var tmdbSearchRawCache = cache.NewTTLInflightCache[[]byte](tmdbSearchRawCacheTTL, 1024)

func SearchMulti(database *db.DB, query string) ([]SearchItem, error) {
	rawBody, _, err := fetchMultiSearchRaw(database, query)
	if err != nil {
		return nil, err
	}
	var raw tmdbMultiSearchResponse
	if err := json.Unmarshal(rawBody, &raw); err != nil {
		return nil, err
	}
	return rememberMultiSearchResults(database, &raw)
}

func SearchMultiRaw(database *db.DB, query string) ([]byte, error) {
	rawBody, _, err := fetchMultiSearchRaw(database, query)
	if err != nil {
		return nil, err
	}
	var raw tmdbMultiSearchResponse
	if err := json.Unmarshal(rawBody, &raw); err != nil {
		return nil, err
	}
	if _, err := rememberMultiSearchResults(database, &raw); err != nil {
		return nil, err
	}
	return rawBody, nil
}

func fetchMultiSearchRaw(database *db.DB, query string) ([]byte, bool, error) {
	q := strings.TrimSpace(query)
	if q == "" {
		return nil, false, errors.New("empty query")
	}
	token, tokenKind := resolveTMDBToken(database)
	if token == "" || tokenKind == "" {
		return nil, false, errors.New("tmdb not configured")
	}
	u, _ := url.Parse(joinTMDBAPI(resolveTMDBAPIBase(database), "search/multi"))
	params := u.Query()
	params.Set("query", q)
	params.Set("page", "1")
	params.Set("include_adult", boolToStr(tmdbIncludeAdult(database)))
	if lang := tmdbDetailLanguage(database); lang != "" {
		params.Set("language", lang)
	}
	if region := tmdbRegion(database); region != "" {
		params.Set("region", region)
	}
	if tokenKind == "v3" {
		params.Set("api_key", token)
	}
	u.RawQuery = params.Encode()

	cacheKey := buildTMDBSearchCacheKey(q, tmdbIncludeAdult(database), tmdbDetailLanguage(database), tmdbRegion(database))
	body, fromCache, err := tmdbSearchRawCache.Do(cacheKey, func() ([]byte, error) {
		req, err := http.NewRequest(http.MethodGet, u.String(), nil)
		if err != nil {
			return nil, err
		}
		req.Header.Set("Accept", "application/json")
		if tokenKind == "v4" {
			req.Header.Set("Authorization", "Bearer "+token)
		}
		client := &http.Client{Timeout: 10 * time.Second}
		resp, err := client.Do(req)
		if err != nil {
			return nil, err
		}
		defer func() { _ = resp.Body.Close() }()
		if resp.StatusCode < 200 || resp.StatusCode >= 300 {
			return nil, fmt.Errorf("tmdb http %d", resp.StatusCode)
		}
		return io.ReadAll(resp.Body)
	})
	if err != nil {
		return nil, false, err
	}
	return body, fromCache, nil
}

func rememberMultiSearchResults(database *db.DB, raw *tmdbMultiSearchResponse) ([]SearchItem, error) {
	if raw == nil {
		return nil, nil
	}
	out := make([]SearchItem, 0, len(raw.Results))
	for _, it := range raw.Results {
		if it.ID <= 0 {
			continue
		}
		mediaType := strings.TrimSpace(it.MediaType)
		if mediaType != "movie" && mediaType != "tv" {
			continue
		}
		title := strings.TrimSpace(it.Title)
		original := strings.TrimSpace(it.OriginalTitle)
		date := strings.TrimSpace(it.ReleaseDate)
		if mediaType == "tv" {
			title = strings.TrimSpace(it.Name)
			original = strings.TrimSpace(it.OriginalName)
			date = strings.TrimSpace(it.FirstAir)
		}
		if title == "" {
			continue
		}
		rawItemJSON, _ := json.Marshal(it)
		out = append(out, SearchItem{
			ID:               it.ID,
			MediaType:        mediaType,
			SearchJSON:       string(rawItemJSON),
			Title:            title,
			OriginalTitle:    original,
			Overview:         strings.TrimSpace(it.Overview),
			PosterPath:       strings.TrimSpace(it.PosterPath),
			BackdropPath:     strings.TrimSpace(it.BackdropPath),
			ReleaseDate:      strings.TrimSpace(it.ReleaseDate),
			FirstAirDate:     strings.TrimSpace(it.FirstAir),
			OriginalLanguage: strings.TrimSpace(it.OriginalLanguage),
			GenreIDs:         append([]int(nil), it.GenreIDs...),
			Popularity:       it.Popularity,
			VoteAverage:      it.VoteAverage,
			VoteCount:        it.VoteCount,
			OriginCountry:    append([]string(nil), it.OriginCountry...),
			Adult:            it.Adult,
			Year:             parseSearchYear(date),
		})
	}
	if database != nil {
		lang := tmdbDetailLanguage(database)
		for _, item := range out {
			if err := RememberSearchHitAsPartial(database, item, lang); err != nil {
				return nil, err
			}
		}
	}
	return out, nil
}

func tmdbIncludeAdult(database *db.DB) bool {
	if database != nil {
		if cfg, err := database.ReadAppConfig(); err == nil {
			return cfg.TMDBIncludeAdult
		}
	}
	return false
}

func tmdbRegion(database *db.DB) string {
	if database != nil {
		if cfg, err := database.ReadAppConfig(); err == nil {
			if region := strings.TrimSpace(cfg.TMDBRegion); region != "" {
				return region
			}
		}
	}
	return "CN"
}

func ResolveByTitlesCached(database *db.DB, kind string, candidates []string, year int, lang string) (tmdbID int, matchedTitle string, err error) {
	k := strings.TrimSpace(kind)
	if k != "movie" && k != "tv" {
		return 0, "", errors.New("invalid args")
	}
	cands := make([]string, 0, len(candidates))
	seen := map[string]struct{}{}
	for _, candidate := range candidates {
		s := strings.TrimSpace(candidate)
		if s == "" {
			continue
		}
		key := strings.ToLower(s)
		if _, ok := seen[key]; ok {
			continue
		}
		seen[key] = struct{}{}
		cands = append(cands, s)
	}
	if len(cands) == 0 {
		return 0, "", nil
	}
	if database != nil {
		if tid, err := database.FindTMDBMediaByTitles(k, cands, year, defaultString(strings.TrimSpace(lang), "zh-CN")); err == nil && tid > 0 {
			return tid, cands[0], nil
		} else if err != nil {
			return 0, "", err
		}
	}
	for _, candidate := range cands {
		items, err := SearchMulti(database, candidate)
		if err != nil {
			return 0, candidate, err
		}
		hit := pickBestSearchItem(items, k, year)
		if hit == nil {
			continue
		}
		if database != nil {
			if err := RememberSearchHitAsPartial(database, *hit, lang); err != nil {
				return 0, candidate, err
			}
		}
		return hit.ID, candidate, nil
	}
	return 0, "", nil
}

func pickBestSearchItem(items []SearchItem, kind string, year int) *SearchItem {
	for _, it := range items {
		if it.ID <= 0 || strings.TrimSpace(it.MediaType) != strings.TrimSpace(kind) {
			continue
		}
		if year > 0 && it.Year > 0 && year != it.Year {
			continue
		}
		cp := it
		return &cp
	}
	for _, it := range items {
		if it.ID <= 0 || strings.TrimSpace(it.MediaType) != strings.TrimSpace(kind) {
			continue
		}
		cp := it
		return &cp
	}
	return nil
}

func parseSearchYear(dateText string) int {
	s := strings.TrimSpace(dateText)
	if len(s) < 4 {
		return 0
	}
	y, err := strconv.Atoi(s[:4])
	if err != nil || y <= 0 {
		return 0
	}
	return y
}

func buildTMDBSearchCacheKey(query string, includeAdult bool, lang string, region string) string {
	return strings.Join([]string{
		strings.ToLower(strings.TrimSpace(query)),
		boolToStr(includeAdult),
		strings.TrimSpace(lang),
		strings.TrimSpace(region),
	}, "|")
}
