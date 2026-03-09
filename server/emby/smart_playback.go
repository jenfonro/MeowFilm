package emby

import (
	"container/heap"
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
	"github.com/jenfonro/meowfilm/server/netdisk"
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
		vid := strings.TrimSpace(it.VideoID)
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
	SiteKey     string
	SiteName    string
	SpiderAPI   string
	VideoID     string
	VideoRemark string
	Score       int
	Seq         int
	NoNoise     bool
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
		if isConfigCenterSite(site{Key: key, API: api, Name: s.Name, Type: s.Type}) {
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

func smartLoadSiteOrder(database *db.DB, u *embyUser) []string {
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

func smartBuildAggregatedSources(database *db.DB, apiBase string, searchTitle string, u *embyUser) ([]smartSource, map[string]int) {
	aggregateRules := embyLoadAggregateCleanRules(database)
	matchBlockKeyword := smartMatchBlockKeyword(searchTitle, aggregateRules)
	matchBlockIndex := smartLoadMatchBlockIndex(database, matchBlockKeyword)
	rawSites, _ := database.ListVideoSourceSites()
	sites := make([]site, 0, len(rawSites))
	for _, s := range rawSites {
		sites = append(sites, site{Key: s.Key, Name: s.Name, API: s.API, Type: s.Type})
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
	ordered := applySiteOrder(sites, smartLoadSiteOrder(database, u))

	orderMap := map[string]int{}
	for i, s := range ordered {
		if s.Key == "" {
			continue
		}
		orderMap[s.Key] = i
	}

	qKey := embyAggKeyWithRules(searchTitle, aggregateRules)

	// Search across sites concurrently; Emby smart-play should not block on the slowest site.
	type task struct {
		Site site
		Idx  int // stable order index
	}
	tasks := make([]task, 0, len(ordered))
	for i, s := range ordered {
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

			raw, err := cache.RequestSpiderSearchCached(apiBase, tt.Site.API, searchTitle, 1)
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
				key := embyAggKeyWithRules(name, aggregateRules)
				if key == "" {
					continue
				}
				score := embyMatchScore(qKey, key)
				if score <= 0 {
					continue
				}
				localSeq++
				local = append(local, smartSource{
					SiteKey:     tt.Site.Key,
					SiteName:    tt.Site.Name,
					SpiderAPI:   tt.Site.API,
					VideoID:     strings.TrimSpace(it.ID),
					VideoRemark: strings.TrimSpace(it.Remark),
					Score:       score,
					Seq:         (tt.Idx+1)*1000 + localSeq, // deterministic tie-break
					NoNoise:     key == qKey,
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
			as := smartExtractSeasonHintFromSource(a.SiteName, a.VideoRemark)
			bs := smartExtractSeasonHintFromSource(b.SiteName, b.VideoRemark)
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
			aHas := smartHasExplicitSeasonMarkerInSource(a.SiteName, a.VideoRemark) || as > 0
			bHas := smartHasExplicitSeasonMarkerInSource(b.SiteName, b.VideoRemark) || bs > 0
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
		k := smartBuildSourceKey(s.SiteKey, s.SpiderAPI, s.VideoID)
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
		key := smartBuildSourceKey(s.SiteKey, s.SpiderAPI, s.VideoID)
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

func smartMaybeLogDoubanMapFallback(global int, seasons []embyTMDBSeason) {
	if !embyDebugLogEnabled() {
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
	embyDebugPrintf(
		"[smart][douban_map] global=%d mapped=S00E%03d reason=no_season_hit valid=%d sum=%d seasons=%v",
		g,
		g,
		valid,
		sum,
		seasons,
	)
}

func smartLoadOrBuildDetailCache(database *db.DB, apiBase string, src smartSource, tmdbSeasons []embyTMDBSeason, tmdbHasMultiSeason bool, settings smartPlaybackSettings, rawCleanRules []string, rawEpisodeRules []string) *smartDetailCacheEntry {
	key := smartBuildSourceKey(src.SiteKey, src.SpiderAPI, src.VideoID)
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

		detailRaw, err := cache.RequestSpiderDetailCached(apiBase, src.SpiderAPI, src.VideoID)
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
		playFrom, playURL := catpawrunner.ExtractDetailPlayFromURL(detailRaw)
		pans := catpawrunner.ParsePlaySources(playFrom, playURL)
		if entry.PanMockEnabled && pans != nil {
			resolved, accessMap := embyResolvePanMockDetailPans(database, src.SiteKey, src.SiteName, 0, tmdbSeasons, tmdbHasMultiSeason, rawCleanRules, rawEpisodeRules, pans)
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
	tmdbSeasons []embyTMDBSeason,
	tmdbHasMultiSeason bool,
	settings smartPlaybackSettings,
	rawCleanRules []string,
	rawEpisodeRules []string,
) (map[int][]smartCandidate, map[int][]smartCandidate) {
	episodeMap := map[int][]smartCandidate{}
	episodeMapLoose := map[int][]smartCandidate{}

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
			rawLower = strings.TrimSpace(rawLower + " " + srcRemarkLower)

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

			cand := smartCandidate{
				SiteKey:         src.SiteKey,
				SiteName:        src.SiteName,
				SpiderAPI:       src.SpiderAPI,
				VideoID:         src.VideoID,
				SrcRemarkLower:  srcRemarkLower,
				PanLabel:        panLabel,
				PanTokenIdx:     panTokenIdx,
				Ep:              ep,
				RawLower:        rawLower,
				MatchSeason:     seasonNo,
				HasSeasonMarker: seasonNo > 0,
				MatchKeyword:    smartComputePriorityMatch(rawLower, settings.KeywordTokensLower),
			}

			if tmdbHasMultiSeason && seasonNo <= 0 {
				list := episodeMapLoose[epNo]
				list = append(list, cand)
				episodeMapLoose[epNo] = list
				continue
			}

			keyNo := epNo
			if seasonNo > 0 {
				if g := smartTMDBGlobalEpisodeNoOf(tmdbSeasons, seasonNo, epNo); g > 0 {
					keyNo = g
				}
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
	srcRemarkLower := strings.ToLower(strings.TrimSpace(src.VideoRemark))
	for _, pan := range pans {
		panLabel := strings.TrimSpace(pan.Label)
		panTokenIdx := smartLabelTokenIdx(panLabel, settings.PanTokenOrderLower)
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
				VideoID:        src.VideoID,
				SrcRemarkLower: srcRemarkLower,
				PanLabel:       panLabel,
				PanTokenIdx:    panTokenIdx,
				Ep:             ep,
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

func smartPickCandidateFromMaps(
	episodeMap map[int][]smartCandidate,
	episodeMapLoose map[int][]smartCandidate,
	src smartSource,
	tmdbSeasons []embyTMDBSeason,
	tmdbHasMultiSeason bool,
	preferSeasonNo int,
	want int,
	settings smartPlaybackSettings,
	requireSeasoned bool,
) *smartCandidate {
	list := smartCandidatesForWant(episodeMap, episodeMapLoose, src, tmdbSeasons, tmdbHasMultiSeason, preferSeasonNo, want, settings, requireSeasoned)
	if len(list) == 0 {
		return nil
	}
	best := list[0]
	return &best
}

func smartCandidatesForWant(
	episodeMap map[int][]smartCandidate,
	episodeMapLoose map[int][]smartCandidate,
	src smartSource,
	tmdbSeasons []embyTMDBSeason,
	tmdbHasMultiSeason bool,
	preferSeasonNo int,
	want int,
	settings smartPlaybackSettings,
	requireSeasoned bool,
) []smartCandidate {
	searchSeasonHint := smartExtractSeasonHintFromSource(src.SiteName, src.VideoRemark)
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
		if !embyDebugLogEnabled() {
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
		embyDebugPrintf(
			"[smart][play_try_status] flow=%d id=%d ms=%d site=(%s) panFlag=%s provider=%s status=%s reason=%s headers=%d url=%s err=%s spider=%s videoId=%s",
			flowID,
			tryID,
			time.Since(tryStart).Milliseconds(),
			smartLogSiteName(cand.SiteKey, cand.SiteName),
			strings.TrimSpace(cand.PanLabel),
			smartPanMockProviderID(database, strings.TrimSpace(cand.PanLabel)),
			strings.TrimSpace(status),
			strings.TrimSpace(reason),
			hc,
			u,
			errMsg,
			strings.TrimSpace(cand.SpiderAPI),
			strings.TrimSpace(cand.VideoID),
		)
	}
	if embyDebugLogEnabled() {
		rawNames := smartExtractRawNamesFromEpisodeURL(cand.Ep.URL)
		raw0 := ""
		if len(rawNames) > 0 {
			raw0 = strings.TrimSpace(rawNames[0])
		}
		embyDebugPrintf(
			"[smart][play_try] flow=%d id=%d site=(%s) panFlag=%s provider=%s matchShowName=%s matchRawName=%s spider=%s videoId=%s",
			flowID,
			tryID,
			smartLogSiteName(cand.SiteKey, cand.SiteName),
			strings.TrimSpace(cand.PanLabel),
			smartPanMockProviderID(database, strings.TrimSpace(cand.PanLabel)),
			strings.TrimSpace(cand.Ep.Name),
			raw0,
			strings.TrimSpace(cand.SpiderAPI),
			strings.TrimSpace(cand.VideoID),
		)
	}

	type playResult struct {
		status  string // ok|empty|err
		playURL string
		headers map[string]string
		err     error
	}

	doPlay := func() playResult {
		pid := smartPanMockProviderID(database, strings.TrimSpace(cand.PanLabel))
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
						if v, ok := embyPanMock189AccessGet(shareID); ok {
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
			downloadURL, playURL, err := netdisk.Yun139Play(database, strings.TrimSpace(cand.PanLabel), strings.TrimSpace(cand.Ep.URL))
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
			u, header, err := netdisk.BaiduPlay(database, strings.TrimSpace(cand.PanLabel), strings.TrimSpace(cand.Ep.URL), "/MeowFilm")
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
			urlPicked := strings.TrimSpace(catpawrunner.PickFirstPlayableURL(playRaw))
			if urlPicked == "" {
				return playResult{status: "empty"}
			}
			urlPicked = catpawrunner.RewriteProxyURLToBase(urlPicked, apiBase, tvUser)
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

func smartFetchDetailAndPickAndPlay(database *db.DB, apiBase string, tvUser string, src smartSource, tmdbSeasons []embyTMDBSeason, tmdbHasMultiSeason bool, preferSeasonNo int, want int, settings smartPlaybackSettings, rawCleanRules []string, rawEpisodeRules []string, requireSeasoned bool) *smartPickResult {
	siteKey := strings.TrimSpace(src.SiteKey)
	spiderApi := strings.TrimSpace(src.SpiderAPI)
	videoId := strings.TrimSpace(src.VideoID)
	if siteKey == "" || spiderApi == "" || videoId == "" || want <= 0 {
		return nil
	}
	searchSeasonHint := smartExtractSeasonHintFromSource(src.SiteName, src.VideoRemark)

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
				tmdbHasMultiSeason,
				preferSeasonNo,
				settings,
				rawCleanRules,
				rawEpisodeRules,
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
			urlPicked := strings.TrimSpace(catpawrunner.PickFirstPlayableURL(playRaw))
			if urlPicked == "" {
				return nil
			}
			urlPicked = catpawrunner.RewriteProxyURLToBase(urlPicked, apiBase, tvUser)
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
	urlPicked := catpawrunner.PickFirstPlayableURL(playRaw)
	if strings.TrimSpace(urlPicked) == "" {
		return nil
	}
	urlPicked = catpawrunner.RewriteProxyURLToBase(urlPicked, apiBase, tvUser)
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

type smartPlaybackPickedMeta struct {
	SiteKey  string
	SiteName string
	VideoID  string
	PanFlag  string
	Provider string
	ShowName string
	RawName  string
	Quality  string
}

func smartResolvePlaybackFromTMDB(database *db.DB, u *embyUser, req smartPlaybackRequest) (finalURL string, finalHeaders map[string]string, picked *smartPlaybackPickedMeta, err error) {
	if database == nil {
		return "", nil, nil, errors.New("invalid database")
	}
	if req.TMDBID <= 0 {
		return "", nil, nil, errors.New("invalid tmdb id")
	}

	apiBase := embyResolveCatApiBaseForUser(database, u)
	if apiBase == "" {
		return "", nil, nil, errors.New("catpawrunner 接口地址未设置")
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
			return "", nil, nil, errors.New("TMDB 请求失败")
		}
		searchTitle = strings.TrimSpace(md.Title)
		want = 1
	} else if strings.TrimSpace(req.Kind) == "tv" {
		td, err := embyTMDBGetTVDetail(database, req.TMDBID)
		if err != nil || td == nil || strings.TrimSpace(td.Title) == "" {
			return "", nil, nil, errors.New("TMDB 请求失败")
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
		return "", nil, nil, errors.New("unsupported kind")
	}
	if strings.TrimSpace(searchTitle) == "" {
		return "", nil, nil, errors.New("missing title")
	}

	settings := smartLoadPlaybackSettings(database)
	rawEpisodeRules, _ := database.ListMagicEpisodeRules()
	rawCleanRules, _ := database.ListMagicEpisodeCleanRegexRules()
	rawMovieRules, _ := database.ListMagicMovieRules()
	if strings.TrimSpace(req.Kind) != "movie" && (len(rawEpisodeRules) == 0 || len(rawCleanRules) == 0) {
		return "", nil, nil, errors.New("magic regex rules 未设置")
	}

	// Search sources and resolve playback in a streaming (search->detail->list) pipeline,
	// aligned with the front-end smart-play strategy:
	// - search across sites concurrently
	// - per-site detail requests are sequential
	// - pan_mock list resolving does not block fetching next details
	resolvedURL, resolvedHeaders, picked, resolveErr := smartResolvePlaybackFromTMDBAlignedCoordinator(database, u, req, apiBase, tvUser, searchTitle, want, tmdbSeasons, settings, rawCleanRules, rawEpisodeRules, rawMovieRules)
	if resolveErr == nil && strings.TrimSpace(resolvedURL) != "" {
		return resolvedURL, resolvedHeaders, picked, nil
	}

	// If TMDB suggests single-season but sources clearly indicate multi-season,
	// probe Douban and only apply it when it actually remaps the requested global episode
	// into a later season (and matches the max season hinted by sources).
	// (Aligned resolver already handles multi-season hints and does not require a second pass here.)

	if resolveErr != nil {
		return "", nil, nil, resolveErr
	}
	return "", nil, nil, errors.New("无可用播放地址")
}

type smartCandidateOffer struct {
	Cand          smartCandidate
	AccessByShare map[string]string // for 189 play
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

func smartResolvePlaybackFromTMDBAlignedCoordinator(
	database *db.DB,
	u *embyUser,
	req smartPlaybackRequest,
	apiBase string,
	tvUser string,
	searchTitle string,
	want int,
	tmdbSeasons []embyTMDBSeason,
	settings smartPlaybackSettings,
	rawCleanRules []string,
	rawEpisodeRules []string,
	rawMovieRules []string,
) (finalURL string, finalHeaders map[string]string, picked *smartPlaybackPickedMeta, err error) {
	if database == nil {
		return "", nil, nil, errors.New("invalid database")
	}
	if strings.TrimSpace(apiBase) == "" {
		return "", nil, nil, errors.New("catpawrunner 接口地址未设置")
	}
	if strings.TrimSpace(searchTitle) == "" || want <= 0 {
		return "", nil, nil, errors.New("missing title")
	}
	isMovieMode := strings.TrimSpace(req.Kind) == "movie" || strings.TrimSpace(req.SubKind) == "movie"

	flowStart := time.Now()
	flowID := atomic.AddUint64(&smartPlayFlowSeq, 1)

	ctx, cancel := context.WithDeadline(context.Background(), time.Now().Add(18*time.Second))
	defer cancel()

	tierGate2At := flowStart.Add(10 * time.Second)
	tierGate3At := flowStart.Add(15 * time.Second)

	attemptKey := func(c smartCandidate) uint64 {
		raw0 := smartFirstRawNameFromURL(strings.TrimSpace(c.Ep.URL))
		h := fnv.New64a()
		_, _ = h.Write([]byte(strings.TrimSpace(c.SiteKey)))
		_, _ = h.Write([]byte{0})
		_, _ = h.Write([]byte(strings.TrimSpace(c.VideoID)))
		_, _ = h.Write([]byte{0})
		_, _ = h.Write([]byte(strings.TrimSpace(c.PanLabel)))
		_, _ = h.Write([]byte{0})
		_, _ = h.Write([]byte(strings.TrimSpace(c.Ep.Flag)))
		_, _ = h.Write([]byte{0})
		_, _ = h.Write([]byte(strings.TrimSpace(raw0)))
		return h.Sum64()
	}

	buildPicked := func(c smartCandidate, feat smartCandidateFeatures) *smartPlaybackPickedMeta {
		rawNames := smartExtractRawNamesFromEpisodeURL(c.Ep.URL)
		raw0 := ""
		if len(rawNames) > 0 {
			raw0 = strings.TrimSpace(rawNames[0])
		}
		return &smartPlaybackPickedMeta{
			SiteKey:  strings.TrimSpace(c.SiteKey),
			SiteName: strings.TrimSpace(c.SiteName),
			VideoID:  strings.TrimSpace(c.VideoID),
			PanFlag:  strings.TrimSpace(c.PanLabel),
			Provider: smartPanMockProviderID(database, strings.TrimSpace(c.PanLabel)),
			ShowName: strings.TrimSpace(c.Ep.Name),
			RawName:  raw0,
			Quality:  strings.TrimSpace(feat.Quality),
		}
	}

	upsertSmartPlayHistoryBestEffort := func(c smartCandidate) {
		// Intentionally disabled: persist history only on Emby session reports.
		// The stream resolver stores the picked site binding in-memory (mediaSourceId -> siteKey/videoId/pan),
		// which the session reporter can then use to write a single canonical history row.
		_ = c
		return
	}

	recomputeMeta := func(seasons []embyTMDBSeason) (out []embyTMDBSeason, hasMulti bool, prefer int, require bool) {
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
		// For long-running / continuous shows (e.g. 国漫、长篇动画), TMDB sometimes splits them into seasons
		// while most site sources keep global episode numbers without season markers. In that case, requiring
		// season signals will drop all candidates (offers=0). Relax the season requirement when any season is long.
		require = hasMulti && prefer >= 2 && !hasLongSeason
		out = make([]embyTMDBSeason, 0, len(seasons))
		out = append(out, seasons...)
		return out, hasMulti, prefer, require
	}

	seasonsForMapping, hasMulti, preferSeasonNo, requireSeasoned := recomputeMeta(tmdbSeasons)
	aggregateRules := embyLoadAggregateCleanRules(database)
	matchBlockKeyword := smartMatchBlockKeyword(searchTitle, aggregateRules)
	matchBlockIndex := smartLoadMatchBlockIndex(database, matchBlockKeyword)
	needDouban := strings.TrimSpace(req.Kind) == "tv" && strings.TrimSpace(req.SubKind) == "episode" && len(tmdbSeasons) < 2 && strings.TrimSpace(searchTitle) != ""
	if needDouban {
		if over, ok := doubanProbeSeasons(database, req.TMDBID, searchTitle, want); ok && len(over) >= 2 {
			seasonsForMapping, hasMulti, preferSeasonNo, requireSeasoned = recomputeMeta(over)
			if embyDebugLogEnabled() {
				mapped := smartTMDBSeasonEpisodeOfGlobal(over, want)
				embyDebugPrintf("[smart][douban] override tmdbId=%d want=%d -> mapped=S%02dE%03d", req.TMDBID, want, mapped.Season, mapped.Episode)
			}
		}
	}

	matchBlockEntryOf := func(siteKey, videoID string) *smartMatchBlockEntry {
		sk := strings.TrimSpace(siteKey)
		vid := strings.TrimSpace(videoID)
		if sk == "" || vid == "" {
			return nil
		}
		return matchBlockIndex[sk+"::"+vid]
	}

	isPanBlocked := func(entry *smartMatchBlockEntry, label string) bool {
		if entry == nil || len(entry.PanFlags) == 0 {
			return false
		}
		key := strings.TrimSpace(label)
		if key == "" {
			return false
		}
		_, ok := entry.PanFlags[key]
		return ok
	}

	tryHistoryFastPath := func() *smartPickResult {
		if database == nil || u == nil {
			return nil
		}
		uid, _ := strconv.ParseInt(strings.TrimSpace(u.ID), 10, 64)
		if uid <= 0 {
			return nil
		}
		kind := strings.TrimSpace(req.Kind)
		if kind != "tv" && kind != "movie" {
			return nil
		}
		contentKey := strings.ToLower(strings.TrimSpace("tmdb:" + kind + ":" + strconv.Itoa(req.TMDBID)))
		row, e := database.GetPlayHistoryLatestByContentKey(uid, contentKey)
		if e != nil || row == nil {
			return nil
		}
		siteKey := strings.TrimSpace(row.SiteKey)
		videoID := strings.TrimSpace(row.VideoID)
		panLabel := strings.TrimSpace(row.PanLabel)
		playFlag := strings.TrimSpace(row.PlayFlag)
		if siteKey == "" || videoID == "" {
			return nil
		}
		if entry := matchBlockEntryOf(siteKey, videoID); entry != nil && entry.BlockAll {
			return nil
		}
		spiderAPI := strings.TrimSpace(embyResolveSpiderAPIBySiteKey(database, siteKey))
		if spiderAPI == "" {
			return nil
		}
		blockedEntry := matchBlockEntryOf(siteKey, videoID)
		src := smartSource{SiteKey: siteKey, SiteName: strings.TrimSpace(row.SiteName), SpiderAPI: spiderAPI, VideoID: videoID, Score: 1000, Seq: 0, NoNoise: true}
		panProviderOfLabel := func(label string) string {
			raw := strings.TrimSpace(label)
			if raw == "" {
				return ""
			}
			key := smartPanMatchLabelText(raw)
			if key == "" {
				return ""
			}
			entries := smartLoadPanMatchEntries(database)
			for _, it := range entries {
				t := strings.TrimSpace(it.TokenLower)
				if t == "" || !strings.Contains(key, t) {
					continue
				}
				if pid := smartPanToProviderID(it.PanLower); pid != "" {
					return pid
				}
			}
			return ""
		}
		tryPlayFromPans := func(pans []catpawrunner.Pan, accessByShareID map[string]string, ignorePanOrder bool) *smartPickResult {
			if len(pans) == 0 {
				return nil
			}
			cands := []smartCandidate{}
			if isMovieMode {
				cands = smartBuildMovieCandidatesFromPans(src, pans, settings, rawCleanRules, rawMovieRules)
			} else {
				epMap, epLoose := smartBuildEpisodeMapsFromPans(src, pans, seasonsForMapping, hasMulti, settings, rawCleanRules, rawEpisodeRules)
				cands = smartCandidatesForWant(epMap, epLoose, src, seasonsForMapping, hasMulti, preferSeasonNo, want, settings, requireSeasoned)
			}
			if len(cands) == 0 {
				return nil
			}
			if ignorePanOrder {
				if best := smartPickBestMatchIgnorePanOrder(cands, hasMulti, preferSeasonNo, settings); best != nil {
					if res := smartTryPlayPickedCandidate(flowID, database, apiBase, tvUser, *best, accessByShareID); res != nil && strings.TrimSpace(res.PlayURL) != "" {
						return res
					}
				}
			}
			limit := 3
			if len(cands) < limit {
				limit = len(cands)
			}
			for i := 0; i < limit; i++ {
				c := cands[i]
				if res := smartTryPlayPickedCandidate(flowID, database, apiBase, tvUser, c, accessByShareID); res != nil && strings.TrimSpace(res.PlayURL) != "" {
					return res
				}
			}
			return nil
		}

		tryPlayFromVod := func(vod string, label string, accessByShareID map[string]string) *smartPickResult {
			episodes := smartParseVodPlayURLToEpisodes(vod)
			if len(episodes) == 0 {
				return nil
			}
			chooseLabel := strings.TrimSpace(label)
			if chooseLabel == "" {
				chooseLabel = "历史"
			}
			pans := []catpawrunner.Pan{{Label: chooseLabel, Episodes: episodes}}
			return tryPlayFromPans(pans, accessByShareID, true)
		}

		// 1) History playFlag fast path: only for pan_mock-like flags.
		if strings.Contains(playFlag, "-") && !isPanBlocked(blockedEntry, playFlag) {
			pid := smartPanMockProviderID(database, playFlag)
			if pid != "" {
				accessByShareID := map[string]string{}
				switch pid {
				case "quark":
					if vod, _, err := netdisk.QuarkList(database, playFlag, ""); err == nil && strings.TrimSpace(vod) != "" {
						if picked := tryPlayFromVod(vod, playFlag, accessByShareID); picked != nil {
							return picked
						}
					}
				case "uc":
					if vod, _, err := netdisk.UCList(database, playFlag, ""); err == nil && strings.TrimSpace(vod) != "" {
						if picked := tryPlayFromVod(vod, playFlag, accessByShareID); picked != nil {
							return picked
						}
					}
				case "139":
					if vod, _, err := netdisk.Yun139List(database, playFlag, ""); err == nil && strings.TrimSpace(vod) != "" {
						if picked := tryPlayFromVod(vod, playFlag, accessByShareID); picked != nil {
							return picked
						}
					}
				case "baidu":
					if vod, _, err := netdisk.BaiduList(database, playFlag, ""); err == nil && strings.TrimSpace(vod) != "" {
						if picked := tryPlayFromVod(vod, playFlag, accessByShareID); picked != nil {
							return picked
						}
					}
				case "189":
					if vod, _, _, err := netdisk.Tianyi189List(database, playFlag, ""); err == nil && strings.TrimSpace(vod) != "" {
						if picked := tryPlayFromVod(vod, playFlag, accessByShareID); picked != nil {
							return picked
						}
					}
				}
			}
		}

		// 2) Fallback to history site detail; prefer previous pan label, but allow new pans too.
		detailRaw, e2 := cache.RequestSpiderDetailCached(apiBase, spiderAPI, videoID)
		if e2 != nil || detailRaw == nil {
			return nil
		}
		playFrom, playURL := catpawrunner.ExtractDetailPlayFromURL(detailRaw)
		pans := catpawrunner.ParsePlaySources(playFrom, playURL)
		chosen := []catpawrunner.Pan{}
		if len(pans) > 0 && blockedEntry != nil {
			pans = smartFilterPansByBlockedFlags(pans, blockedEntry.PanFlags)
		}
		if panLabel != "" {
			for _, p := range pans {
				if strings.TrimSpace(p.Label) == panLabel {
					chosen = append(chosen, p)
					break
				}
			}
			for _, p := range pans {
				if strings.TrimSpace(p.Label) == panLabel {
					continue
				}
				chosen = append(chosen, p)
			}
		} else {
			chosen = append(chosen, pans...)
		}
		accessByShareID := map[string]string{}
		if smartIsPanMockEnabled(detailRaw) {
			if blockedEntry != nil {
				chosen = smartFilterPansByBlockedFlags(chosen, blockedEntry.PanFlags)
			}
			resolved, access := embyResolvePanMockDetailPansIncremental(database, src.SiteKey, src.SiteName, want, seasonsForMapping, hasMulti, rawCleanRules, rawEpisodeRules, chosen, nil)
			chosen = resolved
			if blockedEntry != nil {
				chosen = smartFilterPansByBlockedFlags(chosen, blockedEntry.PanFlags)
			}
			accessByShareID = access
		}
		if len(chosen) == 0 {
			return nil
		}

		// Round 1: when list fails, prefer history playFlag mapped pan provider first.
		// Round 2: fallback to generic smart matching rules over all pans.
		historyProvider := panProviderOfLabel(playFlag)
		if historyProvider == "" {
			historyProvider = panProviderOfLabel(panLabel)
		}
		if historyProvider != "" {
			preferred := make([]catpawrunner.Pan, 0, len(chosen))
			for _, p := range chosen {
				if panProviderOfLabel(strings.TrimSpace(p.Label)) == historyProvider {
					preferred = append(preferred, p)
				}
			}
			if len(preferred) > 0 {
				if picked := tryPlayFromPans(preferred, accessByShareID, false); picked != nil {
					return picked
				}
			}
		}
		if picked := tryPlayFromPans(chosen, accessByShareID, false); picked != nil {
			return picked
		}
		return nil
	}
	historyResultCh := make(chan *smartPickResult, 1)
	go func() {
		historyResultCh <- tryHistoryFastPath()
		close(historyResultCh)
	}()

	qKey := embyAggKeyWithRules(searchTitle, aggregateRules)

	rawSites, _ := database.ListVideoSourceSites()
	sitesList := make([]site, 0, len(rawSites))
	for _, s := range rawSites {
		sitesList = append(sitesList, site{Key: s.Key, Name: s.Name, API: s.API, Type: s.Type})
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
	ordered := applySiteOrder(sitesList, smartLoadSiteOrder(database, u))
	type task struct {
		Site site
		Idx  int
	}
	tasks := make([]task, 0, len(ordered))
	for i, s := range ordered {
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
		if st, ok := states[s.Key]; ok && st.SmartSkip {
			continue
		}
		tasks = append(tasks, task{Site: s, Idx: i})
	}
	if len(tasks) == 0 {
		return "", nil, nil, errors.New("未找到可用资源")
	}

	panRuleEnabled := len(settings.PanTokenOrderLower) > 0
	classifyTier := func(c smartCandidate) int {
		feat := smartComputeCandidateFeatures(c)
		// Tier 1: 4K + pan match rule (token list) if configured.
		if feat.QualityRank == 3 {
			if !panRuleEnabled || c.PanTokenIdx >= 0 {
				return 1
			}
			// Tier 2: 4K but pan not in allow list.
			return 2
		}

		// Tier 3: fallback candidates. For TV multi-season, require season signal.
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

	offerCh := make(chan smartCandidateOffer, 1024)
	var workersWG sync.WaitGroup

	var offersSeen int64
	var offersDeduped int64
	var offersQueued int64
	var offersDroppedFull int64
	attempts := 0

	siteWorker := func(t task) {
		defer workersWG.Done()
		select {
		case <-ctx.Done():
			return
		default:
		}

		raw, err := cache.RequestSpiderSearchCachedWithTimeout(apiBase, t.Site.API, searchTitle, 1, 6*time.Second)
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
			key := embyAggKeyWithRules(name, aggregateRules)
			if key == "" {
				continue
			}
			score := embyMatchScore(qKey, key)
			if score <= 0 {
				continue
			}
			localSeq++
			local = append(local, smartSource{
				SiteKey:     t.Site.Key,
				SiteName:    t.Site.Name,
				SpiderAPI:   t.Site.API,
				VideoID:     id,
				VideoRemark: strings.TrimSpace(it.Remark),
				Score:       score,
				Seq:         (t.Idx+1)*1000 + localSeq,
				NoNoise:     key == qKey,
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
			blockedEntry := matchBlockEntryOf(src.SiteKey, src.VideoID)
			if blockedEntry != nil && blockedEntry.BlockAll {
				continue
			}
			detailRaw, err := cache.RequestSpiderDetailCachedWithTimeout(apiBase, src.SpiderAPI, src.VideoID, 8*time.Second)
			if err != nil || detailRaw == nil {
				continue
			}
			playFrom, playURL := catpawrunner.ExtractDetailPlayFromURL(detailRaw)
			pans := catpawrunner.ParsePlaySources(playFrom, playURL)
			if pans == nil {
				pans = []catpawrunner.Pan{}
			}
			if blockedEntry != nil && len(pans) > 0 {
				pans = smartFilterPansByBlockedFlags(pans, blockedEntry.PanFlags)
			}
			accessByShareID := map[string]string{}
			if smartIsPanMockEnabled(detailRaw) {
				if blockedEntry != nil && len(pans) > 0 {
					pans = smartFilterPansByBlockedFlags(pans, blockedEntry.PanFlags)
				}
				resolved, access := embyResolvePanMockDetailPansIncremental(
					database,
					src.SiteKey,
					src.SiteName,
					want,
					seasonsForMapping,
					hasMulti,
					rawCleanRules,
					rawEpisodeRules,
					pans,
					nil,
				)
				pans = resolved
				if blockedEntry != nil && len(pans) > 0 {
					pans = smartFilterPansByBlockedFlags(pans, blockedEntry.PanFlags)
				}
				accessByShareID = access
			}

			cands := []smartCandidate{}
			scanned := 0
			if isMovieMode {
				for _, p := range pans {
					scanned += len(p.Episodes)
				}
				cands = smartBuildMovieCandidatesFromPans(src, pans, settings, rawCleanRules, rawMovieRules)
			} else {
				epMap, epLoose := smartBuildEpisodeMapsFromPans(src, pans, seasonsForMapping, hasMulti, settings, rawCleanRules, rawEpisodeRules)
				cands = smartCandidatesForWant(epMap, epLoose, src, seasonsForMapping, hasMulti, preferSeasonNo, want, settings, requireSeasoned)
			}
			if isMovieMode && embyDebugLogEnabled() {
				embyDebugPrintf(
					"[smart][movie_rules_eval] site=(%s) scanned=%d hit=%d",
					smartLogSiteName(src.SiteKey, src.SiteName),
					scanned,
					len(cands),
				)
			}
			if len(cands) == 0 {
				continue
			}

			queued4K := 0
			queuedAny := 0
			for _, c := range cands {
				feat := smartComputeCandidateFeatures(c)
				if feat.QualityRank == 3 {
					queued4K++
				}
				queuedAny++
				offer := smartCandidateOffer{
					Cand:          c,
					AccessByShare: accessByShareID,
				}
				select {
				case <-ctx.Done():
					return
				case offerCh <- offer:
				}
				if queued4K >= 2 && queuedAny >= 4 {
					break
				}
			}
		}
	}

	for _, t := range tasks {
		workersWG.Add(1)
		go siteWorker(t)
	}
	go func() {
		workersWG.Wait()
		close(offerCh)
	}()

	pendingSet := map[uint64]struct{}{}
	attempted := map[uint64]struct{}{}
	pq1 := &smartCandidateOfferPQ{hasMulti: hasMulti, preferSeason: preferSeasonNo, matchSettings: settings}
	pq2 := &smartCandidateOfferPQ{hasMulti: hasMulti, preferSeason: preferSeasonNo, matchSettings: settings}
	pq3 := &smartCandidateOfferPQ{hasMulti: hasMulti, preferSeason: preferSeasonNo, matchSettings: settings}
	heap.Init(pq1)
	heap.Init(pq2)
	heap.Init(pq3)

	var mu sync.Mutex
	var offersClosed bool
	wakeCh := make(chan struct{}, 1)

	go func() {
		for off := range offerCh {
			atomic.AddInt64(&offersSeen, 1)
			k := attemptKey(off.Cand)
			mu.Lock()
			if _, ok := attempted[k]; ok {
				atomic.AddInt64(&offersDeduped, 1)
				mu.Unlock()
				continue
			}
			if _, ok := pendingSet[k]; ok {
				atomic.AddInt64(&offersDeduped, 1)
				mu.Unlock()
				continue
			}
			if pq1.Len()+pq2.Len()+pq3.Len() >= smartPlayMaxPendingOffers {
				atomic.AddInt64(&offersDroppedFull, 1)
				mu.Unlock()
				continue
			}
			tier := classifyTier(off.Cand)
			if tier == 0 {
				atomic.AddInt64(&offersDeduped, 1)
				mu.Unlock()
				continue
			}
			pendingSet[k] = struct{}{}
			switch tier {
			case 1:
				heap.Push(pq1, off)
			case 2:
				heap.Push(pq2, off)
			default:
				heap.Push(pq3, off)
			}
			atomic.AddInt64(&offersQueued, 1)
			mu.Unlock()
			select {
			case wakeCh <- struct{}{}:
			default:
			}
		}
		mu.Lock()
		offersClosed = true
		mu.Unlock()
		select {
		case wakeCh <- struct{}{}:
		default:
		}
	}()

	pickNext := func(now time.Time) (smartCandidateOffer, int, bool) {
		allow2 := !now.Before(tierGate2At)
		allow3 := !now.Before(tierGate3At)
		if pq1.Len() > 0 {
			return heap.Pop(pq1).(smartCandidateOffer), 1, true
		}
		if allow2 && pq2.Len() > 0 {
			return heap.Pop(pq2).(smartCandidateOffer), 2, true
		}
		if allow3 && pq3.Len() > 0 {
			return heap.Pop(pq3).(smartCandidateOffer), 3, true
		}
		return smartCandidateOffer{}, 0, false
	}

	waitUntilOrWake := func(deadline time.Time) {
		d := time.Until(deadline)
		if d <= 0 {
			return
		}
		timer := time.NewTimer(d)
		defer timer.Stop()
		select {
		case <-ctx.Done():
		case <-wakeCh:
		case <-timer.C:
		}
	}

	for {
		select {
		case histRes, ok := <-historyResultCh:
			if !ok {
				historyResultCh = nil
			} else if histRes != nil && strings.TrimSpace(histRes.PlayURL) != "" {
				feat := smartComputeCandidateFeatures(histRes.Cand)
				upsertSmartPlayHistoryBestEffort(histRes.Cand)
				cancel()
				if embyDebugLogEnabled() {
					embyDebugPrintf(
						"[smart][flow_done] flow=%d ms=%d status=ok_history_parallel offers=%d queued=%d dedup=%d dropped=%d attempts=%d",
						flowID,
						time.Since(flowStart).Milliseconds(),
						atomic.LoadInt64(&offersSeen),
						atomic.LoadInt64(&offersQueued),
						atomic.LoadInt64(&offersDeduped),
						atomic.LoadInt64(&offersDroppedFull),
						attempts,
					)
				}
				return strings.TrimSpace(histRes.PlayURL), histRes.Headers, buildPicked(histRes.Cand, feat), nil
			}
		default:
		}
		select {
		case <-ctx.Done():
			if embyDebugLogEnabled() {
				embyDebugPrintf(
					"[smart][flow_done] flow=%d ms=%d status=deadline offers=%d queued=%d dedup=%d dropped=%d attempts=%d",
					flowID,
					time.Since(flowStart).Milliseconds(),
					atomic.LoadInt64(&offersSeen),
					atomic.LoadInt64(&offersQueued),
					atomic.LoadInt64(&offersDeduped),
					atomic.LoadInt64(&offersDroppedFull),
					attempts,
				)
			}
			return "", nil, nil, errors.New("无可用播放地址")
		default:
		}

		mu.Lock()
		now := time.Now()
		at, tier, ok := pickNext(now)
		p1 := pq1.Len()
		p2 := pq2.Len()
		p3 := pq3.Len()
		if !ok {
			closed := offersClosed
			mu.Unlock()
			if closed {
				// Do not end early when only gated tiers are pending.
				// Example: queued tier2/tier3 offers, workers finished quickly,
				// but gate time has not arrived yet.
				if p1 == 0 && p2 > 0 && now.Before(tierGate2At) {
					waitUntilOrWake(tierGate2At)
					continue
				}
				if p1 == 0 && p2 == 0 && p3 > 0 && now.Before(tierGate3At) {
					waitUntilOrWake(tierGate3At)
					continue
				}
				if embyDebugLogEnabled() {
					embyDebugPrintf(
						"[smart][flow_done] flow=%d ms=%d status=exhausted offers=%d queued=%d dedup=%d dropped=%d attempts=%d",
						flowID,
						time.Since(flowStart).Milliseconds(),
						atomic.LoadInt64(&offersSeen),
						atomic.LoadInt64(&offersQueued),
						atomic.LoadInt64(&offersDeduped),
						atomic.LoadInt64(&offersDroppedFull),
						attempts,
					)
				}
				return "", nil, nil, errors.New("无可用播放地址")
			}
			// Wait for offers or tier gates.
			if now.Before(tierGate2At) {
				waitUntilOrWake(tierGate2At)
			} else if now.Before(tierGate3At) {
				waitUntilOrWake(tierGate3At)
			} else {
				waitUntilOrWake(now.Add(2 * time.Second))
			}
			continue
		}

		k := attemptKey(at.Cand)
		delete(pendingSet, k)
		attempted[k] = struct{}{}
		mu.Unlock()

		attempts++
		if embyDebugLogEnabled() {
			feat := smartComputeCandidateFeatures(at.Cand)
			embyDebugPrintf(
				"[smart][pick] flow=%d tier=%d site=(%s) panFlag=%s provider=%s matchShowName=%s matchRawName=%s quality=%s",
				flowID,
				tier,
				smartLogSiteName(at.Cand.SiteKey, at.Cand.SiteName),
				strings.TrimSpace(at.Cand.PanLabel),
				smartPanMockProviderID(database, strings.TrimSpace(at.Cand.PanLabel)),
				strings.TrimSpace(at.Cand.Ep.Name),
				strings.TrimSpace(smartFirstRawNameFromURL(at.Cand.Ep.URL)),
				strings.TrimSpace(feat.Quality),
			)
		}
		if res := smartTryPlayPickedCandidate(flowID, database, apiBase, tvUser, at.Cand, at.AccessByShare); res != nil && strings.TrimSpace(res.PlayURL) != "" {
			feat := smartComputeCandidateFeatures(res.Cand)
			if embyDebugLogEnabled() {
				raw0 := smartFirstRawNameFromURL(res.Cand.Ep.URL)
				embyDebugPrintf(
					"[smart][playback_ok] flow=%d ms=%d site=(%s) panFlag=%s provider=%s matchShowName=%s matchRawName=%s quality=%s url=%s",
					flowID,
					time.Since(flowStart).Milliseconds(),
					smartLogSiteName(res.Cand.SiteKey, res.Cand.SiteName),
					strings.TrimSpace(res.Cand.PanLabel),
					smartPanMockProviderID(database, strings.TrimSpace(res.Cand.PanLabel)),
					strings.TrimSpace(res.Cand.Ep.Name),
					strings.TrimSpace(raw0),
					strings.TrimSpace(feat.Quality),
					smartShortURLForLog(res.PlayURL),
				)
			}
			upsertSmartPlayHistoryBestEffort(res.Cand)
			cancel()
			if embyDebugLogEnabled() {
				embyDebugPrintf(
					"[smart][flow_done] flow=%d ms=%d status=ok offers=%d queued=%d dedup=%d dropped=%d attempts=%d",
					flowID,
					time.Since(flowStart).Milliseconds(),
					atomic.LoadInt64(&offersSeen),
					atomic.LoadInt64(&offersQueued),
					atomic.LoadInt64(&offersDeduped),
					atomic.LoadInt64(&offersDroppedFull),
					attempts,
				)
			}
			return strings.TrimSpace(res.PlayURL), res.Headers, buildPicked(res.Cand, feat), nil
		}
	}
}
