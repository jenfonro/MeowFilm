package smart

import (
	"regexp"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

func smartComputePriorityMatch(textLower string, tokensLower []string) smartPriorityMatch {
	text := strings.TrimSpace(textLower)
	tokens := tokensLower
	idx := make([]int, 0, 4)
	for i := 0; i < len(tokens); i++ {
		t := strings.TrimSpace(tokens[i])
		if t == "" {
			continue
		}
		if strings.Contains(text, t) {
			idx = append(idx, i)
		}
	}
	return smartPriorityMatch{Count: len(idx), Indices: idx}
}

func smartComparePriorityMatch(a smartPriorityMatch, b smartPriorityMatch) int {
	if a.Count != b.Count {
		return b.Count - a.Count // more matches first
	}
	n := smartMinInt(len(a.Indices), len(b.Indices))
	for i := 0; i < n; i++ {
		if a.Indices[i] != b.Indices[i] {
			return a.Indices[i] - b.Indices[i] // earlier token first
		}
	}
	return len(a.Indices) - len(b.Indices)
}

func smartParseChineseNumeralToInt(text string) int {
	raw := strings.TrimSpace(text)
	if raw == "" {
		return 0
	}
	s := strings.ReplaceAll(raw, " ", "")
	s = strings.ReplaceAll(s, "两", "二")
	s = strings.ReplaceAll(s, "〇", "零")
	if s == "" {
		return 0
	}
	digit := func(ch rune) int {
		switch ch {
		case '零':
			return 0
		case '一':
			return 1
		case '二':
			return 2
		case '三':
			return 3
		case '四':
			return 4
		case '五':
			return 5
		case '六':
			return 6
		case '七':
			return 7
		case '八':
			return 8
		case '九':
			return 9
		default:
			return -1
		}
	}
	parseSection := func(sec string) (int, bool) {
		total := 0
		num := 0
		for _, ch := range sec {
			if d := digit(ch); d >= 0 {
				num = d
				continue
			}
			unit := 0
			switch ch {
			case '十':
				unit = 10
			case '百':
				unit = 100
			case '千':
				unit = 1000
			case '零':
				unit = 0
			default:
				return 0, false
			}
			if unit == 0 {
				continue
			}
			if num == 0 {
				num = 1
			}
			total += num * unit
			num = 0
		}
		return total + num, true
	}
	if strings.Contains(s, "万") {
		parts := strings.Split(s, "万")
		if len(parts) < 1 || len(parts) > 2 {
			return 0
		}
		left := parts[0]
		right := ""
		if len(parts) == 2 {
			right = parts[1]
		}
		a := 0
		if left != "" {
			v, ok := parseSection(left)
			if !ok {
				return 0
			}
			a = v
		}
		b := 0
		if right != "" {
			v, ok := parseSection(right)
			if !ok {
				return 0
			}
			b = v
		}
		n := a*10000 + b
		if n > 0 {
			return n
		}
		return 0
	}
	if v, ok := parseSection(s); ok && v > 0 {
		return v
	}
	return 0
}

func smartExtractRawNamesFromEpisodeURL(episodeURL string) []string {
	raw := episodeURL
	if strings.TrimSpace(raw) == "" {
		return nil
	}
	stripMeta := func(s string) string {
		out := strings.TrimSpace(s)
		if out == "" {
			return ""
		}
		if idx := strings.Index(out, "$"); idx > 0 {
			out = out[:idx]
		}
		out = regexp.MustCompile(`#\[[^\]]*\]\s*$`).ReplaceAllString(out, "")
		out = regexp.MustCompile(`\s*\[\s*\d+(?:\.\d+)?\s*(?:[KMGT]?B)\s*\]\s*$`).ReplaceAllString(out, "")
		out = regexp.MustCompile(`^【[^】]{1,16}】\s*`).ReplaceAllString(out, "")
		return strings.TrimSpace(out)
	}
	pickSuffixParts := func() (parts []string, delim string) {
		if strings.Contains(raw, "***") {
			parts := strings.Split(raw, "***")
			if len(parts) >= 2 {
				return parts[1:], "***"
			}
			return nil, ""
		}
		if strings.Contains(raw, "|||") {
			parts := strings.Split(raw, "|||")
			if len(parts) >= 2 {
				return parts[1:], "|||"
			}
			return nil, ""
		}
		pipeParts := strings.Split(raw, "|")
		if len(pipeParts) >= 4 {
			return []string{pipeParts[len(pipeParts)-1]}, "|"
		}
		return nil, ""
	}
	suffixParts, delim := pickSuffixParts()
	if len(suffixParts) == 0 {
		// Fallback for ids that embed filename as the last "*" segment (e.g. Tianyi: "<fileId>*<shareId>*<name>").
		if strings.Contains(raw, "*") {
			parts := strings.Split(raw, "*")
			if len(parts) > 0 {
				last := stripMeta(parts[len(parts)-1])
				if last != "" {
					return []string{last}
				}
			}
		}
		return nil
	}
	if delim == "" {
		delim = "***"
	}
	joined := stripMeta(strings.Join(suffixParts, delim))
	out := make([]string, 0, 8)
	push := func(s string) {
		t := stripMeta(s)
		if t == "" {
			return
		}
		for _, existed := range out {
			if existed == t {
				return
			}
		}
		out = append(out, t)
	}
	if joined != "" {
		for _, p := range strings.Split(joined, "#") {
			push(p)
		}
	}
	for _, seg := range suffixParts {
		for _, p := range strings.Split(seg, "#") {
			push(p)
		}
	}
	return out
}

func smartBuildCandidateLowerText(texts []string) string {
	seen := map[string]bool{}
	out := make([]string, 0, len(texts))
	for _, t := range texts {
		tt := strings.TrimSpace(t)
		if tt == "" {
			continue
		}
		key := strings.ToLower(tt)
		if key == "" || seen[key] {
			continue
		}
		seen[key] = true
		out = append(out, key)
	}
	return strings.TrimSpace(strings.Join(out, " "))
}

func smartSplitDisplayPathSegments(display string) []string {
	raw := strings.TrimSpace(display)
	if raw == "" {
		return nil
	}
	raw = strings.ReplaceAll(raw, "\\", "/")
	raw = strings.Trim(raw, "/")
	if raw == "" {
		return nil
	}
	parts := strings.Split(raw, "/")
	out := make([]string, 0, len(parts))
	for _, p := range parts {
		t := strings.TrimSpace(p)
		if t == "" {
			continue
		}
		out = append(out, t)
	}
	if len(out) == 0 {
		return nil
	}
	return out
}

func smartEpisodePathLayers(ep catpawrunner.Episode) (fileName string, currentDir string, parentDir string) {
	rawNames := smartExtractRawNamesFromEpisodeURL(ep.URL)
	for _, n := range rawNames {
		t := strings.TrimSpace(n)
		if t == "" {
			continue
		}
		fileName = t
		break
	}
	segs := smartSplitDisplayPathSegments(ep.Name)
	if len(segs) > 0 {
		currentDir = segs[len(segs)-1]
	}
	if len(segs) > 1 {
		parentDir = segs[len(segs)-2]
	}
	return strings.TrimSpace(fileName), strings.TrimSpace(currentDir), strings.TrimSpace(parentDir)
}

func smartExtractSeasonMarkerText(text string) string {
	t := strings.TrimSpace(text)
	if t == "" {
		return ""
	}
	found := map[int]struct{}{}
	push := func(n int) {
		if n > 0 && n <= 99 {
			found[n] = struct{}{}
		}
	}
	for _, m := range regexp.MustCompile(`(?i)\bS\s*(\d{1,2})\b`).FindAllStringSubmatch(t, -1) {
		if len(m) >= 2 {
			push(intFromDigits(m[1]))
		}
	}
	for _, m := range regexp.MustCompile(`(?i)\bseason\s*(\d{1,2})\b`).FindAllStringSubmatch(t, -1) {
		if len(m) >= 2 {
			push(intFromDigits(m[1]))
		}
	}
	for _, m := range regexp.MustCompile(`第\s*([0-9一二三四五六七八九十百千两〇零]{1,16})\s*季`).FindAllStringSubmatch(t, -1) {
		if len(m) < 2 {
			continue
		}
		n := intFromDigits(m[1])
		if n <= 0 {
			n = smartParseChineseNumeralToInt(m[1])
		}
		push(n)
	}
	if len(found) != 1 {
		return ""
	}
	for n := range found {
		return "第" + strconv.Itoa(n) + "季"
	}
	return ""
}

func smartGuessQualityByLayers(fileName string, currentDir string, parentDir string) (quality string, currentDirIs4K bool) {
	f := strings.TrimSpace(fileName)
	c := strings.TrimSpace(currentDir)
	p := strings.TrimSpace(parentDir)
	if q := smartGuessQuality(f); q != "" {
		return q, false
	}
	qCurr := smartGuessQuality(c)
	if qCurr != "" {
		return qCurr, strings.EqualFold(qCurr, "4K")
	}
	if smartExtractSeasonMarkerText(c) != "" {
		if qParent := smartGuessQuality(p); qParent != "" {
			return qParent, false
		}
	}
	return "", false
}

func smartExtractEpisodeCandidateTexts(ep catpawrunner.Episode) []string {
	rawNames := smartExtractRawNamesFromEpisodeURL(ep.URL)
	fileName, currentDir, parentDir := smartEpisodePathLayers(ep)
	out := make([]string, 0, 6)
	push := func(s string) {
		v := strings.TrimSpace(s)
		if v == "" {
			return
		}
		for _, e := range out {
			if e == v {
				return
			}
		}
		out = append(out, v)
	}
	fileHasSeason := false
	if marker := smartExtractSeasonMarkerText(fileName); marker != "" {
		fileHasSeason = true
	}
	if fileName != "" {
		push(fileName)
	}
	for _, n := range rawNames {
		push(n)
	}
	if !fileHasSeason {
		if currentDir != "" {
			currentMarker := smartExtractSeasonMarkerText(currentDir)
			if currentMarker != "" {
				push(currentMarker)
			}
			push(currentDir)
			if currentMarker == "" {
				if qCurr := smartGuessQuality(currentDir); qCurr != "" && parentDir != "" {
					if parentMarker := smartExtractSeasonMarkerText(parentDir); parentMarker != "" {
						push(parentMarker)
						push(parentDir)
					}
				}
			}
		}
	}
	return out
}

var smartEnhanceTokens = []string{"60fps", "60帧", "hdr", "ddp", "臻彩"}

func smartGuessQuality(hayRaw string) string {
	hay := strings.ToUpper(hayRaw)
	has4k := regexp.MustCompile(`(2160P|2160|4K)`).MatchString(hay)
	has1080 := regexp.MustCompile(`(1080P|1080)`).MatchString(hay)
	has720 := regexp.MustCompile(`(720P|720)`).MatchString(hay)
	hitCount := 0
	if has4k {
		hitCount++
	}
	if has1080 {
		hitCount++
	}
	if has720 {
		hitCount++
	}
	if hitCount >= 2 {
		return ""
	}
	if has4k {
		return "4K"
	}
	if has1080 {
		return "1080P"
	}
	if has720 {
		return "720P"
	}
	return ""
}

func smartGuessFps60(hayRaw string) bool {
	hay := strings.ToLower(hayRaw)
	return strings.Contains(hay, "60fps") || strings.Contains(hay, "60帧")
}

func smartQualityRankOf(q string) int {
	s := strings.ToUpper(strings.TrimSpace(q))
	if s == "4K" {
		return 3
	}
	if s == "1080P" {
		return 2
	}
	if s == "720P" {
		return 1
	}
	return 0
}

func smartBuildHayLower(c smartCandidate) string {
	fileName, currentDir, parentDir := smartEpisodePathLayers(c.Ep)
	_ = parentDir
	parts := make([]string, 0, 4)
	if fileName != "" {
		parts = append(parts, strings.ToLower(fileName))
	}
	if currentDir != "" {
		parts = append(parts, strings.ToLower(currentDir))
	}
	if q, _ := smartGuessQualityByLayers(fileName, currentDir, parentDir); q != "" {
		parts = append(parts, strings.ToLower(q))
	}
	if len(parts) == 0 {
		return strings.TrimSpace(c.RawLower)
	}
	return strings.TrimSpace(strings.Join(parts, " "))
}

func smartComputeCandidateFeatures(c smartCandidate) smartCandidateFeatures {
	hayLower := smartBuildHayLower(c)
	fileName, currentDir, parentDir := smartEpisodePathLayers(c.Ep)
	quality, _ := smartGuessQualityByLayers(fileName, currentDir, parentDir)
	qualityRank := smartQualityRankOf(quality)
	enhance := smartComputePriorityMatch(hayLower, smartEnhanceTokens)
	idx := enhance.Indices
	hasHdr := containsInt(idx, 2)
	fps60 := smartGuessFps60(hayLower) || containsInt(idx, 0) || containsInt(idx, 1)
	// Speed-first strategy: only treat 4K/2160p as the primary goal.
	// HDR/DDP/60FPS are bonus signals (manual switch / display), not stricter ranking requirements.
	tierRank := 10
	if qualityRank == 3 {
		tierRank = 50
	} else if qualityRank == 2 {
		tierRank = 40
	} else if qualityRank == 1 {
		tierRank = 30
	}
	return smartCandidateFeatures{
		HayLower:     hayLower,
		Quality:      quality,
		QualityRank:  qualityRank,
		Fps60:        fps60,
		HasHdr:       hasHdr,
		TierRank:     tierRank,
		EnhanceMatch: enhance,
	}
}

func smartComputeBigHitCount(c smartCandidate, feat smartCandidateFeatures, explicit []string) int {
	hit := 0
	for _, k := range explicit {
		switch k {
		case "网盘":
			if c.PanTokenIdx >= 0 {
				hit++
			}
		case "关键字":
			if c.MatchKeyword.Count > 0 {
				hit++
			}
		case "画质":
			if feat.QualityRank > 0 {
				hit++
			}
		case "帧率":
			if feat.Fps60 {
				hit++
			}
		}
	}
	return hit
}

func smartCompareSmartMatchIgnorePanOrder(a smartCandidate, b smartCandidate, tmdbHasMultiSeason bool, preferSeasonNo int, settings smartPlaybackSettings) int {
	af := smartComputeCandidateFeatures(a)
	bf := smartComputeCandidateFeatures(b)
	if af.TierRank != bf.TierRank {
		return bf.TierRank - af.TierRank
	}

	if tmdbHasMultiSeason && preferSeasonNo > 0 {
		seasonRank := func(m smartCandidate) int {
			matchSeason := m.MatchSeason
			if matchSeason == preferSeasonNo {
				return 4
			}
			hint := m.SearchSeasonHint
			if hint == preferSeasonNo {
				return 3
			}
			hasSeason := m.HasSeasonMarker || hint > 0
			if hasSeason {
				return 1
			}
			return 0
		}
		ar := seasonRank(a)
		br := seasonRank(b)
		if ar != br {
			return br - ar
		}
	}

	ah := smartComputeBigHitCount(a, af, settings.ExplicitKeys)
	bh := smartComputeBigHitCount(b, bf, settings.ExplicitKeys)
	if ah != bh {
		return bh - ah
	}

	ok := settings.OrderKeys
	for _, key := range ok {
		if key == "网盘" {
			continue
		}
		q := 0
		if key == "画质" {
			q = bf.QualityRank - af.QualityRank
		} else if key == "帧率" {
			if bf.Fps60 != af.Fps60 {
				if bf.Fps60 {
					q = 1
				} else {
					q = -1
				}
			}
		} else if key == "关键字" {
			q = smartComparePriorityMatch(a.MatchKeyword, b.MatchKeyword)
		}
		if q != 0 {
			return q
		}
	}

	ex := smartComparePriorityMatch(af.EnhanceMatch, bf.EnhanceMatch)
	if ex != 0 {
		return ex
	}
	return 0
}

func smartComparePanTokenIdx(a int, b int) int {
	av := a
	bv := b
	if av < 0 && bv < 0 {
		return 0
	}
	if av < 0 {
		return 1
	}
	if bv < 0 {
		return -1
	}
	return av - bv
}

func smartCompareSmartMatch(a smartCandidate, b smartCandidate, tmdbHasMultiSeason bool, preferSeasonNo int, settings smartPlaybackSettings) int {
	af := smartComputeCandidateFeatures(a)
	bf := smartComputeCandidateFeatures(b)
	if af.TierRank != bf.TierRank {
		return bf.TierRank - af.TierRank
	}

	if tmdbHasMultiSeason && preferSeasonNo > 0 {
		seasonRank := func(m smartCandidate) int {
			matchSeason := m.MatchSeason
			if matchSeason == preferSeasonNo {
				return 4
			}
			hint := m.SearchSeasonHint
			if hint == preferSeasonNo {
				return 3
			}
			hasSeason := m.HasSeasonMarker || hint > 0
			if hasSeason {
				return 1
			}
			return 0
		}
		ar := seasonRank(a)
		br := seasonRank(b)
		if ar != br {
			return br - ar
		}
	}

	ah := smartComputeBigHitCount(a, af, settings.ExplicitKeys)
	bh := smartComputeBigHitCount(b, bf, settings.ExplicitKeys)
	if ah != bh {
		return bh - ah
	}

	ok := settings.OrderKeys
	if len(ok) == 0 {
		ok = []string{"网盘"}
	}
	for _, key := range ok {
		if key == "网盘" {
			q := smartComparePanTokenIdx(a.PanTokenIdx, b.PanTokenIdx)
			if q != 0 {
				return q
			}
			continue
		}
		q := 0
		if key == "画质" {
			q = bf.QualityRank - af.QualityRank
		} else if key == "帧率" {
			if bf.Fps60 != af.Fps60 {
				if bf.Fps60 {
					q = 1
				} else {
					q = -1
				}
			}
		} else if key == "关键字" {
			q = smartComparePriorityMatch(a.MatchKeyword, b.MatchKeyword)
		}
		if q != 0 {
			return q
		}
	}

	ex := smartComparePriorityMatch(af.EnhanceMatch, bf.EnhanceMatch)
	if ex != 0 {
		return ex
	}
	return 0
}

func smartExtractMaxEpisodeFromBadgeText(text string) int {
	s := strings.TrimSpace(text)
	if s == "" {
		return 0
	}
	m := regexp.MustCompile(`(?i)(?:更新至|更至|更)?\s*(\d{1,5})\s*(?:集|话|回|期|EP|E)\b`).FindStringSubmatch(s)
	if len(m) >= 2 && m[1] != "" {
		n := intFromDigits(m[1])
		if n > 0 {
			return n
		}
	}
	return 0
}

func smartTMDBGlobalEpisodeNoOf(seasons []embyTMDBSeason, season int, episode int) int {
	if episode <= 0 {
		return 0
	}
	if season <= 1 {
		return episode
	}
	sum := 0
	for _, s := range seasons {
		if s.Season <= 0 || s.EpisodeCount <= 0 {
			continue
		}
		if s.Season < season {
			sum += s.EpisodeCount
		}
	}
	return sum + episode
}

func smartPositiveSeasonCount(seasons []embyTMDBSeason) int {
	out := 0
	for _, s := range seasons {
		if s.Season > 0 && s.EpisodeCount > 0 {
			out++
		}
	}
	return out
}

func smartStrictSeasonEpisodeGlobal(seasons []embyTMDBSeason, season int, episode int) int {
	if season <= 0 || episode <= 0 {
		return 0
	}
	seasonCount := 0
	for _, s := range seasons {
		if s.Season == season && s.EpisodeCount > 0 {
			seasonCount = s.EpisodeCount
			break
		}
	}
	if seasonCount <= 0 || episode > seasonCount {
		return 0
	}
	return smartTMDBGlobalEpisodeNoOf(seasons, season, episode)
}

func smartResolveEpisodeMappingForPlayback(seasons []embyTMDBSeason, se smartSeasonEpisode) (match smartSeasonEpisode, global int, ok bool, loose bool) {
	episode := se.Episode
	season := se.Season
	if episode <= 0 {
		return smartSeasonEpisode{}, 0, false, false
	}
	positiveCount := smartPositiveSeasonCount(seasons)

	if season > 0 {
		if g := smartStrictSeasonEpisodeGlobal(seasons, season, episode); g > 0 {
			return smartSeasonEpisode{Season: season, Episode: episode}, g, true, false
		}
		if positiveCount == 1 {
			mapped := smartTMDBSeasonEpisodeOfGlobal(seasons, episode)
			if mapped.Season == 1 && mapped.Episode == episode {
				return mapped, episode, true, false
			}
		}
		return smartSeasonEpisode{}, 0, false, false
	}

	if positiveCount == 1 {
		mapped := smartTMDBSeasonEpisodeOfGlobal(seasons, episode)
		if mapped.Season == 1 && mapped.Episode == episode {
			return mapped, episode, true, false
		}
		return smartSeasonEpisode{}, 0, false, false
	}

	if positiveCount >= 2 {
		return smartSeasonEpisode{}, 0, false, false
	}

	return smartSeasonEpisode{}, 0, false, false
}

func smartResolveEpisodeMappingForPlaybackWithSingleBaseline(
	seasons []embyTMDBSeason,
	se smartSeasonEpisode,
	singleBaselineSeasons []embyTMDBSeason,
	primaryFirstSeasonCount int,
	sourceHasBeyondFirstSeason bool,
) (match smartSeasonEpisode, global int, ok bool, loose bool) {
	if match, global, ok, loose = smartResolveEpisodeMappingForPlayback(seasons, se); ok {
		return match, global, ok, loose
	}
	episode := se.Episode
	if episode <= 0 {
		return smartSeasonEpisode{}, 0, false, false
	}
	if smartPositiveSeasonCount(seasons) < 2 || smartPositiveSeasonCount(singleBaselineSeasons) != 1 {
		return smartSeasonEpisode{}, 0, false, false
	}
	// Frontend-compatible single-baseline fallback:
	// when one side is single-season and the current mapping side is multi-season,
	// fall back to the extracted episode number as a global index only if the
	// single-season side can absorb it.
	mapped := smartTMDBSeasonEpisodeOfGlobal(singleBaselineSeasons, episode)
	if mapped.Season != 1 || mapped.Episode != episode {
		return smartSeasonEpisode{}, 0, false, false
	}
	if se.Season > 0 {
		return mapped, episode, true, false
	}
	if primaryFirstSeasonCount > 0 && episode <= primaryFirstSeasonCount && !sourceHasBeyondFirstSeason {
		return smartSeasonEpisode{}, 0, false, false
	}
	return mapped, episode, true, true
}

func smartNormalizeMaybeGlobalSeasonEpisode(seasons []embyTMDBSeason, se smartSeasonEpisode) smartSeasonEpisode {
	s := se.Season
	e := se.Episode
	if e <= 0 {
		return smartSeasonEpisode{Season: s, Episode: 0}
	}
	if s <= 0 {
		return smartSeasonEpisode{Season: 0, Episode: e}
	}
	seasonCount := 0
	positiveCount := 0
	lastPositiveSeason := 0
	for _, it := range seasons {
		if it.Season > 0 && it.EpisodeCount > 0 {
			positiveCount++
			lastPositiveSeason = it.Season
		}
		if it.Season == s {
			if it.EpisodeCount > 0 {
				seasonCount = it.EpisodeCount
			}
		}
	}
	if seasonCount == 0 || e <= seasonCount {
		return smartSeasonEpisode{Season: s, Episode: e}
	}
	if positiveCount == 1 && lastPositiveSeason > 0 {
		return smartSeasonEpisode{Season: lastPositiveSeason, Episode: e}
	}
	return smartSeasonEpisode{Season: s, Episode: e}
}
