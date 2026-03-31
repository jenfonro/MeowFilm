package smart

import (
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/magic"
	"github.com/jenfonro/meowfilm/server/netdisk"
)

type smartPanMock189AccessEntry struct {
	AccessCode string
	ExpireAt   time.Time
}

var smartPanMock189Access struct {
	mu sync.Mutex
	m  map[string]smartPanMock189AccessEntry // key: shareID
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
	out := pan
	accessByShareID := map[string]string{}
	if database == nil || !out.PanMockEnabled {
		return out, accessByShareID, false, "skip", nil
	}

	label := strings.TrimSpace(out.Label)
	pid := smartPanMockProviderFromLabel(label)
	if pid == "" {
		return out, accessByShareID, false, "skip", nil
	}

	firstURL := ""
	if len(out.Episodes) > 0 {
		firstURL = strings.TrimSpace(out.Episodes[0].URL)
	}

	var (
		vod       string
		fromCache bool
		err       error
	)

	switch pid {
	case "189":
		sc, ac := extractTianyiMockMeta(label, firstURL)
		if sc == "" {
			return out, accessByShareID, false, "skip", nil
		}
		flag := "天意-" + sc
		var shareID string
		vod, shareID, _, fromCache, err = netdisk.Tianyi189ListWithCacheHit(database, flag, ac)
		if err != nil {
			return out, accessByShareID, fromCache, "err", err
		}
		if strings.TrimSpace(vod) == "" {
			return out, accessByShareID, fromCache, "empty", nil
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
			return out, accessByShareID, fromCache, "err", err
		}
		if strings.TrimSpace(vod) == "" {
			return out, accessByShareID, fromCache, "empty", nil
		}
	case "uc":
		pass := extractMockPasscodeFromEpisodeURL(firstURL)
		vod, _, fromCache, err = netdisk.UCListWithCacheHit(database, label, pass)
		if err != nil {
			return out, accessByShareID, fromCache, "err", err
		}
		if strings.TrimSpace(vod) == "" {
			return out, accessByShareID, fromCache, "empty", nil
		}
	case "139":
		pass := extractMockPasscodeFromEpisodeURL(firstURL)
		vod, _, fromCache, err = netdisk.Yun139ListWithCacheHit(database, label, pass)
		if err != nil {
			return out, accessByShareID, fromCache, "err", err
		}
		if strings.TrimSpace(vod) == "" {
			return out, accessByShareID, fromCache, "empty", nil
		}
	case "baidu":
		pass := extractMockPasscodeFromEpisodeURL(firstURL)
		vod, _, fromCache, err = netdisk.BaiduListWithCacheHit(database, label, pass)
		if err != nil {
			return out, accessByShareID, fromCache, "err", err
		}
		if strings.TrimSpace(vod) == "" {
			return out, accessByShareID, fromCache, "empty", nil
		}
	default:
		return out, accessByShareID, false, "skip", nil
	}

	eps := smartParseVodPlayURLToEpisodes(vod)
	for k := range eps {
		eps[k].Flag = label
	}
	out.Episodes = eps
	return out, accessByShareID, fromCache, "ok", nil
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
					// Try to find the episode that matches `want` using the same magic rules as smart playback.
					// If no match can be extracted (naming does not follow rules), keep empty to avoid confusion.
					if want > 0 && len(rawCleanRules) > 0 && len(rawEpisodeRules) > 0 {
						for _, ep := range eps {
							if strings.TrimSpace(ep.URL) == "" {
								continue
							}
							texts := smartExtractEpisodeCandidateTexts(ep)
							jsMatch, e2 := magic.MagicEpisodeExtractFromCandidates(texts, rawCleanRules, rawEpisodeRules)
							if e2 != nil {
								continue
							}
							match, keyNo, ok := smartResolveEpisodeMappingStrict(tmdbSeasons, smartSeasonEpisode{Season: jsMatch.Season, Episode: jsMatch.Episode})
							epNo := match.Episode
							if !ok || epNo <= 0 {
								continue
							}
							if keyNo != want {
								continue
							}
							matchShowName = strings.TrimSpace(ep.Name)
							rawNames := smartExtractRawNamesFromEpisodeURL(strings.TrimSpace(ep.URL))
							if len(rawNames) > 0 {
								matchRawName = strings.TrimSpace(rawNames[0])
							}
							break
						}
					}
				}
				errMsg := ""
				if err != nil {
					errMsg = strings.TrimSpace(err.Error())
				}
				smartDebugPrintf(
					"[smart][pan_list_%s] site=(%s) panFlag=%s provider=%s ms=%d episodes=%d matchShowName=%s matchRawName=%s err=%s",
					status,
					smartLogSiteName(siteKey, siteName),
					label,
					pid,
					ms,
					epCount,
					matchShowName,
					matchRawName,
					errMsg,
				)
			}

			resolvedPan, accessDelta, hit, status, err := smartResolveSinglePanMockPan(database, out[i])
			eps := resolvedPan.Episodes
			if status == "ok" {
				mu.Lock()
				out[i] = resolvedPan
				for sid, acc := range accessDelta {
					accessByShareID[sid] = acc
				}
				mu.Unlock()
			}
			logDone(status, eps, err, hit)
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
	onPanResolved func(panIndex int, episodes []catpawrunner.Episode, accessDelta map[string]string),
) ([]catpawrunner.Pan, map[string]string) {
	out := make([]catpawrunner.Pan, 0, len(pans))
	for _, p := range pans {
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

			emit := func(status string, eps []catpawrunner.Episode, err error, accessDelta map[string]string, fromCache bool) {
				if smartDebugLogEnabled() {
					ms := time.Since(start).Milliseconds()
					epCount := 0
					matchShowName := ""
					matchRawName := ""
					if eps != nil {
						epCount = len(eps)
						// Best-effort: show the matched episode for the current `want`.
						if want > 0 && len(rawCleanRules) > 0 && len(rawEpisodeRules) > 0 {
							for _, ep := range eps {
								if strings.TrimSpace(ep.URL) == "" {
									continue
								}
								texts := smartExtractEpisodeCandidateTexts(ep)
								jsMatch, e2 := magic.MagicEpisodeExtractFromCandidates(texts, rawCleanRules, rawEpisodeRules)
								if e2 != nil {
									continue
								}
								match, keyNo, ok := smartResolveEpisodeMappingStrict(tmdbSeasons, smartSeasonEpisode{Season: jsMatch.Season, Episode: jsMatch.Episode})
								epNo := match.Episode
								if !ok || epNo <= 0 {
									continue
								}
								if keyNo != want {
									continue
								}
								matchShowName = strings.TrimSpace(ep.Name)
								rawNames := smartExtractRawNamesFromEpisodeURL(strings.TrimSpace(ep.URL))
								if len(rawNames) > 0 {
									matchRawName = strings.TrimSpace(rawNames[0])
								}
								break
							}
						}
					}
					errMsg := ""
					if err != nil {
						errMsg = strings.TrimSpace(err.Error())
					}
					pat := "[smart][pan_list_%s] site=(%s) panFlag=%s provider=%s ms=%d episodes=%d matchShowName=%s matchRawName=%s err=%s"
					if fromCache {
						pat = "[smart][cache][pan_list_%s] site=(%s) panFlag=%s provider=%s ms=%d episodes=%d matchShowName=%s matchRawName=%s err=%s"
					}
					smartDebugPrintf(pat, status, smartLogSiteName(siteKey, siteName), label, pid, ms, epCount, matchShowName, matchRawName, errMsg)
				}
				if onPanResolved != nil {
					onPanResolved(i, eps, accessDelta)
				}
			}

			resolvedPan, accessDelta, hit, status, err := smartResolveSinglePanMockPan(database, out[i])
			eps := resolvedPan.Episodes
			if status == "ok" {
				mu.Lock()
				out[i] = resolvedPan
				for sid, acc := range accessDelta {
					accessByShareID[sid] = acc
				}
				mu.Unlock()
			}
			emit(status, eps, err, accessDelta, hit)
		}()
	}

	wg.Wait()
	return out, accessByShareID
}
