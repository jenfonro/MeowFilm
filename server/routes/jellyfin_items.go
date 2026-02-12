package routes

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleJellyfinItems(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	// /Items/{id}/Images/Primary
	if len(parts) >= 3 && strings.EqualFold(parts[1], "Images") && strings.EqualFold(parts[2], "Primary") && r.Method == http.MethodGet {
		jid := parts[0]
		parsed, ok := jellyfinParseItemID(jid)
		if !ok || parsed == nil {
			http.NotFound(w, r)
			return
		}
		if parsed.Source == "douban" && parsed.TMDBID <= 0 && parsed.DoubanID != "" {
			m, _ := jellyfinGetDoubanTMDBMap(database, parsed.Kind, parsed.DoubanID)
			title := ""
			year := 0
			if m != nil {
				title = m.Title
				year = m.Year
			}
			tid, _ := jellyfinResolveTMDBForDouban(database, parsed.Kind, parsed.DoubanID, title, year)
			if tid > 0 {
				parsed.Source = "tmdb"
				parsed.TMDBID = tid
			}
		}
		img := ""
		if parsed.Kind == "movie" {
			d, err := jellyfinTMDBGetMovieDetail(database, parsed.TMDBID)
			if err == nil && d != nil {
				img = d.Poster
			}
		} else if parsed.Kind == "tv" {
			d, err := jellyfinTMDBGetTVDetail(database, parsed.TMDBID)
			if err == nil && d != nil {
				img = d.Poster
			}
		}
		if img == "" {
			http.NotFound(w, r)
			return
		}
		http.Redirect(w, r, jellyfinTMDBImageURL(img, "w500"), http.StatusFound)
		return
	}

	// /Items/{id}/PlaybackInfo
	if len(parts) >= 2 && strings.EqualFold(parts[1], "PlaybackInfo") && (r.Method == http.MethodGet || r.Method == http.MethodPost) {
		_, ok := jellyfinRequireUser(w, r, database)
		if !ok {
			return
		}
		jid := parts[0]
		handleJellyfinPlaybackInfo(w, r, database, serverID, jid)
		return
	}

	http.NotFound(w, r)
}
