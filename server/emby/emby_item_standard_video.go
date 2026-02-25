package emby

func embyEnsureStandardVideoItem(obj map[string]any) {
	if obj == nil {
		return
	}

	if _, ok := obj["Container"]; !ok {
		obj["Container"] = "mp4,m4v"
	}
	if _, ok := obj["PremiereDate"]; !ok {
		obj["PremiereDate"] = ""
	}
	if _, ok := obj["CommunityRating"]; !ok {
		obj["CommunityRating"] = float64(0)
	}
	if _, ok := obj["ProductionYear"]; !ok {
		obj["ProductionYear"] = 0
	}
	if _, ok := obj["IndexNumber"]; !ok {
		obj["IndexNumber"] = 0
	}
	if _, ok := obj["ParentIndexNumber"]; !ok {
		obj["ParentIndexNumber"] = 0
	}
	if _, ok := obj["ParentBackdropItemId"]; !ok {
		obj["ParentBackdropItemId"] = ""
	}
	if _, ok := obj["ParentBackdropImageTags"]; !ok {
		obj["ParentBackdropImageTags"] = []any{}
	}
	if _, ok := obj["SeriesName"]; !ok {
		obj["SeriesName"] = ""
	}
	if _, ok := obj["SeriesId"]; !ok {
		obj["SeriesId"] = ""
	}
	if _, ok := obj["SeasonId"]; !ok {
		obj["SeasonId"] = nil
	}
	if _, ok := obj["SeriesPrimaryImageTag"]; !ok {
		obj["SeriesPrimaryImageTag"] = ""
	}
	if _, ok := obj["SeasonName"]; !ok {
		obj["SeasonName"] = ""
	}

	if _, ok := obj["VideoType"]; !ok {
		obj["VideoType"] = "VideoFile"
	}
	if _, ok := obj["MediaType"]; !ok {
		obj["MediaType"] = "Video"
	}
	if _, ok := obj["LocationType"]; !ok {
		obj["LocationType"] = "FileSystem"
	}
	if _, ok := obj["RunTimeTicks"]; !ok {
		obj["RunTimeTicks"] = int64(0)
	}
	if _, ok := obj["Chapters"]; !ok {
		obj["Chapters"] = []any{}
	}
	if _, ok := obj["People"]; !ok {
		obj["People"] = []any{}
	}
	if _, ok := obj["Size"]; !ok {
		obj["Size"] = int64(0)
	}
	if _, ok := obj["CanDownload"]; !ok {
		// Assume downloads are permitted for video items; clients use this to decide whether to show
		// a download button and to run capability probes.
		obj["CanDownload"] = true
	}
}
