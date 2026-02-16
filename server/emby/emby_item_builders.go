package emby

import (
	"errors"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

func embyBuildPeople(database *db.DB, mediaType string, tmdbID int) []map[string]any {
	credits, err := embyTMDBGetCredits(database, mediaType, tmdbID)
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
			obj["Id"] = embyBuildPersonID(personID)
			obj["PrimaryImageTag"] = "tmdb"
		}
		if strings.TrimSpace(profilePath) != "" {
			embyRememberPersonProfile(personID, profilePath)
		}
		out = append(out, obj)
	}

	for _, c := range credits.Crew {
		if strings.EqualFold(strings.TrimSpace(c.Job), "Director") {
			add(c.ID, c.Name, "", "Director", c.Profile)
		}
	}

	cast := make([]embyTMDBCast, 0, len(credits.Cast))
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

func embyBuildBaseItemFromSearch(it embyTMDBSearchItem) map[string]any {
	title := strings.TrimSpace(it.Title)
	if it.ID <= 0 || title == "" {
		return nil
	}
	switch it.MediaType {
	case "tv":
		return map[string]any{
			"Id":                embyBuildSeriesID(it.ID),
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
			"Id":                embyBuildMovieID(it.ID),
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

func embyBuildItem(database *db.DB, embyID string) (map[string]any, error) {
	parsed, ok := embyParseItemID(embyID)
	if !ok || parsed == nil {
		return nil, nil
	}

	// For Douban IDs, resolve and cache a TMDB mapping on-demand.
	if parsed.Source == "douban" && parsed.TMDBID <= 0 && parsed.DoubanID != "" {
		m, _ := embyGetDoubanTMDBMap(database, parsed.Kind, parsed.DoubanID)
		title := ""
		year := 0
		if m != nil {
			title = m.Title
			year = m.Year
		}
		tid, err := embyResolveTMDBForDouban(database, parsed.Kind, parsed.DoubanID, title, year)
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
		d, err := embyTMDBGetMovieDetail(database, parsed.TMDBID)
		if err != nil || d == nil {
			if err == nil {
				err = errors.New("TMDB 请求失败")
			}
			return nil, err
		}
		id := embyBuildMovieID(parsed.TMDBID)
		mediaSourceID := embyStableHex32(id)
		mediaPath := embyBuildMediaPath(id, "mp4")
		out := map[string]any{
			"Id":           id,
			"Name":         d.Title,
			"SortName":     d.Title,
			"Overview":     d.Overview,
			"Type":         "Movie",
			"MediaType":    "Video",
			"IsFolder":     false,
			"LocationType": "FileSystem",
			"Path":         mediaPath,
			"Container":    "mp4,m4v",
			"VideoType":    "VideoFile",
			"ParentId":     embyViewTMDBMovies,

			"ProductionYear":    d.Year,
			"ImageTags":         map[string]any{"Primary": "tmdb"},
			"BackdropImageTags": []string{"tmdb"},
			"ProviderIds":       map[string]any{"Tmdb": strconv.Itoa(parsed.TMDBID)},
			"UserData":          map[string]any{"Played": false},
			"RunTimeTicks":      int64(0),
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
		if people := embyBuildPeople(database, "movie", parsed.TMDBID); len(people) > 0 {
			out["People"] = people
		}
		embyEnsureStandardItem(out, "")
		return out, nil

	case "tv":
		switch parsed.SubKind {
		case "series":
			d, err := embyTMDBGetTVDetail(database, parsed.TMDBID)
			if err != nil || d == nil {
				if err == nil {
					err = errors.New("TMDB 请求失败")
				}
				return nil, err
			}
			id := embyBuildSeriesID(parsed.TMDBID)
			childCount := 0
			recursiveCount := 0
			for _, s := range d.Seasons {
				if s.Season < 0 || s.EpisodeCount <= 0 {
					continue
				}
				// Strict: do not count unaired future seasons; cap last season by last aired episode.
				if d.LatestSeason > 0 && s.Season > d.LatestSeason {
					continue
				}
				childCount++
				cnt := s.EpisodeCount
				if d.LatestSeason > 0 && s.Season == d.LatestSeason && d.LatestEpisode > 0 && d.LatestEpisode < cnt {
					cnt = d.LatestEpisode
				}
				if cnt > 0 {
					recursiveCount += cnt
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
				"ParentId":     embyViewTMDBTV,

				"ProductionYear":     d.Year,
				"ChildCount":         childCount,
				"RecursiveItemCount": recursiveCount,
				"ImageTags":          map[string]any{"Primary": "tmdb"},
				"BackdropImageTags":  []string{"tmdb"},
				"ProviderIds":        map[string]any{"Tmdb": strconv.Itoa(parsed.TMDBID)},
				"UserData":           map[string]any{"Played": false},
			}
			if people := embyBuildPeople(database, "tv", parsed.TMDBID); len(people) > 0 {
				out["People"] = people
			}
			embyEnsureStandardItem(out, "")
			return out, nil
		case "season":
			seriesID := embyBuildSeriesID(parsed.TMDBID)
			name := "特别篇"
			if parsed.Season > 0 {
				name = "第" + strconv.Itoa(parsed.Season) + "季"
			}
			return map[string]any{
				"Id":          embyBuildSeasonID(parsed.TMDBID, parsed.Season),
				"Name":        name,
				"Type":        "Season",
				"IsFolder":    true,
				"SeriesId":    seriesID,
				"ParentId":    seriesID,
				"IndexNumber": parsed.Season,
				"ImageTags":   map[string]any{"Primary": "tmdb"},
			}, nil
		case "episode":
			seriesID := embyBuildSeriesID(parsed.TMDBID)
			seasonID := embyBuildSeasonID(parsed.TMDBID, parsed.Season)
			seriesName := ""
			seasonName := "第" + strconv.Itoa(parsed.Season) + "季"
			if parsed.Season == 0 {
				seasonName = "特别篇"
			}
			epName := ""
			epOverview := ""
			epAirDate := ""
			sd, err := embyTMDBGetTVSeasonDetail(database, parsed.TMDBID, parsed.Season)
			if err == nil && sd != nil {
				if strings.TrimSpace(sd.Name) != "" {
					seasonName = strings.TrimSpace(sd.Name)
				}
				for _, e := range sd.Episodes {
					if e.Episode == parsed.Episode {
						epName = strings.TrimSpace(e.Name)
						epOverview = strings.TrimSpace(e.Overview)
						epAirDate = strings.TrimSpace(e.AirDate)
						break
					}
				}
			}
			tv, err := embyTMDBGetTVDetail(database, parsed.TMDBID)
			if err == nil && tv != nil {
				seriesName = strings.TrimSpace(tv.Title)
			}
			if epName == "" {
				epName = "第" + strconv.Itoa(parsed.Episode) + "集"
			}
			episodeID := embyBuildEpisodeID(parsed.TMDBID, parsed.Season, parsed.Episode)
			mediaSourceID := embyStableHex32(episodeID)
			mediaPath := embyBuildMediaPath(episodeID, "mp4")
			premiereISO := ""
			if epAirDate != "" {
				if t, err := time.Parse("2006-01-02", epAirDate); err == nil {
					premiereISO = t.UTC().Format(time.RFC3339)
				}
			}
			out := map[string]any{
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
				"LocationType":            "FileSystem",
				"Path":                    mediaPath,
				"Container":               "mp4,m4v",
				"VideoType":               "VideoFile",
				"PremiereDate":            premiereISO,
				"RunTimeTicks":            int64(0),
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
			}
			embyEnsureStandardItem(out, "")
			return out, nil
		default:
			return nil, nil
		}
	default:
		return nil, nil
	}
}
