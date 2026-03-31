package smart

import (
	"context"
	"errors"
	"hash/fnv"
	"math"
	"sort"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/cache"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/config"
	"github.com/jenfonro/meowfilm/server/magic"
	metadata_tmdb "github.com/jenfonro/meowfilm/server/metadata/tmdb"
	"github.com/jenfonro/meowfilm/server/metadata/tvmeta"
	"github.com/jenfonro/meowfilm/server/netdisk"
	"github.com/jenfonro/meowfilm/server/sites"
)

type smartPlaybackRequest struct {
	Kind    string // "movie" | "tv"
	TMDBID  int
	Season  int
	Episode int
	SubKind string // "movie" | "episode"
}

var smartPlayTrySeq uint64
var smartPlayFlowSeq uint64

const (
	smartPlayTryTimeout       = 5 * time.Second
	smartPlayMaxPendingOffers = 5000
)

type smartPanMatchEntry struct {
	TokenLower string
	PanLower   string
}

type smartMatchBlockEntry struct {
	BlockAll bool
	PanFlags map[string]struct{}
}

func smartMatchBlockKeyword(searchTitle string, aggregateRules []string) string {
	title := strings.TrimSpace(searchTitle)
	if title == "" {
		return ""
	}
	if len(aggregateRules) > 0 {
		if out, err := magic.MagicAggregateNormalize(title, aggregateRules); err == nil {
			if cleaned := strings.TrimSpace(out); cleaned != "" {
				return cleaned
			}
		}
	}
	return title
}

func smartLoadMatchBlockIndex(database *db.DB, keyword string) map[string]*smartMatchBlockEntry {
	out := map[string]*smartMatchBlockEntry{}
	if database == nil {
		return out
	}
	kw := strings.TrimSpace(keyword)
	if kw == "" {
		return out
	}
	rows, _ := database.ListSmartMatchBlockItems(kw)
	for _, it := range rows {
		sk := strings.TrimSpace(it.SiteKey)
		vid := strings.TrimSpace(it.SiteDetail)
		if sk == "" || vid == "" {
			continue
		}
		key := sk + "::" + vid
		entry := out[key]
		if entry == nil {
			entry = &smartMatchBlockEntry{BlockAll: false, PanFlags: map[string]struct{}{}}
			out[key] = entry
		}
		src := strings.TrimSpace(it.Source)
		if src == "" || src == "search" {
			entry.BlockAll = true
			entry.PanFlags = map[string]struct{}{}
			continue
		}
		if src == "play" {
			pf := strings.TrimSpace(it.PanFlag)
			if pf != "" && !entry.BlockAll {
				entry.PanFlags[pf] = struct{}{}
			}
		}
	}
	return out
}

func smartFilterPansByBlockedFlags(pans []catpawrunner.Pan, blocked map[string]struct{}) []catpawrunner.Pan {
	if len(pans) == 0 || len(blocked) == 0 {
		return pans
	}
	out := make([]catpawrunner.Pan, 0, len(pans))
	for _, p := range pans {
		label := strings.TrimSpace(p.Label)
		if label == "" {
			out = append(out, p)
			continue
		}
		if _, ok := blocked[label]; ok {
			continue
		}
		out = append(out, p)
	}
	return out
}

var smartPanMatchEntriesCache = struct {
	mu       sync.RWMutex
	expireAt time.Time
	entries  []smartPanMatchEntry
}{}

func smartLoadPanMatchEntries(database *db.DB) []smartPanMatchEntry {
	now := time.Now()
	smartPanMatchEntriesCache.mu.RLock()
	if now.Before(smartPanMatchEntriesCache.expireAt) && len(smartPanMatchEntriesCache.entries) > 0 {
		out := make([]smartPanMatchEntry, 0, len(smartPanMatchEntriesCache.entries))
		out = append(out, smartPanMatchEntriesCache.entries...)
		smartPanMatchEntriesCache.mu.RUnlock()
		return out
	}
	smartPanMatchEntriesCache.mu.RUnlock()

	buildDefault := func() []smartPanMatchEntry {
		return []smartPanMatchEntry{
			{TokenLower: "百度", PanLower: "百度"},
			{TokenLower: "夸克", PanLower: "夸克"},
			{TokenLower: "uc", PanLower: "uc"},
			{TokenLower: "天翼", PanLower: "天翼"},
			{TokenLower: "移动", PanLower: "移动"},
		}
	}
	if database == nil {
		return buildDefault()
	}

	rawPan, errPan := database.ListSmartPanMatchTokens()
	rawMap, _ := database.ListSmartPanAliasMappings()
	if errPan != nil || len(rawPan) == 0 {
		return buildDefault()
	}

	aliasMap := map[string][]string{}
	for _, row := range rawMap {
		pan := strings.ToLower(strings.TrimSpace(row.Pan))
		if pan == "" {
			continue
		}
		parts := strings.Split(strings.ReplaceAll(strings.TrimSpace(row.Aliases), "，", ","), ",")
		list := make([]string, 0, len(parts))
		seenAlias := map[string]bool{}
		for _, p := range parts {
			a := strings.ToLower(strings.TrimSpace(p))
			if a == "" || seenAlias[a] {
				continue
			}
			seenAlias[a] = true
			list = append(list, a)
		}
		if len(list) > 0 {
			aliasMap[pan] = list
		}
	}

	entries := make([]smartPanMatchEntry, 0, 16)
	seen := map[string]bool{}
	for _, t := range rawPan {
		pan := strings.ToLower(strings.TrimSpace(t))
		if pan == "" {
			continue
		}
		add := func(token string) {
			k := strings.ToLower(strings.TrimSpace(token))
			if k == "" {
				return
			}
			dupKey := pan + "::" + k
			if seen[dupKey] {
				return
			}
			seen[dupKey] = true
			entries = append(entries, smartPanMatchEntry{TokenLower: k, PanLower: pan})
		}
		add(pan)
		for _, a := range aliasMap[pan] {
			add(a)
		}
	}
	if len(entries) == 0 {
		return buildDefault()
	}

	smartPanMatchEntriesCache.mu.Lock()
	smartPanMatchEntriesCache.entries = make([]smartPanMatchEntry, 0, len(entries))
	smartPanMatchEntriesCache.entries = append(smartPanMatchEntriesCache.entries, entries...)
	smartPanMatchEntriesCache.expireAt = now.Add(10 * time.Second)
	smartPanMatchEntriesCache.mu.Unlock()
	return entries
}

func smartLoadPlaybackSettings(database *db.DB) smartPlaybackSettings {
	cfg, _ := database.ReadAppConfig()
	mode := config.NormalizeSourceExtractPriority(cfg.SmartSourceExtractPriority)
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

	rawKeyword, _ := database.ListSmartSourcePriorityTokens()
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

	rawPan, _ := database.ListSmartPanMatchTokens()
	rawPanAliasMap, _ := database.ListSmartPanAliasMappings()
	panAliasMap := map[string][]string{}
	for _, row := range rawPanAliasMap {
		panKey := strings.ToLower(strings.TrimSpace(row.Pan))
		if panKey == "" {
			continue
		}
		parts := strings.Split(strings.ReplaceAll(strings.TrimSpace(row.Aliases), "，", ","), ",")
		aliases := make([]string, 0, len(parts))
		seenAlias := map[string]bool{}
		for _, p := range parts {
			a := strings.ToLower(strings.TrimSpace(p))
			if a == "" || seenAlias[a] {
				continue
			}
			seenAlias[a] = true
			aliases = append(aliases, a)
		}
		panAliasMap[panKey] = aliases
	}
	pan := make([]string, 0, len(rawPan))
	seen2 := map[string]bool{}
	for _, t := range rawPan {
		s := strings.ToLower(strings.TrimSpace(t))
		if s == "" {
			continue
		}
		tokens := []string{s}
		if aliases, ok := panAliasMap[s]; ok && len(aliases) > 0 {
			tokens = append(tokens, aliases...)
		}
		for _, tk := range tokens {
			k := strings.ToLower(strings.TrimSpace(tk))
			if k == "" || seen2[k] {
				continue
			}
			seen2[k] = true
			pan = append(pan, k)
		}
	}

	return smartPlaybackSettings{
		Mode:               mode,
		KeywordTokensLower: kw,
		PanTokenOrderLower: pan,
		OrderKeys:          orderKeys,
		ExplicitKeys:       explicit,
	}
}

const (
	smartDetailFailCooldownBase = 30 * time.Second
	smartDetailFailCooldownMax  = 5 * time.Minute
)

type smartSource struct {
	SiteKey    string
	SiteName   string
	SpiderAPI  string
	SiteDetail string
	Remark     string
	Score      int
	Seq        int
	NoNoise    bool
}

type smartDetailCacheEntry struct {
	OK                        bool
	FailCount                 int
	NextRetryAt               time.Time
	LastError                 string
	Source                    smartSource
	Pans                      []catpawrunner.Pan
	PanMockEnabled            bool
	PanMock189AccessByShareID map[string]string
	EpisodeMap                map[int][]smartCandidate
	EpisodeMapLoose           map[int][]smartCandidate
}

func smartBuildSeasonSignature(seasons []smartTMDBSeason) string {
	if len(seasons) == 0 {
		return "-"
	}
	parts := make([]string, 0, len(seasons))
	for _, s := range seasons {
		parts = append(parts, strconv.Itoa(s.Season)+":"+strconv.Itoa(s.EpisodeCount))
	}
	return strings.Join(parts, ",")
}

func smartBuildDetailCacheKey(src smartSource, primarySeasons []smartTMDBSeason, baselineSeasons []smartTMDBSeason, allowSingleBaseline bool, primaryKind string) string {
	base := smartBuildSourceKey(src.SiteKey, src.SpiderAPI, src.SiteDetail)
	if base == "" {
		return ""
	}
	mode := "strict"
	if allowSingleBaseline {
		mode = "full"
	}
	return strings.Join([]string{
		base,
		strings.TrimSpace(strings.ToLower(primaryKind)),
		mode,
		smartBuildSeasonSignature(primarySeasons),
		smartBuildSeasonSignature(baselineSeasons),
	}, "::")
}

func smartCandidateResolutionAllowed(mode string, allowed []string) bool {
	target := strings.TrimSpace(mode)
	if target == "" {
		return false
	}
	if len(allowed) == 0 {
		return true
	}
	for _, item := range allowed {
		if strings.TrimSpace(item) == target {
			return true
		}
	}
	return false
}

var smartDetailCache = struct {
	sync.Mutex
	M        map[string]*smartDetailCacheEntry
	InFlight map[string]chan struct{}
}{
	M:        map[string]*smartDetailCacheEntry{},
	InFlight: map[string]chan struct{}{},
}

func smartGetSearchThreadCount(database *db.DB, u *SmartUser) int {
	if database == nil {
		return 5
	}
	_ = u
	rawSites, _ := database.ListVideoSourceSites()
	states, _ := database.ReadVideoSourceSiteStates()
	n := 0
	for _, s := range rawSites {
		key := strings.TrimSpace(s.Key)
		api := strings.TrimSpace(s.API)
		if key == "" || api == "" {
			continue
		}
		if sites.IsConfigCenterSite(sites.Site{Key: key, API: api, Name: s.Name, Type: s.Type}) {
			continue
		}
		st, ok := states[key]
		if ok {
			if !st.Enabled || !st.Search {
				continue
			}
			if st.SmartSkip {
				continue
			}
		}
		n++
	}
	if n < 1 {
		return 5
	}
	return n
}

func smartLoadSiteOrder(database *db.DB, u *SmartUser) []string {
	if database == nil {
		return nil
	}
	rawSites, _ := database.ListVideoSourceSites()
	states, _ := database.ReadVideoSourceSiteStates()
	type decorated struct {
		k string
		o int
		i int
	}
	ds := make([]decorated, 0, len(rawSites))
	for i, s := range rawSites {
		key := strings.TrimSpace(s.Key)
		if key == "" {
			continue
		}
		ord := 1_000_000_000
		if st, ok := states[key]; ok {
			ord = st.OrderIndex
		}
		ds = append(ds, decorated{k: key, o: ord, i: i})
	}
	sort.Slice(ds, func(i, j int) bool {
		if ds[i].o != ds[j].o {
			return ds[i].o < ds[j].o
		}
		return ds[i].i < ds[j].i
	})
	out := make([]string, 0, len(ds))
	for _, d := range ds {
		out = append(out, d.k)
	}
	return out
}

func smartBuildAggregatedSources(database *db.DB, apiBase string, searchTitle string, u *SmartUser) ([]smartSource, map[string]int) {
	aggregateRules := smartLoadAggregateCleanRules(database)
	matchBlockKeyword := smartMatchBlockKeyword(searchTitle, aggregateRules)
	matchBlockIndex := smartLoadMatchBlockIndex(database, matchBlockKeyword)
	rawSites, _ := database.ListVideoSourceSites()
	sitesList := make([]sites.Site, 0, len(rawSites))
	for _, s := range rawSites {
		sitesList = append(sitesList, sites.Site{Key: s.Key, Name: s.Name, API: s.API, Type: s.Type})
	}
	states, _ := database.ReadVideoSourceSiteStates()
	statusMap := map[string]bool{}
	searchMap := map[string]bool{}
	for k, st := range states {
		if strings.TrimSpace(k) == "" {
			continue
		}
		statusMap[k] = st.Enabled
		searchMap[k] = st.Search
	}
	ordered := sites.ApplySiteOrder(sitesList, smartLoadSiteOrder(database, u))

	orderMap := map[string]int{}
	for i, s := range ordered {
		if s.Key == "" {
			continue
		}
		orderMap[s.Key] = i
	}

	qKey := smartAggKeyWithRules(searchTitle, aggregateRules)

	// Search across sites concurrently; smart playback should not block on the slowest site.
	type task struct {
		Site sites.Site
		Idx  int // stable order index
	}
	tasks := make([]task, 0, len(ordered))
	for i, s := range ordered {
		if s.Key == "" || s.API == "" {
			continue
		}
		if sites.IsConfigCenterSite(s) {
			continue
		}
		if enabled, ok := statusMap[s.Key]; ok && !enabled {
			continue
		}
		if searchEnabled, ok := searchMap[s.Key]; ok && !searchEnabled {
			continue
		}
		if st, ok := states[s.Key]; ok && st.SmartSkip {
			continue
		}
		tasks = append(tasks, task{Site: s, Idx: i})
	}
	if len(tasks) == 0 {
		return nil, orderMap
	}

	threadCount := len(tasks)
	if threadCount < 1 {
		threadCount = 1
	}
	sem := make(chan struct{}, threadCount)
	resCh := make(chan []smartSource, len(tasks))
	var wg sync.WaitGroup

	for _, t := range tasks {
		wg.Add(1)
		go func(tt task) {
			defer wg.Done()
			sem <- struct{}{}
			defer func() { <-sem }()

			raw, err := cache.RequestSpiderSearchDirect(apiBase, tt.Site.API, searchTitle, 1)
			if err != nil {
				return
			}
			items := catpawrunner.NormalizeSearchList(raw)
			local := make([]smartSource, 0, smartMinInt(20, len(items)))
			localSeq := 0
			for _, it := range items {
				name := strings.TrimSpace(it.Name)
				if strings.TrimSpace(it.ID) == "" || name == "" {
					continue
				}
				if entry := matchBlockIndex[tt.Site.Key+"::"+strings.TrimSpace(it.ID)]; entry != nil && entry.BlockAll {
					continue
				}
				key := smartAggKeyWithRules(name, aggregateRules)
				if key == "" {
					continue
				}
				score := smartMatchScore(qKey, key)
				if score <= 0 {
					continue
				}
				localSeq++
				local = append(local, smartSource{
					SiteKey:    tt.Site.Key,
					SiteName:   tt.Site.Name,
					SpiderAPI:  tt.Site.API,
					SiteDetail: strings.TrimSpace(it.ID),
					Remark:     strings.TrimSpace(it.Remark),
					Score:      score,
					Seq:        (tt.Idx+1)*1000 + localSeq, // deterministic tie-break
					NoNoise:    key == qKey,
				})
				if len(local) >= 200 {
					break
				}
			}
			if len(local) > 0 {
				resCh <- local
			}
		}(t)
	}
	go func() {
		wg.Wait()
		close(resCh)
	}()

	out := make([]smartSource, 0, 64)
	for batch := range resCh {
		out = append(out, batch...)
	}
	if len(out) > 200 {
		out = out[:200]
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
			as := smartExtractSeasonHintFromSource(a.SiteName, a.Remark)
			bs := smartExtractSeasonHintFromSource(b.SiteName, b.Remark)
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
			aHas := smartHasExplicitSeasonMarkerInSource(a.SiteName, a.Remark) || as > 0
			bHas := smartHasExplicitSeasonMarkerInSource(b.SiteName, b.Remark) || bs > 0
			if aHas != bHas {
				return aHas
			}
		}

		ap := smartExtractMaxEpisodeFromBadgeText(a.Remark)
		bp := smartExtractMaxEpisodeFromBadgeText(b.Remark)
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
		k := smartBuildSourceKey(s.SiteKey, s.SpiderAPI, s.SiteDetail)
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
		key := smartBuildSourceKey(s.SiteKey, s.SpiderAPI, s.SiteDetail)
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

var smartDoubanMapFallbackLogGate struct {
	mu   sync.Mutex
	last map[int]int64 // key: global episode no
}

func smartMaybeLogDoubanMapFallback(global int, seasons []smartTMDBSeason) {
	if !smartDebugLogEnabled() {
		return
	}
	g := global
	if g <= 0 {
		return
	}
	now := time.Now().UnixMilli()

	smartDoubanMapFallbackLogGate.mu.Lock()
	defer smartDoubanMapFallbackLogGate.mu.Unlock()
	if smartDoubanMapFallbackLogGate.last == nil {
		smartDoubanMapFallbackLogGate.last = map[int]int64{}
	}
	if lastAt, ok := smartDoubanMapFallbackLogGate.last[g]; ok && now-lastAt < 4000 {
		return
	}
	// quick prune
	if len(smartDoubanMapFallbackLogGate.last) > 512 {
		for k, v := range smartDoubanMapFallbackLogGate.last {
			if now-v > 60_000 {
				delete(smartDoubanMapFallbackLogGate.last, k)
			}
		}
	}
	smartDoubanMapFallbackLogGate.last[g] = now

	sum := 0
	valid := 0
	for _, it := range seasons {
		if it.Season > 0 && it.EpisodeCount > 0 {
			sum += it.EpisodeCount
			valid++
		}
	}
	smartDebugPrintf(
		"[smart][douban_map] global=%d mapped=S00E%03d reason=no_season_hit valid=%d sum=%d seasons=%v",
		g,
		g,
		valid,
		sum,
		seasons,
	)
}

func smartLoadOrBuildDetailCache(database *db.DB, apiBase string, src smartSource, primarySeasons []smartTMDBSeason, singleBaselineSeasons []smartTMDBSeason, tmdbHasMultiSeason bool, settings smartPlaybackSettings, rawCleanRules []string, rawEpisodeRules []string, allowSingleBaseline bool, primaryKind string) *smartDetailCacheEntry {
	key := smartBuildDetailCacheKey(src, primarySeasons, singleBaselineSeasons, allowSingleBaseline, primaryKind)
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
			Pans:                      []catpawrunner.Pan{},
			EpisodeMap:                map[int][]smartCandidate{},
			EpisodeMapLoose:           map[int][]smartCandidate{},
			PanMock189AccessByShareID: map[string]string{},
		}

		detailRaw, err := cache.RequestSpiderDetailDirect(apiBase, src.SpiderAPI, src.SiteDetail)
		if err != nil {
			smartLogDetailError(src.SiteKey, src.SiteName, src.SiteDetail, err)
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
		playFrom, playURL := catpawrunner.ExtractDetailPlayFromURL(detailRaw)
		rawPans := catpawrunner.ParsePlaySources(playFrom, playURL)
		if rawPans == nil {
			rawPans = []catpawrunner.Pan{}
		}
		smartLogDetailOK(src.SiteKey, src.SiteName, src.SiteDetail, rawPans)
		resolvedPans := rawPans
		if entry.PanMockEnabled && resolvedPans != nil {
			resolved, accessMap := smartResolvePanMockDetailPans(database, src.SiteKey, src.SiteName, 0, primarySeasons, tmdbHasMultiSeason, rawCleanRules, rawEpisodeRules, resolvedPans)
			resolvedPans = resolved
			if len(accessMap) > 0 {
				entry.PanMock189AccessByShareID = accessMap
			}
			smartLogDetailPanMock(src.SiteKey, src.SiteName, src.SiteDetail, resolvedPans)
		}
		entry.Pans = resolvedPans
		primaryFirstSeasonCount := 0
		for _, s := range primarySeasons {
			if s.Season == 1 && s.EpisodeCount > 0 {
				primaryFirstSeasonCount = s.EpisodeCount
				break
			}
		}
		sourceHasBeyondFirstSeason := smartSourceHasEpisodeBeyondFirstSeason(resolvedPans, rawCleanRules, rawEpisodeRules, primaryFirstSeasonCount)

		srcRemarkLower := strings.ToLower(strings.TrimSpace(src.Remark))
		for _, pan := range resolvedPans {
			panFlag := strings.TrimSpace(pan.Label)
			panTokenIdx := smartLabelTokenIdx(panFlag, settings.PanTokenOrderLower)
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
				rawSeason := jsMatch.Season
				match, keyNo, ok, loose, resolutionMode, degradedReason := smartResolveEpisodeMappingForPlaybackWithMode(
					primarySeasons,
					smartSeasonEpisode{Season: jsMatch.Season, Episode: jsMatch.Episode},
					singleBaselineSeasons,
					primaryFirstSeasonCount,
					sourceHasBeyondFirstSeason,
					allowSingleBaseline,
					primaryKind,
				)
				seasonNo := match.Season
				epNo := match.Episode
				if !ok || epNo <= 0 {
					continue
				}
				rawName := smartStableRawNameFromEpisodeURL(ep.URL)

				cand := smartCandidate{
					SiteKey:          src.SiteKey,
					SiteName:         src.SiteName,
					SpiderAPI:        src.SpiderAPI,
					SiteDetail:       src.SiteDetail,
					SrcRemarkLower:   srcRemarkLower,
					PanFlag:          panFlag,
					PanTokenIdx:      panTokenIdx,
					Ep:               ep,
					RawName:          rawName,
					RawLower:         rawLower,
					MatchSeason:      seasonNo,
					HasSeasonMarker:  rawSeason > 0,
					SearchSeasonHint: 0,
					MatchKeyword:     smartComputePriorityMatch(rawLower, settings.KeywordTokensLower),
					ResolutionMode:   resolutionMode,
					LockedGlobal:     keyNo,
					DegradedReason:   degradedReason,
					StrictMatched:    resolutionMode == "strict-tmdb" || resolutionMode == "strict-douban",
					DegradedMatched:  resolutionMode == "degraded-single-baseline",
				}

				if loose && tmdbHasMultiSeason {
					entry.EpisodeMapLoose[epNo] = append(entry.EpisodeMapLoose[epNo], cand)
					continue
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

type smartDetailState struct {
	Source         smartSource
	OK             bool
	PanMockEnabled bool
	PanMockDone    chan struct{}

	// Updated when pan_mock list resolves (or immediately for non-pan_mock).
	Pans                      []catpawrunner.Pan
	PanMock189AccessByShareID map[string]string
	EpisodeMap                map[int][]smartCandidate
	EpisodeMapLoose           map[int][]smartCandidate

	mu sync.Mutex
}

func (s *smartDetailState) snapshot() (ok bool, panMockEnabled bool, pans []catpawrunner.Pan, access map[string]string, epMap map[int][]smartCandidate, epLoose map[int][]smartCandidate) {
	if s == nil {
		return false, false, nil, nil, nil, nil
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	outPans := make([]catpawrunner.Pan, 0, len(s.Pans))
	for _, p := range s.Pans {
		outPans = append(outPans, p)
	}
	outAccess := map[string]string{}
	for k, v := range s.PanMock189AccessByShareID {
		outAccess[k] = v
	}
	outMap := map[int][]smartCandidate{}
	for k, v := range s.EpisodeMap {
		cp := make([]smartCandidate, 0, len(v))
		cp = append(cp, v...)
		outMap[k] = cp
	}
	outLoose := map[int][]smartCandidate{}
	for k, v := range s.EpisodeMapLoose {
		cp := make([]smartCandidate, 0, len(v))
		cp = append(cp, v...)
		outLoose[k] = cp
	}
	return s.OK, s.PanMockEnabled, outPans, outAccess, outMap, outLoose
}

func smartBuildEpisodeMapsFromPans(
	src smartSource,
	pans []catpawrunner.Pan,
	primarySeasons []smartTMDBSeason,
	singleBaselineSeasons []smartTMDBSeason,
	tmdbHasMultiSeason bool,
	settings smartPlaybackSettings,
	rawCleanRules []string,
	rawEpisodeRules []string,
	allowSingleBaseline bool,
	primaryKind string,
) (map[int][]smartCandidate, map[int][]smartCandidate) {
	episodeMap := map[int][]smartCandidate{}
	episodeMapLoose := map[int][]smartCandidate{}
	primaryFirstSeasonCount := 0
	for _, s := range primarySeasons {
		if s.Season == 1 && s.EpisodeCount > 0 {
			primaryFirstSeasonCount = s.EpisodeCount
			break
		}
	}
	sourceHasBeyondFirstSeason := smartSourceHasEpisodeBeyondFirstSeason(pans, rawCleanRules, rawEpisodeRules, primaryFirstSeasonCount)

	srcRemarkLower := strings.ToLower(strings.TrimSpace(src.Remark))
	for _, pan := range pans {
		panFlag := strings.TrimSpace(pan.Label)
		panTokenIdx := smartLabelTokenIdx(panFlag, settings.PanTokenOrderLower)
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
			rawLower = strings.TrimSpace(rawLower + " " + srcRemarkLower)

			jsMatch, err := magic.MagicEpisodeExtractFromCandidates(texts, rawCleanRules, rawEpisodeRules)
			if err != nil {
				continue
			}
			rawSeason := jsMatch.Season
			match, keyNo, ok, loose, resolutionMode, degradedReason := smartResolveEpisodeMappingForPlaybackWithMode(
				primarySeasons,
				smartSeasonEpisode{Season: jsMatch.Season, Episode: jsMatch.Episode},
				singleBaselineSeasons,
				primaryFirstSeasonCount,
				sourceHasBeyondFirstSeason,
				allowSingleBaseline,
				primaryKind,
			)
			seasonNo := match.Season
			epNo := match.Episode
			if !ok || epNo <= 0 {
				continue
			}
			rawName := smartStableRawNameFromEpisodeURL(ep.URL)

			cand := smartCandidate{
				SiteKey:         src.SiteKey,
				SiteName:        src.SiteName,
				SpiderAPI:       src.SpiderAPI,
				SiteDetail:      src.SiteDetail,
				SrcRemarkLower:  srcRemarkLower,
				PanFlag:         panFlag,
				PanTokenIdx:     panTokenIdx,
				Ep:              ep,
				RawName:         rawName,
				RawLower:        rawLower,
				MatchSeason:     seasonNo,
				HasSeasonMarker: rawSeason > 0,
				MatchKeyword:    smartComputePriorityMatch(rawLower, settings.KeywordTokensLower),
				ResolutionMode:  resolutionMode,
				LockedGlobal:    keyNo,
				DegradedReason:  degradedReason,
				StrictMatched:   resolutionMode == "strict-tmdb" || resolutionMode == "strict-douban",
				DegradedMatched: resolutionMode == "degraded-single-baseline",
			}

			if loose && tmdbHasMultiSeason {
				list := episodeMapLoose[epNo]
				list = append(list, cand)
				episodeMapLoose[epNo] = list
				continue
			}
			list := episodeMap[keyNo]
			list = append(list, cand)
			episodeMap[keyNo] = list
		}
	}
	return episodeMap, episodeMapLoose
}

func smartBuildMovieCandidatesFromPans(
	src smartSource,
	pans []catpawrunner.Pan,
	settings smartPlaybackSettings,
	rawCleanRules []string,
	rawMovieRules []string,
) []smartCandidate {
	if len(rawMovieRules) == 0 || len(pans) == 0 {
		return nil
	}
	out := make([]smartCandidate, 0, 16)
	srcRemarkLower := strings.ToLower(strings.TrimSpace(src.Remark))
	for _, pan := range pans {
		panFlag := strings.TrimSpace(pan.Label)
		panTokenIdx := smartLabelTokenIdx(panFlag, settings.PanTokenOrderLower)
		for _, ep := range pan.Episodes {
			if strings.TrimSpace(ep.URL) == "" {
				continue
			}
			rawNames := smartExtractRawNamesFromEpisodeURL(ep.URL)
			rawName := ""
			for i := len(rawNames) - 1; i >= 0; i-- {
				if strings.TrimSpace(rawNames[i]) != "" {
					rawName = strings.TrimSpace(rawNames[i])
					break
				}
			}
			if rawName == "" {
				continue
			}
			texts := []string{rawName}
			hit, err := magic.MagicMovieMatchFromCandidates(texts, rawCleanRules, rawMovieRules)
			if err != nil || !hit {
				continue
			}
			rawLower := smartBuildCandidateLowerText(texts)
			if rawLower == "" {
				rawLower = strings.ToLower(rawName)
			}
			rawLower = strings.TrimSpace(rawLower + " " + srcRemarkLower)
			out = append(out, smartCandidate{
				SiteKey:        src.SiteKey,
				SiteName:       src.SiteName,
				SpiderAPI:      src.SpiderAPI,
				SiteDetail:     src.SiteDetail,
				SrcRemarkLower: srcRemarkLower,
				PanFlag:        panFlag,
				PanTokenIdx:    panTokenIdx,
				Ep:             ep,
				RawName:        rawName,
				RawLower:       rawLower,
				MatchKeyword:   smartComputePriorityMatch(rawLower, settings.KeywordTokensLower),
			})
		}
	}
	if len(out) == 0 {
		return nil
	}
	sort.SliceStable(out, func(i, j int) bool {
		return smartCompareSmartMatchIgnorePanOrder(out[i], out[j], false, 0, settings) < 0
	})
	return out
}

func smartCandidatesForWant(
	episodeMap map[int][]smartCandidate,
	episodeMapLoose map[int][]smartCandidate,
	src smartSource,
	tmdbSeasons []smartTMDBSeason,
	tmdbHasMultiSeason bool,
	preferSeasonNo int,
	want int,
	settings smartPlaybackSettings,
	requireSeasoned bool,
	allowResolutionModes []string,
) []smartCandidate {
	searchSeasonHint := smartExtractSeasonHintFromSource(src.SiteName, src.Remark)
	wantedInSeason := smartSeasonEpisode{Season: 0, Episode: want}
	if tmdbHasMultiSeason {
		wantedInSeason = smartTMDBSeasonEpisodeOfGlobal(tmdbSeasons, want)
	}
	wantSeasonNo := wantedInSeason.Season
	wantSeasonEp := wantedInSeason.Episode

	candidatesForNo := []smartCandidate{}
	if episodeMap != nil {
		if list, ok := episodeMap[want]; ok && len(list) > 0 {
			for _, c := range list {
				if !smartCandidateResolutionAllowed(c.ResolutionMode, allowResolutionModes) {
					continue
				}
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
		if episodeMapLoose != nil {
			if list, ok := episodeMapLoose[wantSeasonEp]; ok && len(list) > 0 {
				for _, c := range list {
					if !smartCandidateResolutionAllowed(c.ResolutionMode, allowResolutionModes) {
						continue
					}
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
	// Order matches smartPickBestMatchIgnorePanOrder selection behavior.
	sort.SliceStable(candidatesForNo, func(i, j int) bool {
		return smartCompareSmartMatchIgnorePanOrder(candidatesForNo[i], candidatesForNo[j], tmdbHasMultiSeason, preferSeasonNo, settings) < 0
	})
	return candidatesForNo
}

func smartTryPlayPickedCandidate(flowID uint64, database *db.DB, apiBase string, tvUser string, cand smartCandidate, accessByShareID map[string]string) *smartPickResult {
	if strings.TrimSpace(apiBase) == "" || strings.TrimSpace(tvUser) == "" {
		// tvUser can be empty for unauthenticated; still allow catpawrunner play below.
	}
	if strings.TrimSpace(cand.Ep.URL) == "" {
		return nil
	}
	tryID := atomic.AddUint64(&smartPlayTrySeq, 1)
	tryStart := time.Now()
	classifyReason := func(status string, err error) string {
		s := strings.ToLower(strings.TrimSpace(status))
		if s == "ok" {
			return ""
		}
		if s == "timeout" {
			return "timeout"
		}
		if s == "empty" {
			return "empty_url"
		}
		if err == nil {
			return ""
		}
		msg := strings.ToLower(strings.TrimSpace(err.Error()))
		if msg == "" {
			return "unknown"
		}
		if strings.Contains(msg, "http 401") || strings.Contains(msg, "http 403") || strings.Contains(msg, "unauthorized") || strings.Contains(msg, "forbidden") {
			return "auth"
		}
		if strings.Contains(msg, "风险") || strings.Contains(msg, "风控") || strings.Contains(msg, "频繁") || strings.Contains(msg, "too many") || strings.Contains(msg, "rate") || strings.Contains(msg, "captcha") || strings.Contains(msg, "验证码") {
			return "risk_control"
		}
		if strings.Contains(msg, "json") || strings.Contains(msg, "unmarshal") || strings.Contains(msg, "parse") {
			return "parse"
		}
		if strings.Contains(msg, "http ") {
			return "http_status"
		}
		if errors.Is(err, context.DeadlineExceeded) {
			return "timeout"
		}
		return "upstream"
	}

	logStatus := func(status string, playURL string, headers map[string]string, err error) {
		if !smartDebugLogEnabled() {
			return
		}
		errMsg := ""
		if err != nil {
			errMsg = strings.TrimSpace(err.Error())
		}
		reason := classifyReason(status, err)
		u := strings.TrimSpace(playURL)
		hc := 0
		if headers != nil {
			hc = len(headers)
		}
		smartDebugPrintf(
			"[smart][play_try_status] flow=%d id=%d ms=%d site=(%s) panFlag=%s provider=%s status=%s reason=%s headers=%d url=%s err=%s spider=%s siteDetail=%s",
			flowID,
			tryID,
			time.Since(tryStart).Milliseconds(),
			smartLogSiteName(cand.SiteKey, cand.SiteName),
			strings.TrimSpace(cand.PanFlag),
			smartPanMockProviderID(database, strings.TrimSpace(cand.PanFlag)),
			strings.TrimSpace(status),
			strings.TrimSpace(reason),
			hc,
			u,
			errMsg,
			strings.TrimSpace(cand.SpiderAPI),
			strings.TrimSpace(cand.SiteDetail),
		)
	}
	if smartDebugLogEnabled() {
		rawNames := smartExtractRawNamesFromEpisodeURL(cand.Ep.URL)
		raw0 := ""
		if len(rawNames) > 0 {
			raw0 = strings.TrimSpace(rawNames[0])
		}
		smartDebugPrintf(
			"[smart][play_try] flow=%d id=%d site=(%s) panFlag=%s provider=%s matchShowName=%s matchRawName=%s spider=%s siteDetail=%s",
			flowID,
			tryID,
			smartLogSiteName(cand.SiteKey, cand.SiteName),
			strings.TrimSpace(cand.PanFlag),
			smartPanMockProviderID(database, strings.TrimSpace(cand.PanFlag)),
			strings.TrimSpace(cand.Ep.Name),
			raw0,
			strings.TrimSpace(cand.SpiderAPI),
			strings.TrimSpace(cand.SiteDetail),
		)
	}

	type playResult struct {
		status  string // ok|empty|err
		playURL string
		headers map[string]string
		err     error
	}

	doPlay := func() playResult {
		pid := smartPanMockProviderID(database, strings.TrimSpace(cand.PanFlag))
		switch pid {
		case "189":
			ac := ""
			parts := strings.Split(strings.TrimSpace(cand.Ep.URL), "*")
			if len(parts) >= 2 {
				shareID := strings.TrimSpace(parts[1])
				if shareID != "" {
					if v, ok := accessByShareID[shareID]; ok {
						ac = strings.TrimSpace(v)
					}
					if ac == "" {
						if v, ok := smartPanMock189AccessGet(shareID); ok {
							ac = strings.TrimSpace(v)
						}
					}
				}
			}
			u, _, _, _, err := netdisk.Tianyi189Play(database, strings.TrimSpace(cand.Ep.URL), ac)
			if err != nil || strings.TrimSpace(u) == "" {
				if err != nil {
					return playResult{status: "err", err: err}
				}
				return playResult{status: "empty"}
			}
			return playResult{status: "ok", playURL: strings.TrimSpace(u), headers: map[string]string{}}
		case "quark":
			u, header, err := netdisk.QuarkPlayWithTVUser(database, strings.TrimSpace(cand.Ep.URL), "", tvUser)
			if err != nil || strings.TrimSpace(u) == "" {
				if err != nil {
					return playResult{status: "err", err: err}
				}
				return playResult{status: "empty"}
			}
			if header == nil {
				header = map[string]string{}
			}
			return playResult{status: "ok", playURL: strings.TrimSpace(u), headers: header}
		case "uc":
			u, header, err := netdisk.UCPlayWithTVUser(database, strings.TrimSpace(cand.Ep.URL), "", tvUser)
			if err != nil || strings.TrimSpace(u) == "" {
				if err != nil {
					return playResult{status: "err", err: err}
				}
				return playResult{status: "empty"}
			}
			if header == nil {
				header = map[string]string{}
			}
			return playResult{status: "ok", playURL: strings.TrimSpace(u), headers: header}
		case "139":
			downloadURL, playURL, err := netdisk.Yun139Play(database, strings.TrimSpace(cand.PanFlag), strings.TrimSpace(cand.Ep.URL))
			u := strings.TrimSpace(downloadURL)
			if u == "" {
				u = strings.TrimSpace(playURL)
			}
			if err != nil || u == "" {
				if err != nil {
					return playResult{status: "err", err: err}
				}
				return playResult{status: "empty"}
			}
			return playResult{status: "ok", playURL: u, headers: map[string]string{}}
		case "baidu":
			u, header, err := netdisk.BaiduPlay(database, strings.TrimSpace(cand.PanFlag), strings.TrimSpace(cand.Ep.URL), "/MeowFilm")
			if err != nil || strings.TrimSpace(u) == "" {
				if err != nil {
					return playResult{status: "err", err: err}
				}
				return playResult{status: "empty"}
			}
			if header == nil {
				header = map[string]string{}
			}
			return playResult{status: "ok", playURL: strings.TrimSpace(u), headers: header}
		default:
			// Normal site play.
			spiderApi := strings.TrimSpace(cand.SpiderAPI)
			siteID := catpawrunner.ExtractSiteIDFromSpiderAPI(spiderApi)
			playPayload := map[string]any{
				"flag":    strings.TrimSpace(cand.Ep.Flag),
				"id":      strings.TrimSpace(cand.Ep.URL),
				"siteApi": spiderApi,
			}
			if siteID != "" {
				playPayload["siteId"] = siteID
			}
			playRaw, err := catpawrunner.RequestPlayWithTimeout(apiBase, tvUser, playPayload, smartPlayTryTimeout)
			if err != nil {
				return playResult{status: "err", err: err}
			}
			payloadOut := smartBuildCatpawPlayPayload(playRaw, apiBase, tvUser)
			urlPicked, headers := netdisk.PlayPayloadURLHeaders(payloadOut)
			if urlPicked == "" {
				return playResult{status: "empty"}
			}
			return playResult{status: "ok", playURL: urlPicked, headers: headers}
		}
	}

	resCh := make(chan playResult, 1)
	go func() { resCh <- doPlay() }()
	timer := time.NewTimer(smartPlayTryTimeout)
	defer timer.Stop()
	select {
	case <-timer.C:
		logStatus("timeout", "", nil, context.DeadlineExceeded)
		return nil
	case res := <-resCh:
		switch strings.TrimSpace(res.status) {
		case "ok":
			logStatus("ok", res.playURL, res.headers, nil)
			return &smartPickResult{Cand: cand, PlayURL: strings.TrimSpace(res.playURL), Headers: res.headers}
		case "err":
			logStatus("err", "", nil, res.err)
			return nil
		default:
			logStatus("empty", "", nil, nil)
			return nil
		}
	}
}

func smartFetchDetailAndPickAndPlay(database *db.DB, apiBase string, tvUser string, src smartSource, tmdbSeasons []smartTMDBSeason, singleBaselineSeasons []smartTMDBSeason, tmdbHasMultiSeason bool, preferSeasonNo int, want int, settings smartPlaybackSettings, rawCleanRules []string, rawEpisodeRules []string, requireSeasoned bool, allowSingleBaseline bool, primaryKind string, allowResolutionModes []string) *smartPickResult {
	siteKey := strings.TrimSpace(src.SiteKey)
	spiderApi := strings.TrimSpace(src.SpiderAPI)
	siteDetail := strings.TrimSpace(src.SiteDetail)
	if siteKey == "" || spiderApi == "" || siteDetail == "" || want <= 0 {
		return nil
	}
	searchSeasonHint := smartExtractSeasonHintFromSource(src.SiteName, src.Remark)

	cache := smartLoadOrBuildDetailCache(database, apiBase, src, tmdbSeasons, singleBaselineSeasons, tmdbHasMultiSeason, settings, rawCleanRules, rawEpisodeRules, allowSingleBaseline, primaryKind)
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
				if !smartCandidateResolutionAllowed(c.ResolutionMode, allowResolutionModes) {
					continue
				}
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
					if !smartCandidateResolutionAllowed(c.ResolutionMode, allowResolutionModes) {
						continue
					}
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
		allowedAttempts, fallbackAttempts, normalAllowed, normalFallback := smartBuildPanMockGroupAttempts(
			candidatesForNo,
			settings,
			database,
			tmdbHasMultiSeason,
			preferSeasonNo,
		)

		tryGroup := func(at smartPanMockGroupAttempt) *smartPickResult {
			return smartTryPanMockGroup(
				at,
				at.Base,
				want,
				tmdbSeasons,
				singleBaselineSeasons,
				tmdbHasMultiSeason,
				preferSeasonNo,
				settings,
				rawCleanRules,
				rawEpisodeRules,
				allowSingleBaseline,
				primaryKind,
				database,
				tvUser,
				cache.PanMock189AccessByShareID,
			)
		}

		if len(allowedAttempts) > 0 {
			resCh := make(chan *smartPickResult, len(allowedAttempts))
			var wg sync.WaitGroup
			for _, at := range allowedAttempts {
				wg.Add(1)
				go func(a smartPanMockGroupAttempt) {
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
			siteID := catpawrunner.ExtractSiteIDFromSpiderAPI(spiderApi)
			playPayload := map[string]any{
				"flag":    strings.TrimSpace(best.Ep.Flag),
				"id":      strings.TrimSpace(best.Ep.URL),
				"siteApi": spiderApi,
			}
			if siteID != "" {
				playPayload["siteId"] = siteID
			}
			playRaw, err := catpawrunner.RequestPlay(apiBase, tvUser, playPayload)
			if err != nil {
				return nil
			}
			payloadOut := smartBuildCatpawPlayPayload(playRaw, apiBase, tvUser)
			urlPicked, headers := netdisk.PlayPayloadURLHeaders(payloadOut)
			if urlPicked == "" {
				return nil
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
	siteID := catpawrunner.ExtractSiteIDFromSpiderAPI(spiderApi)
	playPayload := map[string]any{
		"flag":    strings.TrimSpace(best.Ep.Flag),
		"id":      strings.TrimSpace(best.Ep.URL),
		"siteApi": spiderApi,
	}
	if siteID != "" {
		playPayload["siteId"] = siteID
	}
	playRaw, err := catpawrunner.RequestPlay(apiBase, tvUser, playPayload)
	if err != nil {
		return nil
	}
	payloadOut := smartBuildCatpawPlayPayload(playRaw, apiBase, tvUser)
	urlPicked, headers := netdisk.PlayPayloadURLHeaders(payloadOut)
	if strings.TrimSpace(urlPicked) == "" {
		return nil
	}
	return &smartPickResult{Cand: *best, PlayURL: urlPicked, Headers: headers}
}

type smartPlaybackPickedMeta struct {
	SiteKey    string
	SiteName   string
	SiteDetail string
	PanFlag    string
	Provider   string
	ShowName   string
	RawName    string
	Quality    string
}

func smartSourceHasEpisodeBeyondFirstSeason(
	pans []catpawrunner.Pan,
	rawCleanRules []string,
	rawEpisodeRules []string,
	firstSeasonCount int,
) bool {
	if firstSeasonCount <= 0 || len(pans) == 0 || len(rawCleanRules) == 0 || len(rawEpisodeRules) == 0 {
		return false
	}
	for _, pan := range pans {
		for _, ep := range pan.Episodes {
			if strings.TrimSpace(ep.URL) == "" {
				continue
			}
			texts := smartExtractEpisodeCandidateTexts(ep)
			jsMatch, err := magic.MagicEpisodeExtractFromCandidates(texts, rawCleanRules, rawEpisodeRules)
			if err != nil {
				continue
			}
			if jsMatch.Episode > firstSeasonCount {
				return true
			}
		}
	}
	return false
}

func smartCollectPlaybackOffersFromTMDB(database *db.DB, u *SmartUser, req smartPlaybackRequest, shouldStop func() bool, emit func(smartCandidateOffer, int)) error {
	if database == nil {
		return errors.New("invalid database")
	}
	if req.TMDBID <= 0 {
		return errors.New("invalid tmdb id")
	}
	apiBase := smartResolveCatApiBaseForUser(database, u)
	if apiBase == "" {
		return errors.New("catpawrunner 接口地址未设置")
	}
	searchTitle := ""
	want := 1
	tmdbSeasons := []smartTMDBSeason{}
	doubanSeasons := []smartTMDBSeason{}
	if strings.TrimSpace(req.Kind) == "movie" {
		md, err := metadata_tmdb.GetMovieDetails(database, req.TMDBID)
		if err != nil || md == nil || strings.TrimSpace(md.Title) == "" {
			return errors.New("TMDB 请求失败")
		}
		searchTitle = strings.TrimSpace(md.Title)
	} else if strings.TrimSpace(req.Kind) == "tv" {
		title, err := metadata_tmdb.GetTVTitle(database, req.TMDBID)
		if err != nil || strings.TrimSpace(title) == "" {
			return errors.New("TMDB 请求失败")
		}
		searchTitle = strings.TrimSpace(title)
		if meta, err := tvmeta.GetTVMeta(database, req.TMDBID); err == nil && meta != nil {
			tmdbSeasons = smartTMDBSeasonsFromTVMeta(meta.TMDBSeasons)
			doubanSeasons = smartTMDBSeasonsFromTVMeta(meta.DoubanSeasons)
		}
		if strings.TrimSpace(req.SubKind) == "episode" {
			want = smartTMDBGlobalEpisodeNoOf(tmdbSeasons, req.Season, req.Episode)
			if want <= 0 {
				want = 1
			}
		}
	} else {
		return errors.New("unsupported kind")
	}
	if strings.TrimSpace(searchTitle) == "" {
		return errors.New("missing title")
	}
	settings := smartLoadPlaybackSettings(database)
	rawEpisodeRules, _ := database.ListMagicEpisodeRules()
	rawCleanRules, _ := database.ListMagicEpisodeCleanRegexRules()
	rawMovieRules, _ := database.ListMagicMovieRules()
	if strings.TrimSpace(req.Kind) != "movie" && (len(rawEpisodeRules) == 0 || len(rawCleanRules) == 0) {
		return errors.New("magic regex rules 未设置")
	}

	emitted := false
	emitWrapped := func(off smartCandidateOffer, tier int) {
		emitted = true
		if emit != nil {
			emit(off, tier)
		}
	}

	err := smartCollectPlaybackOffersFromTMDBAligned(database, u, req, apiBase, searchTitle, want, tmdbSeasons, nil, settings, rawCleanRules, rawEpisodeRules, rawMovieRules, true, false, "tmdb", shouldStop, emitWrapped)
	if err == nil && emitted {
		return nil
	}

	needDoubanAssist := strings.TrimSpace(req.Kind) == "tv" && strings.TrimSpace(req.SubKind) == "episode" && strings.TrimSpace(searchTitle) != ""
	if needDoubanAssist && (len(doubanSeasons) > 0 || len(tmdbSeasons) > 0) {
		tmdbMulti := smartPositiveSeasonCount(tmdbSeasons) >= 2
		doubanMulti := smartPositiveSeasonCount(doubanSeasons) >= 2
		switch {
		case !tmdbMulti && doubanMulti:
			err2 := smartCollectPlaybackOffersFromTMDBAligned(database, u, req, apiBase, searchTitle, want, doubanSeasons, tmdbSeasons, settings, rawCleanRules, rawEpisodeRules, rawMovieRules, false, true, "douban", shouldStop, emitWrapped)
			if err2 == nil && emitted {
				return nil
			}
		case tmdbMulti && !doubanMulti:
			err2 := smartCollectPlaybackOffersFromTMDBAligned(database, u, req, apiBase, searchTitle, want, tmdbSeasons, doubanSeasons, settings, rawCleanRules, rawEpisodeRules, rawMovieRules, false, true, "tmdb", shouldStop, emitWrapped)
			if err2 == nil && emitted {
				return nil
			}
		}
	}
	if err != nil {
		return err
	}
	if emitted {
		return nil
	}
	return errors.New("无可用播放地址")
}

func smartTMDBSeasonsFromTVMeta(rows []tvmeta.SeasonCount) []smartTMDBSeason {
	if len(rows) == 0 {
		return nil
	}
	out := make([]smartTMDBSeason, 0, len(rows))
	for _, row := range rows {
		if row.SeasonNumber <= 0 || row.EpisodeCount <= 0 {
			continue
		}
		out = append(out, smartTMDBSeason{
			Season:       row.SeasonNumber,
			EpisodeCount: row.EpisodeCount,
		})
	}
	return out
}

type smartCandidateOffer struct {
	Cand          smartCandidate
	AccessByShare map[string]string // for 189 play
}

type smartStreamingOfferScheduler struct {
	mu            sync.Mutex
	seen          map[uint64]struct{}
	tier1         []smartCandidateOffer
	tier2         []smartCandidateOffer
	tier3         []smartCandidateOffer
	req           smartPlaybackRequest
	hasMulti      bool
	preferSeason  int
	matchSettings smartPlaybackSettings
	emit          func(smartCandidateOffer, int)
}

func newSmartStreamingOfferScheduler(req smartPlaybackRequest, hasMulti bool, preferSeason int, settings smartPlaybackSettings, emit func(smartCandidateOffer, int)) *smartStreamingOfferScheduler {
	return &smartStreamingOfferScheduler{
		seen:          map[uint64]struct{}{},
		req:           req,
		hasMulti:      hasMulti,
		preferSeason:  preferSeason,
		matchSettings: settings,
		emit:          emit,
	}
}

func smartPlaybackAttemptKey(c smartCandidate) uint64 {
	raw0 := smartFirstRawNameFromURL(strings.TrimSpace(c.Ep.URL))
	h := fnv.New64a()
	_, _ = h.Write([]byte(strings.TrimSpace(c.SiteKey)))
	_, _ = h.Write([]byte{0})
	_, _ = h.Write([]byte(strings.TrimSpace(c.SiteDetail)))
	_, _ = h.Write([]byte{0})
	_, _ = h.Write([]byte(strings.TrimSpace(c.PanFlag)))
	_, _ = h.Write([]byte{0})
	_, _ = h.Write([]byte(strings.TrimSpace(c.Ep.Flag)))
	_, _ = h.Write([]byte{0})
	_, _ = h.Write([]byte(strings.TrimSpace(raw0)))
	return h.Sum64()
}

func smartPlaybackOfferTier(req smartPlaybackRequest, c smartCandidate, hasMulti bool, preferSeasonNo int, settings smartPlaybackSettings) int {
	feat := smartComputeCandidateFeatures(c)
	panRuleEnabled := len(settings.PanTokenOrderLower) > 0
	if feat.QualityRank == 3 {
		if !panRuleEnabled || c.PanTokenIdx >= 0 {
			return 1
		}
		return 2
	}
	kind := strings.TrimSpace(req.Kind)
	sub := strings.TrimSpace(req.SubKind)
	if kind == "movie" || sub == "movie" {
		return 3
	}
	if kind == "tv" && sub == "episode" && hasMulti && preferSeasonNo > 0 {
		if c.MatchSeason == preferSeasonNo || c.SearchSeasonHint == preferSeasonNo {
			return 3
		}
		return 0
	}
	return 3
}

func (s *smartStreamingOfferScheduler) Push(off smartCandidateOffer) {
	if s == nil {
		return
	}
	tier := smartPlaybackOfferTier(s.req, off.Cand, s.hasMulti, s.preferSeason, s.matchSettings)
	if tier == 0 {
		return
	}
	key := smartPlaybackAttemptKey(off.Cand)
	s.mu.Lock()
	if _, ok := s.seen[key]; ok {
		s.mu.Unlock()
		return
	}
	s.seen[key] = struct{}{}
	switch tier {
	case 1:
		s.tier1 = append(s.tier1, off)
	case 2:
		s.tier2 = append(s.tier2, off)
	default:
		s.tier3 = append(s.tier3, off)
	}
	ready := s.drainLocked()
	s.mu.Unlock()
	for _, item := range ready {
		if s.emit != nil {
			s.emit(item.offer, item.tier)
		}
	}
}

func (s *smartStreamingOfferScheduler) drainLocked() []struct {
	offer smartCandidateOffer
	tier  int
} {
	out := make([]struct {
		offer smartCandidateOffer
		tier  int
	}, 0, len(s.tier1)+len(s.tier2)+len(s.tier3))
	for len(s.tier1) > 0 {
		out = append(out, struct {
			offer smartCandidateOffer
			tier  int
		}{offer: s.tier1[0], tier: 1})
		s.tier1 = s.tier1[1:]
	}
	for len(s.tier2) > 0 {
		out = append(out, struct {
			offer smartCandidateOffer
			tier  int
		}{offer: s.tier2[0], tier: 2})
		s.tier2 = s.tier2[1:]
	}
	for len(s.tier3) > 0 {
		out = append(out, struct {
			offer smartCandidateOffer
			tier  int
		}{offer: s.tier3[0], tier: 3})
		s.tier3 = s.tier3[1:]
	}
	return out
}

type smartCandidateOfferPQ struct {
	items         []smartCandidateOffer
	hasMulti      bool
	preferSeason  int
	matchSettings smartPlaybackSettings
}

func (pq smartCandidateOfferPQ) Len() int { return len(pq.items) }
func (pq smartCandidateOfferPQ) Less(i, j int) bool {
	a := pq.items[i].Cand
	b := pq.items[j].Cand
	return smartCompareSmartMatch(a, b, pq.hasMulti, pq.preferSeason, pq.matchSettings) > 0
}
func (pq smartCandidateOfferPQ) Swap(i, j int) { pq.items[i], pq.items[j] = pq.items[j], pq.items[i] }
func (pq *smartCandidateOfferPQ) Push(x any)   { pq.items = append(pq.items, x.(smartCandidateOffer)) }
func (pq *smartCandidateOfferPQ) Pop() any {
	n := len(pq.items)
	x := pq.items[n-1]
	pq.items = pq.items[:n-1]
	return x
}

func smartCollectPlaybackOffersFromTMDBAligned(
	database *db.DB,
	u *SmartUser,
	req smartPlaybackRequest,
	apiBase string,
	searchTitle string,
	want int,
	tmdbSeasons []smartTMDBSeason,
	singleBaselineSeasons []smartTMDBSeason,
	settings smartPlaybackSettings,
	rawCleanRules []string,
	rawEpisodeRules []string,
	rawMovieRules []string,
	strictOnly bool,
	allowSingleBaseline bool,
	primaryKind string,
	shouldStop func() bool,
	emit func(smartCandidateOffer, int),
) error {
	if database == nil {
		return errors.New("invalid database")
	}
	if strings.TrimSpace(apiBase) == "" {
		return errors.New("catpawrunner 接口地址未设置")
	}
	if strings.TrimSpace(searchTitle) == "" || want <= 0 {
		return errors.New("missing title")
	}
	isMovieMode := strings.TrimSpace(req.Kind) == "movie" || strings.TrimSpace(req.SubKind) == "movie"
	allowResolutionModes := []string{"strict-tmdb", "strict-douban", "degraded-single-baseline"}
	if strictOnly {
		allowResolutionModes = []string{"strict-tmdb"}
	}

	ctx, cancel := context.WithDeadline(context.Background(), time.Now().Add(18*time.Second))
	defer cancel()

	recomputeMeta := func(seasons []smartTMDBSeason) (out []smartTMDBSeason, hasMulti bool, prefer int, require bool) {
		positiveSeasons := 0
		hasLongSeason := false
		for _, s := range seasons {
			if s.Season > 0 {
				positiveSeasons++
				if s.EpisodeCount >= 50 {
					hasLongSeason = true
				}
			}
		}
		hasMulti = positiveSeasons >= 2
		prefer = 0
		if hasMulti {
			mapped := smartTMDBSeasonEpisodeOfGlobal(seasons, want)
			if mapped.Season > 0 {
				prefer = mapped.Season
			} else if req.Season > 0 {
				prefer = req.Season
			}
		}
		require = hasMulti && prefer >= 2 && !hasLongSeason
		out = append(out, seasons...)
		return out, hasMulti, prefer, require
	}
	seasonsForMapping, hasMulti, preferSeasonNo, requireSeasoned := recomputeMeta(tmdbSeasons)
	aggregateRules := smartLoadAggregateCleanRules(database)
	matchBlockKeyword := smartMatchBlockKeyword(searchTitle, aggregateRules)
	matchBlockIndex := smartLoadMatchBlockIndex(database, matchBlockKeyword)
	matchBlockEntryOf := func(siteKey, siteDetail string) *smartMatchBlockEntry {
		sk := strings.TrimSpace(siteKey)
		vid := strings.TrimSpace(siteDetail)
		if sk == "" || vid == "" {
			return nil
		}
		return matchBlockIndex[sk+"::"+vid]
	}
	qKey := smartAggKeyWithRules(searchTitle, aggregateRules)

	rawSites, _ := database.ListVideoSourceSites()
	sitesList := make([]sites.Site, 0, len(rawSites))
	for _, s := range rawSites {
		sitesList = append(sitesList, sites.Site{Key: s.Key, Name: s.Name, API: s.API, Type: s.Type})
	}
	states, _ := database.ReadVideoSourceSiteStates()
	statusMap := map[string]bool{}
	searchMap := map[string]bool{}
	for k, st := range states {
		if strings.TrimSpace(k) == "" {
			continue
		}
		statusMap[k] = st.Enabled
		searchMap[k] = st.Search
	}
	ordered := sites.ApplySiteOrder(sitesList, smartLoadSiteOrder(database, u))
	type task struct {
		Site sites.Site
		Idx  int
	}
	tasks := make([]task, 0, len(ordered))
	for i, s := range ordered {
		if s.Key == "" || s.API == "" {
			continue
		}
		if sites.IsConfigCenterSite(s) {
			continue
		}
		if enabled, ok := statusMap[s.Key]; ok && !enabled {
			continue
		}
		if searchEnabled, ok := searchMap[s.Key]; ok && !searchEnabled {
			continue
		}
		if st, ok := states[s.Key]; ok && st.SmartSkip {
			continue
		}
		tasks = append(tasks, task{Site: s, Idx: i})
	}
	if len(tasks) == 0 {
		return errors.New("未找到可用资源")
	}
	scheduler := newSmartStreamingOfferScheduler(req, hasMulti, preferSeasonNo, settings, func(off smartCandidateOffer, tier int) {
		if smartDebugLogEnabled() {
			smartDebugPrintf(
				"[smart][offer_enqueue] tier=%d site=(%s) panFlag=%s provider=%s matchShowName=%s matchRawName=%s",
				tier,
				smartLogSiteName(off.Cand.SiteKey, off.Cand.SiteName),
				strings.TrimSpace(off.Cand.PanFlag),
				smartPanMockProviderID(database, strings.TrimSpace(off.Cand.PanFlag)),
				strings.TrimSpace(off.Cand.Ep.Name),
				strings.TrimSpace(smartFirstRawNameFromURL(off.Cand.Ep.URL)),
			)
		}
		if emit != nil {
			emit(off, tier)
		}
	})
	var workersWG sync.WaitGroup
	siteWorker := func(t task) {
		defer workersWG.Done()
		select {
		case <-ctx.Done():
			return
		default:
		}
		if shouldStop != nil && shouldStop() {
			return
		}
		raw, err := cache.RequestSpiderSearchWithTimeout(apiBase, t.Site.API, searchTitle, 1, 6*time.Second)
		if err != nil {
			return
		}
		items := catpawrunner.NormalizeSearchList(raw)
		local := make([]smartSource, 0, smartMinInt(80, len(items)))
		localSeq := 0
		for _, it := range items {
			name := strings.TrimSpace(it.Name)
			id := strings.TrimSpace(it.ID)
			if id == "" || name == "" {
				continue
			}
			if entry := matchBlockEntryOf(t.Site.Key, id); entry != nil && entry.BlockAll {
				continue
			}
			key := smartAggKeyWithRules(name, aggregateRules)
			if key == "" {
				continue
			}
			score := smartMatchScore(qKey, key)
			if score <= 0 {
				continue
			}
			localSeq++
			local = append(local, smartSource{
				SiteKey:    t.Site.Key,
				SiteName:   t.Site.Name,
				SpiderAPI:  t.Site.API,
				SiteDetail: id,
				Remark:     strings.TrimSpace(it.Remark),
				Score:      score,
				Seq:        (t.Idx+1)*1000 + localSeq,
				NoNoise:    key == qKey,
			})
			if len(local) >= 80 {
				break
			}
		}
		if len(local) == 0 {
			return
		}
		sort.SliceStable(local, func(i, j int) bool {
			ai := local[i]
			aj := local[j]
			if ai.Score != aj.Score {
				return ai.Score > aj.Score
			}
			if ai.NoNoise != aj.NoNoise {
				return ai.NoNoise
			}
			return ai.Seq < aj.Seq
		})
		if len(local) > 20 {
			local = local[:20]
		}
		for _, src := range local {
			select {
			case <-ctx.Done():
				return
			default:
			}
			if shouldStop != nil && shouldStop() {
				return
			}
			blockedEntry := matchBlockEntryOf(src.SiteKey, src.SiteDetail)
			if blockedEntry != nil && blockedEntry.BlockAll {
				continue
			}
			detailRaw, err := cache.RequestSpiderDetailWithTimeout(apiBase, src.SpiderAPI, src.SiteDetail, 8*time.Second)
			if err != nil || detailRaw == nil {
				smartLogDetailError(src.SiteKey, src.SiteName, src.SiteDetail, err)
				continue
			}
			playFrom, playURL := catpawrunner.ExtractDetailPlayFromURL(detailRaw)
			rawPans := catpawrunner.ParsePlaySources(playFrom, playURL)
			if rawPans == nil {
				rawPans = []catpawrunner.Pan{}
			}
			if blockedEntry != nil && len(rawPans) > 0 {
				rawPans = smartFilterPansByBlockedFlags(rawPans, blockedEntry.PanFlags)
			}
			smartLogDetailOK(src.SiteKey, src.SiteName, src.SiteDetail, rawPans)
			resolvedPans := make([]catpawrunner.Pan, len(rawPans))
			copy(resolvedPans, rawPans)
			accessByShareID := map[string]string{}
			if smartIsPanMockEnabled(detailRaw) {
				if blockedEntry != nil && len(resolvedPans) > 0 {
					resolvedPans = smartFilterPansByBlockedFlags(resolvedPans, blockedEntry.PanFlags)
				}
				var pansMu sync.Mutex
				resolved, access := smartResolvePanMockDetailPansIncremental(database, src.SiteKey, src.SiteName, want, seasonsForMapping, hasMulti, rawCleanRules, rawEpisodeRules, resolvedPans, func(panIndex int, episodes []catpawrunner.Episode, accessDelta map[string]string) {
					pansMu.Lock()
					defer pansMu.Unlock()
					if panIndex >= 0 && panIndex < len(resolvedPans) {
						nextPan := resolvedPans[panIndex]
						nextPan.Episodes = episodes
						resolvedPans[panIndex] = nextPan
					}
					for sid, acc := range accessDelta {
						accessByShareID[sid] = acc
					}
				})
				resolvedPans = resolved
				if blockedEntry != nil && len(resolvedPans) > 0 {
					resolvedPans = smartFilterPansByBlockedFlags(resolvedPans, blockedEntry.PanFlags)
				}
				accessByShareID = access
				smartLogDetailPanMock(src.SiteKey, src.SiteName, src.SiteDetail, resolvedPans)
			}
			cands := []smartCandidate{}
			if isMovieMode {
				cands = smartBuildMovieCandidatesFromPans(src, resolvedPans, settings, rawCleanRules, rawMovieRules)
			} else {
				epMap, epLoose := smartBuildEpisodeMapsFromPans(src, resolvedPans, seasonsForMapping, singleBaselineSeasons, hasMulti, settings, rawCleanRules, rawEpisodeRules, allowSingleBaseline, primaryKind)
				cands = smartCandidatesForWant(epMap, epLoose, src, seasonsForMapping, hasMulti, preferSeasonNo, want, settings, requireSeasoned, allowResolutionModes)
			}
			for _, c := range cands {
				select {
				case <-ctx.Done():
					return
				default:
				}
				scheduler.Push(smartCandidateOffer{Cand: c, AccessByShare: accessByShareID})
			}
		}
	}
	for _, t := range tasks {
		workersWG.Add(1)
		go siteWorker(t)
	}
	workersWG.Wait()
	return nil
}

func smartTryPlaybackOffersInternal(database *db.DB, u *SmartUser, offers []smartCandidateOffer) (finalURL string, finalHeaders map[string]string, picked *smartPlaybackPickedMeta, err error) {
	if database == nil {
		return "", nil, nil, errors.New("invalid database")
	}
	if u == nil {
		return "", nil, nil, errors.New("invalid user")
	}
	if len(offers) == 0 {
		return "", nil, nil, errors.New("no offers")
	}
	apiBase := smartResolveCatApiBaseForUser(database, u)
	if strings.TrimSpace(apiBase) == "" {
		return "", nil, nil, errors.New("catpawrunner 接口地址未设置")
	}
	tvUser := strings.TrimSpace(u.Username)
	flowID := atomic.AddUint64(&smartPlayFlowSeq, 1)
	flowStart := time.Now()
	buildPicked := func(c smartCandidate, feat smartCandidateFeatures) *smartPlaybackPickedMeta {
		raw0 := strings.TrimSpace(c.RawName)
		if raw0 == "" {
			raw0 = smartStableRawNameFromEpisodeURL(c.Ep.URL)
		}
		return &smartPlaybackPickedMeta{
			SiteKey:    strings.TrimSpace(c.SiteKey),
			SiteName:   strings.TrimSpace(c.SiteName),
			SiteDetail: strings.TrimSpace(c.SiteDetail),
			PanFlag:    strings.TrimSpace(c.PanFlag),
			Provider:   smartPanMockProviderID(database, strings.TrimSpace(c.PanFlag)),
			ShowName:   strings.TrimSpace(c.Ep.Name),
			RawName:    raw0,
			Quality:    strings.TrimSpace(feat.Quality),
		}
	}
	for _, off := range offers {
		res := smartTryPlayPickedCandidate(flowID, database, apiBase, tvUser, off.Cand, off.AccessByShare)
		playURL := ""
		if res != nil {
			playURL = strings.TrimSpace(res.PlayURL)
		}
		if playURL == "" {
			continue
		}
		feat := smartComputeCandidateFeatures(res.Cand)
		if smartDebugLogEnabled() {
			raw0 := smartFirstRawNameFromURL(res.Cand.Ep.URL)
			smartDebugPrintf(
				"[smart][playback_ok] flow=%d ms=%d site=(%s) panFlag=%s provider=%s matchShowName=%s matchRawName=%s quality=%s url=%s",
				flowID,
				time.Since(flowStart).Milliseconds(),
				smartLogSiteName(res.Cand.SiteKey, res.Cand.SiteName),
				strings.TrimSpace(res.Cand.PanFlag),
				smartPanMockProviderID(database, strings.TrimSpace(res.Cand.PanFlag)),
				strings.TrimSpace(res.Cand.Ep.Name),
				strings.TrimSpace(raw0),
				strings.TrimSpace(feat.Quality),
				playURL,
			)
		}
		return playURL, res.Headers, buildPicked(res.Cand, feat), nil
	}
	return "", nil, nil, errors.New("无可用播放地址")
}

func smartPanEpisodeCount(pans []catpawrunner.Pan) int {
	total := 0
	for _, pan := range pans {
		total += len(pan.Episodes)
	}
	return total
}

func smartLogDetailOK(siteKey string, siteName string, siteDetail string, pans []catpawrunner.Pan) {
	if !smartDebugLogEnabled() {
		return
	}
	smartDebugPrintf(
		"[smart][detail_ok] site=(%s) siteDetail=%s panFlags=%d episodes=%d matchShowName= matchRawName= err=",
		smartLogSiteName(siteKey, siteName),
		strings.TrimSpace(siteDetail),
		len(pans),
		smartPanEpisodeCount(pans),
	)
}

func smartLogDetailError(siteKey string, siteName string, siteDetail string, err error) {
	if !smartDebugLogEnabled() {
		return
	}
	errMsg := ""
	if err != nil {
		errMsg = strings.TrimSpace(err.Error())
	}
	smartDebugPrintf(
		"[smart][detail_error] site=(%s) siteDetail=%s panFlags=0 episodes=0 matchShowName= matchRawName= err=%s",
		smartLogSiteName(siteKey, siteName),
		strings.TrimSpace(siteDetail),
		errMsg,
	)
}

func smartLogDetailPanMock(siteKey string, siteName string, siteDetail string, pans []catpawrunner.Pan) {
	if !smartDebugLogEnabled() {
		return
	}
	smartDebugPrintf(
		"[smart][detail_panmock] site=(%s) siteDetail=%s panFlags=%d episodes=%d matchShowName= matchRawName= err=",
		smartLogSiteName(siteKey, siteName),
		strings.TrimSpace(siteDetail),
		len(pans),
		smartPanEpisodeCount(pans),
	)
}
