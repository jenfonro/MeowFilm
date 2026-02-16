package netdisk

import (
	"encoding/json"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

type panLoginSettingsStore map[string]map[string]any


func readPanLoginSettings(database *db.DB) panLoginSettingsStore {
	root := parseJSONMap(database.GetSetting("pan_login_settings"))
	out := panLoginSettingsStore{}
	for k, v := range root {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		m, _ := v.(map[string]any)
		if m == nil {
			m = map[string]any{}
		}
		out[key] = m
	}
	return out
}

func writePanLoginSettings(database *db.DB, store panLoginSettingsStore) error {
	root := map[string]any{}
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
	b, err := json.Marshal(root)
	if err != nil {
		return err
	}
	return database.SetSetting("pan_login_settings", string(b))
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
