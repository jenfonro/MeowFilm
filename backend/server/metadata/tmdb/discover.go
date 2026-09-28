package tmdb

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

type tmdbDiscoverResponse struct {
	Page       int `json:"page"`
	TotalPages int `json:"total_pages"`
	Total      int `json:"total_results"`
	Results    []struct {
		ID           int    `json:"id"`
		Title        string `json:"title"`
		Name         string `json:"name"`
		PosterPath   string `json:"poster_path"`
		ReleaseDate  string `json:"release_date"`
		FirstAirDate string `json:"first_air_date"`
	} `json:"results"`
}

type DiscoverItem struct {
	ID         int
	MediaType  string
	Title      string
	PosterPath string
	Year       int
}

func Discover(database *db.DB, mediaType string, yearStart int, yearEnd int, sortBy string, page int) (items []DiscoverItem, total int, err error) {
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

	token, tokenKind := resolveTMDBToken(database)
	if token == "" || tokenKind == "" {
		return nil, 0, errors.New("TMDB not configured")
	}
	cfg, _ := database.ReadAppConfig()
	lang := strings.TrimSpace(cfg.TMDBLanguage)
	if lang == "" {
		lang = "zh-CN"
	}
	region := strings.TrimSpace(cfg.TMDBRegion)
	if region == "" {
		region = "CN"
	}
	includeAdult := cfg.TMDBIncludeAdult

	apiBase := resolveTMDBAPIBase(database)
	sort := strings.TrimSpace(sortBy)
	if sort == "" {
		sort = "popularity.desc"
	}
	endpoint := joinTMDBAPI(apiBase, "discover/"+mt)
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
	if tokenKind == "v3" {
		params.Set("api_key", token)
	}
	u.RawQuery = params.Encode()

	req, _ := http.NewRequest(http.MethodGet, u.String(), nil)
	req.Header.Set("Accept", "application/json")
	if tokenKind == "v4" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	client := &http.Client{Timeout: 10 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return nil, 0, err
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, 0, fmt.Errorf("tmdb http %d", resp.StatusCode)
	}
	var data tmdbDiscoverResponse
	if err := json.NewDecoder(resp.Body).Decode(&data); err != nil {
		return nil, 0, err
	}
	out := make([]DiscoverItem, 0, len(data.Results))
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
		out = append(out, DiscoverItem{
			ID:         it.ID,
			MediaType:  mt,
			Title:      title,
			PosterPath: strings.TrimSpace(it.PosterPath),
			Year:       y,
		})
	}
	return out, data.Total, nil
}
