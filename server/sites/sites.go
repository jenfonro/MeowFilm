package sites

import (
	"encoding/json"
	"sort"
	"strings"

	"github.com/jenfonro/meowfilm/server/net"
)

type Site struct {
	Key  string `json:"key"`
	Name string `json:"name"`
	API  string `json:"api"`
	Type *int   `json:"type,omitempty"`
}

func NormalizeAvailability(v string) string {
	raw := strings.TrimSpace(v)
	switch raw {
	case "valid", "invalid", "unknown", "unchecked", "skipped", "category_error", "search_error":
		return raw
	default:
		return "unchecked"
	}
}

func ParseJSONMap(text string) map[string]any {
	return net.ParseJSONMap(text)
}

func MarshalJSON(v any) string {
	return net.MarshalJSON(v)
}

func ParseJSONBoolMap(text string) map[string]bool {
	return net.ParseJSONBoolMap(text)
}

func ParseJSONStringMap(text string) map[string]string {
	return net.ParseJSONStringMap(text)
}

func ParseAvailabilityJSON(text string) map[string]string {
	raw := ParseJSONMap(text)
	out := map[string]string{}
	for k, v := range raw {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		s, _ := v.(string)
		out[key] = NormalizeAvailability(s)
	}
	return out
}

func ParseSitesFromJSON(text string) []Site {
	var raw []map[string]any
	if err := json.Unmarshal([]byte(text), &raw); err != nil {
		return []Site{}
	}
	out := make([]Site, 0, len(raw))
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
		out = append(out, Site{Key: key, Name: name, API: api, Type: tptr})
	}
	return out
}

func ApplySiteOrder(sites []Site, order []string) []Site {
	if len(order) == 0 || len(sites) == 0 {
		return sites
	}
	idx := map[string]int{}
	for i, k := range order {
		idx[k] = i
	}
	type decorated struct {
		s Site
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
	out := make([]Site, 0, len(ds))
	for _, d := range ds {
		out = append(out, d.s)
	}
	return out
}

func IsConfigCenterSite(s Site) bool {
	api := strings.TrimSpace(s.API)
	key := strings.ToLower(strings.TrimSpace(s.Key))
	return strings.Contains(api, "/spider/baseset/") || strings.HasSuffix(api, "/spider/baseset") || strings.Contains(key, "baseset")
}

func ExtractSpiderNameFromAPI(api string) string {
	raw := strings.TrimSpace(api)
	if raw == "" {
		return ""
	}
	const marker = "/spider/"
	i := strings.Index(raw, marker)
	if i < 0 {
		return ""
	}
	rest := raw[i+len(marker):]
	j := strings.Index(rest, "/")
	if j < 0 {
		return ""
	}
	return rest[:j]
}

func DefaultHomeForSite(s Site) bool {
	if ExtractSpiderNameFromAPI(s.API) == "baseset" {
		return false
	}
	return true
}

func MergeSitesWithState(sites []Site, statusMap map[string]bool, homeMap map[string]bool, order []string, availability map[string]any, searchMap map[string]bool, errorMap map[string]string) []map[string]any {
	ordered := ApplySiteOrder(sites, order)
	out := make([]map[string]any, 0, len(ordered))
	for _, s := range ordered {
		enabled, ok := statusMap[s.Key]
		if !ok {
			enabled = true
		}
		home, ok := homeMap[s.Key]
		if !ok {
			home = DefaultHomeForSite(s)
		}
		searchEnabled, ok := searchMap[s.Key]
		if !ok {
			searchEnabled = true
		}
		if IsConfigCenterSite(s) {
			searchEnabled = false
		}
		av := "unchecked"
		if v, ok := availability[s.Key]; ok {
			if sv, ok := v.(string); ok {
				av = NormalizeAvailability(sv)
			}
		}
		errMsg := ""
		if v, ok := errorMap[s.Key]; ok {
			errMsg = strings.TrimSpace(v)
		}
		row := map[string]any{
			"key":          s.Key,
			"name":         s.Name,
			"api":          s.API,
			"enabled":      enabled,
			"home":         home,
			"search":       searchEnabled,
			"availability": av,
		}
		if errMsg != "" {
			row["error"] = errMsg
		}
		if s.Type != nil {
			row["type"] = *s.Type
		}
		out = append(out, row)
	}
	return out
}

type ReconciledState struct {
	Sites        []Site
	Status       map[string]bool
	Home         map[string]bool
	Search       map[string]bool
	Order        []string
	Availability map[string]string
}

func NormalizeSitesSlice(input []Site) []Site {
	out := make([]Site, 0, len(input))
	seen := map[string]struct{}{}
	for _, s := range input {
		key := strings.TrimSpace(s.Key)
		api := strings.TrimSpace(s.API)
		if key == "" || api == "" {
			continue
		}
		if _, ok := seen[key]; ok {
			continue
		}
		seen[key] = struct{}{}
		out = append(out, Site{Key: key, Name: s.Name, API: api, Type: s.Type})
	}
	return out
}

func ReconcileSites(nextSites []Site, prevStatus map[string]bool, prevHome map[string]bool, prevSearch map[string]bool, prevOrder []string, prevAvailability map[string]string) ReconciledState {
	normalizedNew := NormalizeSitesSlice(nextSites)
	keysInNewOrder := make([]string, 0, len(normalizedNew))
	newKeySet := map[string]struct{}{}
	for _, s := range normalizedNew {
		keysInNewOrder = append(keysInNewOrder, s.Key)
		newKeySet[s.Key] = struct{}{}
	}

	nextStatus := map[string]bool{}
	for k, v := range prevStatus {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		if _, ok := newKeySet[key]; !ok {
			continue
		}
		nextStatus[key] = v
	}

	nextHome := map[string]bool{}
	for k, v := range prevHome {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		if _, ok := newKeySet[key]; !ok {
			continue
		}
		nextHome[key] = v
	}

	nextSearch := map[string]bool{}
	for k, v := range prevSearch {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		if _, ok := newKeySet[key]; !ok {
			continue
		}
		nextSearch[key] = v
	}

	nextAvailability := map[string]string{}
	for k, v := range prevAvailability {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		if _, ok := newKeySet[key]; !ok {
			continue
		}
		nextAvailability[key] = NormalizeAvailability(v)
	}

	for _, s := range normalizedNew {
		if _, ok := nextStatus[s.Key]; !ok {
			nextStatus[s.Key] = true
		}
		if _, ok := nextHome[s.Key]; !ok {
			nextHome[s.Key] = DefaultHomeForSite(s)
		}
		if _, ok := nextSearch[s.Key]; !ok {
			nextSearch[s.Key] = true
		}
		if _, ok := nextAvailability[s.Key]; !ok {
			nextAvailability[s.Key] = "unchecked"
		}
	}

	prevOrderFiltered := []string{}
	for _, k := range prevOrder {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		if _, ok := newKeySet[key]; !ok {
			continue
		}
		prevOrderFiltered = append(prevOrderFiltered, key)
	}
	nextOrder := []string{}
	seenOrder := map[string]struct{}{}
	for _, key := range prevOrderFiltered {
		if _, ok := seenOrder[key]; ok {
			continue
		}
		seenOrder[key] = struct{}{}
		nextOrder = append(nextOrder, key)
	}

	lastIndex := -1
	for _, key := range keysInNewOrder {
		idx := indexOf(nextOrder, key)
		if idx >= 0 {
			lastIndex = idx
			continue
		}
		insertAt := lastIndex + 1
		if insertAt < 0 {
			insertAt = 0
		}
		if insertAt > len(nextOrder) {
			insertAt = len(nextOrder)
		}
		nextOrder = append(nextOrder[:insertAt], append([]string{key}, nextOrder[insertAt:]...)...)
		lastIndex = insertAt
	}

	return ReconciledState{
		Sites:        normalizedNew,
		Status:       nextStatus,
		Home:         nextHome,
		Search:       nextSearch,
		Order:        nextOrder,
		Availability: nextAvailability,
	}
}

func indexOf(list []string, v string) int {
	for i := 0; i < len(list); i++ {
		if list[i] == v {
			return i
		}
	}
	return -1
}
