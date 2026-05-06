package smart

import (
	"regexp"
	"sort"
	"strconv"
	"strings"
	"unicode"

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

func smartStripDisplayMetaPrefix(display string) string {
	rest := strings.TrimSpace(display)
	for strings.HasPrefix(rest, "@") {
		m := regexp.MustCompile(`^@([^@/\\]+)`).FindString(rest)
		if strings.TrimSpace(m) == "" {
			break
		}
		rest = strings.TrimSpace(strings.TrimPrefix(rest, m))
	}
	return rest
}

func smartEpisodeListProviderID(ep catpawrunner.Episode) string {
	provider := strings.TrimSpace(catpawrunner.PanMockProviderFromFlag(strings.TrimSpace(ep.Flag)))
	if provider == "" {
		provider = strings.TrimSpace(smartPlayFlagProviderID(strings.TrimSpace(ep.Flag)))
	}
	switch provider {
	case "baidu", "quark", "uc", "139", "189":
		return provider
	default:
		return ""
	}
}

func smartEpisodeWithPanFlag(ep catpawrunner.Episode, panFlag string) catpawrunner.Episode {
	if strings.TrimSpace(ep.Flag) == "" {
		ep.Flag = strings.TrimSpace(panFlag)
	}
	return ep
}

func smartEpisodeDisplayNameLooksLikeListDir(ep catpawrunner.Episode) bool {
	if smartEpisodeListProviderID(ep) == "" {
		return false
	}
	url := strings.TrimSpace(ep.URL)
	if !strings.Contains(url, "|||") && !strings.Contains(url, "***") {
		return false
	}
	display := smartStripDisplayMetaPrefix(ep.Name)
	return strings.HasPrefix(display, "/") || strings.Contains(display, "/") || strings.Contains(display, "\\")
}

func smartEpisodePathLayers(ep catpawrunner.Episode) (fileName string, currentDir string, parentDir string) {
	rawNames := smartExtractRawNamesFromEpisodeURL(ep.URL)
	for _, n := range rawNames {
		t := strings.TrimSpace(n)
		if t == "" {
			continue
		}
		parts := strings.Split(strings.ReplaceAll(t, "\\", "/"), "/")
		fileName = strings.TrimSpace(parts[len(parts)-1])
		if fileName != "" {
			break
		}
	}
	segs := smartSplitDisplayPathSegments(smartStripDisplayMetaPrefix(ep.Name))
	if len(segs) > 0 {
		currentDir = segs[len(segs)-1]
	}
	if len(segs) > 1 {
		parentDir = segs[len(segs)-2]
	}
	return strings.TrimSpace(fileName), strings.TrimSpace(currentDir), strings.TrimSpace(parentDir)
}

func smartEpisodeDirectoryKey(ep catpawrunner.Episode) string {
	rawNames := smartExtractRawNamesFromEpisodeURL(ep.URL)
	for _, n := range rawNames {
		raw := strings.ReplaceAll(strings.TrimSpace(n), "\\", "/")
		raw = strings.Trim(raw, "/")
		if raw == "" {
			continue
		}
		parts := strings.Split(raw, "/")
		if len(parts) >= 2 {
			dir := strings.TrimSpace(strings.Join(parts[:len(parts)-1], "/"))
			if dir != "" {
				return dir
			}
		}
	}
	segs := smartSplitDisplayPathSegments(smartStripDisplayMetaPrefix(ep.Name))
	if len(segs) >= 1 && smartEpisodeDisplayNameLooksLikeListDir(ep) {
		return strings.TrimSpace(strings.Join(segs, "/"))
	}
	if len(segs) >= 2 {
		return strings.TrimSpace(strings.Join(segs[:len(segs)-1], "/"))
	}
	return ""
}

func smartNormalizeSeasonHintText(text string) string {
	raw := strings.TrimSpace(text)
	if raw == "" {
		return ""
	}
	var b strings.Builder
	for _, r := range raw {
		if unicode.IsSpace(r) {
			continue
		}
		if r >= '０' && r <= '９' {
			b.WriteRune('0' + (r - '０'))
			continue
		}
		b.WriteRune(unicode.ToLower(r))
	}
	return strings.TrimSpace(b.String())
}

func smartParseSeasonHintNumber(text string) int {
	raw := smartNormalizeSeasonHintText(text)
	if raw == "" {
		return 0
	}
	if regexp.MustCompile(`^\d+$`).MatchString(raw) {
		return intFromDigits(raw)
	}
	return smartParseChineseNumeralToInt(raw)
}

func smartExtractSeasonHintNumberFromText(text string) int {
	t := smartNormalizeSeasonHintText(text)
	if t == "" {
		return 0
	}
	found := map[int]struct{}{}
	push := func(n int) {
		if n > 0 && n <= 99 {
			found[n] = struct{}{}
		}
	}
	for _, m := range regexp.MustCompile(`(?:^|[^a-z0-9])season0*(\d{1,3})(?:$|[^a-z0-9])`).FindAllStringSubmatch(t, -1) {
		if len(m) >= 2 {
			push(intFromDigits(m[1]))
		}
	}
	for _, m := range regexp.MustCompile(`(?:^|[^a-z0-9])s0*(\d{1,3})(?:e\d{1,5})?(?:$|[^a-z0-9])`).FindAllStringSubmatch(t, -1) {
		if len(m) >= 2 {
			push(intFromDigits(m[1]))
		}
	}
	for _, m := range regexp.MustCompile(`第([0-9一二三四五六七八九十百千两〇零]{1,16})(?:季|部|篇)`).FindAllStringSubmatch(t, -1) {
		if len(m) < 2 {
			continue
		}
		push(smartParseSeasonHintNumber(m[1]))
	}
	if len(found) != 1 {
		return 0
	}
	for n := range found {
		return n
	}
	return 0
}

func smartExtractSeasonMarkerText(text string) string {
	n := smartExtractSeasonHintNumberFromText(text)
	if n <= 0 {
		return ""
	}
	return "第" + strconv.Itoa(n) + "季"
}

func smartEpisodePathSeasonHint(ep catpawrunner.Episode) int {
	if smartEpisodeListProviderID(ep) == "" {
		return 0
	}
	fileName, currentDir, parentDir := smartEpisodePathLayers(ep)
	if n := smartExtractSeasonHintNumberFromText(fileName); n > 0 {
		return n
	}
	currentSeason := smartExtractSeasonHintNumberFromText(currentDir)
	if currentSeason > 0 {
		return currentSeason
	}
	if smartGuessQuality(currentDir) != "" {
		if parentSeason := smartExtractSeasonHintNumberFromText(parentDir); parentSeason > 0 {
			return parentSeason
		}
	}
	return 0
}

func smartGuessQualityByLayers(fileName string, displayName string, currentDir string, parentDir string) (quality string, currentDirIsQuality bool) {
	f := strings.TrimSpace(fileName)
	d := strings.TrimSpace(displayName)
	c := strings.TrimSpace(currentDir)
	p := strings.TrimSpace(parentDir)
	if q := smartGuessQuality(f); q != "" {
		return q, false
	}
	if qDisplay, _ := smartParseDisplayMeta(d); qDisplay != "" {
		return qDisplay, false
	}
	qCurr := smartGuessQuality(c)
	if qCurr != "" {
		return qCurr, true
	}
	if smartExtractSeasonMarkerText(c) != "" {
		if qParent := smartGuessQuality(p); qParent != "" {
			return qParent, false
		}
	}
	if q := smartGuessQuality(d); q != "" {
		return q, false
	}
	return "", false
}

func smartExtractEpisodeCandidateTexts(ep catpawrunner.Episode) []string {
	rawNames := smartExtractRawNamesFromEpisodeURL(ep.URL)
	fileName, currentDir, parentDir := smartEpisodePathLayers(ep)
	displayName := strings.TrimSpace(ep.Name)
	out := make([]string, 0, 8)
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

	// 提取优先级：文件名 -> 展示名 -> raw
	if fileName != "" {
		push(fileName)
	}
	if displayName != "" && displayName != fileName {
		push(displayName)
	}
	for _, n := range rawNames {
		push(n)
	}

	fileHasSeason := false
	if marker := smartExtractSeasonMarkerText(fileName); marker != "" {
		fileHasSeason = true
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
var smartDisplayMetaTokenRe = regexp.MustCompile(`^@([^@/\\\\]+)`)

func smartParseDisplayMeta(displayName string) (quality string, fps60 bool) {
	rest := strings.TrimSpace(displayName)
	for strings.HasPrefix(rest, "@") {
		m := smartDisplayMetaTokenRe.FindStringSubmatch(rest)
		if len(m) < 2 {
			break
		}
		token := strings.ToUpper(strings.TrimSpace(m[1]))
		switch token {
		case "8K":
			quality = "8K"
		case "4K", "2160P":
			quality = "4K"
		case "1080P":
			quality = "1080P"
		case "720P":
			quality = "720P"
		case "60FPS", "120FPS", "60帧", "120帧":
			fps60 = true
		}
		rest = strings.TrimSpace(strings.TrimPrefix(rest, m[0]))
	}
	return quality, fps60
}

func smartGuessQuality(hayRaw string) string {
	hay := strings.ToUpper(hayRaw)
	has8k := regexp.MustCompile(`(4320P|4320|7680|8K)`).MatchString(hay)
	has4k := regexp.MustCompile(`(2160P|2160|4K)`).MatchString(hay)
	has1080 := regexp.MustCompile(`(1080P|1080)`).MatchString(hay)
	has720 := regexp.MustCompile(`(720P|720)`).MatchString(hay)
	hitCount := 0
	if has8k {
		hitCount++
	}
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
	if has8k {
		return "8K"
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
	if s == "8K" {
		return 4
	}
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
	displayName := strings.TrimSpace(c.Ep.Name)
	_ = parentDir
	parts := make([]string, 0, 6)
	if fileName != "" {
		parts = append(parts, strings.ToLower(fileName))
	}
	if currentDir != "" {
		parts = append(parts, strings.ToLower(currentDir))
	}
	if displayName != "" {
		parts = append(parts, strings.ToLower(displayName))
	}
	if q, _ := smartGuessQualityByLayers(fileName, displayName, currentDir, parentDir); q != "" {
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
	quality, _ := smartGuessQualityByLayers(fileName, strings.TrimSpace(c.Ep.Name), currentDir, parentDir)
	displayQuality, displayFPS60 := smartParseDisplayMeta(c.Ep.Name)
	if quality == "" && displayQuality != "" {
		quality = displayQuality
	}
	qualityRank := smartQualityRankOf(quality)
	enhance := smartComputePriorityMatch(hayLower, smartEnhanceTokens)
	idx := enhance.Indices
	hasHdr := containsInt(idx, 2)
	hasDDP := containsInt(idx, 3)
	fps60 := displayFPS60 || smartGuessFps60(hayLower) || containsInt(idx, 0) || containsInt(idx, 1)
	return smartCandidateFeatures{
		HayLower:     hayLower,
		Quality:      quality,
		QualityRank:  qualityRank,
		Fps60:        fps60,
		HasHdr:       hasHdr,
		HasDDP:       hasDDP,
		EnhanceMatch: enhance,
	}
}

func smartStageScore(stage smartCandidateStage) int {
	switch stage {
	case smartCandidateStageManualList:
		return 500
	case smartCandidateStageManualDetail:
		return 400
	case smartCandidateStageHistoryList:
		return 300
	case smartCandidateStageHistoryDetail:
		return 200
	default:
		return 100
	}
}

func smartQualityScore(c smartCandidate, feat smartCandidateFeatures) int {
	text := strings.ToUpper(strings.TrimSpace(strings.Join([]string{
		feat.HayLower,
		c.Ep.Name,
		c.RawLower,
		c.RawName,
		c.SrcRemarkLower,
	}, " ")))
	if text == "" {
		return 0
	}
	base := 0
	if regexp.MustCompile(`(?:4320P|4320|7680|8K)`).MatchString(text) {
		base = 50
	} else if regexp.MustCompile(`(?:2160P|2160|4K|UHD)`).MatchString(text) {
		base = 40
	} else if regexp.MustCompile(`(?:1080P|1080)`).MatchString(text) {
		base = 20
	} else if regexp.MustCompile(`(?:720P|720)`).MatchString(text) {
		base = 10
	} else if regexp.MustCompile(`(?i)(BDRIP|BLURAY|REMUX|WEB[- ]?DL|WEBRIP|HDR|DV|DOLBY|原盘|蓝光|高码|超清|高清|臻彩|杜比|DDP|DD\\+|E-?AC-?3|EAC3)`).MatchString(text) {
		base = 30
	}
	if base == 0 {
		return 0
	}
	bonus := 0
	switch {
	case feat.HasHdr && feat.HasDDP:
		bonus = 6
	case feat.HasHdr:
		bonus = 4
	case feat.HasDDP:
		bonus = 2
	}
	score := base + bonus
	switch base {
	case 40:
		if score >= 50 {
			score = 49
		}
	case 30:
		if score >= 40 {
			score = 39
		}
	case 20:
		if score >= 30 {
			score = 29
		}
	case 10:
		if score >= 20 {
			score = 19
		}
	}
	return score
}

func smartPanScore(c smartCandidate, settings smartPlaybackSettings) int {
	if c.PanTokenIdx < 0 {
		return 0
	}
	n := len(settings.PanTokenOrderLower)
	if n <= 0 || c.PanTokenIdx >= n {
		return 0
	}
	return (n - c.PanTokenIdx) * 10
}

func smartKeywordScore(c smartCandidate, settings smartPlaybackSettings) int {
	if len(settings.KeywordTokensLower) == 0 || c.MatchKeyword.Count <= 0 || len(c.MatchKeyword.Indices) == 0 {
		return 0
	}
	best := c.MatchKeyword.Indices[0]
	if best < 0 || best >= len(settings.KeywordTokensLower) {
		return 0
	}
	return (len(settings.KeywordTokensLower) - best) * 10
}

func smartBuildCandidateScore(c smartCandidate, feat smartCandidateFeatures, settings smartPlaybackSettings) smartCandidateScore {
	return smartCandidateScore{
		Stage:        c.Stage,
		StageScore:   smartStageScore(c.Stage),
		QualityScore: smartQualityScore(c, feat),
		PanScore:     smartPanScore(c, settings),
		KeywordScore: smartKeywordScore(c, settings),
	}
}

func smartScoreByRuleKey(score smartCandidateScore, key smartPlaybackRuleKey) int {
	switch key {
	case smartPlaybackRuleQuality:
		return score.QualityScore
	case smartPlaybackRulePan:
		return score.PanScore
	case smartPlaybackRuleKeyword:
		return score.KeywordScore
	default:
		return 0
	}
}

func smartCompareSeasonRank(a smartCandidate, b smartCandidate, tmdbHasMultiSeason bool, preferSeasonNo int) int {
	if !(tmdbHasMultiSeason && preferSeasonNo > 0) {
		return 0
	}
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
	return 0
}

func smartCompareCandidateCore(a smartCandidate, b smartCandidate, tmdbHasMultiSeason bool, preferSeasonNo int, settings smartPlaybackSettings, ignorePan bool) int {
	af := smartComputeCandidateFeatures(a)
	bf := smartComputeCandidateFeatures(b)
	as := smartBuildCandidateScore(a, af, settings)
	bs := smartBuildCandidateScore(b, bf, settings)

	if as.StageScore != bs.StageScore {
		return bs.StageScore - as.StageScore
	}
	if q := smartCompareSeasonRank(a, b, tmdbHasMultiSeason, preferSeasonNo); q != 0 {
		return q
	}
	for _, key := range settings.OrderedRules {
		if ignorePan && key == smartPlaybackRulePan {
			continue
		}
		av := smartScoreByRuleKey(as, key)
		bv := smartScoreByRuleKey(bs, key)
		if av != bv {
			return bv - av
		}
	}
	if a.StrictMatched != b.StrictMatched {
		if a.StrictMatched {
			return -1
		}
		return 1
	}
	if a.DegradedMatched != b.DegradedMatched {
		if a.DegradedMatched {
			return 1
		}
		return -1
	}
	ex := smartComparePriorityMatch(af.EnhanceMatch, bf.EnhanceMatch)
	if ex != 0 {
		return ex
	}
	q := smartComparePanTokenIdx(a.PanTokenIdx, b.PanTokenIdx)
	if q != 0 {
		return q
	}
	return 0
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
	return smartCompareCandidateCore(a, b, tmdbHasMultiSeason, preferSeasonNo, settings, true)
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
	return smartCompareCandidateCore(a, b, tmdbHasMultiSeason, preferSeasonNo, settings, false)
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

func smartTMDBGlobalEpisodeNoOf(seasons []smartTMDBSeason, season int, episode int) int {
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

func smartPositiveSeasonCount(seasons []smartTMDBSeason) int {
	out := 0
	for _, s := range seasons {
		if s.Season > 0 && s.EpisodeCount > 0 {
			out++
		}
	}
	return out
}

func smartStrictSeasonEpisodeGlobal(seasons []smartTMDBSeason, season int, episode int) int {
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

func smartSeasonCountOf(seasons []smartTMDBSeason, season int) int {
	for _, s := range seasons {
		if s.Season == season && s.EpisodeCount > 0 {
			return s.EpisodeCount
		}
	}
	return 0
}

func smartSumEpisodesBeforeLastSeason(seasons []smartTMDBSeason) int {
	rows := make([]smartTMDBSeason, 0, len(seasons))
	for _, s := range seasons {
		if s.Season > 0 && s.EpisodeCount > 0 {
			rows = append(rows, s)
		}
	}
	if len(rows) <= 1 {
		return 0
	}
	sort.SliceStable(rows, func(i, j int) bool { return rows[i].Season < rows[j].Season })
	sum := 0
	for i := 0; i < len(rows)-1; i++ {
		sum += rows[i].EpisodeCount
	}
	return sum
}

func smartDegradedFallbackBoundary(primary []smartTMDBSeason, baseline []smartTMDBSeason) int {
	primaryMulti := smartPositiveSeasonCount(primary) >= 2
	baselineSingle := smartPositiveSeasonCount(baseline) == 1
	if primaryMulti && baselineSingle {
		if n := smartSumEpisodesBeforeLastSeason(primary); n > 0 {
			return n
		}
	}
	if n := smartSeasonCountOf(primary, 1); n > 0 {
		return n
	}
	return 0
}

func smartResolveEpisodeMappingStrict(seasons []smartTMDBSeason, se smartSeasonEpisode) (match smartSeasonEpisode, global int, ok bool) {
	episode := se.Episode
	season := se.Season
	if episode <= 0 {
		return smartSeasonEpisode{}, 0, false
	}
	positiveCount := smartPositiveSeasonCount(seasons)

	if season > 0 {
		if g := smartStrictSeasonEpisodeGlobal(seasons, season, episode); g > 0 {
			return smartSeasonEpisode{Season: season, Episode: episode}, g, true
		}
		return smartSeasonEpisode{}, 0, false
	}

	if positiveCount == 1 {
		mapped := smartTMDBSeasonEpisodeOfGlobal(seasons, episode)
		if mapped.Season == 1 && mapped.Episode == episode {
			return mapped, episode, true
		}
		return smartSeasonEpisode{}, 0, false
	}

	if positiveCount >= 2 {
		return smartSeasonEpisode{}, 0, false
	}

	return smartSeasonEpisode{}, 0, false
}

func smartResolveEpisodeMappingSingleBaseline(
	seasons []smartTMDBSeason,
	se smartSeasonEpisode,
	singleBaselineSeasons []smartTMDBSeason,
	primaryFallbackBoundary int,
	sourceHasBeyondFirstSeason bool,
) (match smartSeasonEpisode, global int, ok bool, loose bool) {
	episode := se.Episode
	if episode <= 0 {
		return smartSeasonEpisode{}, 0, false, false
	}
	if smartPositiveSeasonCount(seasons) < 2 || smartPositiveSeasonCount(singleBaselineSeasons) != 1 {
		return smartSeasonEpisode{}, 0, false, false
	}
	// Frontend-aligned single-baseline path:
	// when one side is single-season and the current mapping side is multi-season,
	// fall back to the extracted episode number as a global index only if the
	// single-season side can absorb it.
	mapped := smartTMDBSeasonEpisodeOfGlobal(singleBaselineSeasons, episode)
	if mapped.Season != 1 || mapped.Episode != episode {
		return smartSeasonEpisode{}, 0, false, false
	}
	if !sourceHasBeyondFirstSeason {
		return smartSeasonEpisode{}, 0, false, false
	}
	if se.Season > 0 {
		return mapped, episode, true, false
	}
	return mapped, episode, true, true
}

func smartResolveEpisodeMappingForPlaybackWithMode(
	seasons []smartTMDBSeason,
	se smartSeasonEpisode,
	singleBaselineSeasons []smartTMDBSeason,
	primaryFallbackBoundary int,
	sourceHasBeyondFirstSeason bool,
	allowSingleBaseline bool,
	primaryKind string,
) (match smartSeasonEpisode, global int, ok bool, loose bool, resolutionMode string, degradedReason string) {
	if match, global, ok = smartResolveEpisodeMappingStrict(seasons, se); ok {
		label := "strict-tmdb"
		if strings.EqualFold(strings.TrimSpace(primaryKind), "douban") {
			label = "strict-douban"
		}
		return match, global, true, false, label, ""
	}
	if !allowSingleBaseline {
		return smartSeasonEpisode{}, 0, false, false, "", ""
	}
	fallbackBoundary := primaryFallbackBoundary
	if fallbackBoundary <= 0 {
		fallbackBoundary = smartDegradedFallbackBoundary(seasons, singleBaselineSeasons)
	}
	match, global, ok, loose = smartResolveEpisodeMappingSingleBaseline(
		seasons,
		se,
		singleBaselineSeasons,
		fallbackBoundary,
		sourceHasBeyondFirstSeason,
	)
	if !ok {
		return smartSeasonEpisode{}, 0, false, false, "", ""
	}
	reason := "episode-only-fallback"
	if se.Season > 0 {
		reason = "season-marked-fallback"
		loose = false
	}
	return match, global, true, loose, "degraded-single-baseline", reason
}

func smartNormalizeMaybeGlobalSeasonEpisode(seasons []smartTMDBSeason, se smartSeasonEpisode) smartSeasonEpisode {
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
