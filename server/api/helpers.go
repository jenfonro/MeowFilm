package api

import (
	"net/url"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/server/catpawopen"
	mfnet "github.com/jenfonro/meowfilm/server/net"
)

func parseJSONMap(text string) map[string]any {
	return mfnet.ParseJSONMap(text)
}

func parseJSONBoolMap(text string) map[string]bool {
	return mfnet.ParseJSONBoolMap(text)
}

func parseJSONStringArray(text string) []string {
	return mfnet.ParseJSONStringArray(text)
}

func parseJSONStringMap(text string) map[string]string {
	return mfnet.ParseJSONStringMap(text)
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
	return mfnet.ParseAnyBool(v, def)
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

func readStrJSONBody(body map[string]any, key string) string {
	if body == nil || key == "" {
		return ""
	}
	v, ok := body[key]
	if !ok || v == nil {
		return ""
	}
	s, _ := v.(string)
	return strings.TrimSpace(s)
}
