package routes

import (
	"errors"
	"net/url"
	"sort"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func jellyfinBuildPeople(database *db.DB, mediaType string, tmdbID int) []map[string]any {
	credits, err := jellyfinTMDBGetCredits(database, mediaType, tmdbID)
	if err != nil || credits == nil {
		return nil
	}

	out := make([]map[string]any, 0, 16)
	seen := map[string]bool{}

	add := func(personID int, name string, role string, typ string, profilePath string) {
		n := strings.TrimSpace(name)
		t := strings.TrimSpace(typ)
		if n == "" || t == "" {
			return
		}
		k := t + ":" + strconv.Itoa(personID) + ":" + n
		if seen[k] {
			return
		}
		seen[k] = true
		obj := map[string]any{
			"Name": n,
			"Role": strings.TrimSpace(role),
			"Type": t,
		}
		if personID > 0 {
			obj["Id"] = jellyfinBuildPersonID(personID)
			obj["PrimaryImageTag"] = "tmdb"
		}
		if strings.TrimSpace(profilePath) != "" {
			jellyfinRememberPersonProfile(personID, profilePath)
		}
		out = append(out, obj)
	}

	for _, c := range credits.Crew {
		if strings.EqualFold(strings.TrimSpace(c.Job), "Director") {
			add(c.ID, c.Name, "", "Director", c.Profile)
		}
	}

	cast := make([]jellyfinTMDBCast, 0, len(credits.Cast))
	for _, c := range credits.Cast {
		cast = append(cast, c)
	}
	sort.SliceStable(cast, func(i, j int) bool {
		if cast[i].Order == cast[j].Order {
			return cast[i].ID < cast[j].ID
		}
		return cast[i].Order < cast[j].Order
	})
	for _, c := range cast {
		add(c.ID, c.Name, c.Role, "Actor", c.Profile)
		if len(out) >= 20 {
			break
		}
	}

	return out
}

func jellyfinBuildBaseItemFromSearch(it jellyfinTMDBSearchItem) map[string]any {
	title := strings.TrimSpace(it.Title)
	if it.ID <= 0 || title == "" {
		return nil
	}
	switch it.MediaType {
	case "tv":
		return map[string]any{
			"Id":                jellyfinBuildSeriesID(it.ID),
			"Name":              title,
			"Type":              "Series",
			"IsFolder":          true,
			"ProductionYear":    it.Year,
			"ImageTags":         map[string]any{"Primary": "tmdb"},
			"BackdropImageTags": []string{"tmdb"},
			"ProviderIds":       map[string]any{"Tmdb": strconv.Itoa(it.ID)},
		}
	case "movie":
		return map[string]any{
			"Id":                jellyfinBuildMovieID(it.ID),
			"Name":              title,
			"Type":              "Movie",
			"IsFolder":          false,
			"ProductionYear":    it.Year,
			"ImageTags":         map[string]any{"Primary": "tmdb"},
			"BackdropImageTags": []string{"tmdb"},
			"ProviderIds":       map[string]any{"Tmdb": strconv.Itoa(it.ID)},
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
		id := jellyfinBuildMovieID(parsed.TMDBID)
		mediaSourceID := jellyfinStableHex32(id)
		mediaPath := "/jellyfin/media/" + url.PathEscape(id) + ".mp4"
		out := map[string]any{
			"Id":           id,
			"Name":         d.Title,
			"SortName":     d.Title,
			"Overview":     d.Overview,
			"Type":         "Movie",
			"MediaType":    "Video",
			"IsFolder":     false,
			"LocationType": "Remote",
			"Path":         mediaPath,
			"ParentId":     "view_tmdb_movies",

			"ProductionYear":    d.Year,
			"ImageTags":         map[string]any{"Primary": "tmdb"},
			"BackdropImageTags": []string{"tmdb"},
			"ProviderIds":       map[string]any{"Tmdb": strconv.Itoa(parsed.TMDBID)},
			"UserData":          map[string]any{"Played": false},
			"MediaSources": []map[string]any{
				{
					"Id":                   mediaSourceID,
					"MediaSourceId":        mediaSourceID,
					"Protocol":             "File",
					"IsRemote":             false,
					"Path":                 mediaPath,
					"Container":            "mp4",
					"RequiredHttpHeaders":  map[string]string{},
					"SupportsDirectPlay":   true,
					"SupportsDirectStream": true,
					"SupportsTranscoding":  true,
					"SupportsProbing":      true,
					"Type":                 "Default",
				},
			},
			"AlternateMediaSources": []any{},
		}
		if people := jellyfinBuildPeople(database, "movie", parsed.TMDBID); len(people) > 0 {
			out["People"] = people
		}
		return out, nil

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
			id := jellyfinBuildSeriesID(parsed.TMDBID)
			childCount := len(d.Seasons)
			recursiveCount := 0
			for _, s := range d.Seasons {
				if s.EpisodeCount > 0 {
					recursiveCount += s.EpisodeCount
				}
			}
			out := map[string]any{
				"Id":           id,
				"Name":         d.Title,
				"SortName":     d.Title,
				"Overview":     d.Overview,
				"Type":         "Series",
				"MediaType":    "Video",
				"IsFolder":     true,
				"LocationType": "Remote",
				"Path":         "meowfilm://" + id,
				"ParentId":     "view_tmdb_tv",

				"ProductionYear":     d.Year,
				"ChildCount":         childCount,
				"RecursiveItemCount": recursiveCount,
				"ImageTags":          map[string]any{"Primary": "tmdb"},
				"BackdropImageTags":  []string{"tmdb"},
				"ProviderIds":        map[string]any{"Tmdb": strconv.Itoa(parsed.TMDBID)},
				"UserData":           map[string]any{"Played": false},
			}
			if people := jellyfinBuildPeople(database, "tv", parsed.TMDBID); len(people) > 0 {
				out["People"] = people
			}
			return out, nil
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
			seriesName := ""
			seasonName := "第" + strconv.Itoa(parsed.Season) + "季"
			if parsed.Season == 0 {
				seasonName = "特别篇"
			}
			epName := ""
			epOverview := ""
			sd, err := jellyfinTMDBGetTVSeasonDetail(database, parsed.TMDBID, parsed.Season)
			if err == nil && sd != nil {
				if strings.TrimSpace(sd.Name) != "" {
					seasonName = strings.TrimSpace(sd.Name)
				}
				for _, e := range sd.Episodes {
					if e.Episode == parsed.Episode {
						epName = strings.TrimSpace(e.Name)
						epOverview = strings.TrimSpace(e.Overview)
						break
					}
				}
			}
			tv, err := jellyfinTMDBGetTVDetail(database, parsed.TMDBID)
			if err == nil && tv != nil {
				seriesName = strings.TrimSpace(tv.Title)
			}
			if epName == "" {
				epName = "第" + strconv.Itoa(parsed.Episode) + "集"
			}
			episodeID := jellyfinBuildEpisodeID(parsed.TMDBID, parsed.Season, parsed.Episode)
			mediaSourceID := jellyfinStableHex32(episodeID)
			mediaPath := "/jellyfin/media/" + url.PathEscape(episodeID) + ".mp4"
			return map[string]any{
				"Id":                      episodeID,
				"Name":                    epName,
				"SeriesName":              seriesName,
				"SeasonName":              seasonName,
				"Overview":                epOverview,
				"Type":                    "Episode",
				"MediaType":               "Video",
				"IsFolder":                false,
				"SeriesId":                seriesID,
				"SeasonId":                seasonID,
				"ParentId":                seasonID,
				"ParentBackdropItemId":    seriesID,
				"ParentBackdropImageTags": []string{"tmdb"},
				"IndexNumber":             parsed.Episode,
				"ParentIndexNumber":       parsed.Season,
				"LocationType":            "Remote",
				"Path":                    mediaPath,
				"ImageTags":               map[string]any{"Primary": "tmdb"},
				"ProviderIds":             map[string]any{"Tmdb": strconv.Itoa(parsed.TMDBID)},
				"UserData":                map[string]any{"Played": false},
				"MediaSources": []map[string]any{
					{
						"Id":                   mediaSourceID,
						"MediaSourceId":        mediaSourceID,
						"Protocol":             "File",
						"IsRemote":             false,
						"Path":                 mediaPath,
						"Container":            "mp4",
						"RequiredHttpHeaders":  map[string]string{},
						"SupportsDirectPlay":   true,
						"SupportsDirectStream": true,
						"SupportsTranscoding":  true,
						"SupportsProbing":      true,
						"Type":                 "Default",
					},
				},
				"AlternateMediaSources": []any{},
			}, nil
		default:
			return nil, nil
		}
	default:
		return nil, nil
	}
}
