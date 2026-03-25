package tmdb

import (
	"errors"
	"encoding/json"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

type SearchItem struct {
	ID               int
	MediaType        string
	SearchJSON       string
	Title            string
	OriginalTitle    string
	Overview         string
	PosterPath       string
	BackdropPath     string
	ReleaseDate      string
	FirstAirDate     string
	OriginalLanguage string
	GenreIDs         []int
	Popularity       float64
	VoteAverage      float64
	VoteCount        int
	OriginCountry    []string
	Adult            bool
	Year             int
}

// RememberSearchHitAsPartial stores a search hit as partial TMDB cache state.
// This is the only place that should write TMDB "search" level rows.
func RememberSearchHitAsPartial(database *db.DB, hit SearchItem, lang string) error {
	if database == nil {
		return errors.New("db nil")
	}
	typ := strings.TrimSpace(hit.MediaType)
	if (typ != "movie" && typ != "tv") || hit.ID <= 0 {
		return errors.New("invalid args")
	}
	upsert := db.TMDBUpsertMedia{
		Type:             typ,
		ID:               hit.ID,
		Lang:             defaultString(strings.TrimSpace(lang), "zh-CN"),
		SearchJSON:       strings.TrimSpace(hit.SearchJSON),
		Adult:            hit.Adult,
		OriginalLanguage: strings.TrimSpace(hit.OriginalLanguage),
		GenreIDsJSON:     mustJSONInts(hit.GenreIDs),
		OriginCountryJSON: mustJSONStrings(hit.OriginCountry),
		Popularity:       hit.Popularity,
		VoteAverage:      hit.VoteAverage,
		VoteCount:        hit.VoteCount,
		Title:            strings.TrimSpace(hit.Title),
		Original:         strings.TrimSpace(hit.OriginalTitle),
		Overview:         strings.TrimSpace(hit.Overview),
		PosterPath:       strings.TrimSpace(hit.PosterPath),
		BackdropPath:     strings.TrimSpace(hit.BackdropPath),
		MetaLevel:        "search",
		UpdatedAt:        time.Now().Unix(),
	}
	if typ == "movie" {
		upsert.ReleaseDate = strings.TrimSpace(hit.ReleaseDate)
	} else {
		upsert.FirstAirDate = strings.TrimSpace(hit.FirstAirDate)
		upsert.SeasonLevel = "none"
	}
	_, err := database.UpsertTMDBMedia(upsert)
	return err
}

func mustJSONInts(values []int) string {
	if len(values) == 0 {
		return "[]"
	}
	b, err := json.Marshal(values)
	if err != nil {
		return "[]"
	}
	return string(b)
}

func mustJSONStrings(values []string) string {
	if len(values) == 0 {
		return "[]"
	}
	b, err := json.Marshal(values)
	if err != nil {
		return "[]"
	}
	return string(b)
}

func upsertTMDBDetailFull(database *db.DB, m db.TMDBUpsertMedia) error {
	if database == nil {
		return errors.New("db nil")
	}
	m.MetaLevel = "detail"
	if m.UpdatedAt <= 0 {
		m.UpdatedAt = time.Now().Unix()
	}
	_, err := database.UpsertTMDBMedia(m)
	return err
}

func refreshTMDBImagesOnly(database *db.DB, tmdbType string, tmdbID int, lang string, posterPath string, backdropPath string) error {
	if database == nil {
		return errors.New("db nil")
	}
	_, err := database.UpsertTMDBMedia(db.TMDBUpsertMedia{
		Type:         strings.TrimSpace(tmdbType),
		ID:           tmdbID,
		Lang:         defaultString(strings.TrimSpace(lang), "zh-CN"),
		PosterPath:   strings.TrimSpace(posterPath),
		BackdropPath: strings.TrimSpace(backdropPath),
		UpdatedAt:    time.Now().Unix(),
	})
	return err
}
