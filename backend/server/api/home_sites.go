package api

import (
	"github.com/jenfonro/meowfilm/internal/db"
)

func fetchHomeSites(database *db.DB) []map[string]any {
	merged := mergeVideoSourceSites(database)
	if len(merged) == 0 {
		return []map[string]any{}
	}

	out := []map[string]any{}
	for _, s := range merged {
		enabled, _ := s["enabled"].(bool)
		home, _ := s["home"].(bool)
		if !enabled || !home {
			continue
		}
		key, _ := s["key"].(string)
		name, _ := s["name"].(string)
		api, _ := s["api"].(string)
		out = append(out, map[string]any{"key": key, "name": name, "api": api})
	}
	return out
}
