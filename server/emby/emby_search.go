package emby

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleEmbySearch(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	// GET /Search/Tmdb?SearchTerm=...  (debug helper)
	if len(parts) >= 1 && strings.EqualFold(parts[0], "Tmdb") && r.Method == http.MethodGet {
		_, ok := embyRequireUser(w, r, database)
		if !ok {
			return
		}
		q := embyQueryTrimCI(r, "SearchTerm")
		items, err := embyTMDBSearchMulti(database, q)
		if err != nil {
			writeJSON(w, 200, map[string]any{
				"ok":    false,
				"error": err.Error(),
				"count": 0,
				"items": []any{},
			})
			return
		}
		preview := []map[string]any{}
		for i := 0; i < len(items) && i < 5; i += 1 {
			preview = append(preview, map[string]any{
				"id":        items[i].ID,
				"mediaType": items[i].MediaType,
				"title":     items[i].Title,
				"year":      items[i].Year,
			})
		}
		writeJSON(w, 200, map[string]any{
			"ok":    true,
			"count": len(items),
			"items": preview,
		})
		return
	}

	// GET /Search/Hints?SearchTerm=...
	if len(parts) >= 1 && strings.EqualFold(parts[0], "Hints") && r.Method == http.MethodGet {
		_, ok := embyRequireUser(w, r, database)
		if !ok {
			return
		}
		q := embyQueryTrimCI(r, "SearchTerm")
		if q == "" {
			writeJSON(w, 200, map[string]any{"SearchHints": []any{}, "TotalRecordCount": 0})
			return
		}
		items, err := embyTMDBSearchMulti(database, q)
		if err != nil {
			embyBadGateway(w, err)
			return
		}
		hints := []map[string]any{}
		for _, it := range items {
			if it.ID <= 0 || it.Title == "" {
				continue
			}
			jid := ""
			typ := ""
			switch it.MediaType {
			case "tv":
				jid = embyBuildSeriesID(it.ID)
				typ = "Series"
			case "movie":
				jid = embyBuildMovieID(it.ID)
				typ = "Movie"
			}
			if jid == "" {
				continue
			}
			hints = append(hints, map[string]any{
				"Id":   jid,
				"Name": it.Title,
				"Type": typ,
			})
			if len(hints) >= 20 {
				break
			}
		}
		writeJSON(w, 200, map[string]any{
			"SearchHints":      hints,
			"TotalRecordCount": len(hints),
		})
		return
	}
	embyNotFound(w)
}
