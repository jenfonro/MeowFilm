package emby

import (
	"errors"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/metadata/tmdb"
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
	// Short site IDs: keep items small; season/episode lists are provided by /Shows endpoints.
	if siteVideoID, ok := embyParseSiteSeriesIDV2(embyID); ok {
		if database == nil {
			return nil, nil
		}
		sv, err := database.GetSiteVideoByID(siteVideoID)
		if err != nil || sv == nil {
			return nil, err
		}
		title := strings.TrimSpace(sv.Title)
		if title == "" {
			return nil, nil
		}
		siteName := strings.TrimSpace(sv.SiteKey)
		out := map[string]any{
			"Id":                      embyID,
			"Name":                    title,
			"SortName":                title,
			"Overview":                strings.TrimSpace(sv.Remark),
			"Type":                    "Series",
			"MediaType":               "Video",
			"IsFolder":                true,
			"LocationType":            "Remote",
			"Path":                    "meowfilm://" + strings.TrimSpace(embyID),
			"ProductionYear":          0,
			"ImageTags":               map[string]any{"Primary": "site"},
			"BackdropImageTags":       []string{},
			"ProviderIds":             map[string]any{},
			"UserData":                map[string]any{"Played": false},
			"PrimaryImageAspectRatio": 0.6666667,
		}
		if siteName != "" {
			out["ProductionLocations"] = []string{siteName}
		}
		embyEnsureStandardItem(out, "")
		return out, nil
	}
	if siteVideoID, pan, ok := embyParseSiteSeasonIDV2(embyID); ok {
		if database == nil {
			return nil, nil
		}
		sv, err := database.GetSiteVideoByID(siteVideoID)
		if err != nil || sv == nil {
			return nil, err
		}
		seriesID := embyBuildSiteSeriesIDV2(siteVideoID)
		seriesName := strings.TrimSpace(sv.Title)
		if seriesName == "" {
			seriesName = "站点资源"
		}
		name := "第" + intToCN(pan) + "季"
		out := map[string]any{
			"Id":           embyID,
			"Name":         name,
			"SeriesName":   seriesName,
			"Type":         "Season",
			"IsFolder":     true,
			"LocationType": "Remote",
			"SeriesId":     seriesID,
			"ParentId":     seriesID,
			"IndexNumber":  pan,
			"ImageTags":    map[string]any{"Primary": "site", "Thumb": "site"},
			"UserData":     map[string]any{"Played": false},
		}
		embyEnsureStandardItem(out, "")
		return out, nil
	}
	if siteVideoID, pan, epIndex, ok := embyParseSiteEpisodeIDV2(embyID); ok {
		if database == nil {
			return nil, nil
		}
		sv, err := database.GetSiteVideoByID(siteVideoID)
		if err != nil || sv == nil {
			return nil, err
		}
		seriesID := embyBuildSiteSeriesIDV2(siteVideoID)
		seasonID := embyBuildSiteSeasonIDV2(siteVideoID, pan)
		seriesName := strings.TrimSpace(sv.Title)
		if seriesName == "" {
			seriesName = "站点资源"
		}
		seasonName := "第" + intToCN(pan) + "季"
		name := "第" + intToCN(epIndex) + "集"
		mediaPath := embyBuildMediaPath(embyID, "mp4")
		mediaSourceID := embyStableHex32(embyID)
		out := map[string]any{
			"Id":                      embyID,
			"Name":                    name,
			"SeriesName":              seriesName,
			"SeasonName":              seasonName,
			"Overview":                "",
			"Type":                    "Episode",
			"MediaType":               "Video",
			"IsFolder":                false,
			"SeriesId":                seriesID,
			"SeasonId":                seasonID,
			"ParentId":                seasonID,
			"ParentBackdropItemId":    seriesID,
			"ParentBackdropImageTags": []string{"site"},
			"IndexNumber":             epIndex,
			"ParentIndexNumber":       pan,
			"LocationType":            "Remote",
			"Path":                    mediaPath,
			"Container":               "mp4,m4v",
			"CanDownload":             true,
			"RunTimeTicks":            int64(0),
			"Chapters":                []any{},
			"People":                  []any{},
			"Size":                    0,
			"ImageTags":               map[string]any{"Primary": "site", "Thumb": "site"},
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
	}

	parsed, ok := embyParseItemID(embyID)
	if !ok || parsed == nil {
		return nil, nil
	}

	// For Douban IDs, resolve and cache a TMDB mapping on-demand.
	if err := embyNormalizeParsedToTMDB(database, parsed, true); err != nil {
		return nil, err
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
				if t, ok := tmdb.ParseAirDateCNMidnight(epAirDate); ok {
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
