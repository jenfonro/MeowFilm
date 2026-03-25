package emby

import (
	"fmt"
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

func embyViewFolders(database *db.DB, serverID string, infuse bool) []map[string]any {
	secs := embyHomeSections(database)
	out := make([]map[string]any, 0, len(secs))
	for i, s := range secs {
		ct := embyCollectionTypeForHomeSection(s, infuse)
		it := embyBuildViewFolderItem(
			serverID,
			strings.TrimSpace(s.ID),
			strings.TrimSpace(s.Name),
			ct,
		)
		// Keep configured section order stable on clients that auto-sort by SortName.
		it["SortName"] = fmt.Sprintf("%06d", i)
		out = append(out, it)
	}
	return out
}

func embyViewFolderItemByID(database *db.DB, serverID string, id string) (map[string]any, bool) {
	want := strings.TrimSpace(id)
	if want == "" {
		return nil, false
	}
	for i, s := range embyHomeSections(database) {
		if strings.TrimSpace(s.ID) == want {
			ct := embyCollectionTypeForHomeSection(s, false)
			it := embyBuildViewFolderItem(serverID, want, strings.TrimSpace(s.Name), ct)
			it["SortName"] = fmt.Sprintf("%06d", i)
			return it, true
		}
	}
	return nil, false
}

func embyCollectionTypeForHomeSection(s db.EmbyHomeSection, infuse bool) string {
	if strings.EqualFold(strings.TrimSpace(s.Module), "history") {
		if infuse {
			return "homevideos"
		}
		return ""
	}
	if strings.EqualFold(strings.TrimSpace(s.MediaType), "movie") {
		return "movies"
	}
	return "tvshows"
}

func embyUsersViewsResponse(database *db.DB, serverID string, infuse bool) map[string]any {
	items := embyViewFolders(database, serverID, infuse)
	return map[string]any{
		"Items":            items,
		"TotalRecordCount": len(items),
	}
}

func embyUserViewsResponse(database *db.DB, serverID string, infuse bool) map[string]any {
	items := embyViewFolders(database, serverID, infuse)
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
