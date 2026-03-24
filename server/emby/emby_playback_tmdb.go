package emby

import (
	"errors"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/smart"
)

func embyResolvePlaybackPayloadFromTMDB(database *db.DB, u *embyUser, parsed *embyItemID) (payload map[string]any, picked *smartPlaybackPickedMeta, err error) {
	if parsed == nil {
		return nil, nil, errors.New("invalid item")
	}
	req := smartPlaybackRequest{
		Kind:    strings.TrimSpace(parsed.Kind),
		TMDBID:  parsed.TMDBID,
		Season:  parsed.Season,
		Episode: parsed.Episode,
		SubKind: strings.TrimSpace(parsed.SubKind),
	}
	return smart.ResolvePlaybackPayloadFromTMDB(database, smartUserFromEmby(u), req)
}
