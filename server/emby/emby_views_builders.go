package emby

import (
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func embyHomeSections(database *db.DB) []db.EmbyHomeSection {
	if database == nil {
		return db.DefaultEmbyHomeSections()
	}
	list, err := database.ReadEmbyHomeSections()
	if err != nil || len(list) == 0 {
		return db.DefaultEmbyHomeSections()
	}
	return list
}

func embyGroupingOptions(database *db.DB) []map[string]any {
	secs := embyHomeSections(database)
	out := make([]map[string]any, 0, len(secs))
	for _, s := range secs {
		out = append(out, map[string]any{
			"Name": strings.TrimSpace(s.Name),
			"Id":   strings.TrimSpace(s.ID),
		})
	}
	return out
}

func embyViewFolders(database *db.DB, serverID string) []map[string]any {
	secs := embyHomeSections(database)
	out := make([]map[string]any, 0, len(secs))
	for _, s := range secs {
		ct := "tvshows"
		if strings.EqualFold(strings.TrimSpace(s.MediaType), "movie") {
			ct = "movies"
		}
		out = append(out, embyBuildViewFolderItem(
			serverID,
			strings.TrimSpace(s.ID),
			strings.TrimSpace(s.Name),
			ct,
		))
	}
	return out
}

func embyViewFolderItemByID(database *db.DB, serverID string, id string) (map[string]any, bool) {
	want := strings.TrimSpace(id)
	if want == "" {
		return nil, false
	}
	for _, s := range embyHomeSections(database) {
		if strings.TrimSpace(s.ID) == want {
			ct := "tvshows"
			if strings.EqualFold(strings.TrimSpace(s.MediaType), "movie") {
				ct = "movies"
			}
			return embyBuildViewFolderItem(serverID, want, strings.TrimSpace(s.Name), ct), true
		}
	}
	return nil, false
}

func embyUsersViewsResponse(database *db.DB, serverID string) map[string]any {
	items := embyViewFolders(database, serverID)
	return map[string]any{
		"Items":            items,
		"TotalRecordCount": len(items),
	}
}

func embyUserViewsResponse(database *db.DB, serverID string) map[string]any {
	items := embyViewFolders(database, serverID)
	return embyPagedItems(items, 0, len(items))
}

func embyResolveHomeSectionByID(database *db.DB, id string) (db.EmbyHomeSection, bool) {
	want := strings.TrimSpace(id)
	if want == "" {
		return db.EmbyHomeSection{}, false
	}
	for _, s := range embyHomeSections(database) {
		if strings.TrimSpace(s.ID) == want {
			return s, true
		}
	}
	return db.EmbyHomeSection{}, false
}
