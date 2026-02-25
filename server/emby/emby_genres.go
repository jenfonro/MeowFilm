package emby

import (
	"net/http"
	"sort"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleEmbyGenres(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	_, ok := embyRequireUser(w, r, database)
	if !ok {
		return
	}
	if r.Method != http.MethodGet {
		embyMethodNotAllowed(w)
		return
	}

	// Minimal implementation for clients that probe /Genres to render category filters.
	// We don't maintain a real per-library genre index; provide a stable, best-effort set.
	includeItemTypes := strings.ToLower(strings.TrimSpace(embyQueryTrimCI(r, "IncludeItemTypes")))

	type genre struct {
		ID   string
		Name string
	}
	base := []genre{
		{ID: "genre_action", Name: "动作"},
		{ID: "genre_adventure", Name: "冒险"},
		{ID: "genre_animation", Name: "动画"},
		{ID: "genre_comedy", Name: "喜剧"},
		{ID: "genre_crime", Name: "犯罪"},
		{ID: "genre_documentary", Name: "纪录片"},
		{ID: "genre_drama", Name: "剧情"},
		{ID: "genre_family", Name: "家庭"},
		{ID: "genre_fantasy", Name: "奇幻"},
		{ID: "genre_history", Name: "历史"},
		{ID: "genre_horror", Name: "恐怖"},
		{ID: "genre_mystery", Name: "悬疑"},
		{ID: "genre_romance", Name: "爱情"},
		{ID: "genre_scifi", Name: "科幻"},
		{ID: "genre_thriller", Name: "惊悚"},
		{ID: "genre_war", Name: "战争"},
	}

	// If the client requests Series-only, keep the list but drop obviously movie-centric types (best-effort).
	out := base
	if includeItemTypes == "series" {
		filtered := make([]genre, 0, len(base))
		for _, g := range base {
			if g.ID == "genre_documentary" || g.ID == "genre_history" {
				filtered = append(filtered, g)
				continue
			}
			filtered = append(filtered, g)
		}
		out = filtered
	}

	sort.SliceStable(out, func(i, j int) bool { return out[i].Name < out[j].Name })

	items := make([]map[string]any, 0, len(out))
	for _, g := range out {
		items = append(items, map[string]any{
			"Id":           g.ID,
			"Name":         g.Name,
			"Type":         "Genre",
			"IsFolder":     true,
			"LocationType": "Virtual",
			"ServerId":     serverID,
			"ImageTags":    map[string]any{},
			"UserData":     map[string]any{"Played": false},
		})
	}

	writeJSON(w, 200, embyPagedItems(items, 0, len(items)))
}

