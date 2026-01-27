package routes

import (
	"strings"

	"github.com/jenfonro/meowfilm/internal/auth"
	"github.com/jenfonro/meowfilm/internal/db"
)

func fetchHomeSites(database *db.DB) []map[string]any {
	sites := normalizeSitesFromJSON(database.GetSetting("video_source_sites"))
	if len(sites) == 0 {
		return []map[string]any{}
	}
	statusMap := parseJSONBoolMap(database.GetSetting("video_source_site_status"))
	homeMap := parseJSONBoolMap(database.GetSetting("video_source_site_home"))
	order := parseJSONStringArray(database.GetSetting("video_source_site_order"))
	ordered := applySiteOrder(sites, order)

	out := []map[string]any{}
	for _, s := range ordered {
		enabled, ok := statusMap[s.Key]
		if !ok {
			enabled = true
		}
		home, ok := homeMap[s.Key]
		if !ok {
			home = defaultHomeForSite(s)
		}
		if !enabled || !home {
			continue
		}
		out = append(out, map[string]any{"key": s.Key, "name": s.Name, "api": s.API})
	}
	return out
}

func fetchUserHomeSites(database *db.DB, u *auth.User) []map[string]any {
	if u == nil || u.ID <= 0 {
		return []map[string]any{}
	}

	var userAPIBase string
	_ = database.SQL().QueryRow(`SELECT cat_api_base FROM users WHERE id = ? LIMIT 1`, u.ID).Scan(&userAPIBase)
	hasUserAPI := strings.TrimSpace(userAPIBase) != ""

	// Normal users must configure their own CatPawOpen, otherwise treat as "no sites".
	if u.Role == "user" && !hasUserAPI {
		return []map[string]any{}
	}

	// Shared/admin users without their own CatPawOpen: use global home sites directly.
	if !hasUserAPI && (u.Role == "shared" || u.Role == "admin") {
		return fetchHomeSites(database)
	}

	var (
		catSites  string
		catStatus string
		catHome   string
		catOrder  string
	)
	_ = database.SQL().QueryRow(`
		SELECT cat_sites, cat_site_status, cat_site_home, cat_site_order
		FROM users WHERE id = ? LIMIT 1
	`, u.ID).Scan(&catSites, &catStatus, &catHome, &catOrder)

	sites := normalizeSitesFromJSON(catSites)
	statusMap := parseJSONBoolMap(catStatus)
	homeMap := parseJSONBoolMap(catHome)
	order := parseJSONStringArray(catOrder)
	ordered := applySiteOrder(sites, order)

	out := []map[string]any{}
	for _, s := range ordered {
		enabled, ok := statusMap[s.Key]
		if !ok {
			enabled = true
		}
		home, ok := homeMap[s.Key]
		if !ok {
			home = defaultHomeForSite(s)
		}
		if !enabled || !home {
			continue
		}
		out = append(out, map[string]any{"key": s.Key, "name": s.Name, "api": s.API})
	}
	return out
}
