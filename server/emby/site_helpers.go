package emby

import (
	"encoding/json"
	"sort"
	"strings"
)

type site struct {
	Key  string `json:"key"`
	Name string `json:"name"`
	API  string `json:"api"`
	Type *int   `json:"type,omitempty"`
}

func parseJSONMap(text string) map[string]any {
	var m map[string]any
	if err := json.Unmarshal([]byte(text), &m); err != nil || m == nil {
		return map[string]any{}
	}
	return m
}

func parseJSONBoolMap(text string) map[string]bool {
	raw := parseJSONMap(text)
	out := make(map[string]bool, len(raw))
	for k, v := range raw {
		if k == "" {
			continue
		}
		if b, ok := v.(bool); ok {
			out[k] = b
			continue
		}
		switch vv := v.(type) {
		case string:
			out[k] = strings.TrimSpace(vv) == "1" || strings.EqualFold(strings.TrimSpace(vv), "true")
		case float64:
			out[k] = vv != 0
		default:
			out[k] = false
		}
	}
	return out
}

func normalizeSitesFromJSON(text string) []site {
	var raw []map[string]any
	if err := json.Unmarshal([]byte(text), &raw); err != nil {
		return []site{}
	}
	out := make([]site, 0, len(raw))
	seen := map[string]struct{}{}
	for _, it := range raw {
		key, _ := it["key"].(string)
		api, _ := it["api"].(string)
		name, _ := it["name"].(string)
		key = strings.TrimSpace(key)
		api = strings.TrimSpace(api)
		if key == "" || api == "" {
			continue
		}
		if _, ok := seen[key]; ok {
			continue
		}
		seen[key] = struct{}{}

		var tptr *int
		switch v := it["type"].(type) {
		case float64:
			n := int(v)
			tptr = &n
		}
		out = append(out, site{Key: key, Name: name, API: api, Type: tptr})
	}
	return out
}

func applySiteOrder(sites []site, order []string) []site {
	if len(order) == 0 || len(sites) == 0 {
		return sites
	}
	idx := map[string]int{}
	for i, k := range order {
		idx[k] = i
	}
	type decorated struct {
		s site
		i int
		o int
	}
	ds := make([]decorated, 0, len(sites))
	for i, s := range sites {
		o, ok := idx[s.Key]
		if !ok {
			o = 1_000_000_000
		}
		ds = append(ds, decorated{s: s, i: i, o: o})
	}
	sort.Slice(ds, func(i, j int) bool {
		if ds[i].o != ds[j].o {
			return ds[i].o < ds[j].o
		}
		return ds[i].i < ds[j].i
	})
	out := make([]site, 0, len(ds))
	for _, d := range ds {
		out = append(out, d.s)
	}
	return out
}

func isConfigCenterSite(s site) bool {
	api := strings.TrimSpace(s.API)
	key := strings.ToLower(strings.TrimSpace(s.Key))
	return strings.Contains(api, "/spider/baseset/") || strings.HasSuffix(api, "/spider/baseset") || strings.Contains(key, "baseset")
}

func marshalJSON(v any) string {
	b, _ := json.Marshal(v)
	return string(b)
}
