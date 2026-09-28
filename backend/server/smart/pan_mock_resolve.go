package smart

import (
	"context"
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

func extractPanMockPasscodeFromSourceValue(sourceValue string) string {
	return smartPanMockPasscodeFromSourceValue(sourceValue)
}

func extractPanMock189Credentials(panFlag string, sourceValue string) (shareCode string, accessCode string) {
	return smartPanMock189CredentialsFromSourceValue(panFlag, sourceValue)
}

func smartResolvePanFlagEpisodesRaw(database *db.DB, panFlag string, episodeURL string) ([]catpawrunner.Episode, map[string]string, bool, string, error) {
	return smartResolvePanFlagEpisodesRawWithTimeout(database, panFlag, episodeURL, 0)
}

func smartResolvePanFlagEpisodesRawWithTimeout(database *db.DB, panFlag string, episodeURL string, timeout time.Duration) ([]catpawrunner.Episode, map[string]string, bool, string, error) {
	resolve := func() ([]catpawrunner.Episode, map[string]string, bool, string, error) {
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
			sc, ac := extractPanMock189Credentials(label, firstURL)
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
			pass := extractPanMockPasscodeFromSourceValue(firstURL)
			vod, _, fromCache, err = netdisk.QuarkListWithCacheHit(database, label, pass)
			if err != nil {
				return nil, accessByShareID, fromCache, "err", err
			}
			if strings.TrimSpace(vod) == "" {
				return nil, accessByShareID, fromCache, "empty", nil
			}
		case "uc":
			pass := extractPanMockPasscodeFromSourceValue(firstURL)
			vod, _, fromCache, err = netdisk.UCListWithCacheHit(database, label, pass)
			if err != nil {
				return nil, accessByShareID, fromCache, "err", err
			}
			if strings.TrimSpace(vod) == "" {
				return nil, accessByShareID, fromCache, "empty", nil
			}
		case "139":
			pass := extractPanMockPasscodeFromSourceValue(firstURL)
			vod, _, fromCache, err = netdisk.Yun139ListWithCacheHit(database, label, pass)
			if err != nil {
				return nil, accessByShareID, fromCache, "err", err
			}
			if strings.TrimSpace(vod) == "" {
				return nil, accessByShareID, fromCache, "empty", nil
			}
		case "baidu":
			pass := extractPanMockPasscodeFromSourceValue(firstURL)
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
	if timeout <= 0 {
		return resolve()
	}
	type result struct {
		episodes      []catpawrunner.Episode
		accessByShare map[string]string
		fromCache     bool
		status        string
		err           error
	}
	done := make(chan result, 1)
	go func() {
		eps, accessByShare, fromCache, status, err := resolve()
		done <- result{
			episodes:      eps,
			accessByShare: accessByShare,
			fromCache:     fromCache,
			status:        status,
			err:           err,
		}
	}()
	timer := time.NewTimer(timeout)
	defer timer.Stop()
	select {
	case out := <-done:
		return out.episodes, out.accessByShare, out.fromCache, out.status, out.err
	case <-timer.C:
		return nil, map[string]string{}, false, "err", context.DeadlineExceeded
	}
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

func smartResolveSharedPanGroupEpisodes(database *db.DB, record smartDetailSourceRecord) ([]catpawrunner.Episode, map[string]string, bool, string, bool, error) {
	return smartResolveSharedPanGroupEpisodesWithTimeout(database, record, 0)
}

func smartResolveSharedPanGroupEpisodesWithTimeout(database *db.DB, record smartDetailSourceRecord, timeout time.Duration) ([]catpawrunner.Episode, map[string]string, bool, string, bool, error) {
	label := strings.TrimSpace(record.PanFlag)
	groupKey := strings.TrimSpace(record.GroupKey)
	if groupKey == "" {
		groupKey = smartBuildPanMockResolveGroupKey(record)
	}
	if database == nil || label == "" || groupKey == "" {
		return nil, map[string]string{}, false, "skip", false, nil
	}
	waitCh, status, _, found := smartSharedPanListLookup(groupKey)
	if found {
		if status == smartSharedPanListResolving {
			if smartDebugLogEnabled() {
				smartDebugPrintf("[smart][pan_list_skip] panFlag=%s provider=%s reason=shared_resolving", label, smartPanMockProviderFromLabel(label))
			}
			if timeout > 0 {
				timer := time.NewTimer(timeout)
				select {
				case <-waitCh:
				case <-timer.C:
					timer.Stop()
					return nil, map[string]string{}, false, "err", false, context.DeadlineExceeded
				}
				timer.Stop()
			} else {
				<-waitCh
			}
		} else if smartDebugLogEnabled() {
			smartDebugPrintf("[smart][pan_list_skip] panFlag=%s provider=%s reason=shared_resolved", label, smartPanMockProviderFromLabel(label))
		}
		shared := smartSharedPanListGetResolved(groupKey)
		return cloneEpisodeList(shared.Episodes), cloneStringMap(shared.AccessDelta), shared.FromCache, strings.TrimSpace(shared.Status), false, smartSharedPanListErr(shared)
	}

	waitCh = make(chan struct{})
	smartSharedPanListSetResolving(groupKey, waitCh)

	eps, accessDelta, fromCache, finalStatus, err := smartResolvePanFlagEpisodesRawWithTimeout(database, label, strings.TrimSpace(record.SourceValue), timeout)
	smartSharedPanListSetResolved(groupKey, smartSharedPanListResult{
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

func smartResolvePanMockSourceRecords(
	database *db.DB,
	siteKey string,
	siteName string,
	want int,
	tmdbSeasons []TMDBSeason,
	tmdbHasMultiSeason bool,
	rawCleanRules []string,
	rawEpisodeRules []string,
	records []smartDetailSourceRecord,
) ([]smartDetailSourceRecord, map[string]string) {
	return smartResolvePanMockSourceRecordsWithTimeout(database, siteKey, siteName, want, tmdbSeasons, tmdbHasMultiSeason, rawCleanRules, rawEpisodeRules, records, 0)
}

func smartResolvePanMockSourceRecordsWithTimeout(
	database *db.DB,
	siteKey string,
	siteName string,
	want int,
	tmdbSeasons []TMDBSeason,
	tmdbHasMultiSeason bool,
	rawCleanRules []string,
	rawEpisodeRules []string,
	records []smartDetailSourceRecord,
	timeout time.Duration,
) ([]smartDetailSourceRecord, map[string]string) {
	out := make([]smartDetailSourceRecord, len(records))
	copy(out, records)
	accessByShareID := map[string]string{}
	if database == nil || len(out) == 0 {
		return out, accessByShareID
	}

	groups := map[string][]int{}
	for idx := range out {
		if !(out[idx].PanMock && out[idx].Supported) {
			continue
		}
		key := strings.TrimSpace(out[idx].GroupKey)
		if key == "" {
			key = smartBuildPanMockResolveGroupKey(out[idx])
			out[idx].GroupKey = key
		}
		if key == "" {
			out[idx].Status = smartDetailSourceSkipped
			continue
		}
		out[idx].Status = smartDetailSourceRunning
		groups[key] = append(groups[key], idx)
	}

	var wg sync.WaitGroup
	var mu sync.Mutex
	for key, idxs := range groups {
		groupKey := key
		indexes := append([]int(nil), idxs...)
		wg.Add(1)
		go func() {
			defer wg.Done()
			head := out[indexes[0]]
			start := time.Now()
			eps, accessDelta, hit, status, emitAllowed, err := smartResolveSharedPanGroupEpisodesWithTimeout(database, head, timeout)
			if emitAllowed && smartDebugLogEnabled() {
				epCount := len(eps)
				matchShowName := ""
				matchRawName := ""
				if eps != nil {
					matchShowName, matchRawName = smartFindPanListLogMatch(want, tmdbSeasons, rawCleanRules, rawEpisodeRules, eps)
				}
				errMsg := ""
				if err != nil {
					errMsg = strings.TrimSpace(err.Error())
				}
				msg := smartAppendLogMatchSuffix(
					"[smart][pan_list_"+status+"] site=("+smartLogSiteName(siteKey, siteName)+") panFlag="+strings.TrimSpace(head.PanFlag)+" ms="+strconv.FormatInt(time.Since(start).Milliseconds(), 10)+" episodes="+strconv.Itoa(epCount),
					matchShowName,
					matchRawName,
				)
				if errMsg != "" {
					msg += " err=" + errMsg
				}
				smartDebugPrintf("%s", msg)
			}
			mu.Lock()
			defer mu.Unlock()
			for _, idx := range indexes {
				out[idx].GroupKey = groupKey
				out[idx].AccessDelta = cloneStringMap(accessDelta)
				out[idx].ErrText = ""
				switch status {
				case "ok":
					out[idx].Episodes = cloneEpisodeList(eps)
					out[idx].Status = smartDetailSourceResolved
				case "empty":
					out[idx].Episodes = nil
					out[idx].Status = smartDetailSourceEmpty
				case "err":
					out[idx].Episodes = nil
					out[idx].Status = smartDetailSourceError
					if err != nil {
						out[idx].ErrText = strings.TrimSpace(err.Error())
					}
				default:
					out[idx].Episodes = nil
					out[idx].Status = smartDetailSourceSkipped
				}
			}
			_ = hit
			for sid, acc := range accessDelta {
				accessByShareID[sid] = acc
			}
		}()
	}
	wg.Wait()
	return out, accessByShareID
}

func smartResolvePanMockSourceRecordsIncremental(
	database *db.DB,
	siteKey string,
	siteName string,
	want int,
	tmdbSeasons []TMDBSeason,
	tmdbHasMultiSeason bool,
	rawCleanRules []string,
	rawEpisodeRules []string,
	records []smartDetailSourceRecord,
	onGroupResolved func(resolved []smartDetailSourceRecord, accessDelta map[string]string, emitAllowed bool),
) ([]smartDetailSourceRecord, map[string]string) {
	return smartResolvePanMockSourceRecordsIncrementalWithTimeout(database, siteKey, siteName, want, tmdbSeasons, tmdbHasMultiSeason, rawCleanRules, rawEpisodeRules, records, 0, onGroupResolved)
}

func smartResolvePanMockSourceRecordsIncrementalWithTimeout(
	database *db.DB,
	siteKey string,
	siteName string,
	want int,
	tmdbSeasons []TMDBSeason,
	tmdbHasMultiSeason bool,
	rawCleanRules []string,
	rawEpisodeRules []string,
	records []smartDetailSourceRecord,
	timeout time.Duration,
	onGroupResolved func(resolved []smartDetailSourceRecord, accessDelta map[string]string, emitAllowed bool),
) ([]smartDetailSourceRecord, map[string]string) {
	out := make([]smartDetailSourceRecord, len(records))
	copy(out, records)
	accessByShareID := map[string]string{}
	if database == nil || len(out) == 0 {
		return out, accessByShareID
	}

	groups := map[string][]int{}
	for idx := range out {
		if !(out[idx].PanMock && out[idx].Supported) {
			continue
		}
		key := strings.TrimSpace(out[idx].GroupKey)
		if key == "" {
			key = smartBuildPanMockResolveGroupKey(out[idx])
			out[idx].GroupKey = key
		}
		if key == "" {
			out[idx].Status = smartDetailSourceSkipped
			continue
		}
		out[idx].Status = smartDetailSourceRunning
		groups[key] = append(groups[key], idx)
	}

	var wg sync.WaitGroup
	var mu sync.Mutex
	for key, idxs := range groups {
		groupKey := key
		indexes := append([]int(nil), idxs...)
		wg.Add(1)
		go func() {
			defer wg.Done()
			head := out[indexes[0]]
			start := time.Now()
			eps, accessDelta, hit, status, emitAllowed, err := smartResolveSharedPanGroupEpisodesWithTimeout(database, head, timeout)
			if emitAllowed && smartDebugLogEnabled() {
				epCount := len(eps)
				matchShowName := ""
				matchRawName := ""
				if eps != nil {
					matchShowName, matchRawName = smartFindPanListLogMatch(want, tmdbSeasons, rawCleanRules, rawEpisodeRules, eps)
				}
				errMsg := ""
				if err != nil {
					errMsg = strings.TrimSpace(err.Error())
				}
				prefix := "[smart][pan_list_" + status + "]"
				if hit {
					prefix = "[smart][cache][pan_list_" + status + "]"
				}
				msg := smartAppendLogMatchSuffix(
					prefix+" site=("+smartLogSiteName(siteKey, siteName)+") panFlag="+strings.TrimSpace(head.PanFlag)+" ms="+strconv.FormatInt(time.Since(start).Milliseconds(), 10)+" episodes="+strconv.Itoa(epCount),
					matchShowName,
					matchRawName,
				)
				if errMsg != "" {
					msg += " err=" + errMsg
				}
				smartDebugPrintf("%s", msg)
			}

			resolvedGroup := make([]smartDetailSourceRecord, 0, len(indexes))
			mu.Lock()
			for _, idx := range indexes {
				out[idx].GroupKey = groupKey
				out[idx].AccessDelta = cloneStringMap(accessDelta)
				out[idx].ErrText = ""
				switch status {
				case "ok":
					out[idx].Episodes = cloneEpisodeList(eps)
					out[idx].Status = smartDetailSourceResolved
				case "empty":
					out[idx].Episodes = nil
					out[idx].Status = smartDetailSourceEmpty
				case "err":
					out[idx].Episodes = nil
					out[idx].Status = smartDetailSourceError
					if err != nil {
						out[idx].ErrText = strings.TrimSpace(err.Error())
					}
				default:
					out[idx].Episodes = nil
					out[idx].Status = smartDetailSourceSkipped
				}
				resolvedGroup = append(resolvedGroup, out[idx])
			}
			for sid, acc := range accessDelta {
				accessByShareID[sid] = acc
			}
			mu.Unlock()
			if onGroupResolved != nil {
				onGroupResolved(resolvedGroup, cloneStringMap(accessDelta), emitAllowed)
			}
		}()
	}
	wg.Wait()
	return out, accessByShareID
}
