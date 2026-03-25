package emby

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/magic"
	"github.com/jenfonro/meowfilm/server/smart"
)

func embyBuildPlayHistoryContentKey(database *db.DB, contentTitle string, tmdbType string, tmdbID int) string {
	if database == nil {
		return ""
	}
	typ := strings.TrimSpace(strings.ToLower(tmdbType))
	if tmdbID > 0 {
		switch typ {
		case "tv":
			if d, err := embyTMDBGetTVDetail(database, tmdbID); err == nil && d != nil {
				if key := strings.TrimSpace(database.ComputePlayHistoryKeywordKey(strings.TrimSpace(d.Title))); key != "" {
					return key
				}
			}
		case "movie":
			if d, err := embyTMDBGetMovieDetail(database, tmdbID); err == nil && d != nil {
				if key := strings.TrimSpace(database.ComputePlayHistoryKeywordKey(strings.TrimSpace(d.Title))); key != "" {
					return key
				}
			}
		}
	}
	return strings.TrimSpace(database.ComputePlayHistoryKeywordKey(strings.TrimSpace(contentTitle)))
}

func handleEmbySessions(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	u, ok := embyRequireUser(w, r, database)
	if !ok {
		return
	}
	debug := embyDebugLogEnabled()

	if len(parts) == 0 {
		embyNotFound(w)
		return
	}

	head := strings.ToLower(strings.TrimSpace(parts[0]))
	tail := ""
	if len(parts) >= 2 {
		tail = strings.ToLower(strings.TrimSpace(parts[1]))
	}

	if head == "playing" && (tail == "" || tail == "progress" || tail == "stopped") {
		if r.Method != http.MethodPost {
			embyMethodNotAllowed(w)
			return
		}
		// Refresh/clear "playing" state based on session reports, so suppression remains accurate during
		// long playback sessions.
		deviceID := embyClientDeviceID(r)
		if tail == "stopped" {
			embyClearPlaying(u.ID, deviceID)
		} else {
			// Read body once, reuse for both playing guard + history recorder.
			var raw []byte
			if r.Body != nil {
				raw, _ = io.ReadAll(r.Body)
			}
			_ = r.Body.Close()
			r.Body = io.NopCloser(bytes.NewReader(raw))

			var dto embySessionReport
			_ = json.Unmarshal(raw, &dto)
			itemID := strings.TrimSpace(dto.ItemId)
			if itemID == "" && dto.NowPlaying != nil {
				itemID = strings.TrimSpace(dto.NowPlaying.Id)
			}
			if itemID != "" {
				e := embyPlayingGuardEntry{
					ExpireAt: time.Now().Add(embyPlayingGuardTTL),
					ItemID:   itemID,
				}
				if dto.NowPlaying != nil {
					e.NowItemName = strings.TrimSpace(dto.NowPlaying.Name)
					e.SeriesName = strings.TrimSpace(dto.NowPlaying.SeriesName)
					e.SeasonNumber = dto.NowPlaying.ParentIndexNo
					e.EpisodeNo = dto.NowPlaying.IndexNumber
				}
				embyNotePlayingProgress(u.ID, deviceID, e)
				// Maintain a short-lived 302 mapping window for this item to avoid repeated smart resolution
				// when clients re-request playback URLs during active playback.
				if msid := embyComputeMediaSourceID(u.ID, deviceID, itemID); msid != "" {
					embyStreams.ExtendIfLow(msid, 15*time.Second, 20*time.Second)
				}
			}
			// Rewind for history recorder.
			r.Body = io.NopCloser(bytes.NewReader(raw))
		}
		if err := embyRecordPlayHistoryFromSession(r, database, u); err != nil && debug {
			embyDebugPrintf("[emby][sessions] record play history failed err=%q", err.Error())
		}
		w.WriteHeader(http.StatusNoContent)
		return
	}

	embyNotFound(w)
}

type embyNowPlayingItem struct {
	Id              string `json:"Id"`
	Name            string `json:"Name"`
	SeriesName      string `json:"SeriesName"`
	RunTimeTicks    int64  `json:"RunTimeTicks"`
	IndexNumber     int    `json:"IndexNumber"`
	ParentIndexNo   int    `json:"ParentIndexNumber"`
	ProductionYear  int    `json:"ProductionYear"`
	PremiereDateRaw string `json:"PremiereDate"`
}

type embySessionReport struct {
	ItemId        string              `json:"ItemId"`
	PositionTicks int64               `json:"PositionTicks"`
	NowPlaying    *embyNowPlayingItem `json:"NowPlayingItem"`
}

type embyBasicMeta struct {
	Expire time.Time
	Title  string
	Poster string
	Year   int
}

var embyBasicMetaCache = struct {
	sync.Mutex
	M map[string]embyBasicMeta
}{
	M: map[string]embyBasicMeta{},
}

const embyBasicMetaTTL = 6 * time.Hour

func embyCachedBasicMeta(key string) (embyBasicMeta, bool) {
	now := time.Now()
	embyBasicMetaCache.Lock()
	defer embyBasicMetaCache.Unlock()
	hit, ok := embyBasicMetaCache.M[key]
	if !ok {
		return embyBasicMeta{}, false
	}
	if !hit.Expire.IsZero() && hit.Expire.Before(now) {
		delete(embyBasicMetaCache.M, key)
		return embyBasicMeta{}, false
	}
	return hit, true
}

func embyRememberBasicMeta(key string, v embyBasicMeta) {
	embyBasicMetaCache.Lock()
	defer embyBasicMetaCache.Unlock()
	embyBasicMetaCache.M[key] = v
}

func embyRecordPlayHistoryFromSession(r *http.Request, database *db.DB, u *embyUser) error {
	if r == nil || database == nil || database.SQL() == nil || u == nil {
		return nil
	}

	deviceID := embyClientDeviceID(r)

	var dto embySessionReport
	if err := readJSONLoose(r, &dto); err != nil {
		return nil
	}

	itemID := strings.TrimSpace(dto.ItemId)
	if itemID == "" && dto.NowPlaying != nil {
		itemID = strings.TrimSpace(dto.NowPlaying.Id)
	}
	if itemID == "" {
		return nil
	}
	position := dto.PositionTicks
	if position < 0 {
		position = 0
	}
	runtime := int64(0)
	if dto.NowPlaying != nil && dto.NowPlaying.RunTimeTicks > 0 {
		runtime = dto.NowPlaying.RunTimeTicks
	}

	parsed, ok := embyParseItemID(itemID)
	isSiteEpisode := false
	siteVideoID := int64(0)
	sitePan := 0
	siteEp := 0
	if !ok || parsed == nil {
		if v, pan, ep, ok := embyParseSiteEpisodeIDV2(itemID); ok {
			isSiteEpisode = true
			siteVideoID = v
			sitePan = pan
			siteEp = ep
		} else {
			return nil
		}
	} else if parsed.SubKind == "series" || parsed.SubKind == "season" || parsed.SubKind == "settings-season" || parsed.SubKind == "settings-item" {
		return nil
	}

	userID, _ := strconv.ParseInt(strings.TrimSpace(u.ID), 10, 64)
	if userID <= 0 {
		return nil
	}

	tmdbID := 0
	tmdbType := ""
	if !isSiteEpisode && parsed != nil {
		tmdbID = parsed.TMDBID
		if parsed.Kind == "tv" {
			tmdbType = "tv"
		} else if parsed.Kind == "movie" {
			tmdbType = "movie"
		}
	}

	siteDetail := strings.TrimSpace(itemID)
	siteEpisodeIndex := 0
	siteEpisodeFile := ""
	contentTitle := ""
	poster := ""
	remark := ""

	// Prefer data already present in session payload.
	if dto.NowPlaying != nil {
		if tmdbType == "tv" && strings.TrimSpace(dto.NowPlaying.SeriesName) != "" {
			contentTitle = strings.TrimSpace(dto.NowPlaying.SeriesName)
		}
		if contentTitle == "" && strings.TrimSpace(dto.NowPlaying.Name) != "" {
			contentTitle = strings.TrimSpace(dto.NowPlaying.Name)
		}
		if dto.NowPlaying.ProductionYear > 0 {
			remark = strconv.Itoa(dto.NowPlaying.ProductionYear)
		}
	}

	if !isSiteEpisode && parsed != nil && parsed.Kind == "tv" && parsed.SubKind == "episode" {
		siteDetail = embyBuildSeriesID(parsed.TMDBID)
		siteEpisodeIndex = parsed.Episode
		if dto.NowPlaying != nil && strings.TrimSpace(dto.NowPlaying.Name) != "" {
			siteEpisodeFile = strings.TrimSpace(dto.NowPlaying.Name)
		}
		if siteEpisodeFile == "" && parsed.Episode > 0 {
			siteEpisodeFile = fmt.Sprintf("S%02dE%02d", parsed.Season, parsed.Episode)
		}
	}

	// Fill missing title/poster from TMDB (cached).
	metaKey := tmdbType + ":" + strconv.Itoa(tmdbID)
	if !isSiteEpisode && (contentTitle == "" || poster == "" || remark == "") && tmdbID > 0 && tmdbType != "" {
		if hit, ok := embyCachedBasicMeta(metaKey); ok {
			if contentTitle == "" {
				contentTitle = hit.Title
			}
			if poster == "" {
				poster = hit.Poster
			}
			if remark == "" && hit.Year > 0 {
				remark = strconv.Itoa(hit.Year)
			}
		} else {
			title := ""
			poster := ""
			year := 0
			if tmdbType == "tv" {
				d, err := embyTMDBGetTVDetail(database, tmdbID)
				if err == nil && d != nil {
					title = strings.TrimSpace(d.Title)
					poster = strings.TrimSpace(d.Poster)
					year = d.Year
				}
			} else if tmdbType == "movie" {
				d, err := embyTMDBGetMovieDetail(database, tmdbID)
				if err == nil && d != nil {
					title = strings.TrimSpace(d.Title)
					poster = strings.TrimSpace(d.Poster)
					year = d.Year
				}
			}
			embyRememberBasicMeta(metaKey, embyBasicMeta{
				Expire: time.Now().Add(embyBasicMetaTTL),
				Title:  title,
				Poster: poster,
				Year:   year,
			})
			if contentTitle == "" {
				contentTitle = title
			}
			if poster == "" {
				poster = poster
			}
			if remark == "" && year > 0 {
				remark = strconv.Itoa(year)
			}
		}
	}

	if contentTitle == "" {
		contentTitle = itemID
	}
	if !isSiteEpisode && poster != "" {
		poster = embyTMDBImageURL(database, poster, "w500")
	}

	contentKey := ""
	siteKey := "emby"
	playFlag := "emby"
	if isSiteEpisode {
		sv, err := database.GetSiteVideoByID(siteVideoID)
		if err != nil || sv == nil {
			return nil
		}
		siteKey = strings.TrimSpace(sv.SiteKey)
		siteDetail = strings.TrimSpace(sv.SiteDetail)
		contentTitle = strings.TrimSpace(sv.Title)
		remark = strings.TrimSpace(sv.Remark)
		poster = embyNormalizeRedirectImageURL(strings.TrimSpace(sv.Poster))
		siteEpisodeIndex = siteEp
		if dto.NowPlaying != nil && strings.TrimSpace(dto.NowPlaying.Name) != "" {
			siteEpisodeFile = strings.TrimSpace(dto.NowPlaying.Name)
		}
		if siteEpisodeFile == "" {
			siteEpisodeFile = fmt.Sprintf("P%dE%02d", sitePan, siteEp)
		}
		playFlag = "emby_site"
		// Canonicalize site playback as a stable title key so cross-site plays collapse into one history entry.
		contentKey = embyBuildPlayHistoryContentKey(database, contentTitle, "", 0)
		if contentKey == "" {
			contentKey = strings.ToLower(strings.TrimSpace(fmt.Sprintf("site:%s:%s", siteKey, siteDetail)))
		}

		// Normalize: try to map site playback to TMDB play history using magic episode rules.
		// If we can extract an episode and find a strong TMDB match, update the TMDB history too
		// (so users don't see duplicated entries for the same show).
		{
			cands := make([]string, 0, 6)
			if dto.NowPlaying != nil {
				if strings.TrimSpace(dto.NowPlaying.Name) != "" {
					cands = append(cands, strings.TrimSpace(dto.NowPlaying.Name))
				}
				if strings.TrimSpace(dto.NowPlaying.SeriesName) != "" {
					cands = append(cands, strings.TrimSpace(dto.NowPlaying.SeriesName))
				}
			}
			if strings.TrimSpace(siteEpisodeFile) != "" {
				cands = append(cands, strings.TrimSpace(siteEpisodeFile))
			}
			if strings.TrimSpace(contentTitle) != "" {
				cands = append(cands, strings.TrimSpace(contentTitle))
			}
			if siteEp > 0 {
				cands = append(cands, fmt.Sprintf("第%d集", siteEp))
				cands = append(cands, fmt.Sprintf("E%02d", siteEp))
			}

			cleanRules, _ := database.ListMagicEpisodeCleanRegexRules()
			episodeRules, _ := database.ListMagicEpisodeRules()
			se, err := magic.MagicEpisodeExtractFromCandidates(cands, cleanRules, episodeRules)
			if err == nil && se.Episode > 0 && strings.TrimSpace(contentTitle) != "" {
				bestID := 0
				bestScore := 0
				results, err := embyTMDBSearchMulti(database, strings.TrimSpace(contentTitle))
				if err == nil && len(results) > 0 {
					for _, it := range results {
						if strings.ToLower(strings.TrimSpace(it.MediaType)) != "tv" {
							continue
						}
						t := strings.TrimSpace(it.Title)
						if t == "" {
							continue
						}
						score := embyComputeMatchScore(strings.TrimSpace(contentTitle), t)
						if score > bestScore {
							bestScore = score
							bestID = it.ID
						}
					}
				}
				if bestID > 0 && bestScore >= 75 {
					ep := se.Episode
					resolvedSeason := 0
					resolvedEpisode := 0
					detail, detailErr := embyTMDBGetTVDetail(database, bestID)
					if detailErr == nil && detail != nil {
						tmdbSeasons := append([]embyTMDBSeason{}, detail.Seasons...)
						match, _, ok, _, _, _ := smart.ResolveExtractedSeasonEpisodeToGlobal(
							tmdbSeasons,
							nil,
							smart.SeasonEpisode{Season: se.Season, Episode: se.Episode},
							false,
							"tmdb",
							false,
						)
						if ok {
							resolvedSeason = match.Season
							resolvedEpisode = match.Episode
						} else if over, overOK := doubanProbeSeasons(database, bestID, strings.TrimSpace(contentTitle), se.Episode); overOK && len(over) > 0 {
							tmdbMulti := smart.PositiveSeasonCount(tmdbSeasons)
							doubanMulti := smart.PositiveSeasonCount(over)
							switch {
							case tmdbMulti < 2 && doubanMulti >= 2:
								match, _, ok, _, _, _ = smart.ResolveExtractedSeasonEpisodeToGlobal(
									over,
									tmdbSeasons,
									smart.SeasonEpisode{Season: se.Season, Episode: se.Episode},
									true,
									"douban",
									false,
								)
								if ok {
									mapped := smart.TMDBSeasonEpisodeOfGlobal(tmdbSeasons, smart.TMDBGlobalEpisodeNoOf(over, match.Season, match.Episode))
									resolvedSeason = mapped.Season
									resolvedEpisode = mapped.Episode
								}
							case tmdbMulti >= 2 && doubanMulti < 2:
								match, _, ok, _, _, _ = smart.ResolveExtractedSeasonEpisodeToGlobal(
									tmdbSeasons,
									over,
									smart.SeasonEpisode{Season: se.Season, Episode: se.Episode},
									true,
									"tmdb",
									false,
								)
								if ok {
									resolvedSeason = match.Season
									resolvedEpisode = match.Episode
								}
							}
						}
					}
					if resolvedSeason <= 0 || resolvedEpisode <= 0 {
						resolvedSeason = se.Season
						resolvedEpisode = 0
						if se.Season <= 0 {
							// Keep site history only when we cannot reliably normalize.
							bestID = 0
						}
					}
					if bestID <= 0 || resolvedSeason <= 0 || resolvedEpisode <= 0 {
						goto skipTMDBPromote
					}
					ep = resolvedEpisode
					tmdbEpisodeID := embyBuildEpisodeID(bestID, resolvedSeason, ep)
					tmdbTitle := ""
					tmdbPoster := ""
					metaKey := "tv:" + strconv.Itoa(bestID)
					if hit, ok := embyCachedBasicMeta(metaKey); ok {
						tmdbTitle = strings.TrimSpace(hit.Title)
						tmdbPoster = strings.TrimSpace(hit.Poster)
					} else {
						if d, err := embyTMDBGetTVDetail(database, bestID); err == nil && d != nil {
							tmdbTitle = strings.TrimSpace(d.Title)
							tmdbPoster = strings.TrimSpace(d.Poster)
							embyRememberBasicMeta(metaKey, embyBasicMeta{
								Expire: time.Now().Add(embyBasicMetaTTL),
								Title:  tmdbTitle,
								Poster: tmdbPoster,
								Year:   d.Year,
							})
						}
					}
					if tmdbTitle == "" {
						tmdbTitle = strings.TrimSpace(contentTitle)
					}
					if tmdbPoster != "" {
						tmdbPoster = embyTMDBImageURL(database, tmdbPoster, "w500")
					}

					// Promote the canonical record to a TMDB-backed title key to avoid duplicated
					// keyword/TMDB rows while keeping contentKey stable and human-readable.
					tmdbID = bestID
					tmdbType = "tv"
					contentKey = embyBuildPlayHistoryContentKey(database, tmdbTitle, "tv", bestID)
					siteEpisodeIndex = ep
					siteEpisodeFile = fmt.Sprintf("S%02dE%03d", resolvedSeason, ep)
					itemID = tmdbEpisodeID
					if tmdbTitle != "" {
						contentTitle = tmdbTitle
					}
					if tmdbPoster != "" {
						poster = tmdbPoster
					}
				}
			skipTMDBPromote:
			}
		}
	} else {
		contentKey = embyBuildPlayHistoryContentKey(database, contentTitle, tmdbType, tmdbID)
		if contentKey == "" || tmdbID <= 0 || tmdbType == "" {
			contentKey = "emby::" + strings.ToLower(siteDetail)
		}
	}

	// Prefer the site source picked during the actual stream resolution window (report-time only).
	// This lets web and emby share the same canonical history row for quick-start.
	if !isSiteEpisode && tmdbID > 0 && (tmdbType == "tv" || tmdbType == "movie") {
		if msid := embyComputeMediaSourceIDForItem(u.ID, deviceID, itemID); msid != "" {
			if meta, ok := embyStreams.GetMeta(msid); ok {
				if sk := strings.TrimSpace(meta.SiteKey); sk != "" && !strings.EqualFold(sk, "emby") && strings.TrimSpace(meta.SiteDetail) != "" {
					siteKey = strings.TrimSpace(meta.SiteKey)
					siteDetail = strings.TrimSpace(meta.SiteDetail)
					if playFlag == "emby" {
						playFlag = "emby_smart"
					}
				}
			}
		}
	}

	// If this is a TMDB item played from Emby, try to bind the canonical history row to a real site source
	// (last picked smart-play site) so both Emby and Web can "quick start" from the same record.
	// This is best-effort: if no site source exists yet, we fall back to siteKey="emby".
	if !isSiteEpisode && tmdbID > 0 && (tmdbType == "tv" || tmdbType == "movie") && strings.EqualFold(strings.TrimSpace(siteKey), "emby") {
		if prev, err := database.GetPlayHistoryLatestByTMDB(userID, tmdbType, tmdbID); err == nil && prev != nil {
			if sk := strings.TrimSpace(prev.SiteKey); sk != "" && !strings.EqualFold(sk, "emby") &&
				strings.TrimSpace(prev.SpiderAPI) != "" && strings.TrimSpace(prev.SiteDetail) != "" {
				siteKey = strings.TrimSpace(prev.SiteKey)
				siteDetail = strings.TrimSpace(prev.SiteDetail)
				if strings.TrimSpace(poster) == "" {
					poster = strings.TrimSpace(prev.Poster)
				}
				remark = strings.TrimSpace(prev.Remark)
			}
		}
	}

	now := time.Now().Unix()
	tmdbSeason := 0
	tmdbEpisode := 0
	if parsed, ok := embyParseItemID(itemID); ok && parsed != nil && parsed.Source == "tmdb" && parsed.Kind == "tv" {
		if parsed.Season > 0 {
			tmdbSeason = parsed.Season
		}
		if parsed.Episode > 0 {
			tmdbEpisode = parsed.Episode
		}
	}
	return database.UpsertPlayHistory(db.PlayHistoryUpsert{
		UserID:                userID,
		ContentKey:            contentKey,
		SiteKey:               siteKey,
		SiteName:              "",
		SpiderAPI:             "",
		SiteDetail:            siteDetail,
		Poster:                poster,
		Remark:                remark,
		TMDBID:                tmdbID,
		TMDBType:              tmdbType,
		PlayFlag:              playFlag,
		SiteEpisodeIndex:      siteEpisodeIndex,
		SiteEpisodeFile:       siteEpisodeFile,
		TMDBSeason:            tmdbSeason,
		TMDBEpisode:           tmdbEpisode,
		UpdatedAt:             now,
		PlaybackPositionTicks: position,
		PlaybackRuntimeTicks:  runtime,
		PlaybackItemID:        itemID,
	})
}
