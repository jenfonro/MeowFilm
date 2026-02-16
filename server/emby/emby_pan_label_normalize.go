package emby

import "strings"

// embyNormalizePanDisplayLabel normalizes a play source label for display only.
// Important: this must NOT be used for playback "flag" since spiders often require the original label.
func embyNormalizePanDisplayLabel(raw string) string {
	s := strings.TrimSpace(raw)
	if s == "" {
		return ""
	}
	// Keep only the provider marker for mobile UI (e.g. "百度原画-xxx" => "百度").
	for _, k := range []string{"百度", "夸父", "天意", "逸动", "优夕"} {
		if strings.Contains(s, k) {
			return k
		}
	}
	return s
}
