package emby

import (
	"errors"
	"math"
	"regexp"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawopen"
	"github.com/jenfonro/meowfilm/server/config"
	"github.com/jenfonro/meowfilm/server/magic"
	"github.com/jenfonro/meowfilm/server/netdisk"
)

type smartPlaybackRequest struct {
	Kind    string // "movie" | "tv"
	TMDBID  int
	Season  int
	Episode int
	SubKind string // "movie" | "episode"
}

type smartPriorityMatch struct {
	Count   int
	Indices []int
}

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

type smartSeasonEpisode struct {
	Season  int
	Episode int
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
	pickSuffix := func() string {
		if strings.Contains(raw, "***") {
			parts := strings.Split(raw, "***")
			return parts[len(parts)-1]
		}
		if strings.Contains(raw, "|||") {
			parts := strings.Split(raw, "|||")
			return parts[len(parts)-1]
		}
		pipeParts := strings.Split(raw, "|")
		if len(pipeParts) >= 4 {
			return pipeParts[len(pipeParts)-1]
		}
		return ""
	}
	suffix := pickSuffix()
	if strings.TrimSpace(suffix) == "" {
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
	parts := strings.Split(suffix, "#")
	out := make([]string, 0, len(parts))
	for _, p := range parts {
		t := stripMeta(p)
		if t != "" {
			out = append(out, t)
		}
	}
	return out
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

func smartParseVodPlayURLToEpisodes(vodPlayURL string) []catpawopen.Episode {
	raw := strings.TrimSpace(vodPlayURL)
	if raw == "" {
		return nil
	}
	chunks := strings.Split(raw, "#")
	out := make([]catpawopen.Episode, 0, len(chunks))
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
		out = append(out, catpawopen.Episode{Name: name, URL: url})
	}
	return out
}

func smartPanMockProviderID(panLabel string) string {
	s := strings.TrimSpace(panLabel)
	if s == "" {
		return ""
	}
	lower := strings.ToLower(s)
	if strings.Contains(s, "天意") || strings.Contains(s, "天翼") || strings.Contains(s, "189") || strings.Contains(lower, "tianyi") {
		return "tianyi"
	}
	if strings.Contains(s, "逸动") || strings.Contains(s, "和彩云") || strings.Contains(s, "139") || strings.Contains(lower, "yidong") {
		return "139"
	}
	if strings.Contains(s, "夸父") || strings.Contains(s, "夸克") || strings.Contains(lower, "quark") {
		return "quark"
	}
	if strings.Contains(s, "优夕") || strings.Contains(lower, "uc") {
		return "uc"
	}
	if strings.Contains(s, "百度") || strings.Contains(lower, "baidu") {
		return "baidu"
	}
	return ""
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

func smartExtractEpisodeCandidateTexts(ep catpawopen.Episode) []string {
	rawNames := smartExtractRawNamesFromEpisodeURL(ep.URL)
	displayName := strings.TrimSpace(ep.Name)
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
	if displayName != "" {
		push(displayName)
	}
	for _, n := range rawNames {
		push(n)
	}
	return out
}

type smartPlaybackSettings struct {
	Mode               string   // "无" | "网盘" | "关键字"
	KeywordTokensLower []string // smart_source_priority_tokens
	PanTokenOrderLower []string // smart_pan_match_tokens
	OrderKeys          []string // order preference keys
	ExplicitKeys       []string // explicit big conditions
}

func smartLoadPlaybackSettings(database *db.DB) smartPlaybackSettings {
	mode := config.NormalizeSourceExtractPriority(database.GetSetting("smart_source_extract_priority"))
	if mode == "" {
		mode = "无"
	}
	explicit := []string{}
	orderKeys := []string{"网盘"}
	if mode == "网盘" {
		explicit = []string{"网盘"}
		orderKeys = []string{"网盘"}
	} else if mode == "关键字" {
		explicit = []string{"关键字"}
		orderKeys = []string{"关键字", "网盘"}
	}

	rawKeyword := parseJSONStringArray(database.GetSetting("smart_source_priority_tokens"))
	kw := make([]string, 0, len(rawKeyword))
	seen := map[string]bool{}
	for _, t := range rawKeyword {
		s := strings.ToLower(strings.TrimSpace(t))
		if s == "" || seen[s] {
			continue
		}
		seen[s] = true
		kw = append(kw, s)
	}

	rawPan := parseJSONStringArray(database.GetSetting("smart_pan_match_tokens"))
	pan := make([]string, 0, len(rawPan))
	seen2 := map[string]bool{}
	for _, t := range rawPan {
		s := strings.ToLower(strings.TrimSpace(t))
		if s == "" || seen2[s] {
			continue
		}
		seen2[s] = true
		pan = append(pan, s)
	}

	return smartPlaybackSettings{
		Mode:               mode,
		KeywordTokensLower: kw,
		PanTokenOrderLower: pan,
		OrderKeys:          orderKeys,
		ExplicitKeys:       explicit,
	}
}

func smartLabelTokenIdx(label string, panTokenOrderLower []string) int {
	s := strings.ToLower(strings.TrimSpace(label))
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

const (
	smartDetailFailCooldownBase = 30 * time.Second
	smartDetailFailCooldownMax  = 5 * time.Minute
)

type smartSource struct {
	SiteKey     string
	SiteName    string
	SpiderAPI   string
	VideoID     string
	VideoRemark string
	Score       int
	Seq         int
	NoNoise     bool
}

func smartBuildSourceKey(src smartSource) string {
	return strings.TrimSpace(src.SiteKey) + "::" + strings.TrimSpace(src.SpiderAPI) + "::" + strings.TrimSpace(src.VideoID)
}

func smartExtractSeasonHintFromSource(src smartSource) int {
	text := src.SiteName + " " + src.VideoRemark
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

func smartHasExplicitSeasonMarkerInSource(src smartSource) bool {
	t := strings.TrimSpace(src.SiteName + " " + src.VideoRemark)
	if t == "" {
		return false
	}
	return regexp.MustCompile(`(?i)(?:\bS\d{1,2}\b|第\s*\d{1,2}\s*季|season\s*\d{1,2})`).MatchString(t)
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

type smartCandidate struct {
	SiteKey          string
	SiteName         string
	SpiderAPI        string
	VideoID          string
	SrcRemarkLower   string
	PanLabel         string
	PanTokenIdx      int
	Ep               catpawopen.Episode
	RawLower         string
	MatchSeason      int
	HasSeasonMarker  bool
	SearchSeasonHint int
	MatchKeyword     smartPriorityMatch
}

type smartCandidateFeatures struct {
	HayLower     string
	Quality      string
	QualityRank  int
	Fps60        bool
	HasHdr       bool
	TierRank     int
	EnhanceMatch smartPriorityMatch
}

var smartEnhanceTokens = []string{"60fps", "60帧", "hdr", "ddp", "臻彩"}

func smartGuessQuality(hayRaw string) string {
	hay := strings.ToUpper(hayRaw)
	if regexp.MustCompile(`(2160P|2160|4K)`).MatchString(hay) {
		return "4K"
	}
	if regexp.MustCompile(`(1080P|1080)`).MatchString(hay) {
		return "1080P"
	}
	if regexp.MustCompile(`(720P|720)`).MatchString(hay) {
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
	return strings.TrimSpace(c.RawLower + " " + strings.ToLower(strings.TrimSpace(c.Ep.Name)) + " " + strings.ToLower(strings.TrimSpace(c.SrcRemarkLower)))
}

func smartComputeCandidateFeatures(c smartCandidate) smartCandidateFeatures {
	hayLower := smartBuildHayLower(c)
	quality := smartGuessQuality(hayLower)
	qualityRank := smartQualityRankOf(quality)
	enhance := smartComputePriorityMatch(hayLower, smartEnhanceTokens)
	idx := enhance.Indices
	hasHdr := containsInt(idx, 2)
	fps60 := smartGuessFps60(hayLower) || containsInt(idx, 0) || containsInt(idx, 1)
	tierRank := 10
	if qualityRank == 3 && hasHdr && fps60 {
		tierRank = 65
	} else if qualityRank == 3 && hasHdr {
		tierRank = 60
	} else if qualityRank == 3 && fps60 {
		tierRank = 55
	} else if qualityRank == 3 {
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

type smartDetailCacheEntry struct {
	OK                        bool
	FailCount                 int
	NextRetryAt               time.Time
	LastError                 string
	Source                    smartSource
	Pans                      []catpawopen.Pan
	PanMockEnabled            bool
	PanMock189AccessByShareID map[string]string
	EpisodeMap                map[int][]smartCandidate
	EpisodeMapLoose           map[int][]smartCandidate
}

var smartDetailCache = struct {
	sync.Mutex
	M        map[string]*smartDetailCacheEntry
	InFlight map[string]chan struct{}
}{
	M:        map[string]*smartDetailCacheEntry{},
	InFlight: map[string]chan struct{}{},
}

func smartGetSearchThreadCount(database *db.DB, u *embyUser) int {
	if database == nil {
		return 5
	}
	threadCount := 5
	if u != nil && strings.TrimSpace(u.Username) != "" {
		var n int
		_ = database.SQL().QueryRow(`SELECT search_thread_count FROM users WHERE username=? LIMIT 1`, strings.TrimSpace(u.Username)).Scan(&n)
		if n > 0 {
			threadCount = n
		}
	}
	if threadCount < 1 {
		threadCount = 5
	}
	if threadCount > 20 {
		threadCount = 20
	}
	return threadCount
}

func smartLoadSiteOrder(database *db.DB, u *embyUser) []string {
	if database == nil {
		return nil
	}
	if u != nil && strings.TrimSpace(u.Role) == "user" && strings.TrimSpace(u.Username) != "" {
		var raw string
		_ = database.SQL().QueryRow(`SELECT cat_search_order FROM users WHERE username=? LIMIT 1`, strings.TrimSpace(u.Username)).Scan(&raw)
		if strings.TrimSpace(raw) != "" {
			return parseJSONStringArray(raw)
		}
	}
	return parseJSONStringArray(database.GetSetting("video_source_site_order"))
}

func smartBuildAggregatedSources(database *db.DB, apiBase string, searchTitle string, u *embyUser) ([]smartSource, map[string]int) {
	sites := normalizeSitesFromJSON(database.GetSetting("video_source_sites"))
	statusMap := parseJSONBoolMap(database.GetSetting("video_source_site_status"))
	searchMap := parseJSONBoolMap(database.GetSetting("video_source_site_search"))
	ordered := applySiteOrder(sites, smartLoadSiteOrder(database, u))

	orderMap := map[string]int{}
	for i, s := range ordered {
		if s.Key == "" {
			continue
		}
		orderMap[s.Key] = i
	}

	qKey := embyNormalizeAggKey(searchTitle)
	seq := 0
	out := []smartSource{}
	for _, s := range ordered {
		if s.Key == "" || s.API == "" {
			continue
		}
		if isConfigCenterSite(s) {
			continue
		}
		if enabled, ok := statusMap[s.Key]; ok && !enabled {
			continue
		}
		if searchEnabled, ok := searchMap[s.Key]; ok && !searchEnabled {
			continue
		}
		raw, err := catpawopen.RequestSpider(apiBase, s.API, "search", map[string]any{"wd": searchTitle, "page": 1})
		if err != nil {
			continue
		}
		items := catpawopen.NormalizeSearchList(raw)
		for _, it := range items {
			name := strings.TrimSpace(it.Name)
			if strings.TrimSpace(it.ID) == "" || name == "" {
				continue
			}
			key := embyNormalizeAggKey(name)
			if key == "" {
				continue
			}
			score := embyMatchScore(qKey, key)
			if score <= 0 {
				continue
			}
			seq++
			out = append(out, smartSource{
				SiteKey:     s.Key,
				SiteName:    s.Name,
				SpiderAPI:   s.API,
				VideoID:     strings.TrimSpace(it.ID),
				VideoRemark: strings.TrimSpace(it.Remark),
				Score:       score,
				Seq:         seq,
				NoNoise:     key == qKey,
			})
			if len(out) >= 200 {
				break
			}
		}
		if len(out) >= 200 {
			break
		}
	}
	return out, orderMap
}

func smartBuildCandidates(aggregated []smartSource, orderMap map[string]int, tmdbHasMultiSeason bool, preferSeasonNo int, want int) []smartSource {
	cands := make([]smartSource, 0, len(aggregated))
	cands = append(cands, aggregated...)
	sort.SliceStable(cands, func(i, j int) bool {
		a := cands[i]
		b := cands[j]

		if tmdbHasMultiSeason && preferSeasonNo > 0 {
			as := smartExtractSeasonHintFromSource(a)
			bs := smartExtractSeasonHintFromSource(b)
			am := as == preferSeasonNo
			bm := bs == preferSeasonNo
			if am != bm {
				return am
			}
			aWrong := as > 0 && as != preferSeasonNo
			bWrong := bs > 0 && bs != preferSeasonNo
			if aWrong != bWrong {
				return !aWrong
			}
			aHas := smartHasExplicitSeasonMarkerInSource(a) || as > 0
			bHas := smartHasExplicitSeasonMarkerInSource(b) || bs > 0
			if aHas != bHas {
				return aHas
			}
		}

		ap := smartExtractMaxEpisodeFromBadgeText(a.VideoRemark)
		bp := smartExtractMaxEpisodeFromBadgeText(b.VideoRemark)
		aPrefer := ap >= want
		bPrefer := bp >= want
		if aPrefer != bPrefer {
			return aPrefer
		}

		an := a.NoNoise
		bn := b.NoNoise
		if an != bn {
			return bn
		}
		if a.Score != b.Score {
			return a.Score > b.Score
		}
		if a.Seq != b.Seq {
			return a.Seq < b.Seq
		}
		ao, ok := orderMap[a.SiteKey]
		if !ok {
			ao = 999999
		}
		bo, ok := orderMap[b.SiteKey]
		if !ok {
			bo = 999999
		}
		if ao != bo {
			return ao < bo
		}
		return strings.Compare(a.SiteName, b.SiteName) < 0
	})

	seen := map[string]bool{}
	unique := make([]smartSource, 0, len(cands))
	for _, s := range cands {
		k := smartBuildSourceKey(s)
		if k == "" || seen[k] {
			continue
		}
		seen[k] = true
		unique = append(unique, s)
	}

	// Prefer cached OK, then retryable, and push cooldown-failed to the end.
	now := time.Now()
	okCached := []smartSource{}
	retryable := []smartSource{}
	cooldownFailed := []smartSource{}
	for _, s := range unique {
		key := smartBuildSourceKey(s)
		rank := 1
		smartDetailCache.Lock()
		hit := smartDetailCache.M[key]
		smartDetailCache.Unlock()
		if hit != nil {
			if !hit.OK {
				if !hit.NextRetryAt.IsZero() && now.Before(hit.NextRetryAt) {
					rank = 2
				} else {
					rank = 1
				}
			} else if hit.EpisodeMap != nil && hit.EpisodeMapLoose != nil && hit.Pans != nil {
				rank = 0
			}
		}
		if rank == 0 {
			okCached = append(okCached, s)
		} else if rank == 2 {
			cooldownFailed = append(cooldownFailed, s)
		} else {
			retryable = append(retryable, s)
		}
	}
	out := make([]smartSource, 0, len(unique))
	out = append(out, okCached...)
	out = append(out, retryable...)
	out = append(out, cooldownFailed...)
	return out
}

func smartTMDBSeasonEpisodeOfGlobal(seasons []embyTMDBSeason, global int) smartSeasonEpisode {
	g := global
	if g <= 0 {
		return smartSeasonEpisode{Season: 0, Episode: 0}
	}
	left := g
	for _, it := range seasons {
		sn := it.Season
		cnt := it.EpisodeCount
		if sn <= 0 || cnt <= 0 {
			continue
		}
		if left > cnt {
			left -= cnt
			continue
		}
		return smartSeasonEpisode{Season: sn, Episode: left}
	}
	return smartSeasonEpisode{Season: 0, Episode: g}
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
	for _, it := range seasons {
		if it.Season == s {
			if it.EpisodeCount > 0 {
				seasonCount = it.EpisodeCount
			}
			break
		}
	}
	if seasonCount == 0 || e <= seasonCount {
		return smartSeasonEpisode{Season: s, Episode: e}
	}
	mapped := smartTMDBSeasonEpisodeOfGlobal(seasons, e)
	if mapped.Episode <= 0 {
		return smartSeasonEpisode{Season: s, Episode: e}
	}
	if mapped.Season != 0 && mapped.Season != s {
		return smartSeasonEpisode{Season: s, Episode: e}
	}
	if mapped.Episode > seasonCount {
		return smartSeasonEpisode{Season: s, Episode: e}
	}
	return smartSeasonEpisode{Season: s, Episode: mapped.Episode}
}

func smartLoadOrBuildDetailCache(database *db.DB, apiBase string, src smartSource, tmdbSeasons []embyTMDBSeason, tmdbHasMultiSeason bool, settings smartPlaybackSettings, rawCleanRules []string, rawEpisodeRules []string) *smartDetailCacheEntry {
	key := smartBuildSourceKey(src)
	if key == "" {
		return nil
	}

	now := time.Now()
	smartDetailCache.Lock()
	if hit, ok := smartDetailCache.M[key]; ok && hit != nil {
		if !hit.OK {
			if !hit.NextRetryAt.IsZero() && now.Before(hit.NextRetryAt) {
				smartDetailCache.Unlock()
				return hit
			}
		} else if hit.EpisodeMap != nil && hit.EpisodeMapLoose != nil && hit.Pans != nil {
			smartDetailCache.Unlock()
			return hit
		}
	}
	if ch, ok := smartDetailCache.InFlight[key]; ok && ch != nil {
		smartDetailCache.Unlock()
		<-ch
		smartDetailCache.Lock()
		hit := smartDetailCache.M[key]
		smartDetailCache.Unlock()
		return hit
	}
	ch := make(chan struct{})
	smartDetailCache.InFlight[key] = ch
	smartDetailCache.Unlock()

	go func() {
		defer close(ch)
		defer func() {
			smartDetailCache.Lock()
			delete(smartDetailCache.InFlight, key)
			smartDetailCache.Unlock()
		}()

		entry := &smartDetailCacheEntry{
			OK:                        false,
			FailCount:                 0,
			NextRetryAt:               time.Time{},
			LastError:                 "",
			Source:                    src,
			Pans:                      []catpawopen.Pan{},
			EpisodeMap:                map[int][]smartCandidate{},
			EpisodeMapLoose:           map[int][]smartCandidate{},
			PanMock189AccessByShareID: map[string]string{},
		}

		detailRaw, err := catpawopen.RequestSpider(apiBase, src.SpiderAPI, "detail", map[string]any{"id": src.VideoID})
		if err != nil {
			smartDetailCache.Lock()
			prev := smartDetailCache.M[key]
			prevCount := 0
			if prev != nil && !prev.OK && prev.FailCount > 0 {
				prevCount = prev.FailCount
			}
			failCount := prevCount + 1
			delay := time.Duration(math.Min(float64(smartDetailFailCooldownMax), float64(smartDetailFailCooldownBase)*math.Pow(2, float64(smartMaxInt(0, failCount-1)))))
			entry.FailCount = failCount
			entry.LastError = "detail request failed"
			entry.NextRetryAt = time.Now().Add(delay)
			smartDetailCache.M[key] = entry
			smartDetailCache.Unlock()
			return
		}
		if v, ok := detailRaw["pan_mock"]; ok {
			switch x := v.(type) {
			case bool:
				entry.PanMockEnabled = x
			case string:
				entry.PanMockEnabled = strings.TrimSpace(x) == "1" || strings.EqualFold(strings.TrimSpace(x), "true")
			case float64:
				entry.PanMockEnabled = int(x) == 1
			}
		}
		playFrom, playURL := catpawopen.ExtractDetailPlayFromURL(detailRaw)
		pans := catpawopen.ParsePlaySources(playFrom, playURL)
		if entry.PanMockEnabled && pans != nil {
			resolved, accessMap := embyResolvePanMockDetailPans(database, pans)
			pans = resolved
			if len(accessMap) > 0 {
				entry.PanMock189AccessByShareID = accessMap
			}
		}
		entry.Pans = pans

		srcRemarkLower := strings.ToLower(strings.TrimSpace(src.VideoRemark))
		for _, pan := range pans {
			panLabel := strings.TrimSpace(pan.Label)
			panTokenIdx := smartLabelTokenIdx(panLabel, settings.PanTokenOrderLower)
			for _, ep := range pan.Episodes {
				if strings.TrimSpace(ep.URL) == "" {
					continue
				}
				texts := smartExtractEpisodeCandidateTexts(ep)
				primary := ""
				if len(texts) > 0 {
					primary = texts[0]
				}
				if primary == "" {
					primary = ep.Name
				}
				rawLower := smartBuildCandidateLowerText(texts)
				if rawLower == "" {
					rawLower = strings.ToLower(strings.TrimSpace(primary))
				}

				if len(rawCleanRules) == 0 || len(rawEpisodeRules) == 0 {
					entry.FailCount++
					entry.LastError = "missing magic regex rules"
					entry.NextRetryAt = time.Now().Add(10 * time.Minute)
					smartDetailCache.Lock()
					smartDetailCache.M[key] = entry
					smartDetailCache.Unlock()
					return
				}
				jsMatch, err := magic.MagicEpisodeExtractFromCandidates(texts, rawCleanRules, rawEpisodeRules)
				if err != nil {
					entry.FailCount++
					entry.LastError = "js regex error"
					entry.NextRetryAt = time.Now().Add(10 * time.Minute)
					smartDetailCache.Lock()
					smartDetailCache.M[key] = entry
					smartDetailCache.Unlock()
					return
				}
				match := smartNormalizeMaybeGlobalSeasonEpisode(tmdbSeasons, smartSeasonEpisode{Season: jsMatch.Season, Episode: jsMatch.Episode})
				seasonNo := match.Season
				epNo := match.Episode
				if epNo <= 0 {
					continue
				}

				cand := smartCandidate{
					SiteKey:          src.SiteKey,
					SiteName:         src.SiteName,
					SpiderAPI:        src.SpiderAPI,
					VideoID:          src.VideoID,
					SrcRemarkLower:   srcRemarkLower,
					PanLabel:         panLabel,
					PanTokenIdx:      panTokenIdx,
					Ep:               ep,
					RawLower:         rawLower,
					MatchSeason:      seasonNo,
					HasSeasonMarker:  seasonNo > 0,
					SearchSeasonHint: 0,
					MatchKeyword:     smartComputePriorityMatch(rawLower, settings.KeywordTokensLower),
				}

				if tmdbHasMultiSeason && seasonNo <= 0 {
					entry.EpisodeMapLoose[epNo] = append(entry.EpisodeMapLoose[epNo], cand)
					continue
				}

				keyNo := epNo
				if seasonNo > 0 {
					if g := smartTMDBGlobalEpisodeNoOf(tmdbSeasons, seasonNo, epNo); g > 0 {
						keyNo = g
					}
				}
				entry.EpisodeMap[keyNo] = append(entry.EpisodeMap[keyNo], cand)
			}
		}

		entry.OK = true
		smartDetailCache.Lock()
		smartDetailCache.M[key] = entry
		smartDetailCache.Unlock()
	}()

	<-ch
	smartDetailCache.Lock()
	hit := smartDetailCache.M[key]
	smartDetailCache.Unlock()
	return hit
}

func smartPickBestMatch(list []smartCandidate, tmdbHasMultiSeason bool, preferSeasonNo int, settings smartPlaybackSettings) *smartCandidate {
	items := make([]smartCandidate, 0, len(list))
	for _, it := range list {
		items = append(items, it)
	}
	if len(items) == 0 {
		return nil
	}
	best := items[0]
	for i := 1; i < len(items); i++ {
		if smartCompareSmartMatch(best, items[i], tmdbHasMultiSeason, preferSeasonNo, settings) > 0 {
			best = items[i]
		}
	}
	return &best
}

type smartPickResult struct {
	Cand    smartCandidate
	PlayURL string
	Headers map[string]string
}

func smartFetchDetailAndPickAndPlay(database *db.DB, apiBase string, tvUser string, src smartSource, tmdbSeasons []embyTMDBSeason, tmdbHasMultiSeason bool, preferSeasonNo int, want int, settings smartPlaybackSettings, rawCleanRules []string, rawEpisodeRules []string, requireSeasoned bool) *smartPickResult {
	siteKey := strings.TrimSpace(src.SiteKey)
	spiderApi := strings.TrimSpace(src.SpiderAPI)
	videoId := strings.TrimSpace(src.VideoID)
	if siteKey == "" || spiderApi == "" || videoId == "" || want <= 0 {
		return nil
	}
	searchSeasonHint := smartExtractSeasonHintFromSource(src)

	cache := smartLoadOrBuildDetailCache(database, apiBase, src, tmdbSeasons, tmdbHasMultiSeason, settings, rawCleanRules, rawEpisodeRules)
	if cache == nil || !cache.OK {
		return nil
	}

	wantedInSeason := smartSeasonEpisode{Season: 0, Episode: want}
	if tmdbHasMultiSeason {
		wantedInSeason = smartTMDBSeasonEpisodeOfGlobal(tmdbSeasons, want)
	}
	wantSeasonNo := wantedInSeason.Season
	wantSeasonEp := wantedInSeason.Episode

	candidatesForNo := []smartCandidate{}
	if cache.EpisodeMap != nil {
		if list, ok := cache.EpisodeMap[want]; ok && len(list) > 0 {
			for _, c := range list {
				c.SearchSeasonHint = searchSeasonHint
				candidatesForNo = append(candidatesForNo, c)
			}
		}
	}

	if requireSeasoned {
		filtered := make([]smartCandidate, 0, len(candidatesForNo))
		for _, c := range candidatesForNo {
			if c.MatchSeason > 0 {
				filtered = append(filtered, c)
			}
		}
		candidatesForNo = filtered
	} else if tmdbHasMultiSeason && wantSeasonEp > 0 {
		loose := []smartCandidate{}
		if cache.EpisodeMapLoose != nil {
			if list, ok := cache.EpisodeMapLoose[wantSeasonEp]; ok && len(list) > 0 {
				for _, c := range list {
					c.SearchSeasonHint = searchSeasonHint
					loose = append(loose, c)
				}
			}
		}
		if len(loose) > 0 {
			looseFiltered := []smartCandidate{}
			if wantSeasonNo > 0 {
				for _, c := range loose {
					hinted := c.SearchSeasonHint
					if hinted == 0 || hinted == wantSeasonNo {
						looseFiltered = append(looseFiltered, c)
					}
				}
			}
			if len(looseFiltered) > 0 {
				candidatesForNo = append(candidatesForNo, looseFiltered...)
			} else {
				candidatesForNo = append(candidatesForNo, loose...)
			}
		}
	}

	if len(candidatesForNo) == 0 {
		return nil
	}

	if cache.PanMockEnabled {
		type groupAttempt struct {
			Key      string
			Allowed  bool
			Provider string
			Base     smartCandidate
			MetaA    string // provider-specific meta: shareCode for tianyi, passcode for others
			MetaB    string // provider-specific meta: accessCode for tianyi
		}

		allowedTokens := settings.PanTokenOrderLower
		isAllowedLabel := func(label string) bool {
			if len(allowedTokens) == 0 {
				return true
			}
			ll := strings.ToLower(strings.TrimSpace(label))
			for _, t := range allowedTokens {
				tt := strings.ToLower(strings.TrimSpace(t))
				if tt == "" {
					continue
				}
				if strings.Contains(ll, tt) {
					return true
				}
			}
			return false
		}

		groups := map[string]groupAttempt{}
		normalAllowed := []smartCandidate{}
		normalFallback := []smartCandidate{}
		for _, c := range candidatesForNo {
			pid := smartPanMockProviderID(c.PanLabel)
			allowed := isAllowedLabel(c.PanLabel)
			if pid == "" {
				if allowed {
					normalAllowed = append(normalAllowed, c)
				} else {
					normalFallback = append(normalFallback, c)
				}
				continue
			}

			shareKey := strings.TrimSpace(c.PanLabel)
			metaA := ""
			metaB := ""
			if pid == "tianyi" {
				sc, ac := smartExtractTianyiMockMetaFromCandidate(c)
				metaA = sc
				metaB = ac
				if sc != "" {
					shareKey = sc
				}
			} else {
				metaA = smartExtractMockPasscodeFromCandidate(c)
			}
			key := pid + "|" + shareKey
			if prev, ok := groups[key]; ok {
				best := smartPickBestMatchIgnorePanOrder([]smartCandidate{prev.Base, c}, tmdbHasMultiSeason, preferSeasonNo, settings)
				if best != nil && strings.TrimSpace(best.Ep.URL) != strings.TrimSpace(prev.Base.Ep.URL) {
					prev.Base = c
				}
				groups[key] = prev
				continue
			}
			groups[key] = groupAttempt{
				Key:      key,
				Allowed:  allowed,
				Provider: pid,
				Base:     c,
				MetaA:    metaA,
				MetaB:    metaB,
			}
		}

		resolveFromVod := func(base smartCandidate, vodPlayURL string) *smartCandidate {
			eps := smartParseVodPlayURLToEpisodes(vodPlayURL)
			if len(eps) == 0 {
				return nil
			}
			matches := []smartCandidate{}
			for _, ep := range eps {
				if strings.TrimSpace(ep.URL) == "" {
					continue
				}
				texts := smartExtractEpisodeCandidateTexts(ep)
				if len(texts) == 0 || strings.TrimSpace(texts[0]) == "" {
					texts = []string{strings.TrimSpace(ep.Name)}
				}
				jsMatch, err := magic.MagicEpisodeExtractFromCandidates(texts, rawCleanRules, rawEpisodeRules)
				if err != nil {
					continue
				}
				match := smartNormalizeMaybeGlobalSeasonEpisode(tmdbSeasons, smartSeasonEpisode{Season: jsMatch.Season, Episode: jsMatch.Episode})
				seasonNo := match.Season
				epNo := match.Episode
				if epNo <= 0 {
					continue
				}
				keyNo := epNo
				if seasonNo > 0 {
					if g := smartTMDBGlobalEpisodeNoOf(tmdbSeasons, seasonNo, epNo); g > 0 {
						keyNo = g
					}
				}
				if keyNo != want {
					continue
				}
				rawLower := smartBuildCandidateLowerText(texts)
				if rawLower == "" {
					rawLower = strings.ToLower(strings.TrimSpace(ep.Name))
				}
				cand := base
				cand.Ep = ep
				cand.RawLower = rawLower
				cand.MatchSeason = seasonNo
				cand.HasSeasonMarker = seasonNo > 0
				cand.MatchKeyword = smartComputePriorityMatch(rawLower, settings.KeywordTokensLower)
				matches = append(matches, cand)
			}
			if len(matches) == 0 {
				return nil
			}
			return smartPickBestMatchIgnorePanOrder(matches, tmdbHasMultiSeason, preferSeasonNo, settings)
		}

		tryGroup := func(at groupAttempt) *smartPickResult {
			base := at.Base
			switch at.Provider {
			case "tianyi":
				sc := strings.TrimSpace(at.MetaA)
				ac := strings.TrimSpace(at.MetaB)
				if sc == "" {
					sc2, ac2 := smartExtractTianyiMockMetaFromCandidate(base)
					sc = strings.TrimSpace(sc2)
					if ac == "" {
						ac = strings.TrimSpace(ac2)
					}
				}
				if strings.TrimSpace(ac) == "" && cache != nil && len(cache.PanMock189AccessByShareID) > 0 {
					parts := strings.Split(strings.TrimSpace(base.Ep.URL), "*")
					if len(parts) >= 2 {
						shareID := strings.TrimSpace(parts[1])
						if shareID != "" {
							if v, ok := cache.PanMock189AccessByShareID[shareID]; ok {
								ac = strings.TrimSpace(v)
							}
						}
					}
				}
				if u, _, _, _, err := netdisk.Tianyi189Play(database, strings.TrimSpace(base.Ep.URL), ac); err == nil && strings.TrimSpace(u) != "" {
					return &smartPickResult{Cand: base, PlayURL: strings.TrimSpace(u), Headers: map[string]string{}}
				}
				if sc == "" {
					return nil
				}
				flag := "天意-" + sc
				vod, _, _, err := netdisk.Tianyi189List(database, flag, ac)
				if err != nil {
					return nil
				}
				picked := resolveFromVod(base, vod)
				if picked == nil || strings.TrimSpace(picked.Ep.URL) == "" {
					return nil
				}
				if strings.TrimSpace(ac) == "" && cache != nil && len(cache.PanMock189AccessByShareID) > 0 {
					parts := strings.Split(strings.TrimSpace(picked.Ep.URL), "*")
					if len(parts) >= 2 {
						shareID := strings.TrimSpace(parts[1])
						if shareID != "" {
							if v, ok := cache.PanMock189AccessByShareID[shareID]; ok {
								ac = strings.TrimSpace(v)
							}
						}
					}
				}
				u, _, _, _, err := netdisk.Tianyi189Play(database, picked.Ep.URL, ac)
				if err != nil || strings.TrimSpace(u) == "" {
					return nil
				}
				return &smartPickResult{Cand: *picked, PlayURL: u, Headers: map[string]string{}}
			case "quark":
				if strings.TrimSpace(at.MetaA) == "" {
					u, header, err := netdisk.QuarkPlay(database, strings.TrimSpace(base.Ep.URL), "")
					if err == nil && strings.TrimSpace(u) != "" {
						if header == nil {
							header = map[string]string{}
						}
						return &smartPickResult{Cand: base, PlayURL: strings.TrimSpace(u), Headers: header}
					}
				}
				vod, _, err := netdisk.QuarkList(database, strings.TrimSpace(base.PanLabel), strings.TrimSpace(at.MetaA))
				if err != nil {
					return nil
				}
				picked := resolveFromVod(base, vod)
				if picked == nil || strings.TrimSpace(picked.Ep.URL) == "" {
					return nil
				}
				u, header, err := netdisk.QuarkPlay(database, picked.Ep.URL, "")
				if err != nil || strings.TrimSpace(u) == "" {
					return nil
				}
				if header == nil {
					header = map[string]string{}
				}
				return &smartPickResult{Cand: *picked, PlayURL: u, Headers: header}
			case "uc":
				if strings.TrimSpace(at.MetaA) == "" {
					u, header, err := netdisk.UCPlay(database, strings.TrimSpace(base.Ep.URL), "")
					if err == nil && strings.TrimSpace(u) != "" {
						if header == nil {
							header = map[string]string{}
						}
						return &smartPickResult{Cand: base, PlayURL: strings.TrimSpace(u), Headers: header}
					}
				}
				vod, _, err := netdisk.UCList(database, strings.TrimSpace(base.PanLabel), strings.TrimSpace(at.MetaA))
				if err != nil {
					return nil
				}
				picked := resolveFromVod(base, vod)
				if picked == nil || strings.TrimSpace(picked.Ep.URL) == "" {
					return nil
				}
				u, header, err := netdisk.UCPlay(database, picked.Ep.URL, "")
				if err != nil || strings.TrimSpace(u) == "" {
					return nil
				}
				if header == nil {
					header = map[string]string{}
				}
				return &smartPickResult{Cand: *picked, PlayURL: u, Headers: header}
			case "139":
				{
					downloadURL, playURL, err := netdisk.Yun139Play(database, strings.TrimSpace(base.PanLabel), strings.TrimSpace(base.Ep.URL))
					u := strings.TrimSpace(playURL)
					if u == "" {
						u = strings.TrimSpace(downloadURL)
					}
					if err == nil && u != "" {
						return &smartPickResult{Cand: base, PlayURL: u, Headers: map[string]string{}}
					}
				}
				vod, _, err := netdisk.Yun139List(database, strings.TrimSpace(base.PanLabel))
				if err != nil {
					return nil
				}
				picked := resolveFromVod(base, vod)
				if picked == nil || strings.TrimSpace(picked.Ep.URL) == "" {
					return nil
				}
				downloadURL, playURL, err := netdisk.Yun139Play(database, strings.TrimSpace(base.PanLabel), picked.Ep.URL)
				u := strings.TrimSpace(playURL)
				if u == "" {
					u = strings.TrimSpace(downloadURL)
				}
				if err != nil || u == "" {
					return nil
				}
				return &smartPickResult{Cand: *picked, PlayURL: u, Headers: map[string]string{}}
			case "baidu":
				if strings.TrimSpace(at.MetaA) == "" {
					u, header, err := netdisk.BaiduPlay(database, strings.TrimSpace(base.PanLabel), strings.TrimSpace(base.Ep.URL), "/MeowFilm")
					if err == nil && strings.TrimSpace(u) != "" {
						if header == nil {
							header = map[string]string{}
						}
						return &smartPickResult{Cand: base, PlayURL: strings.TrimSpace(u), Headers: header}
					}
				}
				vod, _, err := netdisk.BaiduList(database, strings.TrimSpace(base.PanLabel), strings.TrimSpace(at.MetaA))
				if err != nil {
					return nil
				}
				picked := resolveFromVod(base, vod)
				if picked == nil || strings.TrimSpace(picked.Ep.URL) == "" {
					return nil
				}
				u, header, err := netdisk.BaiduPlay(database, strings.TrimSpace(base.PanLabel), picked.Ep.URL, "/MeowFilm")
				if err != nil || strings.TrimSpace(u) == "" {
					return nil
				}
				if header == nil {
					header = map[string]string{}
				}
				return &smartPickResult{Cand: *picked, PlayURL: strings.TrimSpace(u), Headers: header}
			default:
				return nil
			}
		}

		allowedAttempts := []groupAttempt{}
		fallbackAttempts := []groupAttempt{}
		for _, at := range groups {
			if at.Allowed {
				allowedAttempts = append(allowedAttempts, at)
			} else {
				fallbackAttempts = append(fallbackAttempts, at)
			}
		}

		if len(allowedAttempts) > 0 {
			resCh := make(chan *smartPickResult, len(allowedAttempts))
			var wg sync.WaitGroup
			for _, at := range allowedAttempts {
				wg.Add(1)
				go func(a groupAttempt) {
					defer wg.Done()
					if res := tryGroup(a); res != nil && strings.TrimSpace(res.PlayURL) != "" {
						resCh <- res
					}
				}(at)
			}
			go func() {
				wg.Wait()
				close(resCh)
			}()
			if res, ok := <-resCh; ok && res != nil {
				return res
			}
		}

		tryNormalPlay := func(best *smartCandidate) *smartPickResult {
			if best == nil || strings.TrimSpace(best.Ep.URL) == "" {
				return nil
			}
			siteID := catpawopen.ExtractSiteIDFromSpiderAPI(spiderApi)
			playPayload := map[string]any{
				"flag":    strings.TrimSpace(best.Ep.Flag),
				"id":      strings.TrimSpace(best.Ep.URL),
				"siteApi": spiderApi,
			}
			if siteID != "" {
				playPayload["siteId"] = siteID
			}
			playRaw, err := catpawopen.RequestPlay(apiBase, tvUser, playPayload)
			if err != nil {
				return nil
			}
			urlPicked := strings.TrimSpace(catpawopen.PickFirstPlayableURL(playRaw))
			if urlPicked == "" {
				return nil
			}
			urlPicked = catpawopen.RewriteProxyURLToBase(urlPicked, apiBase, tvUser)
			headers := map[string]string{}
			if h, ok := playRaw["header"].(map[string]any); ok {
				for k, v := range h {
					kk := strings.TrimSpace(k)
					if kk == "" {
						continue
					}
					sv := strings.TrimSpace(embyAnyToString(v))
					if sv == "" {
						continue
					}
					headers[kk] = sv
				}
			}
			return &smartPickResult{Cand: *best, PlayURL: urlPicked, Headers: headers}
		}

		if len(normalAllowed) > 0 {
			best := smartPickBestMatchIgnorePanOrder(normalAllowed, tmdbHasMultiSeason, preferSeasonNo, settings)
			if res := tryNormalPlay(best); res != nil {
				return res
			}
		}
		if len(normalFallback) > 0 {
			best := smartPickBestMatchIgnorePanOrder(normalFallback, tmdbHasMultiSeason, preferSeasonNo, settings)
			if res := tryNormalPlay(best); res != nil {
				return res
			}
		}
		for _, at := range fallbackAttempts {
			if res := tryGroup(at); res != nil && strings.TrimSpace(res.PlayURL) != "" {
				return res
			}
		}
	}

	best := smartPickBestMatch(candidatesForNo, tmdbHasMultiSeason, preferSeasonNo, settings)
	if best == nil || strings.TrimSpace(best.Ep.URL) == "" {
		return nil
	}

	// Verify by calling play and ensure we have a playable url.
	siteID := catpawopen.ExtractSiteIDFromSpiderAPI(spiderApi)
	playPayload := map[string]any{
		"flag":    strings.TrimSpace(best.Ep.Flag),
		"id":      strings.TrimSpace(best.Ep.URL),
		"siteApi": spiderApi,
	}
	if siteID != "" {
		playPayload["siteId"] = siteID
	}
	playRaw, err := catpawopen.RequestPlay(apiBase, tvUser, playPayload)
	if err != nil {
		return nil
	}
	urlPicked := catpawopen.PickFirstPlayableURL(playRaw)
	if strings.TrimSpace(urlPicked) == "" {
		return nil
	}
	urlPicked = catpawopen.RewriteProxyURLToBase(urlPicked, apiBase, tvUser)
	headers := map[string]string{}
	if h, ok := playRaw["header"].(map[string]any); ok {
		for k, v := range h {
			kk := strings.TrimSpace(k)
			if kk == "" {
				continue
			}
			sv := strings.TrimSpace(embyAnyToString(v))
			if sv == "" {
				continue
			}
			headers[kk] = sv
		}
	}
	return &smartPickResult{Cand: *best, PlayURL: urlPicked, Headers: headers}
}

func smartResolvePlaybackFromTMDB(database *db.DB, u *embyUser, req smartPlaybackRequest) (finalURL string, finalHeaders map[string]string, err error) {
	if database == nil {
		return "", nil, errors.New("invalid database")
	}
	if req.TMDBID <= 0 {
		return "", nil, errors.New("invalid tmdb id")
	}

	apiBase := embyResolveCatApiBaseForUser(database, u)
	if apiBase == "" {
		return "", nil, errors.New("CatPawOpen 接口地址未设置")
	}
	tvUser := ""
	if u != nil {
		tvUser = u.Username
	}

	// Resolve TMDB title and want episode (global index for tv).
	searchTitle := ""
	want := 1
	tmdbSeasons := []embyTMDBSeason{}
	if strings.TrimSpace(req.Kind) == "movie" {
		md, err := embyTMDBGetMovieDetail(database, req.TMDBID)
		if err != nil || md == nil || strings.TrimSpace(md.Title) == "" {
			return "", nil, errors.New("TMDB 请求失败")
		}
		searchTitle = strings.TrimSpace(md.Title)
		want = 1
	} else if strings.TrimSpace(req.Kind) == "tv" {
		td, err := embyTMDBGetTVDetail(database, req.TMDBID)
		if err != nil || td == nil || strings.TrimSpace(td.Title) == "" {
			return "", nil, errors.New("TMDB 请求失败")
		}
		searchTitle = strings.TrimSpace(td.Title)
		for _, s := range td.Seasons {
			if s.Season > 0 && s.EpisodeCount > 0 {
				tmdbSeasons = append(tmdbSeasons, s)
			}
		}
		if strings.TrimSpace(req.SubKind) == "episode" {
			want = smartTMDBGlobalEpisodeNoOf(tmdbSeasons, req.Season, req.Episode)
			if want <= 0 {
				want = 1
			}
		} else {
			want = 1
		}
	} else {
		return "", nil, errors.New("unsupported kind")
	}
	if strings.TrimSpace(searchTitle) == "" {
		return "", nil, errors.New("missing title")
	}

	settings := smartLoadPlaybackSettings(database)
	rawEpisodeRules := parseJSONStringArray(database.GetSetting("magic_episode_rules"))
	rawCleanRules := parseJSONStringArray(database.GetSetting("magic_episode_clean_regex_rules"))
	if len(rawEpisodeRules) == 0 || len(rawCleanRules) == 0 {
		return "", nil, errors.New("magic regex rules 未设置")
	}

	// Search sources and build candidates list.
	aggregated, orderMap := smartBuildAggregatedSources(database, apiBase, searchTitle, u)
	if len(aggregated) == 0 {
		return "", nil, errors.New("未找到可用资源")
	}

	// If TMDB suggests single-season but sources clearly indicate multi-season,
	// probe Douban and only apply it when it actually remaps the requested global episode
	// into a later season (and matches the max season hinted by sources).
	if strings.TrimSpace(req.Kind) == "tv" && strings.TrimSpace(req.SubKind) == "episode" && len(tmdbSeasons) < 2 && strings.TrimSpace(searchTitle) != "" {
		maxHint := 0
		for _, src := range aggregated {
			if h := smartExtractSeasonHintFromSource(src); h > maxHint {
				maxHint = h
			}
		}
		if maxHint >= 2 {
			if over, ok := doubanProbeSeasons(database, req.TMDBID, searchTitle); ok && len(over) >= 2 {
				mapped := smartTMDBSeasonEpisodeOfGlobal(over, want)
				if mapped.Season >= 2 && mapped.Season <= maxHint {
					tmdbSeasons = over
					if embyDebugLogEnabled() {
						embyDebugPrintf("[smart][douban] override tmdbId=%d want=%d sourcesMaxSeason=%d -> mapped=S%02dE%03d", req.TMDBID, want, maxHint, mapped.Season, mapped.Episode)
					}
				}
			}
		}
	}

	tmdbHasMultiSeason := len(tmdbSeasons) >= 2
	preferSeasonNo := 0
	if tmdbHasMultiSeason {
		mapped := smartTMDBSeasonEpisodeOfGlobal(tmdbSeasons, want)
		if mapped.Season > 0 {
			preferSeasonNo = mapped.Season
		} else if req.Season > 0 {
			preferSeasonNo = req.Season
		}
		if embyDebugLogEnabled() && mapped.Season > 0 && req.Season > 0 && mapped.Season != req.Season {
			embyDebugPrintf("[smart][douban] season_remap tmdbId=%d want=%d req=S%02dE%03d -> prefer=S%02dE%03d", req.TMDBID, want, req.Season, req.Episode, mapped.Season, mapped.Episode)
		}
	}

	candidates := smartBuildCandidates(aggregated, orderMap, tmdbHasMultiSeason, preferSeasonNo, want)
	if len(candidates) == 0 {
		return "", nil, errors.New("未找到可用资源")
	}

	concurrency := smartGetSearchThreadCount(database, u)
	if concurrency < 1 {
		concurrency = 5
	}

	debugLog := embyDebugLogEnabled()

	tryPickOnce := func(requireSeasoned bool) *smartPickResult {
		hasPanOrder := len(settings.PanTokenOrderLower) > 0
		bestPreferredNoHeaders := (*smartPickResult)(nil)
		bestOtherNoHeaders := (*smartPickResult)(nil)
		bestPreferredWithHeaders := (*smartPickResult)(nil)
		bestOtherWithHeaders := (*smartPickResult)(nil)
		poolSize := concurrency
		if poolSize < 1 {
			poolSize = 1
		}
		cursor := 0
		type settled struct {
			Idx int
			Res *smartPickResult
		}
		results := make(chan settled, len(candidates))
		inFlight := 0
		deadline := time.Now().Add(18 * time.Second)

		launch := func(idx int) {
			src := candidates[idx]
			go func() {
				res := smartFetchDetailAndPickAndPlay(database, apiBase, tvUser, src, tmdbSeasons, tmdbHasMultiSeason, preferSeasonNo, want, settings, rawCleanRules, rawEpisodeRules, requireSeasoned)
				results <- settled{Idx: idx, Res: res}
			}()
		}

		for cursor < len(candidates) && inFlight < poolSize {
			launch(cursor)
			cursor++
			inFlight++
		}

		for inFlight > 0 {
			remain := time.Until(deadline)
			if remain <= 0 {
				break
			}
			var got settled
			timedOut := false
			select {
			case got = <-results:
			case <-time.After(remain):
				timedOut = true
			}
			if timedOut {
				break
			}
			inFlight--
			if got.Res != nil && strings.TrimSpace(got.Res.PlayURL) != "" {
				isPreferredPan := hasPanOrder && got.Res.Cand.PanTokenIdx >= 0
				if len(got.Res.Headers) == 0 {
					if isPreferredPan {
						if bestPreferredNoHeaders == nil || smartCompareSmartMatch(bestPreferredNoHeaders.Cand, got.Res.Cand, tmdbHasMultiSeason, preferSeasonNo, settings) > 0 {
							bestPreferredNoHeaders = got.Res
						}
						// If we have a clear season fit, return early; otherwise keep collecting preferred results
						// to reduce the chance of picking a wrong-season source just because it returned first.
						if tmdbHasMultiSeason && preferSeasonNo > 0 && (got.Res.Cand.MatchSeason == preferSeasonNo || got.Res.Cand.SearchSeasonHint == preferSeasonNo) {
							return bestPreferredNoHeaders
						}
					}
					if bestOtherNoHeaders == nil || smartCompareSmartMatch(bestOtherNoHeaders.Cand, got.Res.Cand, tmdbHasMultiSeason, preferSeasonNo, settings) > 0 {
						bestOtherNoHeaders = got.Res
					}
				} else {
					if isPreferredPan {
						if bestPreferredWithHeaders == nil || smartCompareSmartMatch(bestPreferredWithHeaders.Cand, got.Res.Cand, tmdbHasMultiSeason, preferSeasonNo, settings) > 0 {
							bestPreferredWithHeaders = got.Res
						}
					} else {
						if bestOtherWithHeaders == nil || smartCompareSmartMatch(bestOtherWithHeaders.Cand, got.Res.Cand, tmdbHasMultiSeason, preferSeasonNo, settings) > 0 {
							bestOtherWithHeaders = got.Res
						}
					}
				}
			}
			if cursor < len(candidates) && time.Now().Before(deadline) {
				launch(cursor)
				cursor++
				inFlight++
			}
		}
		if bestPreferredNoHeaders != nil {
			return bestPreferredNoHeaders
		}
		if bestOtherNoHeaders != nil {
			return bestOtherNoHeaders
		}
		if bestPreferredWithHeaders != nil {
			return bestPreferredWithHeaders
		}
		return bestOtherWithHeaders
	}

	best := tryPickOnce(true)
	if best == nil {
		best = tryPickOnce(false)
	}
	if best == nil || strings.TrimSpace(best.PlayURL) == "" {
		return "", nil, errors.New("无可用播放地址")
	}

	if debugLog {
		embyDebugPrintf("[smartplay] picked site=%s pan=%q url=%q headers=%d", best.Cand.SiteKey, best.Cand.PanLabel, best.PlayURL, len(best.Headers))
	}
	return best.PlayURL, best.Headers, nil
}

func containsInt(list []int, v int) bool {
	for _, x := range list {
		if x == v {
			return true
		}
	}
	return false
}

func intFromDigits(s string) int {
	n := 0
	for _, ch := range strings.TrimSpace(s) {
		if ch < '0' || ch > '9' {
			return 0
		}
		n = n*10 + int(ch-'0')
	}
	return n
}

func smartMinInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}

func smartMaxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}
