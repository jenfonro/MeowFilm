package emby

import (
	"errors"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func embyResolvePlaybackFromTMDB(database *db.DB, u *embyUser, parsed *embyItemID) (finalURL string, finalHeaders map[string]string, err error) {
	if parsed == nil {
		return "", nil, errors.New("invalid item")
	}
	req := smartPlaybackRequest{
		Kind:    strings.TrimSpace(parsed.Kind),
		TMDBID:  parsed.TMDBID,
		Season:  parsed.Season,
		Episode: parsed.Episode,
		SubKind: strings.TrimSpace(parsed.SubKind),
	}
	return smartResolvePlaybackFromTMDB(database, u, req)
}
