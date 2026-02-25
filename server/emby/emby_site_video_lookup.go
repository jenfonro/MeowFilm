package emby

import (
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func embyResolveSpiderAPIBySiteKey(database *db.DB, siteKey string) string {
	if database == nil {
		return ""
	}
	want := strings.TrimSpace(siteKey)
	if want == "" {
		return ""
	}
	sites, _ := database.ListVideoSourceSites()
	for _, s := range sites {
		if strings.TrimSpace(s.Key) != want {
			continue
		}
		return strings.TrimSpace(s.API)
	}
	return ""
}

