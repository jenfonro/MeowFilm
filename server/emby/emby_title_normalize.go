package emby

import (
	"regexp"
	"strconv"
	"strings"
)

var (
	embyReCNSeasonSuffix = regexp.MustCompile(`(?i)\s*[（(\[]?\s*第\s*(?:\d+|[一二三四五六七八九十百零〇两]+)\s*季\s*[）)\]]?\s*$`)
	embyReENSeasonSuffix = regexp.MustCompile(`(?i)\s*[（(\[]?\s*season\s*(?:\d+|[ivxlcdm]+)\s*[）)\]]?\s*$`)
	embyReSSeasonSuffix  = regexp.MustCompile(`(?i)\s*[（(\[]?\s*s\s*(?:\d+|[ivxlcdm]+)\s*[）)\]]?\s*$`)

	// Common vendor naming: append "2/3/..." directly to the end of a CJK title to represent seasons.
	// We treat this as a *fallback* query candidate (not a default rewrite) to avoid breaking titles
	// that genuinely end with digits (e.g. "24").
	embyReTrailingDigitsSuffix = regexp.MustCompile(`\s*([0-9０-９]{1,2})\s*$`)

	// Common suffix noise: "...电影/電影/电影版/電影版".
	embyReMovieSuffix = regexp.MustCompile(`\s*(电影版|電影版|电影|電影)\s*$`)

	embyReHasHan = regexp.MustCompile(`[\p{Han}]`)
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

// embyNormalizeTitleForTMDBCandidates returns a small list of normalized queries to try, in order.
// This keeps the original normalized title first, and appends conservative fallbacks that remove
// common suffix noise (e.g. "xxx2" -> "xxx", "xxx电影" -> "xxx") when safe.
func embyNormalizeTitleForTMDBCandidates(kind string, title string) []string {
	k := strings.TrimSpace(strings.ToLower(kind))
	base := strings.TrimSpace(embyNormalizeTitleForTMDB(k, title))
	if base == "" {
		return []string{}
	}

	out := []string{base}

	// Fallback: strip trailing "电影" suffixes (applies to both tv/movie searches).
	if embyReMovieSuffix.MatchString(base) {
		alt := strings.TrimSpace(embyReMovieSuffix.ReplaceAllString(base, ""))
		if alt != "" && alt != base && embyReHasHan.MatchString(alt) {
			out = append(out, alt)
		}
	}

	// Fallback: strip trailing season digits for tv only (e.g. "喜人奇妙夜2" => "喜人奇妙夜").
	if k == "tv" {
		if m := embyReTrailingDigitsSuffix.FindStringSubmatch(base); len(m) >= 2 && strings.TrimSpace(m[1]) != "" {
			n, err := strconv.Atoi(embyToASCIIDigits(strings.TrimSpace(m[1])))
			if err == nil && n >= 2 && n <= 30 {
				alt := strings.TrimSpace(embyReTrailingDigitsSuffix.ReplaceAllString(base, ""))
				if alt != "" && alt != base && embyReHasHan.MatchString(alt) {
					out = append(out, alt)
				}
			}
		}
	}

	seen := map[string]bool{}
	dedup := make([]string, 0, len(out))
	for _, q := range out {
		qq := strings.TrimSpace(q)
		if qq == "" || seen[qq] {
			continue
		}
		seen[qq] = true
		dedup = append(dedup, qq)
	}
	return dedup
}

func embyToASCIIDigits(s string) string {
	if s == "" {
		return ""
	}
	var b strings.Builder
	b.Grow(len(s))
	for _, r := range s {
		if r >= '０' && r <= '９' {
			b.WriteRune('0' + (r - '０'))
			continue
		}
		b.WriteRune(r)
	}
	return b.String()
}
