package emby

import (
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/smart"
)

func embyPanMock189AccessPut(shareID string, accessCode string) {
	smart.PanMock189AccessPut(shareID, accessCode)
}

func embyResolvePanMockDetailPans(
	database *db.DB,
	siteKey string,
	siteName string,
	want int,
	tmdbSeasons []embyTMDBSeason,
	tmdbHasMultiSeason bool,
	rawCleanRules []string,
	rawEpisodeRules []string,
	pans []catpawrunner.Pan,
) ([]catpawrunner.Pan, map[string]string) {
	return smart.ResolvePanMockDetailPans(database, siteKey, siteName, want, tmdbSeasons, tmdbHasMultiSeason, rawCleanRules, rawEpisodeRules, pans)
}

// embyResolvePanMockDetailPansIncremental resolves pan_mock list sources concurrently and calls `onPanResolved`.
func embyResolvePanMockDetailPansIncremental(
	database *db.DB,
	siteKey string,
	siteName string,
	want int,
	tmdbSeasons []embyTMDBSeason,
	tmdbHasMultiSeason bool,
	rawCleanRules []string,
	rawEpisodeRules []string,
	pans []catpawrunner.Pan,
	onPanResolved func(panIndex int, episodes []catpawrunner.Episode, accessDelta map[string]string),
) ([]catpawrunner.Pan, map[string]string) {
	return smart.ResolvePanMockDetailPansIncremental(database, siteKey, siteName, want, tmdbSeasons, tmdbHasMultiSeason, rawCleanRules, rawEpisodeRules, pans, onPanResolved)
}
