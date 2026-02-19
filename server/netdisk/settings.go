package netdisk

import (
	"encoding/json"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

type panLoginSettingsStore map[string]map[string]any


func readPanLoginSettings(database *db.DB) panLoginSettingsStore {
	out := panLoginSettingsStore{}
	if database == nil {
		return out
	}
	root, err := database.ReadPanLoginSettings()
	if err != nil || root == nil {
		return out
	}
	for k, m := range root {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		if m == nil {
			m = map[string]any{}
		}
		out[key] = m
	}
	return out
}

func writePanLoginSettings(database *db.DB, store panLoginSettingsStore) error {
	root := map[string]map[string]any{}
	for k, v := range store {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		if v == nil {
			v = map[string]any{}
		}
		root[key] = v
	}
	if database == nil {
		return nil
	}
	// Keep deterministic encoding for any callers that log the payload.
	_, _ = json.Marshal(root)
	return database.ReplacePanLoginSettings(root)
}

func getPanField(store panLoginSettingsStore, key string, field string) string {
	m := store[strings.TrimSpace(key)]
	if m == nil {
		return ""
	}
	v := m[strings.TrimSpace(field)]
	return strings.TrimSpace(toString(v))
}

func setPanField(store panLoginSettingsStore, key string, field string, value string) {
	k := strings.TrimSpace(key)
	if k == "" {
		return
	}
	m := store[k]
	if m == nil {
		m = map[string]any{}
	}
	m[strings.TrimSpace(field)] = strings.TrimSpace(value)
	store[k] = m
}
