package net

import "strings"

func NormalizeProxyBase(value string) string {
	raw := strings.TrimSpace(value)
	if raw == "" {
		return ""
	}
	if strings.HasSuffix(raw, "/") {
		return raw
	}
	if strings.HasSuffix(raw, "?") || strings.HasSuffix(raw, "&") || strings.HasSuffix(raw, "=") {
		return raw
	}
	return raw + "/"
}

func NormalizeImageURL(value string) string {
	raw := strings.TrimSpace(value)
	if raw == "" {
		return ""
	}
	if strings.HasPrefix(raw, "//") {
		return "https:" + raw
	}
	if strings.HasPrefix(raw, "http://") {
		return "https://" + strings.TrimPrefix(raw, "http://")
	}
	return raw
}

func NormalizeProxyMode(value string) string {
	raw := strings.TrimSpace(value)
	if raw == "" {
		return ""
	}
	parts := strings.FieldsFunc(raw, func(r rune) bool {
		return r == ',' || r == ' ' || r == '\t' || r == '\n' || r == '\r'
	})
	if len(parts) > 0 {
		return strings.TrimSpace(parts[0])
	}
	return raw
}
