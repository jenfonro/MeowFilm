package emby

import (
	"regexp"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/magic"
	"github.com/jenfonro/meowfilm/server/netdisk"
)

type embyPanMock189AccessEntry struct {
	AccessCode string
	ExpireAt   time.Time
}

var embyPanMock189Access struct {
	mu sync.Mutex
	m  map[string]embyPanMock189AccessEntry // key: shareID
}

const embyPanMock189AccessTTL = 30 * time.Minute

func embyPanMock189AccessPut(shareID string, accessCode string) {
	sid := strings.TrimSpace(shareID)
	ac := strings.TrimSpace(accessCode)
	if sid == "" || ac == "" {
		return
	}
	now := time.Now()

	embyPanMock189Access.mu.Lock()
	defer embyPanMock189Access.mu.Unlock()
	if embyPanMock189Access.m == nil {
		embyPanMock189Access.m = map[string]embyPanMock189AccessEntry{}
	}
	// quick cleanup to avoid unbounded growth
	if len(embyPanMock189Access.m) > 4096 {
		for k, v := range embyPanMock189Access.m {
			if !v.ExpireAt.IsZero() && now.After(v.ExpireAt) {
				delete(embyPanMock189Access.m, k)
			}
		}
	}
	embyPanMock189Access.m[sid] = embyPanMock189AccessEntry{
		AccessCode: ac,
		ExpireAt:   now.Add(embyPanMock189AccessTTL),
	}
}

func embyPanMock189AccessGet(shareID string) (string, bool) {
	sid := strings.TrimSpace(shareID)
	if sid == "" {
		return "", false
	}
	now := time.Now()

	embyPanMock189Access.mu.Lock()
	defer embyPanMock189Access.mu.Unlock()
	if embyPanMock189Access.m == nil {
		return "", false
	}
	e, ok := embyPanMock189Access.m[sid]
	if !ok {
		return "", false
	}
	if !e.ExpireAt.IsZero() && now.After(e.ExpireAt) {
		delete(embyPanMock189Access.m, sid)
		return "", false
	}
	ac := strings.TrimSpace(e.AccessCode)
	if ac == "" {
		return "", false
	}
	return ac, true
}

func embyIsPanMockEnabled(detailRaw map[string]any) bool {
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

func embyExtractMockPasscodeFromEpisodeURL(episodeURL string) string {
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

func embyExtractTianyiMockMeta(panLabel string, episodeURL string) (shareCode string, accessCode string) {
	label := strings.TrimSpace(panLabel)
	url := strings.TrimSpace(episodeURL)

	// Fallback: shareCode might already be embedded in the label like "天意-XXXX" / "天翼-XXXX".
	if m := regexp.MustCompile(`(?:天意|天翼)-([A-Za-z0-9]{6,64})`).FindStringSubmatch(label); len(m) == 2 {
		shareCode = strings.TrimSpace(m[1])
	}

	pass := strings.TrimSpace(embyExtractMockPasscodeFromEpisodeURL(url))
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

func embyResolvePanMockDetailPans(
	database *db.DB,
	siteKey string,
	siteName string,
	want int,
	tmdbSeasons []embyTMDBSeason,
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
		pid := smartPanMockProviderID(database, label)
		if pid == "" {
			continue
		}
		firstURL := ""
		if len(out[i].Episodes) > 0 {
			firstURL = strings.TrimSpace(out[i].Episodes[0].URL)
		}
		wg.Add(1)
		go func() {
			defer wg.Done()
			start := time.Now()

			logDone := func(status string, eps []catpawrunner.Episode, err error, fromCache bool) {
				if !embyDebugLogEnabled() {
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
							match := smartNormalizeMaybeGlobalSeasonEpisode(tmdbSeasons, smartSeasonEpisode{Season: jsMatch.Season, Episode: jsMatch.Episode})
							seasonNo := match.Season
							epNo := match.Episode
							if epNo <= 0 {
								continue
							}
							if tmdbHasMultiSeason && seasonNo <= 0 {
								// Multi-season mapping requires a season marker; don't guess.
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
				embyDebugPrintf(
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

			switch pid {
			case "189":
				sc, ac := embyExtractTianyiMockMeta(label, firstURL)
				if sc == "" {
					logDone("skip", nil, nil, false)
					return
				}
				flag := "天意-" + sc
				vod, shareID, _, hit, err := netdisk.Tianyi189ListWithCacheHit(database, flag, ac)
				if err != nil {
					logDone("err", nil, err, hit)
					return
				}
				if strings.TrimSpace(vod) == "" {
					logDone("empty", nil, nil, hit)
					return
				}
				eps := smartParseVodPlayURLToEpisodes(vod)
				for k := range eps {
					eps[k].Flag = label
				}
				mu.Lock()
				out[i].Episodes = eps
				if strings.TrimSpace(shareID) != "" && strings.TrimSpace(ac) != "" {
					sid := strings.TrimSpace(shareID)
					acc := strings.TrimSpace(ac)
					accessByShareID[sid] = acc
					embyPanMock189AccessPut(sid, acc)
				}
				mu.Unlock()
				logDone("ok", eps, nil, hit)
			case "quark":
				pass := embyExtractMockPasscodeFromEpisodeURL(firstURL)
				vod, _, hit, err := netdisk.QuarkListWithCacheHit(database, label, pass)
				if err != nil {
					logDone("err", nil, err, hit)
					return
				}
				if strings.TrimSpace(vod) == "" {
					logDone("empty", nil, nil, hit)
					return
				}
				eps := smartParseVodPlayURLToEpisodes(vod)
				for k := range eps {
					eps[k].Flag = label
				}
				mu.Lock()
				out[i].Episodes = eps
				mu.Unlock()
				logDone("ok", eps, nil, hit)
			case "uc":
				pass := embyExtractMockPasscodeFromEpisodeURL(firstURL)
				vod, _, hit, err := netdisk.UCListWithCacheHit(database, label, pass)
				if err != nil {
					logDone("err", nil, err, hit)
					return
				}
				if strings.TrimSpace(vod) == "" {
					logDone("empty", nil, nil, hit)
					return
				}
				eps := smartParseVodPlayURLToEpisodes(vod)
				for k := range eps {
					eps[k].Flag = label
				}
				mu.Lock()
				out[i].Episodes = eps
				mu.Unlock()
				logDone("ok", eps, nil, hit)
			case "139":
				pass := embyExtractMockPasscodeFromEpisodeURL(firstURL)
				vod, _, hit, err := netdisk.Yun139ListWithCacheHit(database, label, pass)
				if err != nil {
					logDone("err", nil, err, hit)
					return
				}
				if strings.TrimSpace(vod) == "" {
					logDone("empty", nil, nil, hit)
					return
				}
				eps := smartParseVodPlayURLToEpisodes(vod)
				for k := range eps {
					eps[k].Flag = label
				}
				mu.Lock()
				out[i].Episodes = eps
				mu.Unlock()
				logDone("ok", eps, nil, hit)
			case "baidu":
				pass := embyExtractMockPasscodeFromEpisodeURL(firstURL)
				vod, _, hit, err := netdisk.BaiduListWithCacheHit(database, label, pass)
				if err != nil {
					logDone("err", nil, err, hit)
					return
				}
				if strings.TrimSpace(vod) == "" {
					logDone("empty", nil, nil, hit)
					return
				}
				eps := smartParseVodPlayURLToEpisodes(vod)
				for k := range eps {
					eps[k].Flag = label
				}
				mu.Lock()
				out[i].Episodes = eps
				mu.Unlock()
				logDone("ok", eps, nil, hit)
			default:
				logDone("skip", nil, nil, false)
				return
			}
		}()
	}

	wg.Wait()
	return out, accessByShareID
}

// embyResolvePanMockDetailPansIncremental resolves pan_mock list sources concurrently and calls `onPanResolved`
// each time a single pan is resolved (ok/empty/err/skip). This enables smart playback to attempt matching/play
// as soon as any list returns, without waiting for all list requests to finish.
//
// The returned `out` slice contains all resolved episodes when the function returns.
func embyResolvePanMockDetailPansIncremental(
	database *db.DB,
	siteKey string,
	siteName string,
	want int,
	tmdbSeasons []embyTMDBSeason,
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
		pid := smartPanMockProviderID(database, label)
		if pid == "" {
			continue
		}
		firstURL := ""
		if len(out[i].Episodes) > 0 {
			firstURL = strings.TrimSpace(out[i].Episodes[0].URL)
		}
		wg.Add(1)
		go func() {
			defer wg.Done()
			start := time.Now()

			emit := func(status string, eps []catpawrunner.Episode, err error, accessDelta map[string]string, fromCache bool) {
				if embyDebugLogEnabled() {
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
								match := smartNormalizeMaybeGlobalSeasonEpisode(tmdbSeasons, smartSeasonEpisode{Season: jsMatch.Season, Episode: jsMatch.Episode})
								seasonNo := match.Season
								epNo := match.Episode
								if epNo <= 0 {
									continue
								}
								if tmdbHasMultiSeason && seasonNo <= 0 {
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
					embyDebugPrintf(pat, status, smartLogSiteName(siteKey, siteName), label, pid, ms, epCount, matchShowName, matchRawName, errMsg)
				}
				if onPanResolved != nil {
					onPanResolved(i, eps, accessDelta)
				}
			}

			switch pid {
			case "189":
				sc, ac := embyExtractTianyiMockMeta(label, firstURL)
				if sc == "" {
					emit("skip", nil, nil, nil, false)
					return
				}
				flag := "天意-" + sc
				vod, shareID, _, hit, err := netdisk.Tianyi189ListWithCacheHit(database, flag, ac)
				if err != nil {
					emit("err", nil, err, nil, hit)
					return
				}
				if strings.TrimSpace(vod) == "" {
					emit("empty", nil, nil, nil, hit)
					return
				}
				eps := smartParseVodPlayURLToEpisodes(vod)
				for k := range eps {
					eps[k].Flag = label
				}
				delta := map[string]string{}
				mu.Lock()
				out[i].Episodes = eps
				if strings.TrimSpace(shareID) != "" && strings.TrimSpace(ac) != "" {
					sid := strings.TrimSpace(shareID)
					acc := strings.TrimSpace(ac)
					accessByShareID[sid] = acc
					delta[sid] = acc
					embyPanMock189AccessPut(sid, acc)
				}
				mu.Unlock()
				emit("ok", eps, nil, delta, hit)
			case "quark":
				pass := embyExtractMockPasscodeFromEpisodeURL(firstURL)
				vod, _, hit, err := netdisk.QuarkListWithCacheHit(database, label, pass)
				if err != nil {
					emit("err", nil, err, nil, hit)
					return
				}
				if strings.TrimSpace(vod) == "" {
					emit("empty", nil, nil, nil, hit)
					return
				}
				eps := smartParseVodPlayURLToEpisodes(vod)
				for k := range eps {
					eps[k].Flag = label
				}
				mu.Lock()
				out[i].Episodes = eps
				mu.Unlock()
				emit("ok", eps, nil, nil, hit)
			case "uc":
				pass := embyExtractMockPasscodeFromEpisodeURL(firstURL)
				vod, _, hit, err := netdisk.UCListWithCacheHit(database, label, pass)
				if err != nil {
					emit("err", nil, err, nil, hit)
					return
				}
				if strings.TrimSpace(vod) == "" {
					emit("empty", nil, nil, nil, hit)
					return
				}
				eps := smartParseVodPlayURLToEpisodes(vod)
				for k := range eps {
					eps[k].Flag = label
				}
				mu.Lock()
				out[i].Episodes = eps
				mu.Unlock()
				emit("ok", eps, nil, nil, hit)
			case "139":
				pass := embyExtractMockPasscodeFromEpisodeURL(firstURL)
				vod, _, hit, err := netdisk.Yun139ListWithCacheHit(database, label, pass)
				if err != nil {
					emit("err", nil, err, nil, hit)
					return
				}
				if strings.TrimSpace(vod) == "" {
					emit("empty", nil, nil, nil, hit)
					return
				}
				eps := smartParseVodPlayURLToEpisodes(vod)
				for k := range eps {
					eps[k].Flag = label
				}
				mu.Lock()
				out[i].Episodes = eps
				mu.Unlock()
				emit("ok", eps, nil, nil, hit)
			case "baidu":
				pass := embyExtractMockPasscodeFromEpisodeURL(firstURL)
				vod, _, hit, err := netdisk.BaiduListWithCacheHit(database, label, pass)
				if err != nil {
					emit("err", nil, err, nil, hit)
					return
				}
				if strings.TrimSpace(vod) == "" {
					emit("empty", nil, nil, nil, hit)
					return
				}
				eps := smartParseVodPlayURLToEpisodes(vod)
				for k := range eps {
					eps[k].Flag = label
				}
				mu.Lock()
				out[i].Episodes = eps
				mu.Unlock()
				emit("ok", eps, nil, nil, hit)
			default:
				emit("skip", nil, nil, nil, false)
				return
			}
		}()
	}

	wg.Wait()
	return out, accessByShareID
}
