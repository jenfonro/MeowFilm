//go:build !(cgo && (linux || darwin))

package routes

import (
	"strings"
)

func jsSearchComputeMatchScore(q string, title string) (int, error) {
	qRaw := strings.TrimSpace(q)
	qNorm := embyNormalizeForMatch(qRaw)
	name := embyNormalizeForMatch(title)
	if qNorm == "" || name == "" {
		return 0, nil
	}
	if name == qNorm {
		return 1000, nil
	}
	if strings.HasPrefix(name, qNorm) {
		return 900, nil
	}
	if idx := strings.Index(name, qNorm); idx >= 0 {
		posBoost := 60 - minInt(60, idx)
		lenBoost := 40 - minInt(40, maxInt(0, len(name)-len(qNorm)))
		return 800 + posBoost + lenBoost, nil
	}
	tokens := strings.Fields(strings.ToLower(qRaw))
	if len(tokens) >= 2 {
		hit := 0
		for _, t := range tokens {
			if t != "" && strings.Contains(name, t) {
				hit++
			}
		}
		if hit > 0 {
			return 600 + hit*20, nil
		}
	}
	return 0, nil
}
