package emby

import (
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/smart"
)

type embyDoubanTMDBMap = smart.DoubanTMDBMap

func embyGetDoubanTMDBMap(database *db.DB, kind string, doubanID string) (*embyDoubanTMDBMap, error) {
	return smart.GetDoubanTMDBMap(database, kind, doubanID)
}

func embyUpsertDoubanTMDBMap(database *db.DB, m embyDoubanTMDBMap) error {
	return smart.UpsertDoubanTMDBMap(database, m)
}

func embyResolveTMDBForDouban(database *db.DB, kind string, doubanID string, title string, year int) (int, error) {
	return smart.ResolveTMDBForDouban(database, kind, doubanID, title, year)
}
