package tvmeta

import (
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/metadata/douban"
)

const doubanSeasonHintSource = "douban"

func getDoubanTotalEpisodeCount(database *db.DB, tmdbID int, title string, tmdbTotal int) (int, error) {
	hints, err := getDoubanSeasonHints(database, tmdbID, title, tmdbTotal)
	if err != nil {
		return 0, err
	}
	return sumSeasonHints(hints), nil
}

func getDoubanSeasonHints(database *db.DB, tmdbID int, title string, tmdbTotal int) ([]db.TMDBSeasonHint, error) {
	hints, err := database.ListTMDBSeasonHints("tv", tmdbID, doubanSeasonHintSource)
	if err != nil {
		return nil, err
	}
	total := sumSeasonHints(hints)
	if hasValidSeasonHints(hints) && (tmdbTotal <= 0 || total >= tmdbTotal) {
		return hints, nil
	}
	if strings.TrimSpace(title) == "" {
		return hints, nil
	}

	probed, ok := douban.ProbeTVSeasonHints(database, tmdbID, title, tmdbTotal, doubanSeasonHintSource)
	if !ok || len(probed) == 0 {
		return hints, nil
	}
	_ = database.UpsertTMDBSeasonHints("tv", tmdbID, doubanSeasonHintSource, probed)
	return probed, nil
}

func sumSeasonHints(hints []db.TMDBSeasonHint) int {
	sum := 0
	for _, s := range hints {
		if s.SeasonNumber > 0 && s.EpisodeCount > 0 {
			sum += s.EpisodeCount
		}
	}
	return sum
}

func hasValidSeasonHints(hints []db.TMDBSeasonHint) bool {
	for _, s := range hints {
		if s.SeasonNumber > 0 && s.EpisodeCount > 0 {
			return true
		}
	}
	return false
}
