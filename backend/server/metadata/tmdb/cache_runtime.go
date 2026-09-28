package tmdb

import (
	"fmt"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

const (
	tmdbStableRefreshTTL        = 14 * 24 * time.Hour
	tmdbNoNextEpisodeRefreshTTL = 24 * time.Hour
)

const tmdbSeasonFallbackRefreshTTL = 24 * time.Hour

type tvDetailRefreshReason string

const (
	tvDetailRefreshReasonNone        tvDetailRefreshReason = ""
	tvDetailRefreshReasonIncomplete  tvDetailRefreshReason = "incomplete"
	tvDetailRefreshReasonForceTTL    tvDetailRefreshReason = "force_ttl"
	tvDetailRefreshReasonNextEpisode tvDetailRefreshReason = "next_episode"
	tvDetailRefreshReasonNoNextTTL   tvDetailRefreshReason = "no_next_ttl"
)

func GetMovieDetail(database *db.DB, tmdbID int) (*db.TMDBCachedDetail, error) {
	detail, _, err := ensureMovieDetailFresh(database, tmdbID)
	return detail, err
}

func GetTVDetail(database *db.DB, tmdbID int) (*db.TMDBCachedDetail, error) {
	detail, _, err := ensureTVDetailFresh(database, tmdbID)
	return detail, err
}

func GetTVSeasonDetail(database *db.DB, tmdbID int, season int) (*db.TMDBCachedSeasonDetail, error) {
	detail, _, err := ensureTVSeasonFresh(database, tmdbID, season)
	return detail, err
}

func ensureMovieDetailFresh(database *db.DB, tmdbID int) (*db.TMDBCachedDetail, bool, error) {
	if database == nil || tmdbID <= 0 {
		return nil, false, fmt.Errorf("invalid tmdbID/db")
	}
	language := tmdbDetailLanguage(database)
	now := time.Now()
	cached, err := database.ReadTMDBCachedDetail("movie", tmdbID, language)
	if err != nil {
		return nil, false, err
	}
	if cached != nil && strings.TrimSpace(cached.TMDBType) == "movie" {
		_ = database.TouchTMDBMediaAccess("movie", tmdbID, now.Unix())
		if !needsMovieDetailRefresh(cached, now) {
			return cached, false, nil
		}
	}
	if err := fetchTMDBDetailUpstream(database, "movie", tmdbID); err != nil {
		return nil, false, err
	}
	refreshed, err := database.ReadTMDBCachedDetail("movie", tmdbID, language)
	return refreshed, true, err
}

func ensureTVDetailFresh(database *db.DB, tmdbID int) (*db.TMDBCachedDetail, bool, error) {
	if database == nil || tmdbID <= 0 {
		return nil, false, fmt.Errorf("invalid tmdbID/db")
	}
	language := tmdbDetailLanguage(database)
	now := time.Now()
	cached, err := database.ReadTMDBCachedDetail("tv", tmdbID, language)
	if err != nil {
		return nil, false, err
	}
	if cached != nil && strings.TrimSpace(cached.TMDBType) == "tv" {
		_ = database.TouchTMDBMediaAccess("tv", tmdbID, now.Unix())
		needRefresh, reason := decideTVDetailRefresh(cached, now)
		if !needRefresh {
			return cached, false, nil
		}
		if err := fetchTMDBDetailUpstream(database, "tv", tmdbID); err != nil {
			return nil, false, err
		}
		if reason == tvDetailRefreshReasonNextEpisode {
			if err := database.MarkTMDBNextEpisodeRefreshDay("tv", tmdbID, todayCN(now), now.Unix()); err != nil {
				return nil, false, err
			}
		}
		refreshed, err := database.ReadTMDBCachedDetail("tv", tmdbID, language)
		return refreshed, true, err
	}
	if err := fetchTMDBDetailUpstream(database, "tv", tmdbID); err != nil {
		return nil, false, err
	}
	refreshed, err := database.ReadTMDBCachedDetail("tv", tmdbID, language)
	return refreshed, true, err
}

type tvSeasonRefreshReason string

const (
	tvSeasonRefreshReasonNone        tvSeasonRefreshReason = ""
	tvSeasonRefreshReasonMissing     tvSeasonRefreshReason = "missing"
	tvSeasonRefreshReasonStableTTL   tvSeasonRefreshReason = "stable_ttl"
	tvSeasonRefreshReasonScheduleDue tvSeasonRefreshReason = "schedule_due"
	tvSeasonRefreshReasonFallbackTTL tvSeasonRefreshReason = "fallback_ttl"
)

type tvSeasonRefreshDecision struct {
	NeedRefresh   bool
	Reason        tvSeasonRefreshReason
	RefreshDay    string
	AllowThrottle bool
}

func ensureTVSeasonFresh(database *db.DB, tmdbID int, season int) (*db.TMDBCachedSeasonDetail, bool, error) {
	if database == nil || tmdbID <= 0 || season < 0 {
		return nil, false, fmt.Errorf("invalid args")
	}
	language := tmdbDetailLanguage(database)
	cached, err := database.ReadTMDBCachedSeasonDetail(tmdbID, season, language)
	if err != nil {
		return nil, false, err
	}
	now := time.Now()
	decision := decideTVSeasonRefresh(cached, now)
	if !decision.NeedRefresh {
		return cached, false, nil
	}
	raw, err := fetchAndStoreTMDBTVSeasonDetail(database, tmdbID, season, language)
	if err != nil {
		return nil, false, err
	}
	if len(raw) > 0 {
		if decision.AllowThrottle && strings.TrimSpace(decision.RefreshDay) != "" {
			if err := database.MarkTMDBSeasonRefreshDay(tmdbID, season, decision.RefreshDay, now.Unix()); err != nil {
				return nil, false, err
			}
		} else {
			if err := database.MarkTMDBSeasonSyncOK(tmdbID, season, now.Unix()); err != nil {
				return nil, false, err
			}
		}
	}
	refreshed, err := database.ReadTMDBCachedSeasonDetail(tmdbID, season, language)
	return refreshed, true, err
}

func needsMovieDetailRefresh(detail *db.TMDBCachedDetail, now time.Time) bool {
	if detail == nil {
		return true
	}
	if strings.TrimSpace(detail.MetaLevel) != "detail" {
		return true
	}
	return cacheExpired(detail.LastRefreshAt, now, tmdbStableRefreshTTL)
}

func decideTVDetailRefresh(detail *db.TMDBCachedDetail, now time.Time) (bool, tvDetailRefreshReason) {
	if detail == nil {
		return true, tvDetailRefreshReasonIncomplete
	}
	if strings.TrimSpace(detail.MetaLevel) != "detail" {
		return true, tvDetailRefreshReasonIncomplete
	}
	if strings.TrimSpace(detail.SeasonLevel) == "none" || len(detail.Seasons) == 0 {
		return true, tvDetailRefreshReasonIncomplete
	}
	if cacheExpired(detail.LastRefreshAt, now, tmdbStableRefreshTTL) {
		return true, tvDetailRefreshReasonForceTTL
	}
	if isTVEnded(detail.Status) {
		return false, tvDetailRefreshReasonNone
	}
	nextAirDate := strings.TrimSpace(detail.NextEpisodeAirDate)
	if nextAirDate == "" {
		if cacheExpired(detail.LastRefreshAt, now, tmdbNoNextEpisodeRefreshTTL) {
			return true, tvDetailRefreshReasonNoNextTTL
		}
		return false, tvDetailRefreshReasonNone
	}
	today := todayCN(now)
	if !isDueByNextEpisodeAirDate(nextAirDate, today) {
		return false, tvDetailRefreshReasonNone
	}
	if strings.TrimSpace(detail.NextEpisodeRefreshDay) == today {
		return false, tvDetailRefreshReasonNone
	}
	return true, tvDetailRefreshReasonNextEpisode
}

func todayCN(now time.Time) string {
	return tmdbCNDayStart(now).Format("2006-01-02")
}

func isDueByNextEpisodeAirDate(airDate string, today string) bool {
	date := strings.TrimSpace(airDate)
	day := strings.TrimSpace(today)
	if date == "" || day == "" {
		return false
	}
	return date <= day
}

func decideTVSeasonRefresh(cached *db.TMDBCachedSeasonDetail, now time.Time) tvSeasonRefreshDecision {
	if cached == nil {
		return tvSeasonRefreshDecision{NeedRefresh: true, Reason: tvSeasonRefreshReasonMissing}
	}
	today := todayCN(now)
	refreshDay := nextSeasonRefreshDate(cached, today)
	if refreshDay != "" {
		if refreshDay > today {
			return tvSeasonRefreshDecision{}
		}
		if strings.TrimSpace(cached.RefreshOnDay) == today {
			return tvSeasonRefreshDecision{}
		}
		return tvSeasonRefreshDecision{
			NeedRefresh:   true,
			Reason:        tvSeasonRefreshReasonScheduleDue,
			RefreshDay:    today,
			AllowThrottle: true,
		}
	}
	lastAired := latestSeasonAiredDate(cached)
	if lastAired != "" && lastAired < today && !seasonHasUndatedTail(cached) {
		if cacheExpired(cached.LastSyncOKAt, now, tmdbStableRefreshTTL) {
			return tvSeasonRefreshDecision{NeedRefresh: true, Reason: tvSeasonRefreshReasonStableTTL}
		}
		return tvSeasonRefreshDecision{}
	}
	if cacheExpired(cached.LastSyncOKAt, now, tmdbSeasonFallbackRefreshTTL) {
		return tvSeasonRefreshDecision{NeedRefresh: true, Reason: tvSeasonRefreshReasonFallbackTTL}
	}
	return tvSeasonRefreshDecision{}
}

func nextSeasonRefreshDate(cached *db.TMDBCachedSeasonDetail, today string) string {
	if cached == nil {
		return ""
	}
	day := strings.TrimSpace(today)
	if day == "" {
		return ""
	}
	for _, episode := range cached.Episodes {
		airDate := strings.TrimSpace(episode.AirDate)
		if airDate == "" {
			continue
		}
		if airDate >= day {
			return airDate
		}
	}
	return ""
}

func latestSeasonAiredDate(cached *db.TMDBCachedSeasonDetail) string {
	if cached == nil {
		return ""
	}
	last := ""
	for _, episode := range cached.Episodes {
		airDate := strings.TrimSpace(episode.AirDate)
		if airDate == "" {
			continue
		}
		last = airDate
	}
	return last
}

func seasonHasUndatedTail(cached *db.TMDBCachedSeasonDetail) bool {
	if cached == nil || len(cached.Episodes) == 0 {
		return false
	}
	lastDatedIndex := -1
	for i, episode := range cached.Episodes {
		if strings.TrimSpace(episode.AirDate) != "" {
			lastDatedIndex = i
		}
	}
	if lastDatedIndex < 0 {
		return false
	}
	for _, episode := range cached.Episodes[lastDatedIndex+1:] {
		if strings.TrimSpace(episode.AirDate) == "" {
			return true
		}
	}
	return false
}

func isTVEnded(status string) bool {
	return strings.EqualFold(strings.TrimSpace(status), "Ended")
}

func cacheExpired(lastRefreshAt int64, now time.Time, ttl time.Duration) bool {
	if lastRefreshAt <= 0 {
		return true
	}
	return time.Unix(lastRefreshAt, 0).Add(ttl).Before(now)
}
