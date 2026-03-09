package smart

import (
	"regexp"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

func smartParseVodPlayURLToEpisodes(vodPlayURL string) []catpawrunner.Episode {
	raw := strings.TrimSpace(vodPlayURL)
	if raw == "" {
		return nil
	}
	chunks := strings.Split(raw, "#")
	out := make([]catpawrunner.Episode, 0, len(chunks))
	for _, chunk := range chunks {
		s := strings.TrimSpace(chunk)
		if s == "" {
			continue
		}
		name := s
		url := ""
		if idx := strings.Index(s, "$"); idx >= 0 {
			name = strings.TrimSpace(s[:idx])
			url = strings.TrimSpace(s[idx+1:])
		}
		if strings.TrimSpace(url) == "" {
			continue
		}
		out = append(out, catpawrunner.Episode{Name: name, URL: url})
	}
	return out
}

func smartPanToProviderID(panLower string) string {
	s := strings.ToLower(strings.TrimSpace(panLower))
	if s == "" {
		return ""
	}
	switch {
	case strings.Contains(s, "百度"), strings.Contains(s, "baidu"):
		return "baidu"
	case strings.Contains(s, "夸克"), strings.Contains(s, "quark"):
		return "quark"
	case strings.Contains(s, "uc"):
		return "uc"
	case strings.Contains(s, "天翼"):
		return "189"
	case strings.Contains(s, "移动"):
		return "139"
	default:
		return ""
	}
}

func smartPlayFlagProviderID(flagLabel string) string {
	s := strings.TrimSpace(flagLabel)
	if s == "" {
		return ""
	}
	if !strings.Contains(s, "-") {
		return ""
	}
	head := strings.TrimSpace(strings.SplitN(s, "-", 2)[0])
	if head == "" {
		return ""
	}
	switch {
	case strings.Contains(head, "百度"):
		return "baidu"
	case strings.Contains(head, "夸父"):
		return "quark"
	case strings.Contains(head, "优夕"):
		return "uc"
	case strings.Contains(head, "天意"):
		return "189"
	case strings.Contains(head, "逸动"):
		return "139"
	default:
		return ""
	}
}

func smartPanMatchLabelText(label string) string {
	s := strings.ToLower(strings.TrimSpace(label))
	if s == "" {
		return ""
	}
	if strings.Contains(s, "-") {
		parts := strings.SplitN(s, "-", 2)
		return strings.TrimSpace(parts[0])
	}
	return s
}

func smartPanMockProviderID(database *db.DB, panLabel string) string {
	_ = database
	raw := strings.TrimSpace(panLabel)
	if raw == "" || !strings.Contains(raw, "-") {
		return ""
	}
	s := smartPanMatchLabelText(raw)
	if s == "" {
		return ""
	}
	return smartPlayFlagProviderID(s)
}

func smartExtractMockPasscodeFromCandidate(c smartCandidate) string {
	names := smartExtractRawNamesFromEpisodeURL(c.Ep.URL)
	if len(names) == 0 {
		return ""
	}
	raw := strings.TrimSpace(names[0])
	if raw == "" {
		return ""
	}
	if !strings.HasSuffix(strings.ToLower(raw), ".mp4") {
		return ""
	}
	out := raw
	if strings.HasSuffix(strings.ToLower(out), ".mp4") {
		out = strings.TrimSpace(out[:len(out)-4])
	}
	if strings.EqualFold(out, "nopass") {
		return ""
	}
	return strings.TrimSpace(out)
}

func smartExtractTianyiMockMetaFromCandidate(c smartCandidate) (shareCode string, accessCode string) {
	label := strings.TrimSpace(c.PanLabel)
	if m := regexp.MustCompile(`(?:天意|天翼)-([A-Za-z0-9]{6,64})`).FindStringSubmatch(label); len(m) == 2 {
		shareCode = strings.TrimSpace(m[1])
	}
	pass := strings.TrimSpace(smartExtractMockPasscodeFromCandidate(c))
	if pass == "" {
		return shareCode, ""
	}
	if strings.Contains(pass, "-") {
		seg := strings.SplitN(pass, "-", 2)
		if shareCode == "" {
			shareCode = strings.TrimSpace(seg[0])
		}
		accessCode = strings.TrimSpace(seg[1])
	} else {
		accessCode = pass
	}
	if strings.EqualFold(accessCode, "nopass") {
		accessCode = ""
	}
	return shareCode, accessCode
}

func smartTMDBSeasonEpisodeOfGlobal(seasons []embyTMDBSeason, global int) smartSeasonEpisode {
	if global <= 0 {
		return smartSeasonEpisode{Season: 0, Episode: 0}
	}
	left := global
	for _, s := range seasons {
		if s.Season <= 0 || s.EpisodeCount <= 0 {
			continue
		}
		if left <= s.EpisodeCount {
			return smartSeasonEpisode{Season: s.Season, Episode: left}
		}
		left -= s.EpisodeCount
	}
	return smartSeasonEpisode{Season: 0, Episode: 0}
}

func containsInt(list []int, v int) bool {
	for _, n := range list {
		if n == v {
			return true
		}
	}
	return false
}

func intFromDigits(s string) int {
	out := 0
	raw := strings.TrimSpace(s)
	if raw == "" {
		return -1
	}
	for _, ch := range raw {
		if ch < '0' || ch > '9' {
			continue
		}
		out = out*10 + int(ch-'0')
	}
	if out < 0 {
		return -1
	}
	return out
}

func smartMinInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}

func smartPickBestMatchIgnorePanOrder(list []smartCandidate, tmdbHasMultiSeason bool, preferSeasonNo int, settings smartPlaybackSettings) *smartCandidate {
	items := make([]smartCandidate, 0, len(list))
	for _, it := range list {
		items = append(items, it)
	}
	if len(items) == 0 {
		return nil
	}
	best := items[0]
	for i := 1; i < len(items); i++ {
		if smartCompareSmartMatchIgnorePanOrder(best, items[i], tmdbHasMultiSeason, preferSeasonNo, settings) > 0 {
			best = items[i]
		}
	}
	return &best
}

func smartExtractMockPasscodeFromEpisodeURL(episodeURL string) string {
	names := smartExtractRawNamesFromEpisodeURL(episodeURL)
	if len(names) == 0 {
		return ""
	}
	raw := strings.TrimSpace(names[0])
	if raw == "" {
		return ""
	}
	if !strings.HasSuffix(strings.ToLower(raw), ".mp4") {
		// Only placeholder filenames encode passcodes as "<pass>.mp4".
		return ""
	}
	out := raw
	if strings.HasSuffix(strings.ToLower(out), ".mp4") {
		out = strings.TrimSpace(out[:len(out)-4])
	}
	if strings.EqualFold(out, "nopass") {
		return ""
	}
	return strings.TrimSpace(out)
}

func smartExtractTianyiMockMetaFromEpisodeURL(panLabel string, episodeURL string) (shareCode string, accessCode string) {
	label := strings.TrimSpace(panLabel)
	url := strings.TrimSpace(episodeURL)

	// Fallback: shareCode might already be embedded in the label like "天意-XXXX" / "天翼-XXXX".
	if m := regexp.MustCompile(`(?:天意|天翼)-([A-Za-z0-9]{6,64})`).FindStringSubmatch(label); len(m) == 2 {
		shareCode = strings.TrimSpace(m[1])
	}

	pass := strings.TrimSpace(smartExtractMockPasscodeFromEpisodeURL(url))
	if pass == "" {
		return shareCode, ""
	}
	if strings.Contains(pass, "-") {
		seg := strings.SplitN(pass, "-", 2)
		if shareCode == "" && strings.TrimSpace(seg[0]) != "" {
			shareCode = strings.TrimSpace(seg[0])
		}
		if len(seg) == 2 {
			accessCode = strings.TrimSpace(seg[1])
		}
	} else {
		accessCode = pass
	}
	if strings.EqualFold(accessCode, "nopass") {
		accessCode = ""
	}
	return shareCode, accessCode
}

func smartBuildSourceKey(siteKey string, spiderAPI string, videoID string) string {
	return strings.TrimSpace(siteKey) + "::" + strings.TrimSpace(spiderAPI) + "::" + strings.TrimSpace(videoID)
}

func smartExtractSeasonHintFromSource(siteName string, videoRemark string) int {
	text := siteName + " " + videoRemark
	t := strings.TrimSpace(text)
	if t == "" {
		return 0
	}
	if m := regexp.MustCompile(`(?i)\bS(\d{1,2})\b`).FindStringSubmatch(t); len(m) >= 2 && m[1] != "" {
		n := intFromDigits(m[1])
		if n >= 0 && n <= 99 {
			return n
		}
	}
	if m := regexp.MustCompile(`(?i)\bseason\s*(\d{1,2})\b`).FindStringSubmatch(t); len(m) >= 2 && m[1] != "" {
		n := intFromDigits(m[1])
		if n >= 0 && n <= 99 {
			return n
		}
	}
	if m := regexp.MustCompile(`第\s*(\d{1,2})\s*季`).FindStringSubmatch(t); len(m) >= 2 && m[1] != "" {
		n := intFromDigits(m[1])
		if n >= 0 && n <= 99 {
			return n
		}
	}
	return 0
}

func smartHasExplicitSeasonMarkerInSource(siteName string, videoRemark string) bool {
	t := strings.TrimSpace(siteName + " " + videoRemark)
	if t == "" {
		return false
	}
	return regexp.MustCompile(`(?i)(?:\bS\d{1,2}\b|第\s*\d{1,2}\s*季|season\s*\d{1,2})`).MatchString(t)
}

func smartIsPanMockEnabled(detailRaw map[string]any) bool {
	if detailRaw == nil {
		return false
	}
	v, ok := detailRaw["pan_mock"]
	if !ok || v == nil {
		return false
	}
	switch x := v.(type) {
	case bool:
		return x
	case string:
		s := strings.TrimSpace(x)
		return s == "1" || strings.EqualFold(s, "true")
	case float64:
		return int(x) == 1
	default:
		return false
	}
}

func smartLabelTokenIdx(label string, panTokenOrderLower []string) int {
	s := smartPanMatchLabelText(label)
	if s == "" {
		return -1
	}
	for i := 0; i < len(panTokenOrderLower); i++ {
		t := strings.TrimSpace(panTokenOrderLower[i])
		if t == "" {
			continue
		}
		if strings.Contains(s, t) {
			return i
		}
	}
	return -1
}

func smartFirstRawNameFromURL(u string) string {
	rawNames := smartExtractRawNamesFromEpisodeURL(u)
	if len(rawNames) == 0 {
		return ""
	}
	return strings.TrimSpace(rawNames[0])
}

func smartMaxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}

func smartShortURLForLog(raw string) string {
	s := strings.TrimSpace(raw)
	if s == "" {
		return ""
	}
	// Drop query/fragment (often contains large tokens).
	if i := strings.IndexAny(s, "?#"); i >= 0 {
		s = strings.TrimSpace(s[:i])
	}
	const maxLen = 180
	if len(s) <= maxLen {
		return s
	}
	head := 110
	tail := 50
	if head+tail+3 > maxLen {
		head = 90
		tail = 50
	}
	if head+tail+3 > len(s) {
		return s[:maxLen]
	}
	return s[:head] + "..." + s[len(s)-tail:]
}
