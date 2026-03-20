package emby

import "fmt"

const embyTMDBSettingsSeasonIndex = 9999

var embyTMDBSettingsItems = []string{
	"换源",
	"片源错误",
	"天翼",
	"移动",
	"百度",
	"夸克",
	"UC",
}

func embyBuildTMDBSettingsSeasonID(tmdbID int) string {
	return fmt.Sprintf("tmdb_tv_%d_cfg", tmdbID)
}

func embyBuildTMDBSettingsItemID(tmdbID int, index int) string {
	return fmt.Sprintf("tmdb_tv_%d_cfg_i%02d", tmdbID, index)
}

func embyTMDBSettingsItemName(index int) string {
	if index <= 0 || index > len(embyTMDBSettingsItems) {
		return ""
	}
	return embyTMDBSettingsItems[index-1]
}
