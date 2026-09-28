package smart

import (
	"regexp"
	"strings"

	"github.com/jenfonro/meowfilm/server/magic"
)

func smartNormalizeAggKey(s string) string {
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

func smartMatchScore(qKey string, candKey string) int {
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
		posBoost := 60 - smartMinInt(60, idx)
		lenBoost := 40 - smartMinInt(40, smartMaxInt(0, len(candKey)-len(qKey)))
		return 800 + posBoost + lenBoost
	}
	return 0
}

func smartTitleLenForSort(title string) int {
	return len(magic.NormalizeForMatch(title))
}

func smartComputeMatchScore(query string, title string) int {
	n, err := magic.ComputeMatchScore(query, title)
	if err != nil {
		return 0
	}
	return n
}
