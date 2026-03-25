package emby

import (
	"net/http"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/metadata/tmdb"
)

func embyIsAiredDate(airDate string, now time.Time) bool {
	// Treat TMDB air_date (date-only) as Beijing midnight, regardless of server timezone.
	s := strings.TrimSpace(airDate)
	if s == "" {
		return true
	}
	return tmdb.IsAirDateAiredOrToday(s, now)
}

func embyBuildTMDBEpisodeItem(
	seriesID string,
	seriesName string,
	seasonID string,
	seasonName string,
	parentBackdropTags []string,
	seasonNo int,
	episodeNo int,
	name string,
	overview string,
	premiereISO string,
) map[string]any {
	epID := ""
	if parsed, ok := embyParseItemID(seriesID); ok && parsed != nil && parsed.TMDBID > 0 {
		epID = embyBuildEpisodeID(parsed.TMDBID, seasonNo, episodeNo)
	}
	mediaPath := embyBuildMediaPath(epID, "mp4")
	mediaSourceID := embyStableHex32(epID)
	return map[string]any{
		"Id":                      epID,
		"Name":                    name,
		"SeriesName":              seriesName,
		"SeasonName":              seasonName,
		"Overview":                overview,
		"Type":                    "Episode",
		"MediaType":               "Video",
		"IsFolder":                false,
		"LocationType":            "Remote",
		"Path":                    mediaPath,
		"Container":               "mp4,m4v",
		"PremiereDate":            premiereISO,
		"CanDownload":             true,
		"RunTimeTicks":            0,
		"Chapters":                []any{},
		"People":                  []any{},
		"Size":                    0,
		"SeriesId":                seriesID,
		"SeasonId":                seasonID,
		"ParentId":                seasonID,
		"ParentBackdropItemId":    seriesID,
		"ParentBackdropImageTags": parentBackdropTags,
		"IndexNumber":             episodeNo,
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
	}
}

func embyBuildSiteEpisodeItem(
	siteVideoID int64,
	seriesID string,
	seriesName string,
	seasonID string,
	seasonName string,
	seasonNo int,
	episodeNo int,
	name string,
) map[string]any {
	epID := embyBuildSiteEpisodeIDV2(siteVideoID, seasonNo, episodeNo)
	mediaPath := embyBuildMediaPath(epID, "mp4")
	mediaSourceID := embyStableHex32(epID)
	return map[string]any{
		"Id":                      epID,
		"Name":                    name,
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
		"CanDownload":             true,
		"RunTimeTicks":            0,
		"Chapters":                []any{},
		"People":                  []any{},
		"Size":                    0,
		"SeriesId":                seriesID,
		"SeasonId":                seasonID,
		"ParentId":                seasonID,
		"ParentBackdropItemId":    seriesID,
		"ParentBackdropImageTags": []string{"site"},
		"IndexNumber":             episodeNo,
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
	}
}

func handleEmbyShows(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	// GET /Shows/NextUp?UserId=...
	if len(parts) >= 1 && strings.EqualFold(parts[0], "NextUp") && r.Method == http.MethodGet {
		u, ok := embyRequireUser(w, r, database)
		if !ok {
			return
		}
		userID := embyQueryTrimCI(r, "UserId")
		if userID != "" && !strings.EqualFold(strings.TrimSpace(userID), strings.TrimSpace(u.ID)) {
			writeJSON(w, 200, embyPagedEmpty(0))
			return
		}
		seriesID := embyQueryTrimCI(r, "SeriesId")
		if strings.TrimSpace(seriesID) == "" {
			writeJSON(w, 200, embyPagedEmpty(0))
			return
		}
		startIndex := embyQueryIntClamped(r, "StartIndex", 0, 0, 1<<30)
		limit := embyQueryIntClamped(r, "Limit", 12, 1, 60)
		fieldsParam := embyQueryGetCI(r, "fields")

		// Support both TMDB series ids and short site series ids.
		isSeries := false
		siteSeriesVideoID := int64(0)
		if parsed, ok := embyParseItemID(seriesID); ok && parsed != nil && parsed.Kind == "tv" && parsed.SubKind == "series" {
			isSeries = true
		} else if v, ok := embyParseSiteSeriesIDV2(seriesID); ok && v > 0 {
			isSeries = true
			siteSeriesVideoID = v
		}
		if !isSeries {
			writeJSON(w, 200, embyPagedEmpty(startIndex))
			return
		}

		uid, _ := strconv.ParseInt(strings.TrimSpace(u.ID), 10, 64)
		if uid <= 0 || database == nil {
			writeJSON(w, 200, embyPagedEmpty(startIndex))
			return
		}

		// Prefer TMDB metadata lookup so web-generated history drives NextUp even after
		// contentKey is normalized to a stable title key.
		var row *db.PlayHistoryRow
		var err error
		if parsed, ok := embyParseItemID(seriesID); ok && parsed != nil && parsed.Source == "tmdb" && parsed.Kind == "tv" && parsed.SubKind == "series" && parsed.TMDBID > 0 {
			row, err = database.GetPlayHistoryLatestByTMDB(uid, "tv", parsed.TMDBID)
		} else {
			siteKey := "emby"
			siteDetail := strings.TrimSpace(seriesID)
			if siteSeriesVideoID > 0 {
				// sitev_<id>: query by real site key + spider video id.
				if sv, e := database.GetSiteVideoByID(siteSeriesVideoID); e == nil && sv != nil {
					if strings.TrimSpace(sv.SiteKey) != "" && strings.TrimSpace(sv.SiteDetail) != "" {
						siteKey = strings.TrimSpace(sv.SiteKey)
						siteDetail = strings.TrimSpace(sv.SiteDetail)
					}
				}
			}
			row, err = database.GetPlayHistoryLatestBySiteVideo(uid, siteKey, siteDetail)
		}
		if err != nil || row == nil || strings.TrimSpace(row.PlaybackItemID) == "" {
			writeJSON(w, 200, embyPagedEmpty(startIndex))
			return
		}

		total := 1
		if startIndex > 0 || limit <= 0 {
			writeJSON(w, 200, embyPagedItems([]map[string]any{}, startIndex, total))
			return
		}

		itemID := strings.TrimSpace(row.PlaybackItemID)
		obj, err := embyBuildItem(database, itemID)
		if err != nil || obj == nil {
			writeJSON(w, 200, embyPagedEmpty(startIndex))
			return
		}

		snap := embyPlayHistorySnapshot{
			Pos:     row.PlaybackPositionTicks,
			Runtime: row.PlaybackRuntimeTicks,
			Updated: row.UpdatedAt,
		}
		embyApplyPlayHistoryToItemUserData(u.ID, itemID, obj, snap)

		pos := row.PlaybackPositionTicks
		if pos < 0 {
			pos = 0
		}
		runtime := row.PlaybackRuntimeTicks
		if runtime <= 0 && pos > 0 {
			runtime = pos + int64(60*1e7)
		}
		if runtime > 0 {
			obj["RunTimeTicks"] = runtime
		}

		embyEnsureShowsItemFields(obj, fieldsParam)
		if _, ok := obj["ServerId"]; !ok && strings.TrimSpace(serverID) != "" {
			obj["ServerId"] = serverID
		}
		writeJSON(w, 200, embyPagedItems([]map[string]any{obj}, startIndex, total))
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
			// Short site series: expose seasons derived from spider "detail" play sources.
			if siteVideoID, ok := embyParseSiteSeriesIDV2(seriesID); ok {
				sv, err := database.GetSiteVideoByID(siteVideoID)
				if err != nil || sv == nil {
					writeJSON(w, 200, embyPagedItems([]map[string]any{}, 0, 0))
					return
				}
				spiderAPI := embyResolveSpiderAPIBySiteKey(database, sv.SiteKey)
				if strings.TrimSpace(spiderAPI) == "" {
					writeJSON(w, 200, embyPagedItems([]map[string]any{}, 0, 0))
					return
				}
				pans, err := embyFetchSiteDetailPansDedup(database, u, strings.TrimSpace(spiderAPI), strings.TrimSpace(sv.SiteDetail))
				if err != nil {
					writeJSON(w, 200, embyPagedItems([]map[string]any{}, 0, 0))
					return
				}
				seriesName := strings.TrimSpace(sv.Title)
				if seriesName == "" {
					seriesName = "站点资源"
				}
				seasons := make([]map[string]any, 0, len(pans))
				for i, pan := range pans {
					seasonNo := i + 1
					label := embyNormalizePanDisplayLabel(pan.Label)
					if label == "" {
						label = "第" + intToCN(seasonNo) + "季"
					}
					seasonID := embyBuildSiteSeasonIDV2(siteVideoID, seasonNo)
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
		seasons = append(seasons, map[string]any{
			"Id":                      embyBuildTMDBSettingsSeasonID(parsed.TMDBID),
			"Name":                    "设置",
			"SeriesName":              seriesName,
			"Type":                    "Season",
			"IsFolder":                true,
			"LocationType":            "Remote",
			"SeriesId":                seriesID,
			"ParentId":                seriesID,
			"ParentBackdropItemId":    seriesID,
			"ParentBackdropImageTags": []string{"tmdb"},
			"IndexNumber":             embyTMDBSettingsSeasonIndex,
			"ProductionYear":          d.Year,
			"ChildCount":              len(embyTMDBSettingsItems),
			"ImageTags":               map[string]any{"Primary": "tmdb", "Thumb": "tmdb"},
			"UserData":                map[string]any{"Played": false},
		})
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
			// Short site series: list episodes from spider "detail" play sources.
			if siteVideoID, ok := embyParseSiteSeriesIDV2(seriesID); ok {
				sv, err := database.GetSiteVideoByID(siteVideoID)
				if err != nil || sv == nil {
					writeJSON(w, 200, embyPagedItems([]map[string]any{}, 0, 0))
					return
				}
				spiderAPI := embyResolveSpiderAPIBySiteKey(database, sv.SiteKey)
				if strings.TrimSpace(spiderAPI) == "" {
					writeJSON(w, 200, embyPagedItems([]map[string]any{}, 0, 0))
					return
				}
				pans, err := embyFetchSiteDetailPansDedup(database, u, strings.TrimSpace(spiderAPI), strings.TrimSpace(sv.SiteDetail))
				if err != nil {
					writeJSON(w, 200, embyPagedItems([]map[string]any{}, 0, 0))
					return
				}
				seriesName := strings.TrimSpace(sv.Title)
				if seriesName == "" {
					seriesName = "站点资源"
				}
				seasonID := embyQueryTrimCI(r, "SeasonId")
				seasonNo := 1
				label := ""
				if strings.TrimSpace(seasonID) != "" {
					if sv2, pan2, ok := embyParseSiteSeasonIDV2(seasonID); ok && sv2 == siteVideoID {
						seasonNo = pan2
					}
				}
				if strings.TrimSpace(seasonID) == "" {
					out := make([]map[string]any, 0, 64)
					for panIdx, pan := range pans {
						curSeasonNo := panIdx + 1
						curSeasonID := embyBuildSiteSeasonIDV2(siteVideoID, curSeasonNo)
						if curSeasonID == "" {
							continue
						}
						curSeasonName := embyNormalizePanDisplayLabel(pan.Label)
						if curSeasonName == "" {
							curSeasonName = "第" + intToCN(curSeasonNo) + "季"
						}
						allRawSame := true
						firstRawName := ""
						for _, ep := range pan.Episodes {
							epURL := strings.TrimSpace(ep.URL)
							if epURL == "" {
								continue
							}
							rawName := ""
							if rawNames := smartExtractRawNamesFromEpisodeURL(epURL); len(rawNames) > 0 {
								rawName = strings.TrimSpace(rawNames[0])
							}
							if firstRawName == "" {
								firstRawName = rawName
							} else if rawName != firstRawName {
								allRawSame = false
								break
							}
						}
						for i, ep := range pan.Episodes {
							epURL := strings.TrimSpace(ep.URL)
							if epURL == "" {
								continue
							}
							epIndex := i + 1
							epName := strings.TrimSpace(ep.Name)
							rawName := ""
							if rawNames := smartExtractRawNamesFromEpisodeURL(epURL); len(rawNames) > 0 {
								rawName = strings.TrimSpace(rawNames[0])
							}
							if !allRawSame {
								pid := embyPanMockProviderFromLabel(strings.TrimSpace(ep.Flag))
								if pid == "" {
									pid = embyPanMockProviderFromLabel(strings.TrimSpace(pan.Label))
								}
								preferFile := pan.PanMockEnabled && pid != ""
								epName = embyPickEpisodeDisplayName(epName, rawName, strings.ToLower(seriesName), preferFile)
							}
							if epName == "" {
								epName = "第" + intToCN(epIndex) + "集"
							}
							out = append(out, embyBuildSiteEpisodeItem(
								siteVideoID,
								seriesID,
								seriesName,
								curSeasonID,
								curSeasonName,
								curSeasonNo,
								epIndex,
								epName,
							))
						}
					}
					{
						ids := make([]string, 0, len(out))
						for _, it := range out {
							if id, ok := it["Id"].(string); ok && strings.TrimSpace(id) != "" {
								ids = append(ids, strings.TrimSpace(id))
							}
						}
						hit := embyQueryPlayHistoryByItemIDs(database, u.ID, ids)
						if len(hit) > 0 {
							for _, it := range out {
								id, _ := it["Id"].(string)
								id = strings.TrimSpace(id)
								if id == "" {
									continue
								}
								if snap, ok := hit[id]; ok {
									embyApplyPlayHistoryToItemUserData(u.ID, id, it, snap)
								}
							}
						}
					}
					sort.Slice(out, func(i, j int) bool {
						aSeason := intVal(out[i]["ParentIndexNumber"])
						bSeason := intVal(out[j]["ParentIndexNumber"])
						if aSeason != bSeason {
							return aSeason < bSeason
						}
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
				if seasonNo <= 0 {
					seasonNo = 1
				}
				// Pick the pan by seasonNo.
				var pan catpawrunner.Pan
				if seasonNo-1 >= 0 && seasonNo-1 < len(pans) {
					pan = pans[seasonNo-1]
				} else {
					writeJSON(w, 200, embyPagedItems([]map[string]any{}, 0, 0))
					return
				}
				seasonName := strings.TrimSpace(label)
				if seasonName == "" {
					seasonName = embyNormalizePanDisplayLabel(pan.Label)
				}
				if seasonName == "" {
					seasonName = "第" + intToCN(seasonNo) + "季"
				}

				allRawSame := true
				firstRawName := ""
				for _, ep := range pan.Episodes {
					epURL := strings.TrimSpace(ep.URL)
					if epURL == "" {
						continue
					}
					rawName := ""
					if rawNames := smartExtractRawNamesFromEpisodeURL(epURL); len(rawNames) > 0 {
						rawName = strings.TrimSpace(rawNames[0])
					}
					if firstRawName == "" {
						firstRawName = rawName
					} else if rawName != firstRawName {
						allRawSame = false
						break
					}
				}

				out := make([]map[string]any, 0, len(pan.Episodes))
				for i, ep := range pan.Episodes {
					epURL := strings.TrimSpace(ep.URL)
					if epURL == "" {
						continue
					}
					epIndex := i + 1
					epName := strings.TrimSpace(ep.Name)
					rawName := ""
					if rawNames := smartExtractRawNamesFromEpisodeURL(epURL); len(rawNames) > 0 {
						rawName = strings.TrimSpace(rawNames[0])
					}
					if !allRawSame {
						pid := embyPanMockProviderFromLabel(strings.TrimSpace(ep.Flag))
						if pid == "" {
							pid = embyPanMockProviderFromLabel(strings.TrimSpace(pan.Label))
						}
						preferFile := pan.PanMockEnabled && pid != ""
						epName = embyPickEpisodeDisplayName(epName, rawName, strings.ToLower(seriesName), preferFile)
					}
					if epName == "" {
						epName = "第" + intToCN(epIndex) + "集"
					}
					out = append(out, embyBuildSiteEpisodeItem(
						siteVideoID,
						seriesID,
						seriesName,
						seasonID,
						seasonName,
						seasonNo,
						epIndex,
						epName,
					))
				}
				// Populate per-episode playback position from play_history so clients can render resume/progress.
				{
					ids := make([]string, 0, len(out))
					for _, it := range out {
						if id, ok := it["Id"].(string); ok && strings.TrimSpace(id) != "" {
							ids = append(ids, strings.TrimSpace(id))
						}
					}
					hit := embyQueryPlayHistoryByItemIDs(database, u.ID, ids)
					if len(hit) > 0 {
						for _, it := range out {
							id, _ := it["Id"].(string)
							id = strings.TrimSpace(id)
							if id == "" {
								continue
							}
							if snap, ok := hit[id]; ok {
								embyApplyPlayHistoryToItemUserData(u.ID, id, it, snap)
							}
						}
					}
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
			if !ok || sParsed == nil || sParsed.Kind != "tv" || sParsed.TMDBID != parsed.TMDBID {
				embyNotFound(w)
				return
			}
			if sParsed.SubKind == "settings-season" {
				out := make([]map[string]any, 0, len(embyTMDBSettingsItems))
				for i, name := range embyTMDBSettingsItems {
					itemIndex := i + 1
					epID := embyBuildTMDBSettingsItemID(parsed.TMDBID, itemIndex)
					mediaPath := embyBuildMediaPath(epID, "mp4")
					mediaSourceID := embyStableHex32(epID)
					out = append(out, map[string]any{
						"Id":                      epID,
						"Name":                    name,
						"SeriesName":              seriesName,
						"SeasonName":              "设置",
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
						"ParentBackdropImageTags": []string{"tmdb"},
						"IndexNumber":             itemIndex,
						"ParentIndexNumber":       embyTMDBSettingsSeasonIndex,
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
			if sParsed.SubKind != "season" {
				embyNotFound(w)
				return
			}
			seasonNo = sParsed.Season
		} else {
			// Without SeasonId, return all aired episodes across all seasons.
			out := make([]map[string]any, 0, 64)
			now := time.Now()
			for _, s := range d.Seasons {
				if s.Season < 0 || s.EpisodeCount <= 0 {
					continue
				}
				if d.LatestSeason > 0 && s.Season > d.LatestSeason {
					continue
				}
				maxEpisodeAllowed := 0
				if d.LatestSeason > 0 && s.Season == d.LatestSeason && d.LatestEpisode > 0 {
					maxEpisodeAllowed = d.LatestEpisode
				}
				episodes, err := embyTMDBGetTVSeasonEpisodes(database, parsed.TMDBID, s.Season)
				if err != nil {
					embyWriteError(w, 502, "TMDB 请求失败")
					return
				}
				curSeasonID := embyBuildSeasonID(parsed.TMDBID, s.Season)
				curSeasonName := "第" + intToCN(s.Season) + "季"
				if s.Season == 0 {
					curSeasonName = "特别篇"
				}
				for _, e := range episodes {
					if e.Episode <= 0 {
						continue
					}
					if maxEpisodeAllowed > 0 && e.Episode > maxEpisodeAllowed {
						continue
					}
					if !embyIsAiredDate(e.AirDate, now) {
						continue
					}
					name := strings.TrimSpace(e.Name)
					if name == "" {
						name = "第" + intToCN(e.Episode) + "集"
					}
					premiereISO := ""
					if airDate := strings.TrimSpace(e.AirDate); airDate != "" {
						if t, ok := tmdb.ParseAirDateCNMidnight(airDate); ok {
							premiereISO = t.UTC().Format(time.RFC3339)
						}
					}
					out = append(out, embyBuildTMDBEpisodeItem(
						seriesID,
						seriesName,
						curSeasonID,
						curSeasonName,
						[]string{"tmdb"},
						s.Season,
						e.Episode,
						name,
						e.Overview,
						premiereISO,
					))
				}
			}
			{
				ids := make([]string, 0, len(out))
				for _, it := range out {
					if id, ok := it["Id"].(string); ok && strings.TrimSpace(id) != "" {
						ids = append(ids, strings.TrimSpace(id))
					}
				}
				hit := embyQueryPlayHistoryByItemIDs(database, u.ID, ids)
				if len(hit) > 0 {
					for _, it := range out {
						id, _ := it["Id"].(string)
						id = strings.TrimSpace(id)
						if id == "" {
							continue
						}
						if snap, ok := hit[id]; ok {
							embyApplyPlayHistoryToItemUserData(u.ID, id, it, snap)
						}
					}
				}
			}
			sort.Slice(out, func(i, j int) bool {
				aSeason := intVal(out[i]["ParentIndexNumber"])
				bSeason := intVal(out[j]["ParentIndexNumber"])
				if aSeason != bSeason {
					return aSeason < bSeason
				}
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
		now := time.Now()
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
			premiere := strings.TrimSpace(e.AirDate)
			premiereISO := ""
			if premiere != "" {
				if t, ok := tmdb.ParseAirDateCNMidnight(premiere); ok {
					premiereISO = t.UTC().Format(time.RFC3339)
				}
			}
			out = append(out, embyBuildTMDBEpisodeItem(
				seriesID,
				seriesName,
				seasonID,
				seasonName,
				[]string{"tmdb"},
				seasonNo,
				e.Episode,
				name,
				e.Overview,
				premiereISO,
			))
		}
		// Populate per-episode playback position from play_history so clients can render resume/progress.
		{
			ids := make([]string, 0, len(out))
			for _, it := range out {
				if id, ok := it["Id"].(string); ok && strings.TrimSpace(id) != "" {
					ids = append(ids, strings.TrimSpace(id))
				}
			}
			hit := embyQueryPlayHistoryByItemIDs(database, u.ID, ids)
			if len(hit) > 0 {
				for _, it := range out {
					id, _ := it["Id"].(string)
					id = strings.TrimSpace(id)
					if id == "" {
						continue
					}
					if snap, ok := hit[id]; ok {
						embyApplyPlayHistoryToItemUserData(u.ID, id, it, snap)
					}
				}
			}
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
			// Allow download for non-folder video items by default.
			isFolder, _ := obj["IsFolder"].(bool)
			if !isFolder {
				obj["CanDownload"] = true
			} else {
				obj["CanDownload"] = false
			}
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
