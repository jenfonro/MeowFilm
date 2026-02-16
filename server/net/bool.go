package net

import "strings"

func ParseBoolQuery(v string, def bool) bool {
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

func ParseAnyBool(v any, def bool) bool {
	switch vv := v.(type) {
	case bool:
		return vv
	case float64:
		return vv != 0
	case string:
		return ParseBoolQuery(vv, def)
	default:
		return def
	}
}
