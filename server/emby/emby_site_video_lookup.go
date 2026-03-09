package emby

import (
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/smart"
)

func embyResolveSpiderAPIBySiteKey(database *db.DB, siteKey string) string {
	return smart.ResolveSpiderAPIBySiteKey(database, siteKey)
}
