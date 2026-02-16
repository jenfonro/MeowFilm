package emby

import (
	"regexp"
	"strings"
)

func embyNormalizeAggKey(s string) string {
	raw := strings.ToLower(strings.TrimSpace(s))
	if raw == "" {
		return ""
	}
	re := regexp.MustCompile(`[\s\.\-_,，:：;；!！?？·•/\\|]+`)
	raw = re.ReplaceAllString(raw, "")
	raw = strings.ReplaceAll(raw, "\u200b", "")
	raw = strings.ReplaceAll(raw, "\u200c", "")
	raw = strings.ReplaceAll(raw, "\u200d", "")
	raw = strings.ReplaceAll(raw, "\ufeff", "")
	return strings.TrimSpace(raw)
}

func embyMatchScore(qKey string, candKey string) int {
	if qKey == "" || candKey == "" {
		return 0
	}
	if candKey == qKey {
		return 1000
	}
	if strings.HasPrefix(candKey, qKey) {
		return 900
	}
	if idx := strings.Index(candKey, qKey); idx >= 0 {
		posBoost := 60 - matchMinInt(60, idx)
		lenBoost := 40 - matchMinInt(40, matchMaxInt(0, len(candKey)-len(qKey)))
		return 800 + posBoost + lenBoost
	}
	return 0
}

func matchMinInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}

func matchMaxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}
