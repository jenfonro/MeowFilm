package emby

import (
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

// embyNormalizeParsedToTMDB resolves Douban IDs to TMDB IDs on-demand.
// When strict is true, it returns an error if TMDB cannot be matched.
func embyNormalizeParsedToTMDB(database *db.DB, parsed *embyItemID, strict bool) error {
	if database == nil || parsed == nil {
		return errorString("invalid args")
	}
	if strings.TrimSpace(parsed.Source) != "douban" || parsed.TMDBID > 0 || strings.TrimSpace(parsed.DoubanID) == "" {
		return nil
	}
	m, _ := embyGetDoubanTMDBMap(database, parsed.Kind, parsed.DoubanID)
	title := ""
	year := 0
	if m != nil {
		title = m.Title
		year = m.Year
	}
	tid, err := embyResolveTMDBForDouban(database, parsed.Kind, parsed.DoubanID, title, year)
	if err != nil {
		return err
	}
	if tid <= 0 {
		if strict {
			return errorString("TMDB 未匹配")
		}
		return nil
	}
	parsed.Source = "tmdb"
	parsed.TMDBID = tid
	return nil
}

