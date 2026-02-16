package emby

import "strings"

func embyEnsureStandardItem(obj map[string]any, serverID string) {
	if obj == nil {
		return
	}

	embyEnsureStandardBaseItem(obj, serverID)

	isFolder, _ := obj["IsFolder"].(bool)
	typ, _ := obj["Type"].(string)
	typ = strings.TrimSpace(typ)

	if isFolder || (typ != "Movie" && typ != "Episode") {
		return
	}

	embyEnsureStandardVideoItem(obj)
	embyEnsureStandardUserData(obj)
	embyEnsureStandardMediaSources(obj)
}
