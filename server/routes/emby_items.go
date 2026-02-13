package routes

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleEmbyItems(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	// /Items/{id}/Images/{type}...
	// Emby commonly uses:
	// - /Items/{id}/Images/Primary
	// - /Items/{id}/Images/Thumb
	// - /Items/{id}/Images/Backdrop/{index}
	if len(parts) >= 3 && strings.EqualFold(parts[1], "Images") && r.Method == http.MethodGet {
		jid := parts[0]
		imgType := strings.ToLower(strings.TrimSpace(parts[2]))
		index := 0
		if len(parts) >= 4 {
			// /Backdrop/0
			if n := strings.TrimSpace(parts[3]); n != "" {
				// ignore parse errors; use 0
				if v := intValStr(n); v >= 0 {
					index = v
				}
			}
		}
		parsed, ok := embyParseItemID(jid)
		if !ok || parsed == nil {
			embyNotFound(w)
			return
		}
		if parsed.Source == "douban" && parsed.TMDBID <= 0 && parsed.DoubanID != "" {
			m, _ := embyGetDoubanTMDBMap(database, parsed.Kind, parsed.DoubanID)
			title := ""
			year := 0
			if m != nil {
				title = m.Title
				year = m.Year
			}
			tid, _ := embyResolveTMDBForDouban(database, parsed.Kind, parsed.DoubanID, title, year)
			if tid > 0 {
				parsed.Source = "tmdb"
				parsed.TMDBID = tid
			}
		}

		imgPath := ""
		switch imgType {
		case "primary", "thumb":
			imgPath = embyResolveItemImagePath(database, parsed, "primary", index)
			if imgPath == "" && imgType == "thumb" {
				imgPath = embyResolveItemImagePath(database, parsed, "thumb", index)
			}
			size := "w500"
			if imgType == "thumb" {
				size = "w342"
			}
			if parsed.Kind == "person" {
				size = "w185"
			}
			if imgPath == "" {
				embyNotFound(w)
				return
			}
			http.Redirect(w, r, embyTMDBImageURL(imgPath, size), http.StatusFound)
			return
		case "backdrop":
			imgPath = embyResolveItemImagePath(database, parsed, "backdrop", index)
			if imgPath == "" {
				embyNotFound(w)
				return
			}
			http.Redirect(w, r, embyTMDBImageURL(imgPath, "w1280"), http.StatusFound)
			return
		default:
			embyNotFound(w)
			return
		}
	}

	// /Items/{id}/PlaybackInfo
	if len(parts) >= 2 && strings.EqualFold(parts[1], "PlaybackInfo") && (r.Method == http.MethodGet || r.Method == http.MethodPost) {
		_, ok := embyRequireUser(w, r, database)
		if !ok {
			return
		}
		jid := parts[0]
		handleEmbyPlaybackInfo(w, r, database, serverID, jid)
		return
	}

	embyNotFound(w)
}

func embyResolveItemImagePath(database *db.DB, parsed *embyItemID, kind string, index int) string {
	if parsed == nil {
		return ""
	}
	switch parsed.Kind {
	case "person":
		p, err := embyTMDBGetPersonProfile(database, parsed.TMDBID)
		if err != nil {
			return ""
		}
		return p
	case "movie":
		d, err := embyTMDBGetMovieDetail(database, parsed.TMDBID)
		if err != nil || d == nil {
			return ""
		}
		return d.Poster
	case "tv":
		switch parsed.SubKind {
		case "series":
			d, err := embyTMDBGetTVDetail(database, parsed.TMDBID)
			if err != nil || d == nil {
				return ""
			}
			if kind == "backdrop" {
				if strings.TrimSpace(d.Backdrop) != "" {
					return d.Backdrop
				}
				return d.Poster
			}
			return d.Poster
		case "season":
			// Prefer season poster.
			d, err := embyTMDBGetTVDetail(database, parsed.TMDBID)
			if err == nil && d != nil {
				for _, s := range d.Seasons {
					if s.Season == parsed.Season && strings.TrimSpace(s.Poster) != "" {
						return s.Poster
					}
				}
				if kind == "backdrop" {
					if strings.TrimSpace(d.Backdrop) != "" {
						return d.Backdrop
					}
					return d.Poster
				}
			}
			sd, err := embyTMDBGetTVSeasonDetail(database, parsed.TMDBID, parsed.Season)
			if err == nil && sd != nil && strings.TrimSpace(sd.Poster) != "" {
				return strings.TrimSpace(sd.Poster)
			}
			return ""
		case "episode":
			sd, err := embyTMDBGetTVSeasonDetail(database, parsed.TMDBID, parsed.Season)
			if err == nil && sd != nil {
				for _, e := range sd.Episodes {
					if e.Episode == parsed.Episode && strings.TrimSpace(e.Still) != "" {
						return strings.TrimSpace(e.Still)
					}
				}
				// If this episode doesn't have a still, fall back to season poster (better than series poster).
				if strings.TrimSpace(sd.Poster) != "" {
					return strings.TrimSpace(sd.Poster)
				}
			}
			// fallback: series backdrop/poster
			d, err := embyTMDBGetTVDetail(database, parsed.TMDBID)
			if err != nil || d == nil {
				return ""
			}
			if kind == "backdrop" {
				if strings.TrimSpace(d.Backdrop) != "" {
					return d.Backdrop
				}
				return d.Poster
			}
			return d.Poster
		default:
			return ""
		}
	default:
		return ""
	}
}

func intValStr(s string) int {
	n := 0
	for _, ch := range strings.TrimSpace(s) {
		if ch < '0' || ch > '9' {
			return -1
		}
		n = n*10 + int(ch-'0')
	}
	return n
}
