package emby

import (
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/smart"
)

func doubanProbeSeasons(database *db.DB, tmdbID int, keyword string, wantGlobal int) ([]embyTMDBSeason, bool) {
	return smart.DoubanProbeSeasons(database, tmdbID, keyword, wantGlobal)
}
