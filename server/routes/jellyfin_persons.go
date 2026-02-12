package routes

import (
	"net/http"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

// GET /Persons?searchTerm=...
// Used by some clients during global search to fetch people matches.
func handleJellyfinPersons(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	_, ok := jellyfinRequireUser(w, r, database)
	if !ok {
		return
	}
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}

	searchTerm := strings.TrimSpace(jellyfinQueryGetCI(r, "SearchTerm"))
	if searchTerm == "" {
		searchTerm = strings.TrimSpace(jellyfinQueryGetCI(r, "searchTerm"))
	}
	startIndex, _ := strconv.Atoi(jellyfinQueryGetCI(r, "StartIndex"))
	if startIndex < 0 {
		startIndex = 0
	}
	limit, _ := strconv.Atoi(jellyfinQueryGetCI(r, "Limit"))
	if limit <= 0 {
		limit = 24
	}
	if limit > 60 {
		limit = 60
	}

	if searchTerm == "" {
		writeJSON(w, 200, map[string]any{
			"Items":            []any{},
			"StartIndex":       startIndex,
			"TotalRecordCount": 0,
		})
		return
	}

	results, err := jellyfinTMDBSearchMulti(database, searchTerm)
	if err != nil {
		jellyfinWriteError(w, 502, err.Error())
		return
	}

	items := make([]map[string]any, 0, limit)
	for _, it := range results {
		if it.MediaType != "person" || it.ID <= 0 || strings.TrimSpace(it.Title) == "" {
			continue
		}
		obj := map[string]any{
			"Id":           jellyfinBuildPersonID(it.ID),
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

	writeJSON(w, 200, map[string]any{
		"Items":            page,
		"StartIndex":       startIndex,
		"TotalRecordCount": total,
	})
}
