package tmdb

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

func upsertTMDBDetailExternalIDs(database *db.DB, tmdbType string, tmdbID int, imdbID string, tvdbID int, youtubeKey string) {
	if database == nil || tmdbID <= 0 {
		return
	}
	if id := strings.TrimSpace(imdbID); id != "" {
		_ = database.UpsertTMDBExternalID(tmdbType, tmdbID, "imdb", id)
	}
	if tvdbID > 0 {
		_ = database.UpsertTMDBExternalID(tmdbType, tmdbID, "tvdb", strconv.Itoa(tvdbID))
	}
	if key := strings.TrimSpace(youtubeKey); key != "" {
		_ = database.UpsertTMDBExternalID(tmdbType, tmdbID, "youtube", key)
	}
}

func firstYouTubeVideoKey(results []struct {
	Site string `json:"site"`
	Key  string `json:"key"`
	Type string `json:"type"`
}) string {
	bestType := ""
	bestKey := ""
	for _, item := range results {
		if !strings.EqualFold(strings.TrimSpace(item.Site), "YouTube") {
			continue
		}
		key := strings.TrimSpace(item.Key)
		if key == "" {
			continue
		}
		videoType := strings.TrimSpace(item.Type)
		if bestKey == "" {
			bestKey = key
			bestType = videoType
			continue
		}
		if strings.EqualFold(videoType, "Trailer") && !strings.EqualFold(bestType, "Trailer") {
			bestKey = key
			bestType = videoType
		}
	}
	return bestKey
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

func fetchAndStoreTMDBTVSeasonDetail(database *db.DB, tmdbID int, season int, language string) ([]byte, error) {
	if database == nil || tmdbID <= 0 || season < 0 {
		return nil, fmt.Errorf("invalid args")
	}
	raw, err := fetchTMDBTVSeasonDetail(database, tmdbID, season, language)
	if err != nil {
		return nil, err
	}
	if raw == nil {
		return nil, nil
	}
	rawJSON, _ := json.Marshal(raw)
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
		DetailJSON:   string(rawJSON),
		PosterPath:   strings.TrimSpace(raw.PosterPath),
		Name:         strings.TrimSpace(raw.Name),
	}})
	return rawJSON, nil
}

func fetchTMDBDetailUpstream(database *db.DB, mediaType string, tmdbID int) error {
	if mediaType != "tv" && mediaType != "movie" {
		return fmt.Errorf("invalid mediaType")
	}
	if tmdbID <= 0 || database == nil {
		return fmt.Errorf("invalid tmdbID/db")
	}

	token, tokenKind := resolveTMDBToken(database)
	if token == "" || tokenKind == "" {
		return fmt.Errorf("tmdb not configured")
	}

	language := tmdbDetailLanguage(database)

	apiBase := resolveTMDBAPIBase(database)
	u, _ := url.Parse(joinTMDBAPI(apiBase, mediaType+"/"+strconv.Itoa(tmdbID)))
	params := u.Query()
	if language != "" {
		params.Set("language", language)
	}
	params.Set("append_to_response", "external_ids,videos,images")
	if tokenKind == "v3" {
		params.Set("api_key", token)
	}
	u.RawQuery = params.Encode()

	req, err := http.NewRequest(http.MethodGet, u.String(), nil)
	if err != nil {
		return err
	}
	req.Header.Set("Accept", "application/json")
	if tokenKind == "v4" {
		req.Header.Set("Authorization", "Bearer "+token)
	}

	client := &http.Client{Timeout: 15 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 4096))
		return &UpstreamError{
			StatusCode: resp.StatusCode,
			Body:       strings.TrimSpace(string(body)),
			Message:    "tmdb request failed",
		}
	}

	if mediaType == "tv" {
		body, err := io.ReadAll(resp.Body)
		if err != nil {
			return err
		}
		var detail tmdbTVDetailsResponse
		if err := json.Unmarshal(body, &detail); err != nil {
			return err
		}
		_ = upsertTMDBDetailFull(database, db.TMDBUpsertMedia{
			Type:         "tv",
			ID:           tmdbID,
			Lang:         language,
			DetailJSON:   string(body),
			Title:        strings.TrimSpace(detail.Name),
			Original:     strings.TrimSpace(detail.OriginalName),
			Overview:     strings.TrimSpace(detail.Overview),
			Status:       strings.TrimSpace(detail.Status),
			InProduction: detail.InProduction,
			PosterPath:   strings.TrimSpace(detail.PosterPath),
			BackdropPath: strings.TrimSpace(detail.BackdropPath),
			NextEpisodeAirDate: strings.TrimSpace(func() string {
				if detail.NextEpisodeToAir == nil {
					return ""
				}
				return detail.NextEpisodeToAir.AirDate
			}()),
			FirstAirDate: strings.TrimSpace(detail.FirstAir),
		})
		imdbID := ""
		tvdbID := 0
		if detail.ExternalIDs != nil {
			imdbID = strings.TrimSpace(detail.ExternalIDs.IMDBID)
			tvdbID = detail.ExternalIDs.TVDBID
		}
		youtubeKey := ""
		if detail.Videos != nil {
			youtubeKey = firstYouTubeVideoKey(detail.Videos.Results)
		}
		upsertTMDBDetailExternalIDs(database, "tv", tmdbID, imdbID, tvdbID, youtubeKey)
		seasonRows := make([]db.TMDBSeason, 0, len(detail.Seasons))
		for _, s := range detail.Seasons {
			seasonRows = append(seasonRows, db.TMDBSeason{
				TMDBSeasonID: s.ID,
				SeasonNumber: s.SeasonNumber,
				EpisodeCount: s.EpisodeCount,
				AirDate:      strings.TrimSpace(s.AirDate),
				PosterPath:   strings.TrimSpace(s.PosterPath),
				Name:         strings.TrimSpace(s.Name),
				Overview:     strings.TrimSpace(s.Overview),
			})
		}
		_ = database.UpsertTMDBSeasons("tv", tmdbID, language, seasonRows)
		return nil
	}

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return err
	}
	var detail tmdbMovieDetailsResponse
	if err := json.Unmarshal(body, &detail); err != nil {
		return err
	}
	_ = upsertTMDBDetailFull(database, db.TMDBUpsertMedia{
		Type:         "movie",
		ID:           tmdbID,
		Lang:         language,
		DetailJSON:   string(body),
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
	imdbID := ""
	if detail.ExternalIDs != nil {
		imdbID = strings.TrimSpace(detail.ExternalIDs.IMDBID)
	}
	youtubeKey := ""
	if detail.Videos != nil {
		youtubeKey = firstYouTubeVideoKey(detail.Videos.Results)
	}
	upsertTMDBDetailExternalIDs(database, "movie", tmdbID, imdbID, 0, youtubeKey)
	return nil
}
