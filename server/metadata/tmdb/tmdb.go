package tmdb

import (
	"encoding/json"
	"fmt"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	mfnet "github.com/jenfonro/meowfilm/server/net"
)

type tmdbTVDetailsResponse struct {
	Status           string `json:"status"`
	InProduction     bool   `json:"in_production"`
	NumberOfEpisodes int    `json:"number_of_episodes"`
	Homepage         string `json:"homepage"`
	ExternalIDs      *struct {
		IMDBID string `json:"imdb_id"`
		TVDBID int    `json:"tvdb_id"`
	} `json:"external_ids"`
	Videos *struct {
		Results []struct {
			Site string `json:"site"`
			Key  string `json:"key"`
			Type string `json:"type"`
		} `json:"results"`
	} `json:"videos"`
	LastEpisodeToAir *struct {
		SeasonNumber  int    `json:"season_number"`
		EpisodeNumber int    `json:"episode_number"`
		AirDate       string `json:"air_date"`
	} `json:"last_episode_to_air"`
	NextEpisodeToAir *struct {
		SeasonNumber  int    `json:"season_number"`
		EpisodeNumber int    `json:"episode_number"`
		AirDate       string `json:"air_date"`
	} `json:"next_episode_to_air"`
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
	ID           int    `json:"id"`
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

type TVSeasonDetailResponse = tmdbTVSeasonDetailResponse

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
	Homepage    string `json:"homepage"`
	ExternalIDs *struct {
		IMDBID string `json:"imdb_id"`
	} `json:"external_ids"`
	Videos *struct {
		Results []struct {
			Site string `json:"site"`
			Key  string `json:"key"`
			Type string `json:"type"`
		} `json:"results"`
	} `json:"videos"`
}

type MovieDetailsResponse = tmdbMovieDetailsResponse

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

func resolveTMDBAPIBase(database *db.DB) string {
	raw := ""
	if database != nil {
		if cfg, err := database.ReadAppConfig(); err == nil {
			raw = strings.TrimSpace(cfg.TMDBAPIBase)
		}
	}
	base := catpawrunner.NormalizeHTTPBase(raw)
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
	base := catpawrunner.NormalizeHTTPBase(raw)
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

func GetRawDetailPayload(database *db.DB, mediaType string, tmdbID int) (map[string]any, error) {
	if mediaType != "tv" && mediaType != "movie" {
		return nil, fmt.Errorf("invalid mediaType")
	}
	if tmdbID <= 0 || database == nil {
		return nil, fmt.Errorf("invalid tmdbID/db")
	}

	var err error
	if mediaType == "movie" {
		_, _, err = ensureMovieDetailFresh(database, tmdbID)
	} else {
		_, _, err = ensureTVDetailFresh(database, tmdbID)
	}
	if err != nil {
		return nil, err
	}
	raw, err := database.ReadTMDBRawDetailJSON(mediaType, tmdbID)
	if err != nil {
		return nil, err
	}
	if strings.TrimSpace(raw) == "" {
		return nil, nil
	}
	var out map[string]any
	if err := json.Unmarshal([]byte(raw), &out); err != nil {
		return nil, err
	}
	return out, nil
}

func GetRawDetailJSON(database *db.DB, mediaType string, tmdbID int) ([]byte, error) {
	if mediaType != "tv" && mediaType != "movie" {
		return nil, fmt.Errorf("invalid mediaType")
	}
	if tmdbID <= 0 || database == nil {
		return nil, fmt.Errorf("invalid tmdbID/db")
	}
	var err error
	if mediaType == "movie" {
		_, _, err = ensureMovieDetailFresh(database, tmdbID)
	} else {
		_, _, err = ensureTVDetailFresh(database, tmdbID)
	}
	if err != nil {
		return nil, err
	}
	raw, err := database.ReadTMDBRawDetailJSON(mediaType, tmdbID)
	if err != nil {
		return nil, err
	}
	if strings.TrimSpace(raw) == "" {
		return nil, nil
	}
	return []byte(raw), nil
}

func GetTVDetails(database *db.DB, tmdbID int) (*TVDetailsResponse, error) {
	raw, err := GetRawDetailJSON(database, "tv", tmdbID)
	if err != nil || len(raw) == 0 {
		return nil, err
	}
	var out tmdbTVDetailsResponse
	if err := json.Unmarshal(raw, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

func GetTVTitle(database *db.DB, tmdbID int) (string, error) {
	detail, err := GetTVDetails(database, tmdbID)
	if err != nil || detail == nil {
		return "", err
	}
	if title := strings.TrimSpace(detail.Name); title != "" {
		return title, nil
	}
	return strings.TrimSpace(detail.OriginalName), nil
}

func GetMovieDetails(database *db.DB, tmdbID int) (*MovieDetailsResponse, error) {
	raw, err := GetRawDetailJSON(database, "movie", tmdbID)
	if err != nil || len(raw) == 0 {
		return nil, err
	}
	var out tmdbMovieDetailsResponse
	if err := json.Unmarshal(raw, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

func GetMovieTitle(database *db.DB, tmdbID int) (string, error) {
	detail, err := GetMovieDetails(database, tmdbID)
	if err != nil || detail == nil {
		return "", err
	}
	if title := strings.TrimSpace(detail.Title); title != "" {
		return title, nil
	}
	return strings.TrimSpace(detail.Original), nil
}

func GetDetailForBackend(database *db.DB, mediaType string, tmdbID int) (*db.TMDBCachedDetail, error) {
	if mediaType != "tv" && mediaType != "movie" {
		return nil, fmt.Errorf("invalid mediaType")
	}
	if database == nil || tmdbID <= 0 {
		return nil, fmt.Errorf("invalid tmdbID/db")
	}
	if mediaType == "movie" {
		detail, _, err := ensureMovieDetailFresh(database, tmdbID)
		return detail, err
	}
	detail, _, err := ensureTVDetailFresh(database, tmdbID)
	return detail, err
}

func GetTVSeasonDetailForBackend(database *db.DB, tmdbID int, season int) (*db.TMDBCachedSeasonDetail, error) {
	if database == nil || tmdbID <= 0 || season < 0 {
		return nil, fmt.Errorf("invalid args")
	}
	detail, _, err := ensureTVSeasonFresh(database, tmdbID, season)
	return detail, err
}

func GetRawTVSeasonPayload(database *db.DB, tmdbID int, season int) (map[string]any, error) {
	if database == nil || tmdbID <= 0 || season < 0 {
		return nil, fmt.Errorf("invalid args")
	}
	_, _, err := ensureTVSeasonFresh(database, tmdbID, season)
	if err != nil {
		return nil, err
	}
	raw, err := database.ReadTMDBRawSeasonDetailJSON(tmdbID, season)
	if err != nil {
		return nil, err
	}
	if strings.TrimSpace(raw) == "" {
		return nil, nil
	}
	var out map[string]any
	if err := json.Unmarshal([]byte(raw), &out); err != nil {
		return nil, err
	}
	return out, nil
}

func GetRawTVSeasonJSON(database *db.DB, tmdbID int, season int) ([]byte, error) {
	if database == nil || tmdbID <= 0 || season < 0 {
		return nil, fmt.Errorf("invalid args")
	}
	_, _, err := ensureTVSeasonFresh(database, tmdbID, season)
	if err != nil {
		return nil, err
	}
	raw, err := database.ReadTMDBRawSeasonDetailJSON(tmdbID, season)
	if err != nil {
		return nil, err
	}
	if strings.TrimSpace(raw) == "" {
		return nil, nil
	}
	return []byte(raw), nil
}

func GetTVSeasonDetails(database *db.DB, tmdbID int, season int) (*TVSeasonDetailResponse, error) {
	raw, err := GetRawTVSeasonJSON(database, tmdbID, season)
	if err != nil || len(raw) == 0 {
		return nil, err
	}
	var out tmdbTVSeasonDetailResponse
	if err := json.Unmarshal(raw, &out); err != nil {
		return nil, err
	}
	return &out, nil
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

func defaultString(v, def string) string {
	return mfnet.DefaultString(v, def)
}

func ParseYearFromDate(v string) int {
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
