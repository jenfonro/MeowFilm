package smart

import (
	"regexp"
	"strings"
)

var (
	episodeMarkerRe = regexp.MustCompile(`(?i)(?:ep|episode|e)\s*\d{1,5}|第\s*\d+\s*集`)
	seasonMarkerRe  = regexp.MustCompile(`(?i)s\s*\d{1,2}|第\s*\d+\s*季`)
)

// Align with frontend: pan_mock provider lookup only depends on label/flag.
func smartPanMockProviderFromLabel(label string) string {
	return smartPlayFlagProviderID(strings.TrimSpace(label))
}

func isAllDigits(s string) bool {
	if s == "" {
		return false
	}
	for _, r := range s {
		if r < '0' || r > '9' {
			return false
		}
	}
	return true
}

func isAlphaNumOnly(s string) bool {
	if s == "" {
		return false
	}
	for _, r := range s {
		if (r >= '0' && r <= '9') || (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') {
			continue
		}
		return false
	}
	return true
}

func scoreEpisodeDisplayName(name string, titleLower string) int {
	text := strings.TrimSpace(name)
	if text == "" {
		return -999
	}
	lower := strings.ToLower(text)
	score := 0
	if episodeMarkerRe.MatchString(text) {
		score += 5
	}
	if seasonMarkerRe.MatchString(text) {
		score += 4
	}
	if strings.Contains(lower, "2160p") || strings.Contains(lower, "4k") || strings.Contains(lower, "1080p") || strings.Contains(lower, "720p") {
		score += 2
	}
	if titleLower != "" && strings.Contains(lower, titleLower) {
		score += 2
	}
	if len([]rune(text)) < 6 {
		score -= 3
	}
	if isAllDigits(text) && len(text) >= 10 {
		score -= 5
	}
	if isAlphaNumOnly(text) && len(text) >= 24 {
		score -= 5
	}
	if strings.HasSuffix(strings.ToLower(text), ".mkv") ||
		strings.HasSuffix(strings.ToLower(text), ".mp4") ||
		strings.HasSuffix(strings.ToLower(text), ".avi") ||
		strings.HasSuffix(strings.ToLower(text), ".flv") ||
		strings.HasSuffix(strings.ToLower(text), ".mov") ||
		strings.HasSuffix(strings.ToLower(text), ".wmv") ||
		strings.HasSuffix(strings.ToLower(text), ".m4v") {
		score += 1
	}
	return score
}

func pickEpisodeDisplayName(displayName string, fileName string, titleLower string, preferFile bool) string {
	name := strings.TrimSpace(displayName)
	file := strings.TrimSpace(fileName)
	if preferFile && file != "" {
		return file
	}
	if name == "" {
		return file
	}
	if file == "" {
		return name
	}
	scoreName := scoreEpisodeDisplayName(name, titleLower)
	scoreFile := scoreEpisodeDisplayName(file, titleLower)
	if scoreName >= scoreFile {
		return name
	}
	return file
}
