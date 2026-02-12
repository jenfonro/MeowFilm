package routes

import (
	"errors"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func jellyfinBuildBaseItemFromSearch(it jellyfinTMDBSearchItem) map[string]any {
	title := strings.TrimSpace(it.Title)
	if it.ID <= 0 || title == "" {
		return nil
	}
	switch it.MediaType {
	case "tv":
		return map[string]any{
			"Id":             jellyfinBuildSeriesID(it.ID),
			"Name":           title,
			"Type":           "Series",
			"IsFolder":       true,
			"ProductionYear": it.Year,
			"ImageTags":      map[string]any{"Primary": "tmdb"},
			"ProviderIds":    map[string]any{"Tmdb": strconv.Itoa(it.ID)},
		}
	case "movie":
		return map[string]any{
			"Id":             jellyfinBuildMovieID(it.ID),
			"Name":           title,
			"Type":           "Movie",
			"IsFolder":       false,
			"ProductionYear": it.Year,
			"ImageTags":      map[string]any{"Primary": "tmdb"},
			"ProviderIds":    map[string]any{"Tmdb": strconv.Itoa(it.ID)},
		}
	default:
		return nil
	}
}

func jellyfinBuildItem(database *db.DB, jellyfinID string) (map[string]any, error) {
	parsed, ok := jellyfinParseItemID(jellyfinID)
	if !ok || parsed == nil {
		return nil, nil
	}

	// For Douban IDs, resolve and cache a TMDB mapping on-demand.
	if parsed.Source == "douban" && parsed.TMDBID <= 0 && parsed.DoubanID != "" {
		m, _ := jellyfinGetDoubanTMDBMap(database, parsed.Kind, parsed.DoubanID)
		title := ""
		year := 0
		if m != nil {
			title = m.Title
			year = m.Year
		}
		tid, err := jellyfinResolveTMDBForDouban(database, parsed.Kind, parsed.DoubanID, title, year)
		if err != nil {
			return nil, err
		}
		if tid <= 0 {
			return nil, errors.New("TMDB 未匹配")
		}
		parsed.Source = "tmdb"
		parsed.TMDBID = tid
	}

	switch parsed.Kind {
	case "movie":
		d, err := jellyfinTMDBGetMovieDetail(database, parsed.TMDBID)
		if err != nil || d == nil {
			if err == nil {
				err = errors.New("TMDB 请求失败")
			}
			return nil, err
		}
		return map[string]any{
			"Id":             jellyfinBuildMovieID(parsed.TMDBID),
			"Name":           d.Title,
			"Overview":       d.Overview,
			"Type":           "Movie",
			"IsFolder":       false,
			"ProductionYear": d.Year,
			"ImageTags":      map[string]any{"Primary": "tmdb"},
			"ProviderIds":    map[string]any{"Tmdb": strconv.Itoa(parsed.TMDBID)},
		}, nil

	case "tv":
		switch parsed.SubKind {
		case "series":
			d, err := jellyfinTMDBGetTVDetail(database, parsed.TMDBID)
			if err != nil || d == nil {
				if err == nil {
					err = errors.New("TMDB 请求失败")
				}
				return nil, err
			}
			return map[string]any{
				"Id":             jellyfinBuildSeriesID(parsed.TMDBID),
				"Name":           d.Title,
				"Overview":       d.Overview,
				"Type":           "Series",
				"IsFolder":       true,
				"ProductionYear": d.Year,
				"ImageTags":      map[string]any{"Primary": "tmdb"},
				"ProviderIds":    map[string]any{"Tmdb": strconv.Itoa(parsed.TMDBID)},
			}, nil
		case "season":
			seriesID := jellyfinBuildSeriesID(parsed.TMDBID)
			name := "特别篇"
			if parsed.Season > 0 {
				name = "第" + strconv.Itoa(parsed.Season) + "季"
			}
			return map[string]any{
				"Id":          jellyfinBuildSeasonID(parsed.TMDBID, parsed.Season),
				"Name":        name,
				"Type":        "Season",
				"IsFolder":    true,
				"SeriesId":    seriesID,
				"ParentId":    seriesID,
				"IndexNumber": parsed.Season,
				"ImageTags":   map[string]any{"Primary": "tmdb"},
			}, nil
		case "episode":
			seriesID := jellyfinBuildSeriesID(parsed.TMDBID)
			seasonID := jellyfinBuildSeasonID(parsed.TMDBID, parsed.Season)
			return map[string]any{
				"Id":                jellyfinBuildEpisodeID(parsed.TMDBID, parsed.Season, parsed.Episode),
				"Name":              "第" + strconv.Itoa(parsed.Episode) + "集",
				"Type":              "Episode",
				"IsFolder":          false,
				"SeriesId":          seriesID,
				"SeasonId":          seasonID,
				"ParentId":          seasonID,
				"IndexNumber":       parsed.Episode,
				"ParentIndexNumber": parsed.Season,
				"ImageTags":         map[string]any{"Primary": "tmdb"},
			}, nil
		default:
			return nil, nil
		}
	default:
		return nil, nil
	}
}
