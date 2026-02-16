package net

import (
	"encoding/json"
	"strings"
)

func MarshalJSON(v any) string {
	b, _ := json.Marshal(v)
	return string(b)
}

func ParseJSONMap(text string) map[string]any {
	var m map[string]any
	if err := json.Unmarshal([]byte(text), &m); err != nil || m == nil {
		return map[string]any{}
	}
	return m
}

func ParseJSONBoolMap(text string) map[string]bool {
	raw := ParseJSONMap(text)
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

func ParseJSONStringArray(text string) []string {
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

func ParseJSONStringMap(text string) map[string]string {
	raw := ParseJSONMap(text)
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
