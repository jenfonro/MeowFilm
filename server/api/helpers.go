package api

import (
	"encoding/json"
	"net/url"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/server/catpawopen"
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
	return catpawopen.NormalizeAPIBase(inputURL)
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
