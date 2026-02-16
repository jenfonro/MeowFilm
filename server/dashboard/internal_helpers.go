package dashboard

import (
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/url"
	"sort"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/server/catpawopen"
)

func writeJSON(w http.ResponseWriter, status int, payload any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	enc := json.NewEncoder(w)
	enc.SetEscapeHTML(false)
	_ = enc.Encode(payload)
}

func readJSONLoose(r *http.Request, dst any) error {
	if r == nil || dst == nil {
		return errors.New("invalid args")
	}
	defer func() { _ = r.Body.Close() }()
	dec := json.NewDecoder(io.LimitReader(r.Body, 1<<20))
	if err := dec.Decode(dst); err != nil {
		return err
	}
	return nil
}

func methodNotAllowed(w http.ResponseWriter) {
	writeJSON(w, http.StatusMethodNotAllowed, map[string]any{"success": false, "message": "Method not allowed"})
}

func parseForm(r *http.Request) {
	if r == nil {
		return
	}
	_ = r.ParseForm()
}

func boolFromForm(v string) bool {
	s := strings.ToLower(strings.TrimSpace(v))
	return s == "1" || s == "true" || s == "on" || s == "yes"
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

func parseAnyBool(v any, def bool) bool {
	switch vv := v.(type) {
	case bool:
		return vv
	case float64:
		return vv != 0
	case string:
		return parseBoolQuery(vv, def)
	default:
		return def
	}
}

func defaultString(v, def string) string {
	if strings.TrimSpace(v) == "" {
		return def
	}
	return v
}

func normalizeHTTPBase(value string) string {
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
	return catpawopen.NormalizeAPIBase(inputURL)
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

func normalizeAvailability(v string) string {
	raw := strings.TrimSpace(v)
	switch raw {
	case "valid", "invalid", "unknown", "unchecked", "skipped", "category_error", "search_error":
		return raw
	default:
		return "unchecked"
	}
}

func isConfigCenterSite(s site) bool {
	api := strings.TrimSpace(s.API)
	key := strings.ToLower(strings.TrimSpace(s.Key))
	return strings.Contains(api, "/spider/baseset/") || strings.HasSuffix(api, "/spider/baseset") || strings.Contains(key, "baseset")
}

func mergeSitesWithState(sites []site, statusMap map[string]bool, homeMap map[string]bool, order []string, availability map[string]any, searchMap map[string]bool, errorMap map[string]string) []map[string]any {
	ordered := applySiteOrder(sites, order)
	out := make([]map[string]any, 0, len(ordered))
	for _, s := range ordered {
		enabled, ok := statusMap[s.Key]
		if !ok {
			enabled = true
		}
		home, ok := homeMap[s.Key]
		if !ok {
			home = defaultHomeForSite(s)
		}
		searchEnabled, ok := searchMap[s.Key]
		if !ok {
			searchEnabled = true
		}
		if isConfigCenterSite(s) {
			searchEnabled = false
		}
		av := "unchecked"
		if v, ok := availability[s.Key]; ok {
			if sv, ok := v.(string); ok {
				av = normalizeAvailability(sv)
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

type goProxyServer struct {
	Name        string      `json:"name"`
	DisplayName string      `json:"displayName"`
	Base        string      `json:"base"`
	Pans        goProxyPans `json:"pans"`
}

type goProxyPans struct {
	Baidu bool `json:"baidu"`
	Quark bool `json:"quark"`
}

func normalizeGoProxyServers(value string) []goProxyServer {
	var list []any
	if err := json.Unmarshal([]byte(value), &list); err != nil {
		return []goProxyServer{}
	}
	out := []goProxyServer{}
	seen := map[string]struct{}{}
	for _, it := range list {
		var base string
		var name string
		var displayName string
		var pans map[string]any
		switch vv := it.(type) {
		case string:
			base = normalizeHTTPBase(vv)
		case map[string]any:
			if n, ok := vv["name"].(string); ok {
				name = strings.TrimSpace(n)
			}
			if n, ok := vv["displayName"].(string); ok {
				displayName = strings.TrimSpace(n)
			}
			if b, ok := vv["base"].(string); ok {
				base = normalizeHTTPBase(b)
			}
			if base == "" {
				if b, ok := vv["apiBase"].(string); ok {
					base = normalizeHTTPBase(b)
				} else if b, ok := vv["api"].(string); ok {
					base = normalizeHTTPBase(b)
				} else if b, ok := vv["url"].(string); ok {
					base = normalizeHTTPBase(b)
				}
			}
			if p, ok := vv["pans"].(map[string]any); ok {
				pans = p
			}
		}
		if base == "" {
			continue
		}
		key := strings.ToLower(base)
		if _, ok := seen[key]; ok {
			continue
		}
		seen[key] = struct{}{}
		if name == "" {
			if u, err := url.Parse(base); err == nil {
				name = strings.TrimSpace(u.Hostname())
				if name == "" {
					name = strings.TrimSpace(u.Host)
				}
			}
		}
		if displayName == "" {
			displayName = name
		}
		baidu := true
		quark := true
		if pans != nil {
			if v, ok := pans["baidu"]; ok {
				baidu = parseAnyBool(v, true)
			}
			if v, ok := pans["quark"]; ok {
				quark = parseAnyBool(v, true)
			}
		}
		out = append(out, goProxyServer{
			Name:        name,
			DisplayName: displayName,
			Base:        base,
			Pans:        goProxyPans{Baidu: baidu, Quark: quark},
		})
	}
	return out
}

func normalizeContentKey(s string) string {
	return strings.TrimSpace(strings.ToLower(strings.Join(strings.Fields(s), "")))
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
