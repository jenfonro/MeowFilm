package emby_service

import (
	"fmt"
	"log"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	metadata_tmdb "github.com/jenfonro/meowfilm/server/metadata/tmdb"
	"github.com/jenfonro/meowfilm/server/smart"
)

type SessionPlaybackPayload struct {
	ItemID        string `json:"ItemId"`
	MediaSourceID string `json:"MediaSourceId"`
	PlaySessionID string `json:"PlaySessionId"`
	PositionTicks int64  `json:"PositionTicks"`
	RunTimeTicks  int64  `json:"RunTimeTicks"`
}

type tmdbHistoryInitMeta struct {
	ContentKey  string
	Title       string
	Poster      string
	Remark      string
	TMDBSeason  int
	TMDBEpisode int
}

func resolveTMDBHistoryKeyFromPlaybackItem(itemID string) (tmdbType string, tmdbID int, season int, episode int, ok bool) {
	ref := parseItemRef(strings.TrimSpace(itemID))
	if ref == nil || ref.Source != "tmdb" || ref.NumericID <= 0 {
		return "", 0, 0, 0, false
	}
	return strings.TrimSpace(ref.MediaType), ref.NumericID, ref.Pan, ref.Episode, true
}

func resolveSiteHistoryRefFromPlaybackItem(itemID string) (*itemRef, bool) {
	ref := parseItemRefAny(strings.TrimSpace(itemID))
	if ref == nil || ref.Source != "site" || ref.SubKind != "episode" {
		return nil, false
	}
	if strings.TrimSpace(ref.SiteKey) == "" || strings.TrimSpace(ref.SiteDetail) == "" || ref.Episode <= 0 {
		return nil, false
	}
	return ref, true
}

func HandleSessionPlaying(database *db.DB, userID int64, payload SessionPlaybackPayload) error {
	if strings.TrimSpace(payload.MediaSourceID) != "" {
		_ = ExtendPlaybackStreamTTL(strings.TrimSpace(payload.MediaSourceID), 60*time.Second, 15*time.Minute)
	}
	if handlePlaybackSwitchSessionAction(database, userID, payload, "playing") {
		return nil
	}
	if database == nil || userID <= 0 {
		return nil
	}
	typ, tmdbID, season, episode, ok := resolveTMDBHistoryKeyFromPlaybackItem(payload.ItemID)
	if ok {
		meta := resolveTMDBHistoryInitMeta(database, typ, tmdbID, season, episode)
		if strings.TrimSpace(meta.ContentKey) == "" || strings.TrimSpace(meta.Title) == "" {
			return nil
		}
		target := resolveSessionPlaybackTarget(userID, payload)
		if target == nil {
			return nil
		}
		siteKey := ""
		siteDetail := ""
		playFlag := ""
		siteEpisodeIndex := 0
		siteEpisodeFile := ""
		if strings.TrimSpace(target.SiteKey) != "" {
			siteKey = strings.TrimSpace(target.SiteKey)
		}
		if strings.TrimSpace(target.SiteDetail) != "" {
			siteDetail = strings.TrimSpace(target.SiteDetail)
		}
		playFlag = strings.TrimSpace(target.PanFlag)
		siteEpisodeIndex = maxInt(0, target.SiteEpisodeIndex)
		siteEpisodeFile = strings.TrimSpace(target.SiteEpisodeFile)
		if siteKey == "" || siteDetail == "" {
			return nil
		}
		now := time.Now().Unix()
		return database.UpsertTMDBPlayHistory(db.TMDBPlayHistoryUpsert{
			UserID:           userID,
			TMDBID:           tmdbID,
			TMDBType:         typ,
			TMDBSeason:       meta.TMDBSeason,
			TMDBEpisode:      meta.TMDBEpisode,
			ContentKey:       meta.ContentKey,
			Title:            meta.Title,
			SiteKey:          siteKey,
			SiteDetail:       siteDetail,
			Poster:           meta.Poster,
			Remark:           meta.Remark,
			PlayFlag:         playFlag,
			SiteEpisodeIndex: siteEpisodeIndex,
			SiteEpisodeFile:  siteEpisodeFile,
			PlaybackItemID:   strings.TrimSpace(payload.ItemID),
			UpdatedAt:        now,
		})
	}
	return upsertSiteHistoryFromSession(database, userID, payload)
}

func HandleSessionProgress(database *db.DB, userID int64, payload SessionPlaybackPayload) error {
	if strings.TrimSpace(payload.MediaSourceID) != "" {
		_ = ExtendPlaybackStreamTTL(strings.TrimSpace(payload.MediaSourceID), 60*time.Second, 15*time.Minute)
	}
	if handlePlaybackSwitchSessionAction(database, userID, payload, "progress") {
		return nil
	}
	return upsertSessionProgress(database, userID, payload)
}

func HandleSessionStopped(database *db.DB, userID int64, payload SessionPlaybackPayload) error {
	if handlePlaybackSwitchSessionAction(database, userID, payload, "stopped") {
		return nil
	}
	return upsertSessionProgress(database, userID, payload)
}

func handlePlaybackSwitchSessionAction(database *db.DB, userID int64, payload SessionPlaybackPayload, trigger string) bool {
	itemID := strings.TrimSpace(payload.ItemID)
	_ = extendPlaybackSwitchSessionByItem(userID, itemID, playbackSwitchSessionTTL)
	handled, action, applied, status := triggerPlaybackSwitchAction(database, userID, payload)
	if !handled {
		return false
	}
	if status == "expired" || status == "done" || status == "running" {
		log.Printf("[emby][switch_action_skip] item=%s action=%s trigger=%s reason=%s", itemID, strings.TrimSpace(action), strings.TrimSpace(trigger), strings.TrimSpace(status))
		return true
	}
	log.Printf("[emby][switch_action] item=%s action=%s applied=%t trigger=%s status=%s", itemID, strings.TrimSpace(action), applied, strings.TrimSpace(trigger), strings.TrimSpace(status))
	return true
}

func upsertSessionProgress(database *db.DB, userID int64, payload SessionPlaybackPayload) error {
	playbackItemID := strings.TrimSpace(payload.ItemID)
	typ, tmdbID, _, _, ok := resolveTMDBHistoryKeyFromPlaybackItem(playbackItemID)
	if database == nil || userID <= 0 {
		return nil
	}
	if ok {
		return database.UpdateTMDBPlayHistoryProgress(
			userID,
			typ,
			tmdbID,
			playbackItemID,
			payload.PositionTicks,
			payload.RunTimeTicks,
			time.Now().Unix(),
		)
	}
	siteRef, siteOK := resolveSiteHistoryRefFromPlaybackItem(playbackItemID)
	target := resolveSessionPlaybackTarget(userID, payload)
	siteKey := ""
	siteDetail := ""
	if siteOK && siteRef != nil {
		siteKey = strings.TrimSpace(siteRef.SiteKey)
		siteDetail = strings.TrimSpace(siteRef.SiteDetail)
	}
	if target != nil {
		if siteKey == "" {
			siteKey = strings.TrimSpace(target.SiteKey)
		}
		if siteDetail == "" {
			siteDetail = strings.TrimSpace(target.SiteDetail)
		}
		if playbackItemID == "" {
			playbackItemID = strings.TrimSpace(target.ItemID)
		}
	}
	if siteKey == "" || siteDetail == "" {
		return nil
	}
	affected, err := database.UpdateSitePlayHistoryProgress(
		userID,
		siteKey,
		siteDetail,
		playbackItemID,
		payload.PositionTicks,
		payload.RunTimeTicks,
		time.Now().Unix(),
	)
	if err != nil {
		return err
	}
	if affected > 0 {
		return nil
	}
	// Create site history only when we can reliably parse episode identity.
	if !siteOK || siteRef == nil {
		if target != nil {
			tmp := payload
			if strings.TrimSpace(tmp.ItemID) == "" {
				tmp.ItemID = strings.TrimSpace(target.ItemID)
			}
			if tmp.ItemID != "" {
				if _, parsed := resolveSiteHistoryRefFromPlaybackItem(tmp.ItemID); parsed {
					return upsertSiteHistoryFromSession(database, userID, tmp)
				}
			}
		}
		return nil
	}
	return upsertSiteHistoryFromSession(database, userID, payload)
}

func resolveSessionPlaybackTarget(userID int64, payload SessionPlaybackPayload) *PlaybackStreamTarget {
	itemID := strings.TrimSpace(payload.ItemID)
	mediaSourceID := strings.TrimSpace(payload.MediaSourceID)
	playSessionID := strings.TrimSpace(payload.PlaySessionID)
	if target, ok := LoadPlaybackStreamTarget(userID, itemID, mediaSourceID, playSessionID); ok && target != nil {
		return target
	}
	if target, ok := LoadPlaybackStreamTargetByMediaSource(mediaSourceID); ok && target != nil {
		return target
	}
	return nil
}

func upsertSiteHistoryFromSession(database *db.DB, userID int64, payload SessionPlaybackPayload) error {
	if database == nil || userID <= 0 {
		return nil
	}
	target := resolveSessionPlaybackTarget(userID, payload)
	itemID := strings.TrimSpace(payload.ItemID)
	if itemID == "" && target != nil {
		itemID = strings.TrimSpace(target.ItemID)
	}
	ref, ok := resolveSiteHistoryRefFromPlaybackItem(itemID)
	if !ok || ref == nil {
		return nil
	}
	siteKey := strings.TrimSpace(ref.SiteKey)
	siteDetail := strings.TrimSpace(ref.SiteDetail)
	siteTitle := strings.TrimSpace(ref.SiteTitle)
	if siteKey == "" || siteDetail == "" || siteTitle == "" {
		return nil
	}
	existing, _ := database.GetPlayHistoryLatestBySiteVideo(userID, siteKey, siteDetail)
	contentKey := ""
	if existing != nil {
		contentKey = strings.TrimSpace(existing.ContentKey)
	}
	if contentKey == "" {
		if key := strings.TrimSpace(database.ComputePlayHistoryKeywordKey(siteTitle)); key != "" {
			contentKey = key
		} else {
			contentKey = siteTitle
		}
	}
	if contentKey == "" {
		return nil
	}
	playFlag := strings.TrimSpace(ref.SitePlayFlag)
	siteEpisodeFile := strings.TrimSpace(smart.FirstRawNameFromURL(strings.TrimSpace(ref.SiteEpisodeURL)))
	siteEpisodeIndex := maxInt(0, ref.Episode)
	poster := ""
	remark := ""
	if existing != nil {
		playFlag = firstNonEmptyString(playFlag, strings.TrimSpace(existing.PlayFlag))
		siteEpisodeFile = firstNonEmptyString(siteEpisodeFile, strings.TrimSpace(existing.SiteEpisodeFile))
		poster = strings.TrimSpace(existing.Poster)
		remark = strings.TrimSpace(existing.Remark)
	}
	if poster == "" || remark == "" {
		if meta, err := fetchSiteDetailMeta(database, userID, siteKey, siteDetail); err == nil {
			if poster == "" {
				poster = strings.TrimSpace(meta.Pic)
			}
			if remark == "" {
				remark = firstNonEmptyString(strings.TrimSpace(meta.Remark), strings.TrimSpace(meta.Overview))
			}
		}
	}
	if target != nil {
		playFlag = firstNonEmptyString(strings.TrimSpace(target.PanFlag), playFlag)
		siteEpisodeFile = firstNonEmptyString(strings.TrimSpace(target.SiteEpisodeFile), siteEpisodeFile)
		if target.SiteEpisodeIndex > 0 {
			siteEpisodeIndex = target.SiteEpisodeIndex
		}
	}
	now := time.Now().Unix()
	return database.UpsertPlayHistory(db.PlayHistoryUpsert{
		UserID:                userID,
		ContentKey:            contentKey,
		SiteKey:               siteKey,
		SiteDetail:            siteDetail,
		Poster:                poster,
		Remark:                remark,
		TMDBID:                0,
		TMDBType:              "",
		PlayFlag:              playFlag,
		SiteEpisodeIndex:      maxInt(0, siteEpisodeIndex),
		SiteEpisodeFile:       siteEpisodeFile,
		TMDBSeason:            0,
		TMDBEpisode:           0,
		UpdatedAt:             now,
		PlaybackPositionTicks: payload.PositionTicks,
		PlaybackRuntimeTicks:  payload.RunTimeTicks,
		PlaybackItemID:        itemID,
	})
}

func resolveTMDBHistoryInitMeta(database *db.DB, tmdbType string, tmdbID int, season int, episode int) tmdbHistoryInitMeta {
	meta := tmdbHistoryInitMeta{
		TMDBSeason:  season,
		TMDBEpisode: episode,
	}
	switch strings.TrimSpace(tmdbType) {
	case "movie":
		if detail, err := metadata_tmdb.GetMovieDetails(database, tmdbID); err == nil && detail != nil {
			meta.Title = strings.TrimSpace(detail.Title)
			meta.Remark = strconv.Itoa(maxInt(0, YearFromDate(strings.TrimSpace(detail.ReleaseDate))))
			meta.Poster = strings.TrimSpace(detail.PosterPath)
		}
		if strings.TrimSpace(meta.Remark) == "0" {
			meta.Remark = ""
		}
	case "tv":
		seriesTitle := ""
		status := ""
		nextSeasonNumber := 0
		nextEpisodeNumber := 0
		totalEpisodes := 0
		seasonCount := 0
		if detail, err := metadata_tmdb.GetTVDetails(database, tmdbID); err == nil && detail != nil {
			seriesTitle = firstNonEmptyString(strings.TrimSpace(detail.Name), strings.TrimSpace(detail.OriginalName))
			status = strings.TrimSpace(detail.Status)
			meta.Poster = strings.TrimSpace(detail.PosterPath)
			for _, s := range detail.Seasons {
				if s.SeasonNumber > 0 {
					seasonCount++
					if s.EpisodeCount > 0 {
						totalEpisodes += s.EpisodeCount
					}
				}
			}
			if detail.NextEpisodeToAir != nil {
				nextSeasonNumber = maxInt(0, detail.NextEpisodeToAir.SeasonNumber)
				nextEpisodeNumber = maxInt(0, detail.NextEpisodeToAir.EpisodeNumber)
			}
		}
		meta.Title = seriesTitle
		meta.Remark = tvHistoryRemark(status, seasonCount, totalEpisodes, nextSeasonNumber, nextEpisodeNumber)
	default:
		return meta
	}
	if database != nil {
		if key := strings.TrimSpace(database.ComputePlayHistoryKeywordKey(strings.TrimSpace(meta.Title))); key != "" {
			meta.ContentKey = key
		}
	}
	meta.Title = strings.TrimSpace(meta.Title)
	meta.ContentKey = strings.TrimSpace(meta.ContentKey)
	return meta
}

func tvHistoryRemark(status string, seasonCount int, totalEpisodes int, nextSeasonNumber int, nextEpisodeNumber int) string {
	state := strings.TrimSpace(status)
	multi := seasonCount > 1
	currentEpisodeNumber := maxInt(0, nextEpisodeNumber-1)
	switch {
	case strings.EqualFold(state, "Ended"):
		if totalEpisodes > 0 {
			if multi {
				return fmt.Sprintf("共%d季%d集", seasonCount, totalEpisodes)
			}
			return fmt.Sprintf("共%d集", totalEpisodes)
		}
		return ""
	case strings.EqualFold(state, "Continuing"), strings.EqualFold(state, "Returning Series"), strings.EqualFold(state, "In Production"):
		if currentEpisodeNumber > 0 {
			if nextSeasonNumber > 1 && multi {
				return fmt.Sprintf("更新至第%d季第%d集", nextSeasonNumber, currentEpisodeNumber)
			}
			return fmt.Sprintf("更新至第%d集", currentEpisodeNumber)
		}
		return ""
	default:
		return ""
	}
}
