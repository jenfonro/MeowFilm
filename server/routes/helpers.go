package routes

import (
	"encoding/json"
	"net/url"
	"sort"
	"strconv"
	"strings"
)

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

func parseJSONStringArray(text string) []string {
	var arr []any
	if err := json.Unmarshal([]byte(text), &arr); err != nil {
		return []string{}
	}
	out := make([]string, 0, len(arr))
	seen := map[string]struct{}{}
	for _, v := range arr {
		s, ok := v.(string)
		if !ok {
			continue
		}
		s = strings.TrimSpace(s)
		if s == "" {
			continue
		}
		if _, ok := seen[s]; ok {
			continue
		}
		seen[s] = struct{}{}
		out = append(out, s)
	}
	return out
}

func parseJSONStringMap(text string) map[string]string {
	raw := parseJSONMap(text)
	out := make(map[string]string, len(raw))
	for k, v := range raw {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		s, ok := v.(string)
		if !ok {
			continue
		}
		val := strings.TrimSpace(s)
		if val == "" {
			continue
		}
		out[key] = val
	}
	return out
}

func parseBoolQuery(v string, def bool) bool {
	s := strings.ToLower(strings.TrimSpace(v))
	if s == "" {
		return def
	}
	if s == "1" || s == "true" || s == "yes" || s == "on" {
		return true
	}
	if s == "0" || s == "false" || s == "no" || s == "off" {
		return false
	}
	return def
}

func parseIntQuery(v string, def, min, max int) int {
	s := strings.TrimSpace(v)
	if s == "" {
		return def
	}
	n, err := strconv.Atoi(s)
	if err != nil {
		return def
	}
	if n < min {
		return min
	}
	if n > max {
		return max
	}
	return n
}

func normalizeHttpBase(value string) string {
	raw := strings.TrimSpace(value)
	if raw == "" {
		return ""
	}
	u, err := url.Parse(raw)
	if err != nil {
		return ""
	}
	if u.Scheme != "http" && u.Scheme != "https" {
		return ""
	}
	u.RawQuery = ""
	u.Fragment = ""
	u.Path = strings.TrimRight(u.Path, "/")
	return strings.TrimRight(u.String(), "/")
}

func normalizeCatPawOpenAPIBase(inputURL string) string {
	raw := strings.TrimSpace(inputURL)
	if raw == "" {
		return ""
	}
	u, err := url.Parse(raw)
	if err != nil {
		return ""
	}
	if u.Scheme != "http" && u.Scheme != "https" {
		return ""
	}

	path := u.Path
	if path == "" {
		path = "/"
	}

	// If user pasted a spider API (/spider/...), trim back to the service base.
	if idx := strings.Index(path, "/spider/"); idx >= 0 {
		path = path[:idx]
		if path == "" {
			path = "/"
		}
	}

	path = strings.TrimRight(path, "/")
	if strings.HasSuffix(path, "/spider") {
		path = strings.TrimSuffix(path, "/spider")
	}
	path = strings.TrimRight(path, "/")
	for _, suffix := range []string{"/full-config", "/config", "/website"} {
		if strings.HasSuffix(path, suffix) {
			path = strings.TrimSuffix(path, suffix)
			path = strings.TrimRight(path, "/")
		}
	}
	if path == "" {
		path = "/"
	}
	if !strings.HasSuffix(path, "/") {
		path += "/"
	}

	u.Path = path
	u.RawQuery = ""
	u.Fragment = ""
	return u.String()
}

func normalizeMountPath(value string) string {
	p := strings.TrimSpace(value)
	if p == "" {
		return ""
	}
	if !strings.HasPrefix(p, "/") {
		p = "/" + p
	}
	if !strings.HasSuffix(p, "/") {
		p += "/"
	}
	for strings.Contains(p, "//") {
		p = strings.ReplaceAll(p, "//", "/")
	}
	return p
}

type site struct {
	Key  string `json:"key"`
	Name string `json:"name"`
	API  string `json:"api"`
	Type *int   `json:"type,omitempty"`
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

func extractSpiderNameFromAPI(api string) string {
	raw := strings.TrimSpace(api)
	if raw == "" {
		return ""
	}
	// /spider/<name>/
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

func defaultHomeForSite(s site) bool {
	if extractSpiderNameFromAPI(s.API) == "baseset" {
		return false
	}
	return true
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
