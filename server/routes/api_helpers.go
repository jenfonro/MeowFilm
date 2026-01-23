package routes

import (
	"encoding/json"
	"net/url"
	"strings"
)

type goProxyServer struct {
	Base string      `json:"base"`
	Pans goProxyPans `json:"pans"`
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
		var pans map[string]any
		switch vv := it.(type) {
		case string:
			base = normalizeHTTPBase(vv)
		case map[string]any:
			if b, ok := vv["base"].(string); ok {
				base = normalizeHTTPBase(b)
			}
			if p, ok := vv["pans"].(map[string]any); ok {
				pans = p
			}
		}
		if base == "" {
			continue
		}
		if _, ok := seen[base]; ok {
			continue
		}
		seen[base] = struct{}{}
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
		out = append(out, goProxyServer{Base: base, Pans: goProxyPans{Baidu: baidu, Quark: quark}})
	}
	return out
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

func normalizeAvailability(v string) string {
	raw := strings.TrimSpace(v)
	switch raw {
	case "valid", "invalid", "unknown", "unchecked":
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

func mergeSitesWithState(sites []site, statusMap map[string]bool, homeMap map[string]bool, order []string, availability map[string]any, searchMap map[string]bool) []map[string]any {
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
		row := map[string]any{
			"key":          s.Key,
			"name":         s.Name,
			"api":          s.API,
			"enabled":      enabled,
			"home":         home,
			"search":       searchEnabled,
			"availability": av,
		}
		if s.Type != nil {
			row["type"] = *s.Type
		}
		out = append(out, row)
	}
	return out
}

func normalizeContentKey(s string) string {
	return strings.TrimSpace(strings.ToLower(strings.Join(strings.Fields(s), "")))
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

func isAllowedDoubanImageHost(hostname string) bool {
	host := strings.ToLower(strings.TrimSpace(hostname))
	if host == "" {
		return false
	}
	if strings.HasPrefix(host, "img") && strings.HasSuffix(host, ".doubanio.com") {
		mid := strings.TrimSuffix(strings.TrimPrefix(host, "img"), ".doubanio.com")
		if mid == "" {
			return false
		}
		for i := 0; i < len(mid); i++ {
			if mid[i] < '0' || mid[i] > '9' {
				return false
			}
		}
		return true
	}
	switch host {
	case "img3.doubanio.com", "img.doubanio.cmliussss.net", "img.doubanio.cmliussss.com":
		return true
	default:
		return false
	}
}
