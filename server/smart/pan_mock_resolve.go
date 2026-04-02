package smart

import (
	"errors"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/magic"
	"github.com/jenfonro/meowfilm/server/netdisk"
)

func smartFindPanListLogMatch(
	want int,
	tmdbSeasons []TMDBSeason,
	rawCleanRules []string,
	rawEpisodeRules []string,
	eps []catpawrunner.Episode,
) (matchShowName string, matchRawName string) {
	if len(eps) == 0 || len(rawCleanRules) == 0 || len(rawEpisodeRules) == 0 {
		return "", ""
	}
	for _, ep := range eps {
		if strings.TrimSpace(ep.URL) == "" {
			continue
		}
		texts := smartExtractEpisodeCandidateTexts(ep)
		jsMatch, err := magic.MagicEpisodeExtractFromCandidates(texts, rawCleanRules, rawEpisodeRules)
		if err != nil {
			continue
		}
		match, keyNo, ok, _, _, _ := smartResolveEpisodeMappingForPlaybackWithMode(
			tmdbSeasons,
			smartSeasonEpisode{Season: jsMatch.Season, Episode: jsMatch.Episode},
			nil,
			0,
			false,
			false,
			"tmdb",
		)
		if !ok || match.Episode <= 0 || (want > 0 && keyNo != want) {
			continue
		}
		rawNames := smartExtractRawNamesFromEpisodeURL(strings.TrimSpace(ep.URL))
		rawName := ""
		if len(rawNames) > 0 {
			rawName = strings.TrimSpace(rawNames[0])
		}
		return strings.TrimSpace(ep.Name), rawName
	}
	return "", ""
}

type smartPanMock189AccessEntry struct {
	AccessCode string
	ExpireAt   time.Time
}

type smartSharedPanListState string

const (
	smartSharedPanListResolving smartSharedPanListState = "resolving"
	smartSharedPanListResolved  smartSharedPanListState = "resolved"
)

type smartSharedPanListResult struct {
	State       smartSharedPanListState
	Status      string
	Episodes    []catpawrunner.Episode
	AccessDelta map[string]string
	ErrText     string
	FromCache   bool
	ResolvedAt  time.Time
	WaitCh      chan struct{}
}

var smartPanMock189Access struct {
	mu sync.Mutex
	m  map[string]smartPanMock189AccessEntry // key: shareID
}

var smartSharedPanList struct {
	mu sync.Mutex
	m  map[string]smartSharedPanListResult // key: panFlag
}

const smartPanMock189AccessTTL = 30 * time.Minute

func smartPanMock189AccessPut(shareID string, accessCode string) {
	sid := strings.TrimSpace(shareID)
	ac := strings.TrimSpace(accessCode)
	if sid == "" || ac == "" {
		return
	}
	now := time.Now()

	smartPanMock189Access.mu.Lock()
	defer smartPanMock189Access.mu.Unlock()
	if smartPanMock189Access.m == nil {
		smartPanMock189Access.m = map[string]smartPanMock189AccessEntry{}
	}
	// quick cleanup to avoid unbounded growth
	if len(smartPanMock189Access.m) > 4096 {
		for k, v := range smartPanMock189Access.m {
			if !v.ExpireAt.IsZero() && now.After(v.ExpireAt) {
				delete(smartPanMock189Access.m, k)
			}
		}
	}
	smartPanMock189Access.m[sid] = smartPanMock189AccessEntry{
		AccessCode: ac,
		ExpireAt:   now.Add(smartPanMock189AccessTTL),
	}
}

func smartPanMock189AccessGet(shareID string) (string, bool) {
	sid := strings.TrimSpace(shareID)
	if sid == "" {
		return "", false
	}
	now := time.Now()

	smartPanMock189Access.mu.Lock()
	defer smartPanMock189Access.mu.Unlock()
	if smartPanMock189Access.m == nil {
		return "", false
	}
	e, ok := smartPanMock189Access.m[sid]
	if !ok {
		return "", false
	}
	if !e.ExpireAt.IsZero() && now.After(e.ExpireAt) {
		delete(smartPanMock189Access.m, sid)
		return "", false
	}
	ac := strings.TrimSpace(e.AccessCode)
	if ac == "" {
		return "", false
	}
	return ac, true
}

func extractMockPasscodeFromEpisodeURL(episodeURL string) string {
	return smartExtractMockPasscodeFromEpisodeURL(episodeURL)
}

func extractTianyiMockMeta(panFlag string, episodeURL string) (shareCode string, accessCode string) {
	return smartExtractTianyiMockMetaFromEpisodeURL(panFlag, episodeURL)
}

func smartResolveSinglePanMockPan(database *db.DB, pan catpawrunner.Pan) (catpawrunner.Pan, map[string]string, bool, string, error) {
	out, accessByShareID, fromCache, status, _, err := smartResolveSinglePanMockPanShared(database, pan)
	return out, accessByShareID, fromCache, status, err
}

func smartResolveSinglePanMockPanShared(database *db.DB, pan catpawrunner.Pan) (catpawrunner.Pan, map[string]string, bool, string, bool, error) {
	out := pan
	accessByShareID := map[string]string{}
	if database == nil || !out.PanMockEnabled {
		return out, accessByShareID, false, "skip", false, nil
	}

	label := strings.TrimSpace(out.Label)
	pid := smartPanMockProviderFromLabel(label)
	if pid == "" {
		return out, accessByShareID, false, "skip", false, nil
	}

	firstURL := ""
	firstName := ""
	if len(out.Episodes) > 0 {
		firstURL = strings.TrimSpace(out.Episodes[0].URL)
		firstName = strings.TrimSpace(out.Episodes[0].Name)
	}
	eps, accessDelta, fromCache, status, emitAllowed, err := smartResolveSharedPanFlagEpisodes(database, label, firstURL)
	if smartDebugLogEnabled() && emitAllowed {
		smartDebugPrintf("[smart][pan_list_input] panFlag=%s accesscode=%s", label, firstName)
	}
	if err != nil {
		return out, accessByShareID, fromCache, status, emitAllowed, err
	}
	if status != "ok" {
		return out, accessByShareID, fromCache, status, emitAllowed, nil
	}
	for k := range eps {
		eps[k].Flag = label
	}
	for sid, acc := range accessDelta {
		accessByShareID[sid] = acc
	}
	out.Episodes = eps
	return out, accessByShareID, fromCache, "ok", emitAllowed, nil
}

func smartResolveSharedPanFlagEpisodes(database *db.DB, panFlag string, episodeURL string) ([]catpawrunner.Episode, map[string]string, bool, string, bool, error) {
	label := strings.TrimSpace(panFlag)
	if database == nil || label == "" {
		return nil, map[string]string{}, false, "skip", false, nil
	}
	waitCh, status, _, found := smartSharedPanListLookup(label)
	if found {
		if status == smartSharedPanListResolving {
			if smartDebugLogEnabled() {
				smartDebugPrintf("[smart][pan_list_skip] panFlag=%s provider=%s reason=shared_resolving", label, smartPanMockProviderFromLabel(label))
			}
			<-waitCh
		} else if smartDebugLogEnabled() {
			smartDebugPrintf("[smart][pan_list_skip] panFlag=%s provider=%s reason=shared_resolved", label, smartPanMockProviderFromLabel(label))
		}
		shared := smartSharedPanListGetResolved(label)
		return cloneEpisodeList(shared.Episodes), cloneStringMap(shared.AccessDelta), shared.FromCache, strings.TrimSpace(shared.Status), false, smartSharedPanListErr(shared)
	}

	waitCh = make(chan struct{})
	smartSharedPanListSetResolving(label, waitCh)

	eps, accessDelta, fromCache, finalStatus, err := smartResolvePanFlagEpisodesRaw(database, label, episodeURL)
	smartSharedPanListSetResolved(label, smartSharedPanListResult{
		State:       smartSharedPanListResolved,
		Status:      strings.TrimSpace(finalStatus),
		Episodes:    cloneEpisodeList(eps),
		AccessDelta: cloneStringMap(accessDelta),
		ErrText:     smartSharedPanListErrText(err),
		FromCache:   fromCache,
		ResolvedAt:  time.Now(),
		WaitCh:      waitCh,
	})
	close(waitCh)
	return cloneEpisodeList(eps), cloneStringMap(accessDelta), fromCache, strings.TrimSpace(finalStatus), true, err
}

func smartResolvePanFlagEpisodesRaw(database *db.DB, panFlag string, episodeURL string) ([]catpawrunner.Episode, map[string]string, bool, string, error) {
	label := strings.TrimSpace(panFlag)
	accessByShareID := map[string]string{}
	pid := smartPanMockProviderFromLabel(label)
	if database == nil || pid == "" {
		return nil, accessByShareID, false, "skip", nil
	}
	firstURL := strings.TrimSpace(episodeURL)
	var (
		vod       string
		fromCache bool
		err       error
	)
	switch pid {
	case "189":
		sc, ac := extractTianyiMockMeta(label, firstURL)
		if sc == "" {
			return nil, accessByShareID, false, "skip", nil
		}
		flag := "天意-" + sc
		var shareID string
		vod, shareID, _, fromCache, err = netdisk.Tianyi189ListWithCacheHit(database, flag, ac)
		if err != nil {
			return nil, accessByShareID, fromCache, "err", err
		}
		if strings.TrimSpace(vod) == "" {
			return nil, accessByShareID, fromCache, "empty", nil
		}
		if strings.TrimSpace(shareID) != "" && strings.TrimSpace(ac) != "" {
			sid := strings.TrimSpace(shareID)
			acc := strings.TrimSpace(ac)
			accessByShareID[sid] = acc
			smartPanMock189AccessPut(sid, acc)
		}
	case "quark":
		pass := extractMockPasscodeFromEpisodeURL(firstURL)
		vod, _, fromCache, err = netdisk.QuarkListWithCacheHit(database, label, pass)
		if err != nil {
			return nil, accessByShareID, fromCache, "err", err
		}
		if strings.TrimSpace(vod) == "" {
			return nil, accessByShareID, fromCache, "empty", nil
		}
	case "uc":
		pass := extractMockPasscodeFromEpisodeURL(firstURL)
		vod, _, fromCache, err = netdisk.UCListWithCacheHit(database, label, pass)
		if err != nil {
			return nil, accessByShareID, fromCache, "err", err
		}
		if strings.TrimSpace(vod) == "" {
			return nil, accessByShareID, fromCache, "empty", nil
		}
	case "139":
		pass := extractMockPasscodeFromEpisodeURL(firstURL)
		vod, _, fromCache, err = netdisk.Yun139ListWithCacheHit(database, label, pass)
		if err != nil {
			return nil, accessByShareID, fromCache, "err", err
		}
		if strings.TrimSpace(vod) == "" {
			return nil, accessByShareID, fromCache, "empty", nil
		}
	case "baidu":
		pass := extractMockPasscodeFromEpisodeURL(firstURL)
		vod, _, fromCache, err = netdisk.BaiduListWithCacheHit(database, label, pass)
		if err != nil {
			return nil, accessByShareID, fromCache, "err", err
		}
		if strings.TrimSpace(vod) == "" {
			return nil, accessByShareID, fromCache, "empty", nil
		}
	default:
		return nil, accessByShareID, false, "skip", nil
	}
	eps := smartParseVodPlayURLToEpisodes(vod)
	return eps, accessByShareID, fromCache, "ok", nil
}

func smartSharedPanListLookup(panFlag string) (chan struct{}, smartSharedPanListState, smartSharedPanListResult, bool) {
	label := strings.TrimSpace(panFlag)
	if label == "" {
		return nil, "", smartSharedPanListResult{}, false
	}
	smartSharedPanList.mu.Lock()
	defer smartSharedPanList.mu.Unlock()
	if smartSharedPanList.m == nil {
		smartSharedPanList.m = map[string]smartSharedPanListResult{}
	}
	out, ok := smartSharedPanList.m[label]
	if !ok {
		return nil, "", smartSharedPanListResult{}, false
	}
	return out.WaitCh, out.State, out, true
}

func smartSharedPanListSetResolving(panFlag string, waitCh chan struct{}) {
	label := strings.TrimSpace(panFlag)
	if label == "" {
		return
	}
	smartSharedPanList.mu.Lock()
	defer smartSharedPanList.mu.Unlock()
	if smartSharedPanList.m == nil {
		smartSharedPanList.m = map[string]smartSharedPanListResult{}
	}
	smartSharedPanList.m[label] = smartSharedPanListResult{
		State:  smartSharedPanListResolving,
		WaitCh: waitCh,
	}
}

func smartSharedPanListSetResolved(panFlag string, result smartSharedPanListResult) {
	label := strings.TrimSpace(panFlag)
	if label == "" {
		return
	}
	smartSharedPanList.mu.Lock()
	defer smartSharedPanList.mu.Unlock()
	if smartSharedPanList.m == nil {
		smartSharedPanList.m = map[string]smartSharedPanListResult{}
	}
	smartSharedPanList.m[label] = result
}

func smartSharedPanListGetResolved(panFlag string) smartSharedPanListResult {
	label := strings.TrimSpace(panFlag)
	smartSharedPanList.mu.Lock()
	defer smartSharedPanList.mu.Unlock()
	if smartSharedPanList.m == nil {
		return smartSharedPanListResult{}
	}
	return smartSharedPanList.m[label]
}

func smartSharedPanListErrText(err error) string {
	if err == nil {
		return ""
	}
	return strings.TrimSpace(err.Error())
}

func smartSharedPanListErr(result smartSharedPanListResult) error {
	if strings.TrimSpace(result.ErrText) == "" {
		return nil
	}
	return errors.New(strings.TrimSpace(result.ErrText))
}

func cloneEpisodeList(in []catpawrunner.Episode) []catpawrunner.Episode {
	if len(in) == 0 {
		return nil
	}
	out := make([]catpawrunner.Episode, len(in))
	copy(out, in)
	return out
}

func cloneStringMap(in map[string]string) map[string]string {
	if len(in) == 0 {
		return map[string]string{}
	}
	out := make(map[string]string, len(in))
	for k, v := range in {
		out[strings.TrimSpace(k)] = strings.TrimSpace(v)
	}
	return out
}

func smartResolvePanMockDetailPans(
	database *db.DB,
	siteKey string,
	siteName string,
	want int,
	tmdbSeasons []TMDBSeason,
	tmdbHasMultiSeason bool,
	rawCleanRules []string,
	rawEpisodeRules []string,
	pans []catpawrunner.Pan,
) ([]catpawrunner.Pan, map[string]string) {
	out := make([]catpawrunner.Pan, 0, len(pans))
	for _, p := range pans {
		p.PanMockEnabled = true
		out = append(out, p)
	}
	accessByShareID := map[string]string{}
	if database == nil || len(out) == 0 {
		return out, accessByShareID
	}

	var wg sync.WaitGroup
	var mu sync.Mutex

	for idx := range out {
		i := idx
		label := strings.TrimSpace(out[i].Label)
		pid := smartPanMockProviderFromLabel(label)
		if pid == "" {
			continue
		}
		wg.Add(1)
		go func() {
			defer wg.Done()
			start := time.Now()

			logDone := func(status string, eps []catpawrunner.Episode, err error, fromCache bool) {
				if !smartDebugLogEnabled() {
					return
				}
				ms := time.Since(start).Milliseconds()
				epCount := 0
				matchShowName := ""
				matchRawName := ""
				if eps != nil {
					epCount = len(eps)
					matchShowName, matchRawName = smartFindPanListLogMatch(want, tmdbSeasons, rawCleanRules, rawEpisodeRules, eps)
				}
				errMsg := ""
				if err != nil {
					errMsg = strings.TrimSpace(err.Error())
				}
				msg := smartAppendLogMatchSuffix(
					"[smart][pan_list_"+status+"] site=("+smartLogSiteName(siteKey, siteName)+") panFlag="+label+" ms="+strconv.FormatInt(ms, 10)+" episodes="+strconv.Itoa(epCount),
					matchShowName,
					matchRawName,
				)
				if errMsg != "" {
					msg += " err=" + errMsg
				}
				smartDebugPrintf("%s", msg)
			}

			resolvedPan, accessDelta, hit, status, emitAllowed, err := smartResolveSinglePanMockPanShared(database, out[i])
			eps := resolvedPan.Episodes
			if status == "ok" {
				mu.Lock()
				out[i] = resolvedPan
				for sid, acc := range accessDelta {
					accessByShareID[sid] = acc
				}
				mu.Unlock()
			}
			if emitAllowed {
				logDone(status, eps, err, hit)
			}
		}()
	}

	wg.Wait()
	return out, accessByShareID
}

// smartResolvePanMockDetailPansIncremental resolves pan_mock list sources concurrently and calls `onPanResolved`
// each time a single pan is resolved (ok/empty/err/skip). This enables smart playback to attempt matching/play
// as soon as any list returns, without waiting for all list requests to finish.
//
// The returned `out` slice contains all resolved episodes when the function returns.
func smartResolvePanMockDetailPansIncremental(
	database *db.DB,
	siteKey string,
	siteName string,
	want int,
	tmdbSeasons []TMDBSeason,
	tmdbHasMultiSeason bool,
	rawCleanRules []string,
	rawEpisodeRules []string,
	pans []catpawrunner.Pan,
	onPanResolved func(panIndex int, episodes []catpawrunner.Episode, accessDelta map[string]string, emitAllowed bool),
) ([]catpawrunner.Pan, map[string]string) {
	out := make([]catpawrunner.Pan, 0, len(pans))
	for _, p := range pans {
		p.PanMockEnabled = true
		out = append(out, p)
	}
	accessByShareID := map[string]string{}
	if database == nil || len(out) == 0 {
		return out, accessByShareID
	}

	var wg sync.WaitGroup
	var mu sync.Mutex

	for idx := range out {
		i := idx
		label := strings.TrimSpace(out[i].Label)
		pid := smartPanMockProviderFromLabel(label)
		if pid == "" {
			continue
		}
		wg.Add(1)
		go func() {
			defer wg.Done()
			start := time.Now()

			emit := func(status string, eps []catpawrunner.Episode, err error, accessDelta map[string]string, fromCache bool, emitAllowed bool) {
				if smartDebugLogEnabled() && emitAllowed {
				ms := time.Since(start).Milliseconds()
				epCount := 0
				matchShowName := ""
				matchRawName := ""
				if eps != nil {
					epCount = len(eps)
					matchShowName, matchRawName = smartFindPanListLogMatch(want, tmdbSeasons, rawCleanRules, rawEpisodeRules, eps)
				}
					errMsg := ""
					if err != nil {
						errMsg = strings.TrimSpace(err.Error())
					}
					prefix := "[smart][pan_list_" + status + "]"
					if fromCache {
						prefix = "[smart][cache][pan_list_" + status + "]"
					}
					msg := smartAppendLogMatchSuffix(
						prefix+" site=("+smartLogSiteName(siteKey, siteName)+") panFlag="+label+" ms="+strconv.FormatInt(ms, 10)+" episodes="+strconv.Itoa(epCount),
						matchShowName,
						matchRawName,
					)
					if errMsg != "" {
						msg += " err=" + errMsg
					}
					smartDebugPrintf("%s", msg)
				}
				if onPanResolved != nil {
					onPanResolved(i, eps, accessDelta, emitAllowed)
				}
			}

			resolvedPan, accessDelta, hit, status, emitAllowed, err := smartResolveSinglePanMockPanShared(database, out[i])
			eps := resolvedPan.Episodes
			if status == "ok" {
				mu.Lock()
				out[i] = resolvedPan
				for sid, acc := range accessDelta {
					accessByShareID[sid] = acc
				}
				mu.Unlock()
			}
			emit(status, eps, err, accessDelta, hit, emitAllowed)
		}()
	}

	wg.Wait()
	return out, accessByShareID
}
