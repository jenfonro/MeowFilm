package routes

import (
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func normalizeSourceExtractPriority(raw string) string {
	s := strings.TrimSpace(raw)
	if s == "" {
		return "无"
	}

	if s == "无" || s == "网盘" || s == "关键字" {
		return s
	}
	return "无"
}

func resolveSearchCoverSite(sites []map[string]any, preferredRaw string) string {
	preferred := strings.TrimSpace(preferredRaw)
	keySet := map[string]struct{}{}
	enabledFirst := ""
	first := ""
	for _, s := range sites {
		k, _ := s["key"].(string)
		k = strings.TrimSpace(k)
		if k == "" {
			continue
		}
		if first == "" {
			first = k
		}
		keySet[k] = struct{}{}
		if enabledFirst == "" {
			enabled, _ := s["enabled"].(bool)
			if enabled {
				enabledFirst = k
			}
		}
	}
	if preferred != "" {
		if _, ok := keySet[preferred]; ok {
			return preferred
		}
	}
	if enabledFirst != "" {
		return enabledFirst
	}
	return first
}

func mergeVideoSourceSites(database *db.DB) []map[string]any {
	sites := normalizeSitesFromJSON(database.GetSetting("video_source_sites"))
	statusMap := parseJSONBoolMap(database.GetSetting("video_source_site_status"))
	homeMap := parseJSONBoolMap(database.GetSetting("video_source_site_home"))
	searchMap := parseJSONBoolMap(database.GetSetting("video_source_site_search"))
	order := parseJSONStringArray(database.GetSetting("video_source_site_order"))
	availability := parseJSONMap(database.GetSetting("video_source_site_availability"))
	errorMap := parseJSONStringMap(database.GetSetting("video_source_site_error"))
	return mergeSitesWithState(sites, statusMap, homeMap, order, availability, searchMap, errorMap)
}
