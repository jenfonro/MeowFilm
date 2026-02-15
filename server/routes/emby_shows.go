package routes

import (
	"net/http"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

func embyIsAiredDate(airDate string, now time.Time) bool {
	s := strings.TrimSpace(airDate)
	if s == "" {
		return true
	}
	t, err := time.Parse("2006-01-02", s)
	if err != nil {
		return true
	}
	// Treat "air_date" as UTC midnight for a stable comparison.
	return !t.UTC().After(now.UTC())
}

func handleEmbyShows(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	// GET /Shows/NextUp?UserId=...
	if len(parts) >= 1 && strings.EqualFold(parts[0], "NextUp") && r.Method == http.MethodGet {
		_, ok := embyRequireUser(w, r, database)
		if !ok {
			return
		}
		writeJSON(w, 200, embyPagedEmpty(0))
		return
	}

	// /Shows/{id}/Seasons
	if len(parts) >= 2 && strings.EqualFold(parts[1], "Seasons") && r.Method == http.MethodGet {
		u, ok := embyRequireUser(w, r, database)
		if !ok {
			return
		}
		fieldsParam := embyQueryGetCI(r, "fields")
		seriesID := parts[0]
		parsed, ok := embyParseItemID(seriesID)
		if !ok || parsed == nil || parsed.Kind != "tv" || parsed.SubKind != "series" {
			// Site-mapped series: expose seasons derived from the spider "detail" play sources.
			if e, ok := embySiteMapGet(seriesID); ok && strings.TrimSpace(e.Name) != "" {
				pans, _, err := embyLoadSiteDetailPans(database, u, seriesID)
				if err != nil {
					writeJSON(w, 200, embyPagedItems([]map[string]any{}, 0, 0))
					return
				}
				seriesName := strings.TrimSpace(e.Name)
				seasons := make([]map[string]any, 0, len(pans))
				for i, pan := range pans {
					seasonNo := i + 1
					label := strings.TrimSpace(pan.Label)
					if label == "" {
						label = "第" + intToCN(seasonNo) + "季"
					}
					seasonID := embyBuildSiteSeasonID(seriesID, seasonNo, label)
					if seasonID == "" {
						continue
					}
					seasons = append(seasons, map[string]any{
						"Id":                      seasonID,
						"Name":                    label,
						"SeriesName":              seriesName,
						"Type":                    "Season",
						"IsFolder":                true,
						"LocationType":            "Remote",
						"SeriesId":                seriesID,
						"ParentId":                seriesID,
						"ParentBackdropItemId":    seriesID,
						"ParentBackdropImageTags": []string{"site"},
						"IndexNumber":             seasonNo,
						"ProductionYear":          0,
						"ChildCount":              len(pan.Episodes),
						"ImageTags":               map[string]any{"Primary": "site", "Thumb": "site"},
						"UserData":                map[string]any{"Played": false},
					})
				}
				sort.Slice(seasons, func(i, j int) bool {
					return intVal(seasons[i]["IndexNumber"]) < intVal(seasons[j]["IndexNumber"])
				})
				for _, it := range seasons {
					if id, ok := it["Id"].(string); ok && strings.TrimSpace(id) != "" {
						if _, ok := it["Path"]; !ok {
							it["Path"] = "meowfilm://" + strings.TrimSpace(id)
						}
					}
					embyEnsureShowsItemFields(it, fieldsParam)
					if _, ok := it["ServerId"]; !ok && strings.TrimSpace(serverID) != "" {
						it["ServerId"] = serverID
					}
				}
				writeJSON(w, 200, embyPagedItems(seasons, 0, len(seasons)))
				return
			}
			embyNotFound(w)
			return
		}
		d, err := embyTMDBGetTVDetail(database, parsed.TMDBID)
		if err != nil || d == nil {
			embyWriteError(w, 502, "TMDB 请求失败")
			return
		}
		seriesName := strings.TrimSpace(d.Title)
		seasons := make([]map[string]any, 0, len(d.Seasons))
		for _, s := range d.Seasons {
			if s.Season < 0 || s.EpisodeCount <= 0 {
				continue
			}
			// Strict: only include seasons that have aired (based on TMDB last_episode_to_air).
			if d.LatestSeason > 0 && s.Season > d.LatestSeason {
				continue
			}
			childCnt := s.EpisodeCount
			if d.LatestSeason > 0 && s.Season == d.LatestSeason && d.LatestEpisode > 0 && d.LatestEpisode < childCnt {
				childCnt = d.LatestEpisode
			}
			name := "特别篇"
			if s.Season > 0 {
				name = "第" + intToCN(s.Season) + "季"
			}
			seasons = append(seasons, map[string]any{
				"Id":                      embyBuildSeasonID(parsed.TMDBID, s.Season),
				"Name":                    name,
				"SeriesName":              seriesName,
				"Type":                    "Season",
				"IsFolder":                true,
				"LocationType":            "Remote",
				"SeriesId":                seriesID,
				"ParentId":                seriesID,
				"ParentBackdropItemId":    seriesID,
				"ParentBackdropImageTags": []string{"tmdb"},
				"IndexNumber":             s.Season,
				"ProductionYear":          d.Year,
				"ChildCount":              childCnt,
				"ImageTags":               map[string]any{"Primary": "tmdb", "Thumb": "tmdb"},
				"UserData":                map[string]any{"Played": false},
			})
		}
		sort.Slice(seasons, func(i, j int) bool {
			return intVal(seasons[i]["IndexNumber"]) < intVal(seasons[j]["IndexNumber"])
		})
		for _, it := range seasons {
			if id, ok := it["Id"].(string); ok && strings.TrimSpace(id) != "" {
				if _, ok := it["Path"]; !ok {
					it["Path"] = "meowfilm://" + strings.TrimSpace(id)
				}
			}
			embyEnsureShowsItemFields(it, fieldsParam)
			if _, ok := it["ServerId"]; !ok && strings.TrimSpace(serverID) != "" {
				it["ServerId"] = serverID
			}
		}
		writeJSON(w, 200, embyPagedItems(seasons, 0, len(seasons)))
		return
	}

	// /Shows/{id}/Episodes?SeasonId=...
	if len(parts) >= 2 && strings.EqualFold(parts[1], "Episodes") && r.Method == http.MethodGet {
		u, ok := embyRequireUser(w, r, database)
		if !ok {
			return
		}
		fieldsParam := embyQueryGetCI(r, "fields")
		seriesID := parts[0]
		parsed, ok := embyParseItemID(seriesID)
		if !ok || parsed == nil || parsed.Kind != "tv" || parsed.SubKind != "series" {
			// Site-mapped series: list episodes from the spider "detail" play sources.
			if e, ok := embySiteMapGet(seriesID); ok && strings.TrimSpace(e.Name) != "" {
				pans, _, err := embyLoadSiteDetailPans(database, u, seriesID)
				if err != nil {
					writeJSON(w, 200, embyPagedItems([]map[string]any{}, 0, 0))
					return
				}
				seriesName := strings.TrimSpace(e.Name)
				seasonID := embyQueryTrimCI(r, "SeasonId")
				seasonNo := 1
				label := ""
				if strings.TrimSpace(seasonID) != "" {
					if s, ok := embySiteSeasonMapGet(seasonID); ok && strings.TrimSpace(s.SeriesID) == strings.TrimSpace(seriesID) {
						seasonNo = s.SeasonNo
						label = strings.TrimSpace(s.Label)
					} else {
						// If cache was cold, try to rebuild once (embyLoadSiteDetailPans populates the maps).
						if _, _, err := embyLoadSiteDetailPans(database, u, seriesID); err == nil {
							if s, ok := embySiteSeasonMapGet(seasonID); ok && strings.TrimSpace(s.SeriesID) == strings.TrimSpace(seriesID) {
								seasonNo = s.SeasonNo
								label = strings.TrimSpace(s.Label)
							}
						}
					}
				} else {
					// Default to first season.
					if len(pans) > 0 {
						seasonNo = 1
						label = strings.TrimSpace(pans[0].Label)
					}
					seasonID = embyBuildSiteSeasonID(seriesID, seasonNo, label)
				}
				if seasonNo <= 0 {
					seasonNo = 1
				}
				// Pick the pan by seasonNo.
				var pan embyCatPan
				if seasonNo-1 >= 0 && seasonNo-1 < len(pans) {
					pan = pans[seasonNo-1]
				} else {
					writeJSON(w, 200, embyPagedItems([]map[string]any{}, 0, 0))
					return
				}
				seasonName := strings.TrimSpace(label)
				if seasonName == "" {
					seasonName = strings.TrimSpace(pan.Label)
				}
				if seasonName == "" {
					seasonName = "第" + intToCN(seasonNo) + "季"
				}

				out := make([]map[string]any, 0, len(pan.Episodes))
				for i, ep := range pan.Episodes {
					epURL := strings.TrimSpace(ep.URL)
					if epURL == "" {
						continue
					}
					epIndex := i + 1
					epName := strings.TrimSpace(ep.Name)
					if epName == "" {
						epName = "第" + intToCN(epIndex) + "集"
					}
					epID := embyBuildSiteEpisodeID(seasonID, epIndex, epURL)
					if epID == "" {
						continue
					}
					mediaPath := embyBuildMediaPath(epID, "mp4")
					mediaSourceID := embyStableHex32(epID)
					out = append(out, map[string]any{
						"Id":                      epID,
						"Name":                    epName,
						"SeriesName":              seriesName,
						"SeasonName":              seasonName,
						"Overview":                "",
						"Type":                    "Episode",
						"MediaType":               "Video",
						"IsFolder":                false,
						"LocationType":            "Remote",
						"Path":                    mediaPath,
						"Container":               "mp4,m4v",
						"PremiereDate":            "",
						"CanDownload":             false,
						"RunTimeTicks":            0,
						"Chapters":                []any{},
						"People":                  []any{},
						"Size":                    0,
						"SeriesId":                seriesID,
						"SeasonId":                seasonID,
						"ParentId":                seasonID,
						"ParentBackdropItemId":    seriesID,
						"ParentBackdropImageTags": []string{"site"},
						"IndexNumber":             epIndex,
						"ParentIndexNumber":       seasonNo,
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
					})
				}
				sort.Slice(out, func(i, j int) bool {
					return intVal(out[i]["IndexNumber"]) < intVal(out[j]["IndexNumber"])
				})
				for _, it := range out {
					embyEnsureShowsItemFields(it, fieldsParam)
					if id, ok := it["Id"].(string); ok && strings.TrimSpace(id) != "" {
						if _, ok := it["Etag"]; !ok {
							it["Etag"] = embyStableEtag(strings.TrimSpace(id))
						}
					}
					if _, ok := it["ServerId"]; !ok && strings.TrimSpace(serverID) != "" {
						it["ServerId"] = serverID
					}
				}
				writeJSON(w, 200, embyPagedItems(out, 0, len(out)))
				return
			}
			embyNotFound(w)
			return
		}
		d, err := embyTMDBGetTVDetail(database, parsed.TMDBID)
		if err != nil || d == nil {
			embyWriteError(w, 502, "TMDB 请求失败")
			return
		}
		seriesName := strings.TrimSpace(d.Title)
		seasonID := embyQueryTrimCI(r, "SeasonId")
		seasonNo := 1
		if seasonID != "" {
			sParsed, ok := embyParseItemID(seasonID)
			if !ok || sParsed == nil || sParsed.Kind != "tv" || sParsed.SubKind != "season" || sParsed.TMDBID != parsed.TMDBID {
				embyNotFound(w)
				return
			}
			seasonNo = sParsed.Season
		} else {
			// Emby typically allows omitting SeasonId to list episodes; default to S01 when present.
			seasonNo = 0
			for _, s := range d.Seasons {
				if s.Season >= 1 {
					seasonNo = s.Season
					break
				}
			}
			if seasonNo == 0 {
				seasonNo = 1
			}
			seasonID = embyBuildSeasonID(parsed.TMDBID, seasonNo)
		}
		seasonName := "第" + intToCN(seasonNo) + "季"
		if seasonNo == 0 {
			seasonName = "特别篇"
		}
		// Strict: season beyond latest aired => empty.
		if d.LatestSeason > 0 && seasonNo > d.LatestSeason {
			writeJSON(w, 200, embyPagedItems([]map[string]any{}, 0, 0))
			return
		}
		maxEpisodeAllowed := 0
		if d.LatestSeason > 0 && seasonNo == d.LatestSeason && d.LatestEpisode > 0 {
			maxEpisodeAllowed = d.LatestEpisode
		}
		episodes, err := embyTMDBGetTVSeasonEpisodes(database, parsed.TMDBID, seasonNo)
		if err != nil {
			embyWriteError(w, 502, "TMDB 请求失败")
			return
		}
		out := make([]map[string]any, 0, len(episodes))
		now := time.Now().UTC()
		for _, e := range episodes {
			if e.Episode <= 0 {
				continue
			}
			if maxEpisodeAllowed > 0 && e.Episode > maxEpisodeAllowed {
				continue
			}
			// Filter unaired episodes by air_date when present.
			if !embyIsAiredDate(e.AirDate, now) {
				continue
			}
			name := strings.TrimSpace(e.Name)
			if name == "" {
				name = "第" + intToCN(e.Episode) + "集"
			}
			epID := embyBuildEpisodeID(parsed.TMDBID, seasonNo, e.Episode)
			mediaPath := embyBuildMediaPath(epID, "mp4")
			mediaSourceID := embyStableHex32(epID)
			premiere := strings.TrimSpace(e.AirDate)
			premiereISO := ""
			if premiere != "" {
				if t, err := time.Parse("2006-01-02", premiere); err == nil {
					premiereISO = t.UTC().Format(time.RFC3339)
				}
			}
			out = append(out, map[string]any{
				"Id":                      epID,
				"Name":                    name,
				"SeriesName":              seriesName,
				"SeasonName":              seasonName,
				"Overview":                e.Overview,
				"Type":                    "Episode",
				"MediaType":               "Video",
				"IsFolder":                false,
				"LocationType":            "Remote",
				"Path":                    mediaPath,
				"Container":               "mp4,m4v",
				"PremiereDate":            premiereISO,
				"CanDownload":             false,
				"RunTimeTicks":            0,
				"Chapters":                []any{},
				"People":                  []any{},
				"Size":                    0,
				"SeriesId":                seriesID,
				"SeasonId":                seasonID,
				"ParentId":                seasonID,
				"ParentBackdropItemId":    seriesID,
				"ParentBackdropImageTags": []string{"tmdb"},
				"IndexNumber":             e.Episode,
				"ParentIndexNumber":       seasonNo,
				"ImageTags":               map[string]any{"Primary": "tmdb", "Thumb": "tmdb"},
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
			})
		}
		sort.Slice(out, func(i, j int) bool {
			return intVal(out[i]["IndexNumber"]) < intVal(out[j]["IndexNumber"])
		})
		for _, it := range out {
			embyEnsureShowsItemFields(it, fieldsParam)
			if id, ok := it["Id"].(string); ok && strings.TrimSpace(id) != "" {
				if _, ok := it["Etag"]; !ok {
					it["Etag"] = embyStableEtag(strings.TrimSpace(id))
				}
			}
			if _, ok := it["ServerId"]; !ok && strings.TrimSpace(serverID) != "" {
				it["ServerId"] = serverID
			}
		}
		writeJSON(w, 200, embyPagedItems(out, 0, len(out)))
		return
	}

	embyNotFound(w)
}

func embyEnsureShowsItemFields(obj map[string]any, fieldsParam string) {
	if obj == nil {
		return
	}
	id, _ := obj["Id"].(string)
	id = strings.TrimSpace(id)
	isFolder, _ := obj["IsFolder"].(bool)
	typ, _ := obj["Type"].(string)
	typ = strings.TrimSpace(typ)

	fields := embyFieldsSet(fieldsParam)
	nowISO := time.Now().UTC().Format(time.RFC3339)

	if _, want := fields["Genres"]; want {
		if _, ok := obj["Genres"]; !ok {
			obj["Genres"] = []string{}
		}
	}
	if _, want := fields["ParentId"]; want {
		if _, ok := obj["ParentId"]; !ok {
			obj["ParentId"] = ""
		}
	}
	if _, want := fields["MediaSources"]; want {
		if _, ok := obj["MediaSources"]; !ok {
			if !isFolder && id != "" && (typ == "Episode" || typ == "Movie") {
				mediaPath := embyBuildMediaPath(id, "mp4")
				mediaSourceID := embyStableHex32(id)
				obj["MediaSources"] = []map[string]any{
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
				}
			} else {
				obj["MediaSources"] = []any{}
			}
		}
	}
	if _, want := fields["AlternateMediaSources"]; want {
		if _, ok := obj["AlternateMediaSources"]; !ok {
			obj["AlternateMediaSources"] = []any{}
		}
	}
	if _, want := fields["ProviderIds"]; want {
		if _, ok := obj["ProviderIds"]; !ok {
			obj["ProviderIds"] = map[string]any{}
		}
	}
	if _, want := fields["Overview"]; want {
		if _, ok := obj["Overview"]; !ok {
			obj["Overview"] = ""
		}
	}
	if _, want := fields["Path"]; want {
		if _, ok := obj["Path"]; !ok {
			if !isFolder && id != "" {
				obj["Path"] = embyBuildMediaPath(id, "mp4")
			} else if id != "" {
				obj["Path"] = "meowfilm://" + id
			} else {
				obj["Path"] = ""
			}
		}
	}
	if _, want := fields["RunTimeTicks"]; want {
		if _, ok := obj["RunTimeTicks"]; !ok {
			obj["RunTimeTicks"] = 0
		}
	}
	if _, want := fields["Chapters"]; want {
		if _, ok := obj["Chapters"]; !ok {
			obj["Chapters"] = []any{}
		}
	}
	if _, want := fields["People"]; want {
		if _, ok := obj["People"]; !ok {
			obj["People"] = []any{}
		}
	}
	if _, want := fields["Size"]; want {
		if _, ok := obj["Size"]; !ok {
			obj["Size"] = 0
		}
	}
	if _, want := fields["CanDownload"]; want {
		if _, ok := obj["CanDownload"]; !ok {
			obj["CanDownload"] = false
		}
	}
	if _, want := fields["DateModified"]; want {
		if _, ok := obj["DateModified"]; !ok {
			obj["DateModified"] = nowISO
		}
	}
}

func intVal(v any) int {
	switch vv := v.(type) {
	case int:
		return vv
	case int64:
		return int(vv)
	case float64:
		return int(vv)
	default:
		return 0
	}
}

func intToCN(n int) string {
	if n <= 0 {
		return ""
	}
	cn := []string{"", "一", "二", "三", "四", "五", "六", "七", "八", "九", "十"}
	if n <= 10 {
		return cn[n]
	}
	// Keep simple for now; UI label isn't critical for Infuse.
	return strconv.Itoa(n)
}
