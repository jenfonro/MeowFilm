package emby

import (
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/smart"
)

func embyAnyToString(v any) string {
	return smart.AnyToString(v)
}

func embyResolveCatApiBaseForUser(database *db.DB, u *embyUser) string {
	return smart.ResolveCatApiBaseForUser(database, smartUserFromEmby(u))
}
