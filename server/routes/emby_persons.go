package routes

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

// GET /Persons?searchTerm=...
// Used by some clients during global search to fetch people matches.
func handleEmbyPersons(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	_, ok := embyRequireUser(w, r, database)
	if !ok {
		return
	}
	if r.Method != http.MethodGet {
		embyMethodNotAllowed(w)
		return
	}

	searchTerm := embyQueryTrimCI(r, "SearchTerm")
	startIndex := embyQueryIntClamped(r, "StartIndex", 0, 0, 1<<30)
	limit := embyQueryIntClamped(r, "Limit", 24, 1, 60)

	if searchTerm == "" {
		writeJSON(w, 200, embyPagedEmpty(startIndex))
		return
	}

	results, err := embyTMDBSearchMulti(database, searchTerm)
	if err != nil {
		embyBadGateway(w, err)
		return
	}

	items := make([]map[string]any, 0, limit)
	for _, it := range results {
		if it.MediaType != "person" || it.ID <= 0 || strings.TrimSpace(it.Title) == "" {
			continue
		}
		obj := map[string]any{
			"Id":           embyBuildPersonID(it.ID),
			"Name":         strings.TrimSpace(it.Title),
			"Type":         "Person",
			"IsFolder":     false,
			"ImageTags":    map[string]any{"Primary": "tmdb"},
			"UserData":     map[string]any{"Played": false},
			"ServerId":     serverID,
			"LocationType": "Remote",
		}
		items = append(items, obj)
		if len(items) >= startIndex+limit {
			break
		}
	}

	total := len(items)
	end := startIndex + limit
	if end > total {
		end = total
	}
	page := []map[string]any{}
	if startIndex < total {
		page = items[startIndex:end]
	}

	writeJSON(w, 200, embyPagedItems(page, startIndex, total))
}
