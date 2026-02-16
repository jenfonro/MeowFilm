package emby

func embyDefaultGroupingOptions() []map[string]any {
	return []map[string]any{
		{"Name": "剧集", "Id": embyViewTMDBTV},
		{"Name": "电影", "Id": embyViewTMDBMovies},
		{"Name": "动漫", "Id": embyViewTMDBAnime},
		{"Name": "综艺", "Id": embyViewTMDBShow},
	}
}

func embyDefaultViewFolders(serverID string) []map[string]any {
	return []map[string]any{
		embyBuildViewFolderItem(serverID, embyViewTMDBTV, "剧集", "tvshows"),
		embyBuildViewFolderItem(serverID, embyViewTMDBMovies, "电影", "movies"),
		embyBuildViewFolderItem(serverID, embyViewTMDBAnime, "动漫", "tvshows"),
		embyBuildViewFolderItem(serverID, embyViewTMDBShow, "综艺", "tvshows"),
	}
}

func embyDefaultViewFolderItemByID(serverID string, id string) (map[string]any, bool) {
	switch id {
	case embyViewTMDBTV:
		return embyBuildViewFolderItem(serverID, id, "剧集", "tvshows"), true
	case embyViewTMDBMovies:
		return embyBuildViewFolderItem(serverID, id, "电影", "movies"), true
	case embyViewTMDBAnime:
		return embyBuildViewFolderItem(serverID, id, "动漫", "tvshows"), true
	case embyViewTMDBShow:
		return embyBuildViewFolderItem(serverID, id, "综艺", "tvshows"), true
	default:
		return nil, false
	}
}

func embyDefaultUsersViewsResponse(serverID string) map[string]any {
	items := embyDefaultViewFolders(serverID)
	return map[string]any{
		"Items":            items,
		"TotalRecordCount": len(items),
	}
}

func embyDefaultUserViewsResponse(serverID string) map[string]any {
	items := embyDefaultViewFolders(serverID)
	return embyPagedItems(items, 0, len(items))
}
