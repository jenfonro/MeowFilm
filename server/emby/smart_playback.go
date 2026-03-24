package emby

import (
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/smart"
)

type smartPlaybackRequest = smart.PlaybackRequest
type smartPlaybackPickedMeta = smart.PlaybackPickedMeta

func smartUserFromEmby(u *embyUser) *smart.User {
	if u == nil {
		return nil
	}
	return &smart.User{
		ID:       u.ID,
		Username: u.Username,
		Role:     u.Role,
		Status:   u.Status,
	}
}

func smartLoadSiteOrder(database *db.DB, u *embyUser) []string {
	return smart.LoadSiteOrder(database, smartUserFromEmby(u))
}
