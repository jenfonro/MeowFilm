package routes

import (
	"regexp"
	"strings"
)

var (
	embyReCNSeasonSuffix = regexp.MustCompile(`(?i)\s*[（(\[]?\s*第\s*\d+\s*季\s*[）)\]]?\s*$`)
	embyReENSeasonSuffix = regexp.MustCompile(`(?i)\s*[（(\[]?\s*season\s*\d+\s*[）)\]]?\s*$`)
	embyReSSeasonSuffix  = regexp.MustCompile(`(?i)\s*[（(\[]?\s*s\s*\d+\s*[）)\]]?\s*$`)
)

func embyNormalizeTitleForTMDB(kind string, title string) string {
	k := strings.TrimSpace(strings.ToLower(kind))
	s := strings.TrimSpace(title)
	if s == "" {
		return ""
	}
	if k != "tv" {
		return s
	}
	orig := s
	s = embyReCNSeasonSuffix.ReplaceAllString(s, "")
	s = embyReENSeasonSuffix.ReplaceAllString(s, "")
	s = embyReSSeasonSuffix.ReplaceAllString(s, "")
	s = strings.TrimSpace(s)
	if s == "" {
		return strings.TrimSpace(orig)
	}
	return s
}

