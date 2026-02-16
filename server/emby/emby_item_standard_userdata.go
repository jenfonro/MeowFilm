package emby

func embyEnsureStandardUserData(obj map[string]any) {
	if obj == nil {
		return
	}
	ud, _ := obj["UserData"].(map[string]any)
	if ud == nil {
		ud = map[string]any{}
	}
	if _, ok := ud["PlayedPercentage"]; !ok {
		ud["PlayedPercentage"] = float64(0)
	}
	if _, ok := ud["PlaybackPositionTicks"]; !ok {
		ud["PlaybackPositionTicks"] = int64(0)
	}
	if _, ok := ud["PlayCount"]; !ok {
		ud["PlayCount"] = 0
	}
	if _, ok := ud["IsFavorite"]; !ok {
		ud["IsFavorite"] = false
	}
	if _, ok := ud["LastPlayedDate"]; !ok {
		ud["LastPlayedDate"] = ""
	}
	if _, ok := ud["Played"]; !ok {
		ud["Played"] = false
	}
	if _, ok := ud["Key"]; !ok {
		ud["Key"] = ""
	}
	obj["UserData"] = ud
}
