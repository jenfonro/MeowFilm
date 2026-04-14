package emby_service

import (
	"github.com/jenfonro/meowfilm/internal/db"
	metadata_tmdb "github.com/jenfonro/meowfilm/server/metadata/tmdb"
)

type tmdbSeasonEpisodes struct {
	Season   int
	Episodes int
}

func tmdbHasUnairedBySeasonsAndLast(seasons []tmdbSeasonEpisodes, lastSeason int, lastEpisode int) bool {
	latestSeasonNo := 0
	latestSeasonEpisodes := 0
	for _, season := range seasons {
		if season.Season <= 0 || season.Episodes <= 0 {
			continue
		}
		if season.Season > latestSeasonNo {
			latestSeasonNo = season.Season
			latestSeasonEpisodes = season.Episodes
			continue
		}
		if season.Season == latestSeasonNo && season.Episodes > latestSeasonEpisodes {
			latestSeasonEpisodes = season.Episodes
		}
	}
	if latestSeasonNo <= 0 || latestSeasonEpisodes <= 0 || lastSeason <= 0 {
		return false
	}
	if latestSeasonNo > lastSeason {
		return true
	}
	if latestSeasonNo < lastSeason {
		return false
	}
	if lastEpisode < 0 {
		lastEpisode = 0
	}
	return latestSeasonEpisodes > lastEpisode
}

func tmdbHasUnairedEpisodes(detail *metadata_tmdb.TVDetailsResponse) bool {
	if detail == nil {
		return false
	}
	seasons := make([]tmdbSeasonEpisodes, 0, len(detail.Seasons))
	for _, season := range detail.Seasons {
		seasons = append(seasons, tmdbSeasonEpisodes{Season: season.SeasonNumber, Episodes: season.EpisodeCount})
	}
	lastSeason := 0
	lastEpisode := 0
	if detail.LastEpisodeToAir != nil {
		lastSeason = detail.LastEpisodeToAir.SeasonNumber
		lastEpisode = detail.LastEpisodeToAir.EpisodeNumber
	}
	return tmdbHasUnairedBySeasonsAndLast(seasons, lastSeason, lastEpisode)
}

func tmdbHasUnairedEpisodesFromCachedDetail(detail *db.TMDBCachedDetail) bool {
	if detail == nil {
		return false
	}
	seasons := make([]tmdbSeasonEpisodes, 0, len(detail.Seasons))
	for _, season := range detail.Seasons {
		seasons = append(seasons, tmdbSeasonEpisodes{Season: season.SeasonNumber, Episodes: season.EpisodeCount})
	}
	return tmdbHasUnairedBySeasonsAndLast(seasons, detail.LatestSeason, detail.LatestEpisode)
}
