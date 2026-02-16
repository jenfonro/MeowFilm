package emby

import (
	"strings"
	"unicode"
)

func embyNormalizeForMatch(s string) string {
	in := strings.ToLower(strings.TrimSpace(s))
	if in == "" {
		return ""
	}
	var b strings.Builder
	b.Grow(len(in))
	for _, r := range in {
		// Match frontend normalizeForMatch: remove whitespace and zero-width chars.
		if unicode.IsSpace(r) || r == '\u200b' || r == '\u200c' || r == '\u200d' || r == '\ufeff' {
			continue
		}
		b.WriteRune(r)
	}
	return strings.TrimSpace(b.String())
}

func embyTitleLenForSort(title string) int {
	return len(embyNormalizeForMatch(title))
}

func embyComputeMatchScore(query string, title string) int {
	n, err := jsSearchComputeMatchScore(query, title)
	if err != nil {
		return 0
	}
	return n
}
