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
)

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

	videoID := strings.TrimSpace(itemID)
	episodeIndex := 0
	episodeName := ""
	videoTitle := ""
	videoPoster := ""
	videoRemark := ""

	// Prefer data already present in session payload.
	if dto.NowPlaying != nil {
		if tmdbType == "tv" && strings.TrimSpace(dto.NowPlaying.SeriesName) != "" {
			videoTitle = strings.TrimSpace(dto.NowPlaying.SeriesName)
		}
		if videoTitle == "" && strings.TrimSpace(dto.NowPlaying.Name) != "" {
			videoTitle = strings.TrimSpace(dto.NowPlaying.Name)
		}
		if dto.NowPlaying.ProductionYear > 0 {
			videoRemark = strconv.Itoa(dto.NowPlaying.ProductionYear)
		}
	}

	if !isSiteEpisode && parsed != nil && parsed.Kind == "tv" && parsed.SubKind == "episode" {
		videoID = embyBuildSeriesID(parsed.TMDBID)
		episodeIndex = parsed.Episode
		if dto.NowPlaying != nil && strings.TrimSpace(dto.NowPlaying.Name) != "" {
			episodeName = strings.TrimSpace(dto.NowPlaying.Name)
		}
		if episodeName == "" && parsed.Episode > 0 {
			episodeName = fmt.Sprintf("S%02dE%02d", parsed.Season, parsed.Episode)
		}
	}

	// Fill missing title/poster from TMDB (cached).
	metaKey := tmdbType + ":" + strconv.Itoa(tmdbID)
	if !isSiteEpisode && (videoTitle == "" || videoPoster == "" || videoRemark == "") && tmdbID > 0 && tmdbType != "" {
		if hit, ok := embyCachedBasicMeta(metaKey); ok {
			if videoTitle == "" {
				videoTitle = hit.Title
			}
			if videoPoster == "" {
				videoPoster = hit.Poster
			}
			if videoRemark == "" && hit.Year > 0 {
				videoRemark = strconv.Itoa(hit.Year)
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
			if videoTitle == "" {
				videoTitle = title
			}
			if videoPoster == "" {
				videoPoster = poster
			}
			if videoRemark == "" && year > 0 {
				videoRemark = strconv.Itoa(year)
			}
		}
	}

	if videoTitle == "" {
		videoTitle = itemID
	}
	if !isSiteEpisode && videoPoster != "" {
		videoPoster = embyTMDBImageURL(database, videoPoster, "w500")
	}

	contentKey := ""
	siteKey := "emby"
	playFlag := "emby"
	panLabel := ""
	if isSiteEpisode {
		sv, err := database.GetSiteVideoByID(siteVideoID)
		if err != nil || sv == nil {
			return nil
		}
		siteKey = strings.TrimSpace(sv.SiteKey)
		videoID = strings.TrimSpace(sv.VideoID)
		videoTitle = strings.TrimSpace(sv.Title)
		videoRemark = strings.TrimSpace(sv.Remark)
		videoPoster = embyNormalizeRedirectImageURL(strings.TrimSpace(sv.Poster))
		episodeIndex = siteEp
		if dto.NowPlaying != nil && strings.TrimSpace(dto.NowPlaying.Name) != "" {
			episodeName = strings.TrimSpace(dto.NowPlaying.Name)
		}
		if episodeName == "" {
			episodeName = fmt.Sprintf("P%dE%02d", sitePan, siteEp)
		}
		playFlag = "emby_site"
		// Canonicalize site playback as keyword key so cross-site plays collapse into one history entry.
		// If we later discover TMDB id for the same title, DB layer will promote keyword-key rows to tmdb:*.
		contentKey = strings.ToLower(strings.TrimSpace(database.ComputePlayHistoryKeywordKey(videoTitle)))
		if contentKey == "" {
			contentKey = strings.ToLower(strings.TrimSpace(fmt.Sprintf("site:%s:%s", siteKey, videoID)))
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
			if strings.TrimSpace(episodeName) != "" {
				cands = append(cands, strings.TrimSpace(episodeName))
			}
			if strings.TrimSpace(videoTitle) != "" {
				cands = append(cands, strings.TrimSpace(videoTitle))
			}
			if siteEp > 0 {
				cands = append(cands, fmt.Sprintf("第%d集", siteEp))
				cands = append(cands, fmt.Sprintf("E%02d", siteEp))
			}

			cleanRules, _ := database.ListMagicEpisodeCleanRegexRules()
			episodeRules, _ := database.ListMagicEpisodeRules()
			se, err := magic.MagicEpisodeExtractFromCandidates(cands, cleanRules, episodeRules)
			if err == nil && se.Episode > 0 && strings.TrimSpace(videoTitle) != "" {
				bestID := 0
				bestScore := 0
				results, err := embyTMDBSearchMulti(database, strings.TrimSpace(videoTitle))
				if err == nil && len(results) > 0 {
					for _, it := range results {
						if strings.ToLower(strings.TrimSpace(it.MediaType)) != "tv" {
							continue
						}
						t := strings.TrimSpace(it.Title)
						if t == "" {
							continue
						}
						score := embyComputeMatchScore(strings.TrimSpace(videoTitle), t)
						if score > bestScore {
							bestScore = score
							bestID = it.ID
						}
					}
				}
				if bestID > 0 && bestScore >= 75 {
					season := se.Season
					if season <= 0 {
						season = 1
					}
					ep := se.Episode

					tmdbEpisodeID := embyBuildEpisodeID(bestID, season, ep)

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
						tmdbTitle = strings.TrimSpace(videoTitle)
					}
					if tmdbPoster != "" {
						tmdbPoster = embyTMDBImageURL(database, tmdbPoster, "w500")
					}

					// Promote the canonical record to TMDB id to avoid duplicated keyword+TMDB rows.
					tmdbID = bestID
					tmdbType = "tv"
					contentKey = strings.ToLower(strings.TrimSpace(fmt.Sprintf("tmdb:tv:%d", bestID)))
					episodeIndex = ep
					episodeName = fmt.Sprintf("S%02dE%03d", season, ep)
					itemID = tmdbEpisodeID
					if tmdbTitle != "" {
						videoTitle = tmdbTitle
					}
					if tmdbPoster != "" {
						videoPoster = tmdbPoster
					}
				}
			}
		}
	} else {
		contentKey = strings.TrimSpace(strings.ToLower(fmt.Sprintf("tmdb:%s:%d", tmdbType, tmdbID)))
		if contentKey == "tmdb::0" || contentKey == "tmdb:0:0" || tmdbID <= 0 || tmdbType == "" {
			contentKey = "emby::" + strings.ToLower(videoID)
		}
	}

	// Prefer the site source picked during the actual stream resolution window (report-time only).
	// This lets web and emby share the same canonical history row for quick-start.
	if !isSiteEpisode && strings.HasPrefix(strings.ToLower(contentKey), "tmdb:") {
		if msid := embyComputeMediaSourceIDForItem(u.ID, deviceID, itemID); msid != "" {
			if meta, ok := embyStreams.GetMeta(msid); ok {
				if sk := strings.TrimSpace(meta.SiteKey); sk != "" && !strings.EqualFold(sk, "emby") && strings.TrimSpace(meta.VideoID) != "" {
					siteKey = strings.TrimSpace(meta.SiteKey)
					videoID = strings.TrimSpace(meta.VideoID)
					if strings.TrimSpace(panLabel) == "" {
						panLabel = strings.TrimSpace(meta.PanLabel)
					}
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
	if !isSiteEpisode && strings.HasPrefix(strings.ToLower(contentKey), "tmdb:") && strings.EqualFold(strings.TrimSpace(siteKey), "emby") {
		if prev, err := database.GetPlayHistoryLatestByContentKey(userID, contentKey); err == nil && prev != nil {
			if sk := strings.TrimSpace(prev.SiteKey); sk != "" && !strings.EqualFold(sk, "emby") &&
				strings.TrimSpace(prev.SpiderAPI) != "" && strings.TrimSpace(prev.VideoID) != "" {
				siteKey = strings.TrimSpace(prev.SiteKey)
				videoID = strings.TrimSpace(prev.VideoID)
				if strings.TrimSpace(videoTitle) == "" {
					videoTitle = strings.TrimSpace(prev.VideoTitle)
				}
				if strings.TrimSpace(videoPoster) == "" {
					videoPoster = strings.TrimSpace(prev.VideoPoster)
				}
				videoRemark = strings.TrimSpace(prev.VideoRemark)
				if strings.TrimSpace(panLabel) == "" {
					panLabel = strings.TrimSpace(prev.PanLabel)
				}
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
		VideoID:               videoID,
		VideoTitle:            videoTitle,
		VideoPoster:           videoPoster,
		VideoRemark:           videoRemark,
		TMDBID:                tmdbID,
		TMDBType:              tmdbType,
		PanLabel:              panLabel,
		PlayFlag:              playFlag,
		EpisodeIndex:          episodeIndex,
		EpisodeName:           episodeName,
		TMDBSeason:            tmdbSeason,
		TMDBEpisode:           tmdbEpisode,
		UpdatedAt:             now,
		PlaybackPositionTicks: position,
		PlaybackRuntimeTicks:  runtime,
		PlaybackItemID:        itemID,
	})
}
