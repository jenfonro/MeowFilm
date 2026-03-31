package emby_service

import (
	"fmt"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	metadata_tmdb "github.com/jenfonro/meowfilm/server/metadata/tmdb"
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

func ResolveTMDBHistoryKeyFromPlaybackItem(itemID string) (tmdbType string, tmdbID int, season int, episode int, ok bool) {
	ref := parseItemRef(strings.TrimSpace(itemID))
	if ref == nil || ref.Source != "tmdb" || ref.NumericID <= 0 {
		return "", 0, 0, 0, false
	}
	return strings.TrimSpace(ref.MediaType), ref.NumericID, ref.Pan, ref.Episode, true
}

func HandleSessionPlaying(database *db.DB, userID int64, payload SessionPlaybackPayload) error {
	if strings.TrimSpace(payload.MediaSourceID) != "" {
		_ = ExtendPlaybackStreamTTL(strings.TrimSpace(payload.MediaSourceID), 60*time.Second, 15*time.Minute)
	}
	typ, tmdbID, season, episode, ok := ResolveTMDBHistoryKeyFromPlaybackItem(payload.ItemID)
	if !ok || database == nil || userID <= 0 {
		return nil
	}
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

func HandleSessionProgress(database *db.DB, userID int64, payload SessionPlaybackPayload) error {
	if strings.TrimSpace(payload.MediaSourceID) != "" {
		_ = ExtendPlaybackStreamTTL(strings.TrimSpace(payload.MediaSourceID), 60*time.Second, 15*time.Minute)
	}
	return upsertSessionProgress(database, userID, payload)
}

func HandleSessionStopped(database *db.DB, userID int64, payload SessionPlaybackPayload) error {
	return upsertSessionProgress(database, userID, payload)
}

func upsertSessionProgress(database *db.DB, userID int64, payload SessionPlaybackPayload) error {
	playbackItemID := strings.TrimSpace(payload.ItemID)
	typ, tmdbID, _, _, ok := ResolveTMDBHistoryKeyFromPlaybackItem(playbackItemID)
	if !ok || database == nil || userID <= 0 {
		return nil
	}
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
