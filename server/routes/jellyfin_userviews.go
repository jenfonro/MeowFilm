package routes

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleJellyfinUserViews(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	// GET /UserViews/GroupingOptions?userId=...
	if len(parts) >= 1 && strings.EqualFold(parts[0], "GroupingOptions") && r.Method == http.MethodGet {
		u, ok := jellyfinRequireUser(w, r, database)
		if !ok {
			return
		}
		userID := strings.TrimSpace(r.URL.Query().Get("userId"))
		if userID == "" {
			userID = strings.TrimSpace(r.URL.Query().Get("UserId"))
		}
		if userID != "" && strings.TrimSpace(userID) != strings.TrimSpace(u.ID) {
			jellyfinWriteError(w, 404, "Not found")
			return
		}
		writeJSON(w, 200, []map[string]any{
			{"Name": "TMDB 剧集", "Id": "view_tmdb_tv"},
			{"Name": "TMDB 电影", "Id": "view_tmdb_movies"},
		})
		return
	}

	// GET /UserViews?userId=...
	if len(parts) == 0 && r.Method == http.MethodGet {
		u, ok := jellyfinRequireUser(w, r, database)
		if !ok {
			return
		}
		userID := strings.TrimSpace(r.URL.Query().Get("userId"))
		if userID == "" {
			userID = strings.TrimSpace(r.URL.Query().Get("UserId"))
		}
		if userID != "" && strings.TrimSpace(userID) != strings.TrimSpace(u.ID) {
			jellyfinWriteError(w, 404, "Not found")
			return
		}
		writeJSON(w, 200, map[string]any{
			"Items": []map[string]any{
				jellyfinBuildViewFolderItem(serverID, "view_tmdb_tv", "TMDB 剧集", "tvshows"),
				jellyfinBuildViewFolderItem(serverID, "view_tmdb_movies", "TMDB 电影", "movies"),
			},
			"StartIndex":       0,
			"TotalRecordCount": 2,
		})
		return
	}

	http.NotFound(w, r)
}
