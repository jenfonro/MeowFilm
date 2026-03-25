package tmdb

import (
	"errors"
	"regexp"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

var (
	reCNSeasonSuffix       = regexp.MustCompile(`(?i)\s*[（(\[]?\s*第\s*(?:\d+|[一二三四五六七八九十百零〇两]+)\s*季\s*[）)\]]?\s*$`)
	reENSeasonSuffix       = regexp.MustCompile(`(?i)\s*[（(\[]?\s*season\s*(?:\d+|[ivxlcdm]+)\s*[）)\]]?\s*$`)
	reSSeasonSuffix        = regexp.MustCompile(`(?i)\s*[（(\[]?\s*s\s*(?:\d+|[ivxlcdm]+)\s*[）)\]]?\s*$`)
	reTrailingDigitsSuffix = regexp.MustCompile(`\s*([0-9０-９]{1,2})\s*$`)
	reMovieSuffix          = regexp.MustCompile(`\s*(电影版|電影版|电影|電影)\s*$`)
	reHasHan               = regexp.MustCompile(`[\p{Han}]`)
)

func normalizeTitleForResolve(kind string, title string) string {
	k := strings.TrimSpace(strings.ToLower(kind))
	s := strings.TrimSpace(title)
	if s == "" {
		return ""
	}
	if k != "tv" {
		return s
	}
	orig := s
	s = reCNSeasonSuffix.ReplaceAllString(s, "")
	s = reENSeasonSuffix.ReplaceAllString(s, "")
	s = reSSeasonSuffix.ReplaceAllString(s, "")
	s = strings.TrimSpace(s)
	if s == "" {
		return strings.TrimSpace(orig)
	}
	return s
}

func toASCIIDigits(s string) string {
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

func NormalizeTitleCandidates(kind string, title string) []string {
	k := strings.TrimSpace(strings.ToLower(kind))
	base := strings.TrimSpace(normalizeTitleForResolve(k, title))
	if base == "" {
		return []string{}
	}

	out := []string{base}

	if reMovieSuffix.MatchString(base) {
		alt := strings.TrimSpace(reMovieSuffix.ReplaceAllString(base, ""))
		if alt != "" && alt != base && reHasHan.MatchString(alt) {
			out = append(out, alt)
		}
	}

	if k == "tv" {
		if m := reTrailingDigitsSuffix.FindStringSubmatch(base); len(m) >= 2 && strings.TrimSpace(m[1]) != "" {
			n, err := strconv.Atoi(toASCIIDigits(strings.TrimSpace(m[1])))
			if err == nil && n >= 2 && n <= 30 {
				alt := strings.TrimSpace(reTrailingDigitsSuffix.ReplaceAllString(base, ""))
				if alt != "" && alt != base && reHasHan.MatchString(alt) {
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

func ResolveByTitlesFromCache(database *db.DB, kind string, candidates []string, year int, lang string) (tmdbID int, matchedTitle string, err error) {
	k := strings.TrimSpace(kind)
	if k != "movie" && k != "tv" {
		return 0, "", errors.New("invalid args")
	}
	cands := make([]string, 0, len(candidates))
	seen := map[string]struct{}{}
	for _, candidate := range candidates {
		s := strings.TrimSpace(candidate)
		if s == "" {
			continue
		}
		key := strings.ToLower(s)
		if _, ok := seen[key]; ok {
			continue
		}
		seen[key] = struct{}{}
		cands = append(cands, s)
	}
	if len(cands) == 0 {
		return 0, "", nil
	}
	if database == nil {
		return 0, "", nil
	}
	tid, err := database.FindTMDBMediaByTitles(k, cands, year, defaultString(strings.TrimSpace(lang), "zh-CN"))
	if err != nil || tid <= 0 {
		return 0, "", err
	}
	return tid, cands[0], nil
}

func ResolveByTitleFromCache(database *db.DB, kind string, title string, year int, lang string) (tmdbID int, matchedTitle string, err error) {
	return ResolveByTitlesFromCache(database, kind, NormalizeTitleCandidates(kind, title), year, lang)
}
